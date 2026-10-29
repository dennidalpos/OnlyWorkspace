using System.Net;
using System.Net.Sockets;
using System.Text;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

public sealed class HttpErrorCancellationTests
{
    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpContent _content;
        public Handler(HttpContent content) => _content = content;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = _content });
    }

    private static ILlmClient Client(string provider, HttpClient http) => provider switch
    {
        "ollama" => new OllamaClient(Profile(provider), httpClient: http),
        "openai" => new OpenAIClient(Profile(provider), httpClient: http),
        "anthropic" => new AnthropicClient(Profile(provider), httpClient: http),
        _ => new GeminiClient(Profile(provider), httpClient: http)
    };

    private static LlmProfileConfig Profile(string type) => new()
    {
        Type = type, Model = "synthetic", FirstTokenTimeoutSeconds = 5, IdleTimeoutSeconds = 1
    };

    [Theory]
    [InlineData("ollama", true)]
    [InlineData("openai", true)]
    [InlineData("anthropic", true)]
    [InlineData("gemini", true)]
    [InlineData("ollama", false)]
    [InlineData("openai", false)]
    [InlineData("anthropic", false)]
    [InlineData("gemini", false)]
    public async Task ErrorBodyAfterHeaders_IsInterrupted_AndItsReadIsFinished(string provider, bool cancel)
    {
        var stream = new PendingReadStream("{\"error\":");
        using var http = new HttpClient(new Handler(new UnbufferedHttpContent(stream, "application/json")))
            { BaseAddress = new Uri("http://localhost/") };
        var client = Client(provider, http);
        using var owner = (IDisposable)client;
        using var cts = new CancellationTokenSource();
        var pending = client.CompleteStructuredAsync(new[] { ChatMessage.User("synthetic") }, "{}", cts.Token);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancel)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        else
            await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.True(pending.IsCompleted);
        Assert.True(stream.WasDisposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealLoopbackError_TricklingBodyCannotOutliveCancellationOrDeadline(bool cancel)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var serverStop = new CancellationTokenSource();
        var headersSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            using var stream = peer.GetStream();
            using var request = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            while (!string.IsNullOrEmpty(await request.ReadLineAsync())) { }
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 1000\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length);
            headersSent.SetResult(true);
            try
            {
                while (!serverStop.IsCancellationRequested)
                {
                    await stream.WriteAsync(new byte[] { (byte)' ' }, 0, 1);
                    await Task.Delay(250, serverStop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException) { }
        });
        try
        {
            var profile = Profile("ollama");
            profile.BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new OllamaClient(profile);
            using var cts = new CancellationTokenSource();
            var pending = client.CompleteStructuredAsync(new[] { ChatMessage.User("synthetic") }, "{}", cts.Token);
            await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // The body would trickle for 250 s: the bounds prove it cannot, with margin for a saturated CI runner,
            // where the 2 s once allowed after Cancel ran out while continuations waited for a thread (A120).
            if (cancel)
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(20)));
            }
            else await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(pending.IsCompleted);
        }
        finally
        {
            serverStop.Cancel();
            listener.Stop();
            await server.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task OrdinaryErrorBody_PreservesStatusAndDiagnostic(string provider)
    {
        using var http = new HttpClient(new Handler(new StringContent("{\"error\":{\"message\":\"synthetic diagnostic\",\"param\":\"model\"}}")))
            { BaseAddress = new Uri("http://localhost/") };
        var client = Client(provider, http);
        using var owner = (IDisposable)client;
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.CompleteStructuredAsync(new[] { ChatMessage.User("test") }, "{}"));
        Assert.Contains("400", error.Message);
        Assert.Contains("synthetic diagnostic", error.Message);
    }
}
