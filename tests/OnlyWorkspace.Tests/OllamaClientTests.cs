using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;
using Xunit;

namespace OnlyWorkspace.Tests;

public class OllamaClientTests
{
    [Fact]
    public async Task DirectStructuredRequest_IncludesSchemaInBudget_BeforeHttp()
    {
        int calls = 0;
        using var http = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }))
        { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        using var client = new OllamaClient(new LlmProfileConfig { ContextBudgetTokens = 1000 }, httpClient: http);
        string schema = "{\"type\":\"string\",\"description\":\"" + new string('s', 500) + "\"}";
        await Assert.ThrowsAsync<OnlyWorkspace.Core.Context.ContextTooLongException>(() => client.CompleteStructuredAsync(new[] { ChatMessage.User(new string('x', 3000)) }, schema));
        Assert.Equal(0, calls);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsSuccess_WhenModelExists()
    {
        var json = "{\"models\":[{\"name\":\"llama3.1:8b\"},{\"name\":\"mistral:latest\"}]}";
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Equal("/api/tags", req.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var profile = new LlmProfileConfig
        {
            BaseUrl = "http://localhost:11434",
            Model = "llama3.1:8b"
        };

        using var ollama = new OllamaClient(profile, httpClient: client);
        var health = await ollama.CheckHealthAsync();

        Assert.True(health.IsHealthy);
        Assert.Equal(2, health.AvailableModels.Count);
        Assert.Contains("llama3.1:8b", health.AvailableModels);
    }

    [Fact]
    public async Task StreamChatAsync_YieldsChunks_FromNdjson()
    {
        var ndjson = "{\"message\":{\"content\":\"Ciao \"},\"done\":false}\n{\"message\":{\"content\":\"mondo!\"},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true}\n";
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Equal("/api/chat", req.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var profile = new LlmProfileConfig { Model = "llama3.1:8b" };

        using var ollama = new OllamaClient(profile, httpClient: client);
        var messages = new[] { ChatMessage.User("Saluta") };

        var chunks = new List<string>();
        await foreach (var chunk in ollama.StreamChatAsync(messages))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Ciao ", chunks[0]);
        Assert.Equal("mondo!", chunks[1]);
    }

    [Fact]
    public async Task StreamChatAsync_CapturesTokenUsage_AndDetectsTruncation()
    {
        var ndjson = "{\"message\":{\"content\":\"Test\"},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true,\"prompt_eval_count\":120,\"eval_count\":50,\"done_reason\":\"length\"}\n";
        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var profile = new LlmProfileConfig { Model = "llama3.1:8b" };

        using var ollama = new OllamaClient(profile, httpClient: client);
        var messages = new[] { ChatMessage.User("Test") };

        await foreach (var _ in ollama.StreamChatAsync(messages)) { }

        Assert.NotNull(ollama.LastTokenUsage);
        Assert.Equal(120, ollama.LastTokenUsage!.PromptTokens);
        Assert.Equal(50, ollama.LastTokenUsage.CompletionTokens);
        Assert.True(ollama.LastCompletion!.IsTruncated);
        Assert.Equal("length", ollama.LastCompletion!.Reason);
    }

    [Fact]
    public async Task StreamChatAsync_Throws_WhenTheStreamEndsWithAnErrorLine()
    {
        var ndjson = "{\"message\":{\"content\":\"Par\"},\"done\":false}\n{\"error\":\"an error was encountered while running the model\"}\n";
        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson")
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        using var ollama = new OllamaClient(new LlmProfileConfig { Model = "llama3.1:8b" }, httpClient: client);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in ollama.StreamChatAsync(new[] { ChatMessage.User("Test") })) { }
        });
        Assert.Contains("an error was encountered", ex.Message);
    }

    [Fact]
    public async Task HttpError_CarriesTheServerMessage()
    {
        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"model 'nope' not found\"}", Encoding.UTF8, "application/json")
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        using var ollama = new OllamaClient(new LlmProfileConfig { Model = "nope" }, httpClient: client);

        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => ollama.CompleteStructuredAsync(new[] { ChatMessage.User("Test") }, "{}"));
        Assert.Contains("HTTP 404", ex.Message);
        Assert.Contains("model 'nope' not found", ex.Message);
    }

    [Fact]
    public async Task StructuredResponse_StreamsSchemaAndJoinsDeltasWithUsage()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("object", body.RootElement.GetProperty("format").GetProperty("type").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnbufferedHttpContent("{\"message\":{\"content\":\"{\\\"ok\\\":\"},\"done\":false}\n" +
                    "{\"message\":{\"content\":\"true}\"},\"done\":true,\"prompt_eval_count\":12,\"eval_count\":4,\"done_reason\":\"length\"}\n", "application/x-ndjson")
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        using var client = new OllamaClient(new LlmProfileConfig { Model = "fixture" }, httpClient: http);
        Assert.Equal("{\"ok\":true}", await client.CompleteStructuredAsync(new[] { ChatMessage.User("Test") }, "{\"type\":\"object\"}"));
        Assert.Equal(12, client.LastTokenUsage!.PromptTokens);
        Assert.Equal(4, client.LastTokenUsage.CompletionTokens);
        Assert.True(client.LastCompletion!.IsTruncated);
    }

    [Theory]
    [InlineData("{\"message\":{\"content\":\"{}\"},\"done\":false}\n", "completamento")]
    [InlineData("{\"error\":\"model failed\"}\n", "model failed")]
    [InlineData("{\"message\":{\"content\":\"\"},\"done\":true}\n", "strutturata")]
    public async Task StructuredResponse_RejectsIncompleteEmptyOrFailedStreams(string body, string error)
    {
        using var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnbufferedHttpContent(body, "application/x-ndjson")
        }))
        { BaseAddress = new Uri("http://localhost:11434/") };
        using var client = new OllamaClient(new LlmProfileConfig { Model = "fixture" }, httpClient: http);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteStructuredAsync(new[] { ChatMessage.User("Test") }, "{}"));
        Assert.Contains(error, ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StructuredResponse_CancelAndIdleTimeoutDisposePendingReads(bool cancel)
    {
        var stream = new PendingReadStream("{\"message\":{\"content\":\"{\"},\"done\":false}\n");
        using var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnbufferedHttpContent(stream, "application/x-ndjson")
        }))
        { BaseAddress = new Uri("http://localhost:11434/") };
        using var client = new OllamaClient(new LlmProfileConfig { Model = "fixture", IdleTimeoutSeconds = 1 }, httpClient: http);
        using var cts = new CancellationTokenSource();
        var pending = client.CompleteStructuredAsync(new[] { ChatMessage.User("Test") }, "{}", cts.Token);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.True(stream.WasDisposed);
    }
}
