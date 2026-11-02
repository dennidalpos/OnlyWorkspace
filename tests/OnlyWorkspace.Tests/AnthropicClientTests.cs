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

public class AnthropicClientTests
{
    [Theory]
    [InlineData("https://fixture.invalid", "/v1/models")]
    [InlineData("https://fixture.invalid/v1", "/v1/models")]
    [InlineData("https://fixture.invalid/V1///", "/V1/models")]
    [InlineData("https://fixture.invalid/tenant/", "/tenant/v1/models")]
    [InlineData("https://fixture.invalid/gateway/v1", "/gateway/v1/models")]
    [InlineData("https://fixture.invalid/v1beta", "/v1beta/v1/models")]
    public async Task ListModels_PreservesConfiguredApiPrefix(string baseUrl, string expectedPath)
    {
        string? requestedPath = null;
        using var http = new HttpClient(new MockHttpMessageHandler(request =>
        {
            requestedPath = request.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"fixture\"}]}") };
        })) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        using var client = new AnthropicClient(new LlmProfileConfig { Type = "anthropic", BaseUrl = baseUrl, Model = "fixture" }, httpClient: http);

        Assert.Equal(new[] { "fixture" }, await client.ListModelsAsync());
        Assert.Equal(expectedPath, requestedPath);
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
        var json = "{\"data\":[{\"id\":\"claude-3-5-sonnet-20241022\"},{\"id\":\"claude-3-haiku-20240307\"}]}";
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.True(req.Headers.Contains("x-api-key"));
            Assert.Equal("ant-test-key", System.Linq.Enumerable.First(req.Headers.GetValues("x-api-key")));
            Assert.True(req.Headers.Contains("anthropic-version"));
            Assert.Equal("2023-06-01", System.Linq.Enumerable.First(req.Headers.GetValues("anthropic-version")));
            Assert.EndsWith("/models", req.RequestUri?.AbsolutePath ?? string.Empty);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var profile = new LlmProfileConfig
        {
            Type = "anthropic",
            BaseUrl = "https://api.anthropic.com/v1",
            Model = "claude-3-5-sonnet-20241022"
        };

        using var anthropic = new AnthropicClient(profile, secretToken: "ant-test-key", httpClient: client);
        var health = await anthropic.CheckHealthAsync();

        Assert.True(health.IsHealthy);
        Assert.Equal(2, health.AvailableModels.Count);
        Assert.Contains("claude-3-5-sonnet-20241022", health.AvailableModels);
    }

    [Fact]
    public async Task StreamChatAsync_YieldsChunks_FromSseStream()
    {
        var sseData = "event: message_start\n" +
                      "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\"}}\n\n" +
                      "event: content_block_delta\n" +
                      "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Risposta \"}}\n\n" +
                      "event: content_block_delta\n" +
                      "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"da Claude.\"}}\n\n" +
                      "event: message_stop\n" +
                      "data: {\"type\":\"message_stop\"}\n\n";

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.EndsWith("/messages", req.RequestUri?.AbsolutePath ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseData, Encoding.UTF8, "text/event-stream")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var profile = new LlmProfileConfig
        {
            Type = "anthropic",
            BaseUrl = "https://api.anthropic.com/v1",
            Model = "claude-3-5-sonnet-20241022"
        };

        using var anthropic = new AnthropicClient(profile, secretToken: "ant-test-key", httpClient: client);
        var messages = new[]
        {
            ChatMessage.System("Sei un assistente."),
            ChatMessage.User("Ciao")
        };

        var chunks = new List<string>();
        await foreach (var chunk in anthropic.StreamProtocolAsync(messages))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Risposta ", chunks[0]);
        Assert.Equal("da Claude.", chunks[1]);
    }

    [Fact]
    public async Task CompleteStructuredAsync_ReturnsCleanJson()
    {
        var jsonResponse = "{\"content\":[{\"type\":\"text\",\"text\":\"```json\\n{\\\"status\\\":\\\"ok\\\"}\\n```\"}]}";

        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var profile = new LlmProfileConfig
        {
            Type = "anthropic",
            BaseUrl = "https://api.anthropic.com/v1",
            Model = "claude-3-5-sonnet-20241022"
        };

        using var anthropic = new AnthropicClient(profile, secretToken: "ant-test-key", httpClient: client);
        var messages = new[] { ChatMessage.User("Verifica") };

        var result = await anthropic.CompleteProtocolAsync(messages, "{}");
        Assert.Equal("{\"status\":\"ok\"}", result);
    }

    private static AnthropicClient Create(string model, Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new AnthropicClient(
            new LlmProfileConfig { Type = "anthropic", BaseUrl = "https://api.anthropic.com/v1", Model = model },
            "ant-test-key",
            new HttpClient(new MockHttpMessageHandler(responder)) { BaseAddress = new Uri("https://api.anthropic.com/v1/") });

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CompleteStructuredAsync_SendsStrictSchema_AndSkipsThinkingBlock()
    {
        string? body = null;
        using var anthropic = Create("claude-structured-test", req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return Json("{\"content\":[{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"x\"},{\"type\":\"text\",\"text\":\"{\\\"items\\\":[]}\"}]," +
                        "\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}");
        });

        var result = await anthropic.CompleteProtocolAsync(new[] { ChatMessage.User("Estrai") }, OnlyWorkspace.Core.Prompts.PromptCatalog.ActionItemsJsonSchema);

        Assert.Equal("{\"items\":[]}", result);
        using var sent = System.Text.Json.JsonDocument.Parse(body!);
        var schema = sent.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("additionalProperties").GetBoolean());
        // Optional properties stay optional (unlike OpenAI strict mode).
        Assert.Equal(2, schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("required").GetArrayLength());
    }

    [Fact]
    public async Task CompleteStructuredAsync_DropsRejectedTemperature_AndRetries()
    {
        var bodies = new List<string>();
        using var anthropic = Create("claude-no-temperature-test", req =>
        {
            bodies.Add(req.Content!.ReadAsStringAsync().Result);
            return bodies.Count == 1
                ? Json("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"`temperature` is deprecated for this model.\"}}", HttpStatusCode.BadRequest)
                : Json("{\"content\":[{\"type\":\"text\",\"text\":\"{}\"}],\"stop_reason\":\"end_turn\"}");
        });

        await anthropic.CompleteProtocolAsync(new[] { ChatMessage.User("Verifica") }, "{}");

        Assert.Equal(2, bodies.Count);
        Assert.Contains("\"temperature\"", bodies[0]);
        Assert.DoesNotContain("\"temperature\"", bodies[1]);
    }

    [Fact]
    public async Task CompleteStructuredAsync_Throws_OnRefusal()
    {
        using var anthropic = Create("claude-refusal-test", _ => Json("{\"content\":[],\"stop_reason\":\"refusal\",\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => anthropic.CompleteProtocolAsync(new[] { ChatMessage.User("Verifica") }, "{}"));
    }

    [Fact]
    public async Task ListModelsAsync_AsksForAllModels()
    {
        string? query = null;
        using var anthropic = Create("claude-list-test", req =>
        {
            query = req.RequestUri!.Query;
            return Json("{\"data\":[{\"id\":\"claude-a\"}],\"has_more\":false}");
        });

        Assert.Single(await anthropic.ListModelsAsync());
        Assert.Equal("?limit=1000", query);
    }

    [Fact]
    public async Task MaxTokens_IsNotTheProfiles_AndFollowsTheCapTheApiStates()
    {
        var sent = new List<int>();
        // Unique model name: the learned cap is cached per model for the process.
        using var anthropic = Create("claude-old-" + Guid.NewGuid().ToString("N"), req =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            int maxTokens = body.RootElement.GetProperty("max_tokens").GetInt32();
            sent.Add(maxTokens);
            return maxTokens > 8192
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"max_tokens: " + maxTokens +
                                                " > 8192, which is the maximum allowed number of output tokens for this model\"}}", Encoding.UTF8, "application/json")
                }
                : Json("{\"content\":[{\"type\":\"text\",\"text\":\"{}\"}],\"stop_reason\":\"end_turn\"}");
        });

        await anthropic.CompleteProtocolAsync(new[] { ChatMessage.User("Ciao") }, "{}");
        await anthropic.CompleteProtocolAsync(new[] { ChatMessage.User("Ciao") }, "{}");

        // Streaming request, then the provider's stated cap (remembered).
        Assert.Equal(new[] { 64000, 8192, 8192 }, sent);
    }

    [Fact]
    public async Task GetModelInfoAsync_NullLimitsAreZero()
    {
        using var anthropic = Create("claude-null-limits-test", _ => Json("{\"id\":\"claude-x\",\"max_input_tokens\":null,\"max_tokens\":null}"));

        var info = await anthropic.GetModelInfoAsync("claude-x");
        Assert.Equal(0, info!.ContextWindow);
        Assert.Equal(0, info.MaxOutputTokens);
    }

    private static string Event(object payload) => "data: " + System.Text.Json.JsonSerializer.Serialize(payload) + "\n\n";

    [Fact]
    public async Task StructuredResponse_StreamsSchemaTextAndThinkingUsage()
    {
        using var client = Create("claude-stream-" + Guid.NewGuid().ToString("N"), req =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("json_schema", body.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
            Assert.Equal(64000, body.RootElement.GetProperty("max_tokens").GetInt32());
            var sse = "event: message_start\n" + Event(new { type = "message_start", message = new { usage = new { input_tokens = 15 } } }) +
                Event(new { type = "content_block_delta", delta = new { type = "thinking_delta", thinking = "hidden" } }) +
                Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = "{\"ok\":" } }) +
                Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = "true}" } }) +
                Event(new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 23 } }) +
                Event(new { type = "message_stop" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnbufferedHttpContent(sse) };
        });
        Assert.Equal("{\"ok\":true}", await client.CompleteProtocolAsync(new[] { ChatMessage.User("Test") }, "{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}}}"));
        Assert.Equal(15, client.LastTokenUsage!.PromptTokens);
        Assert.Equal(23, client.LastTokenUsage.CompletionTokens);
        Assert.False(client.LastCompletion!.IsTruncated);
    }

    [Theory]
    [InlineData("refusal", true, "rifiutato")]
    [InlineData("end_turn", false, "completamento")]
    public async Task StructuredResponse_RejectsRefusalAndMissingCompletion(string reason, bool completed, string error)
    {
        var sse = Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = "{}" } }) +
            Event(new { type = "message_delta", delta = new { stop_reason = reason } });
        if (completed) sse += Event(new { type = "message_stop" });
        using var client = Create("claude-stream-invalid-" + Guid.NewGuid().ToString("N"), _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnbufferedHttpContent(sse)
        });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteProtocolAsync(new[] { ChatMessage.User("Test") }, "{}"));
        Assert.Contains(error, ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatStream_WithoutCompletion_IsReported(bool sse)
    {
        var body = sse
            ? Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = "Risposta a metÃ " } })
            : "{\"content\":[{\"type\":\"text\",\"text\":\"ignorata\"}]}";
        using var client = Create("claude-chat-cut-" + Guid.NewGuid().ToString("N"), _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnbufferedHttpContent(body)
        });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.StreamProtocolAsync(new[] { ChatMessage.User("Test") })) { }
        });
        Assert.Contains(sse ? "completamento" : "flusso di risposta", ex.Message);
    }

    [Fact]
    public async Task StructuredResponse_PropagatesStreamErrorAfterPartialText()
    {
        var sse = Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = "{" } }) +
            Event(new { type = "error", error = new { type = "overloaded_error", message = "provider overloaded" } });
        using var client = Create("claude-stream-error-" + Guid.NewGuid().ToString("N"), _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnbufferedHttpContent(sse)
        });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteProtocolAsync(new[] { ChatMessage.User("Test") }, "{}"));
        Assert.Contains("provider overloaded", ex.Message);
    }

    [Fact]
    public async Task StructuredResponse_PreservesTruncationAndSchemaFallback()
    {
        var calls = 0;
        using var client = Create("claude-schema-fallback-" + Guid.NewGuid().ToString("N"), req =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            if (++calls == 1)
            {
                Assert.True(body.RootElement.TryGetProperty("output_config", out _));
                return Json("{\"error\":{\"message\":\"output_config not supported\"}}", HttpStatusCode.BadRequest);
            }
            Assert.False(body.RootElement.TryGetProperty("output_config", out _));
            Assert.Contains("schema", body.RootElement.GetProperty("system").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnbufferedHttpContent(Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = "{}" } }) +
                    Event(new { type = "message_delta", delta = new { stop_reason = "max_tokens" }, usage = new { output_tokens = 8192 } }) +
                    Event(new { type = "message_stop" }))
            };
        });
        Assert.Equal("{}", await client.CompleteProtocolAsync(new[] { ChatMessage.User("Test") }, "{\"type\":\"object\",\"properties\":{}}"));
        Assert.Equal(2, calls);
        Assert.True(client.LastCompletion!.IsTruncated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StructuredResponse_CancelAndIdleTimeoutDisposePendingReads(bool cancel)
    {
        var stream = new PendingReadStream(Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = "{" } }));
        using var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnbufferedHttpContent(stream)
        })) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        using var client = new AnthropicClient(new LlmProfileConfig { Model = "fixture", BaseUrl = "https://api.anthropic.com/v1", IdleTimeoutSeconds = 1 }, httpClient: http);
        using var cts = new CancellationTokenSource();
        var pending = client.CompleteProtocolAsync(new[] { ChatMessage.User("Test") }, "{}", cts.Token);
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
    public void Factory_CreatesAnthropicClient()
    {
        var profile = new LlmProfileConfig
        {
            Id = "anthropic-cloud",
            Type = "anthropic",
            BaseUrl = "https://api.anthropic.com/v1",
            Model = "claude-3-5-sonnet-20241022"
        };

        var client = LlmClientFactory.CreateClient(profile, "ant-key");
        Assert.NotNull(client);
        Assert.IsType<AnthropicClient>(client);
    }
}
