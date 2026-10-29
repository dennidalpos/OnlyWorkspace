using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;
using Xunit;

namespace OnlyWorkspace.Tests;

public class GeminiClientTests
{
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
        var json = "{\"models\":[{\"name\":\"models/gemini-1.5-flash\",\"displayName\":\"Gemini 1.5 Flash\"},{\"name\":\"models/gemini-1.5-pro\",\"displayName\":\"Gemini 1.5 Pro\"}]}";
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.True(req.Headers.Contains("x-goog-api-key"));
            Assert.Equal("gemini-test-key", System.Linq.Enumerable.First(req.Headers.GetValues("x-goog-api-key")));
            Assert.EndsWith("/models", req.RequestUri?.AbsolutePath ?? string.Empty);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/") };
        var profile = new LlmProfileConfig
        {
            Type = "gemini",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-1.5-flash"
        };

        using var gemini = new GeminiClient(profile, secretToken: "gemini-test-key", httpClient: client);
        var health = await gemini.CheckHealthAsync();

        Assert.True(health.IsHealthy);
        Assert.Equal(2, health.AvailableModels.Count);
        Assert.Contains("gemini-1.5-flash", health.AvailableModels);
    }

    [Fact]
    public async Task StreamChatAsync_YieldsChunks_FromSseStream()
    {
        var sseData = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Risposta \"}]}}]}\n\n" +
                      "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"da Gemini!\"}]},\"finishReason\":\"STOP\"}]}\n\n";

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Contains("streamGenerateContent", req.RequestUri?.AbsolutePath ?? string.Empty);
            Assert.Contains("alt=sse", req.RequestUri?.Query ?? string.Empty);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseData, Encoding.UTF8, "text/event-stream")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/") };
        var profile = new LlmProfileConfig
        {
            Type = "gemini",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-1.5-flash"
        };

        using var gemini = new GeminiClient(profile, secretToken: "gemini-test-key", httpClient: client);
        var messages = new[]
        {
            ChatMessage.System("Sei un assistente."),
            ChatMessage.User("Ciao")
        };

        var chunks = new List<string>();
        await foreach (var chunk in gemini.StreamChatAsync(messages))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Risposta ", chunks[0]);
        Assert.Equal("da Gemini!", chunks[1]);
    }

    [Fact]
    public async Task CompleteStructuredAsync_ReturnsCleanJson()
    {
        var jsonResponse = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"```json\\n{\\\"sentiment\\\":\\\"positive\\\"}\\n```\"}]}}]}";

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Contains("streamGenerateContent", req.RequestUri?.AbsolutePath ?? string.Empty);
            Assert.Contains("alt=sse", req.RequestUri?.Query ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/") };
        var profile = new LlmProfileConfig
        {
            Type = "gemini",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-1.5-flash"
        };

        using var gemini = new GeminiClient(profile, secretToken: "gemini-test-key", httpClient: client);
        var messages = new[] { ChatMessage.User("Analizza") };

        var result = await gemini.CompleteStructuredAsync(messages, "{}");
        Assert.Equal("{\"sentiment\":\"positive\"}", result);
    }

    private static GeminiClient Create(string model, Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new GeminiClient(
            new LlmProfileConfig { Type = "gemini", BaseUrl = "https://generativelanguage.googleapis.com/v1beta", Model = model, Temperature = 0.3 },
            "gemini-test-key",
            new HttpClient(new MockHttpMessageHandler(responder)) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/") });

    private static HttpResponseMessage Json(string json) =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CompleteStructuredAsync_JoinsTextParts_AndCountsThinkingTokens()
    {
        string? body = null;
        using var gemini = Create("gemini-3-test", req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return Json("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ragionamento\",\"thought\":true},{\"text\":\"{\\\"a\\\":\"},{\"text\":\"1}\",\"thoughtSignature\":\"x\"}]},\"finishReason\":\"STOP\"}]," +
                        "\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":4,\"thoughtsTokenCount\":20}}");
        });

        var result = await gemini.CompleteStructuredAsync(new[] { ChatMessage.User("Analizza") }, "{}");

        Assert.Equal("{\"a\":1}", result);
        Assert.Equal(24, gemini.LastTokenUsage!.CompletionTokens);
        // Gemini 3+: temperature left at the provider default.
        Assert.DoesNotContain("\"temperature\"", body);
        Assert.Contains("\"responseFormat\":{\"text\":{\"mimeType\":\"APPLICATION_JSON\",\"schema\":{}}}", body);
        Assert.DoesNotContain("responseSchema", body);
    }

    [Fact]
    public async Task CompleteStructuredAsync_MakesEveryPropertyRequired()
    {
        string? body = null;
        using var gemini = Create("gemini-3-test", req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return Json("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{}\"}]}}]}");
        });

        await gemini.CompleteStructuredAsync(new[] { ChatMessage.User("Analizza") },
            "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"due\":{\"type\":\"string\"}},\"required\":[\"title\"]}");

        // Optional properties made gemini-3.8-flash stop after the first array item (2026-10-11).
        Assert.Contains("\"due\":{\"type\":[\"string\",\"null\"]}", body);
        Assert.Contains("\"required\":[\"title\",\"due\"]", body);
    }

    [Fact]
    public async Task CompleteStructuredAsync_SendsTemperature_ToGemini2()
    {
        string? body = null;
        using var gemini = Create("gemini-2.5-flash", req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return Json("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{}\"}]}}]}");
        });

        await gemini.CompleteStructuredAsync(new[] { ChatMessage.User("Analizza") }, "{}");

        // The value, not its text: System.Text.Json on net48 writes 0.3 as 0.29999999999999999.
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(0.3, json.RootElement.GetProperty("generationConfig").GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task StreamChatAsync_Throws_WhenPromptIsBlocked()
    {
        using var gemini = Create("gemini-blocked-test", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"promptFeedback\":{\"blockReason\":\"PROHIBITED_CONTENT\"},\"usageMetadata\":{\"promptTokenCount\":5}}\n\n", Encoding.UTF8, "text/event-stream")
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in gemini.StreamChatAsync(new[] { ChatMessage.User("Ciao") })) { }
        });
        Assert.Contains("PROHIBITED_CONTENT", ex.Message);
    }

    [Fact]
    public async Task ListModelsAsync_KeepsOnlyGenerateContentModels()
    {
        string? query = null;
        using var gemini = Create("gemini-list-test", req =>
        {
            query = req.RequestUri!.Query;
            return Json("{\"models\":[{\"name\":\"models/gemini-3-flash\",\"supportedGenerationMethods\":[\"generateContent\",\"countTokens\"]}," +
                        "{\"name\":\"models/gemini-embedding-001\",\"supportedGenerationMethods\":[\"embedContent\"]}]}");
        });

        var models = await gemini.ListModelsAsync();

        Assert.Equal(new[] { "gemini-3-flash" }, models);
        Assert.Equal("?pageSize=1000", query);
    }

    private static string Event(object payload) => "data: " + System.Text.Json.JsonSerializer.Serialize(payload) + "\n\n";

    [Fact]
    public async Task StructuredResponse_StreamsStrictSchemaTextAndThinkingUsage()
    {
        using var client = Create("gemini-3-stream", req =>
        {
            Assert.EndsWith(":streamGenerateContent", req.RequestUri!.AbsolutePath);
            Assert.Equal("?alt=sse", req.RequestUri.Query);
            using var body = System.Text.Json.JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            var format = body.RootElement.GetProperty("generationConfig").GetProperty("responseFormat").GetProperty("text");
            Assert.Equal("APPLICATION_JSON", format.GetProperty("mimeType").GetString());
            Assert.Equal("ok", format.GetProperty("schema").GetProperty("required")[0].GetString());
            var sse = Event(new { candidates = new[] { new { content = new { parts = new[] { new { thought = true, text = "hidden" } } } } } }) +
                Event(new { candidates = new[] { new { content = new { parts = new[] { new { text = "{\"ok\":" } } } } } }) +
                Event(new { candidates = new[] { new { content = new { parts = new[] { new { text = "true}" } } }, finishReason = "STOP" } }, usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 5, thoughtsTokenCount = 20 } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnbufferedHttpContent(sse) };
        });
        Assert.Equal("{\"ok\":true}", await client.CompleteStructuredAsync(new[] { ChatMessage.User("Test") }, "{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}}}"));
        Assert.Equal(10, client.LastTokenUsage!.PromptTokens);
        Assert.Equal(25, client.LastTokenUsage.CompletionTokens);
        Assert.False(client.LastCompletion!.IsTruncated);
    }

    [Theory]
    [InlineData("SAFETY", "SAFETY")]
    [InlineData(null, "completamento")]
    public async Task StructuredResponse_RejectsBlockedOrIncompleteText(string? reason, string error)
    {
        var sse = Event(new { candidates = new[] { new { content = new { parts = new[] { new { text = "{}" } } }, finishReason = reason } } });
        using var client = Create("gemini-3-invalid-stream", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnbufferedHttpContent(sse) });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteStructuredAsync(new[] { ChatMessage.User("Test") }, "{}"));
        Assert.Contains(error, ex.Message);
    }

    [Fact]
    public async Task ChatStream_WithoutFinishReason_IsReported()
    {
        var sse = Event(new { candidates = new[] { new { content = new { parts = new[] { new { text = "Risposta a metÃ " } } } } } });
        using var client = Create("gemini-3-chat-cut", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnbufferedHttpContent(sse) });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.StreamChatAsync(new[] { ChatMessage.User("Test") })) { }
        });
        Assert.Contains("completamento", ex.Message);
    }

    [Fact]
    public async Task StructuredResponse_PropagatesStreamErrorAfterPartialText()
    {
        var sse = Event(new { candidates = new[] { new { content = new { parts = new[] { new { text = "{" } } } } } }) +
            Event(new { error = new { code = 429, message = "quota exhausted" } });
        using var client = Create("gemini-3-stream-error", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnbufferedHttpContent(sse) });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteStructuredAsync(new[] { ChatMessage.User("Test") }, "{}"));
        Assert.Contains("quota exhausted", ex.Message);
    }

    [Fact]
    public async Task StructuredResponse_ReportsHttpQuotaWithoutRetryOrWaiting()
    {
        var calls = 0;
        using var http = new HttpClient(new TransientRetryHandler(new MockHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{\"error\":{\"message\":\"quota exhausted\",\"status\":\"RESOURCE_EXHAUSTED\"}}")
            };
        }))) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/") };
        using var client = new GeminiClient(new LlmProfileConfig { Model = "fixture", BaseUrl = "https://generativelanguage.googleapis.com/v1beta" }, httpClient: http);
        var ex = await Assert.ThrowsAsync<LlmRequestException>(() => client.CompleteStructuredAsync(new[] { ChatMessage.User("Test") }, "{}"));
        Assert.Equal(429, ex.Status);
        Assert.Contains("quota exhausted", ex.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task StructuredResponse_PreservesTruncation()
    {
        var sse = Event(new { candidates = new[] { new { content = new { parts = new[] { new { text = "{}" } } }, finishReason = "MAX_TOKENS" } }, usageMetadata = new { candidatesTokenCount = 20 } });
        using var client = Create("gemini-3-truncated-stream", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnbufferedHttpContent(sse) });
        Assert.Equal("{}", await client.CompleteStructuredAsync(new[] { ChatMessage.User("Test") }, "{}"));
        Assert.True(client.LastCompletion!.IsTruncated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StructuredResponse_CancelAndIdleTimeoutDisposePendingReads(bool cancel)
    {
        var stream = new PendingReadStream(Event(new { candidates = new[] { new { content = new { parts = new[] { new { text = "{" } } } } } }));
        using var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnbufferedHttpContent(stream)
        })) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/") };
        using var client = new GeminiClient(new LlmProfileConfig { Model = "fixture", BaseUrl = "https://generativelanguage.googleapis.com/v1beta", IdleTimeoutSeconds = 1 }, httpClient: http);
        using var cts = new CancellationTokenSource();
        var pending = client.CompleteStructuredAsync(new[] { ChatMessage.User("Test") }, "{}", cts.Token);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public void Factory_CreatesGeminiClient()
    {
        var profile = new LlmProfileConfig
        {
            Id = "gemini-cloud",
            Type = "gemini",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-1.5-flash"
        };

        var client = LlmClientFactory.CreateClient(profile, "gemini-key");
        Assert.NotNull(client);
        Assert.IsType<GeminiClient>(client);
    }
}
