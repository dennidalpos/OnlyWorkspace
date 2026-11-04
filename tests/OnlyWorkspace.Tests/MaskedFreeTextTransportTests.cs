using System.Net;
using System.Net.Sockets;
using System.Text;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

/// <summary>Free text through the real dispatcher and a real HTTP client: masked at Standard, refused at Minimo.</summary>
public sealed class MaskedFreeTextTransportTests
{
    private const string Schema = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"],\"additionalProperties\":false}";
    private const string Text = "Da Anna Bianchi <anna@esempio.it>: conferma il bonifico su IT60X0542811101000000123456";

    private static LlmProfileConfig Profile(TcpListener listener, ExposureLevel level) => new()
    {
        Type = "openai", Model = "synthetic", External = true, Exposure = level,
        BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/v1"
    };

    [Fact]
    public async Task Standard_SendsOnlyMaskedText_OverRealHttp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var profile = Profile(listener, ExposureLevel.Standard);
            var capture = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(); using var stream = socket.GetStream();
                var header = new StringBuilder(); var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) { Assert.Equal(1, await stream.ReadAsync(one, 0, 1)); header.Append((char)one[0]); }
                int length = int.Parse(header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Substring(15).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var bytes = new byte[length];
                for (int offset = 0; offset < length;) { int n = await stream.ReadAsync(bytes, offset, length - offset); if (n <= 0) break; offset += n; }
                byte[] body = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"{\\\"name\\\":\\\"ok\\\"}\"},\"finish_reason\":\"stop\"}]}");
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, 0, head.Length); await stream.WriteAsync(body, 0, body.Length);
                return Encoding.UTF8.GetString(bytes);
            });
            using var dispatch = new LlmRequestDispatcher(new OpenAIClient(profile), profile, audit: TestPrivacyAudit.Store);
            await dispatch.CompleteStructuredAsync(new[] { ChatMessage.User(Text) }, Schema);
            string wire = await capture;
            Assert.DoesNotContain("anna@esempio.it", wire);
            Assert.DoesNotContain("Anna Bianchi", wire);
            Assert.DoesNotContain("IT60X0542811101000000123456", wire);
            Assert.Contains("conferma il bonifico", wire);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Minimal_RefusesFreeText_BeforeAnyConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var profile = Profile(listener, ExposureLevel.Minimal);
            using var dispatch = new LlmRequestDispatcher(new OpenAIClient(profile), profile, audit: TestPrivacyAudit.Store);
            await Assert.ThrowsAsync<DataEgressBlockedException>(() => dispatch.CompleteStructuredAsync(new[] { ChatMessage.User(Text) }, Schema));
            Assert.False(listener.Pending());
            // A client used directly, without the dispatcher, refuses unmasked text at every level.
            profile.Exposure = ExposureLevel.Standard;
            using var bare = new OpenAIClient(profile);
            await Assert.ThrowsAsync<DataEgressBlockedException>(() => bare.CompleteStructuredAsync(new[] { ChatMessage.User(Text) }, Schema));
            Assert.False(listener.Pending());
        }
        finally { listener.Stop(); }
    }
}
