using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;
using Xunit;

namespace OnlyWorkspace.Tests;

public class OpenAIClientTests
{
    [Fact]
    public async Task QwenStructuredRequestPreservesBoundsWithoutAnOutputTokenCap()
    {
        string? sent = null;
        using var http = new HttpClient(new MockHttpMessageHandler(req =>
        {
            sent = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"summary\\\":\\\"Review requested.\\\"}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":8}}") };
        })) { BaseAddress = new Uri("https://fixture.example.test/v1/") };
        using var client = new OpenAIClient(new LlmProfileConfig { Type = "qwen", Model = "qwen3.8-flash", BaseUrl = "https://fixture.example.test/v1", DisableThinking = true }, secretToken: "synthetic", httpClient: http);
        await client.CompleteStructuredAsync(new[] { ChatMessage.User("Synthetic JSON request") }, "{\"type\":\"object\",\"properties\":{\"summary\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":73}},\"required\":[\"summary\"],\"additionalProperties\":false}");
        using var doc = System.Text.Json.JsonDocument.Parse(sent!);
        var format = doc.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("json_schema").GetProperty("schema");
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal(1, schema.GetProperty("properties").GetProperty("summary").GetProperty("minLength").GetInt32());
        Assert.Equal(73, schema.GetProperty("properties").GetProperty("summary").GetProperty("maxLength").GetInt32());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(doc.RootElement.TryGetProperty("max_completion_tokens", out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletionReason_WithoutUsage_IsIndependent_AndResets(bool plain, bool truncated)
    {
        int requests = 0;
        using var http = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            string reason = requests++ == 0 && truncated ? "length" : "stop";
            object choice = plain
                ? new { message = new { content = "{}" }, finish_reason = reason }
                : (object)new { delta = new { content = "{}" }, finish_reason = reason };
            string json = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { choice } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(plain ? json : "data: " + json + "\n\ndata: [DONE]\n\n") };
        })) { BaseAddress = new Uri("http://localhost:1234/v1/") };
        using var client = new OpenAIClient(new LlmProfileConfig { Type = "lmstudio", Model = "synthetic" }, httpClient: http);
        using var dispatched = new LlmRequestDispatcher(client, new LlmProfileConfig { Type = "lmstudio", Model = "synthetic", ContextBudgetTokens = 4096 }, audit: TestPrivacyAudit.Store);
        var messages = new[] { ChatMessage.User("synthetic") };
        Assert.Equal("{}", await dispatched.CompleteStructuredAsync(messages, "{}"));
        Assert.Null(dispatched.LastTokenUsage);
        Assert.Equal(truncated, dispatched.LastCompletion!.IsTruncated);
        Assert.Equal(truncated ? "length" : "stop", dispatched.LastCompletion.Reason);
        if (truncated) Assert.Throws<OnlyWorkspace.Core.Agents.InvalidModelOutputException>(() => OnlyWorkspace.Core.Agents.ModelOutput.EnsureComplete(dispatched.LastCompletion));
        else OnlyWorkspace.Core.Agents.ModelOutput.EnsureComplete(dispatched.LastCompletion);
        Assert.Equal("{}", await dispatched.CompleteStructuredAsync(messages, "{}"));
        Assert.False(dispatched.LastCompletion!.IsTruncated);
        Assert.Null(dispatched.LastTokenUsage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LengthStream_WithAbsentOrLateUsage_RemainsTruncated(bool lateUsage)
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}\n\n" +
            (lateUsage ? "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7}}\n\n" : "") + "data: [DONE]\n\n";
        using var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse) }))
            { BaseAddress = new Uri("http://localhost:1234/v1/") };
        using var client = new OpenAIClient(new LlmProfileConfig { Type = "lmstudio", Model = "synthetic" }, httpClient: http);
        await foreach (var chunk in client.StreamChatAsync(new[] { ChatMessage.User("x") })) Assert.Equal("partial", chunk);
        Assert.True(client.LastCompletion!.IsTruncated);
        Assert.Equal("length", client.LastCompletion.Reason);
        if (lateUsage) Assert.Equal(7, client.LastTokenUsage!.CompletionTokens);
        else Assert.Null(client.LastTokenUsage);
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
        var json = "{\"data\":[{\"id\":\"gpt-4o\"},{\"id\":\"gpt-4o-mini\"}]}";
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("sk-test-123", req.Headers.Authorization?.Parameter);
            Assert.EndsWith("/models", req.RequestUri?.AbsolutePath ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var profile = new LlmProfileConfig
        {
            Type = "openai",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o"
        };

        using var openAi = new OpenAIClient(profile, secretToken: "sk-test-123", httpClient: client);
        var health = await openAi.CheckHealthAsync();

        Assert.True(health.IsHealthy);
        Assert.Equal(2, health.AvailableModels.Count);
        Assert.Contains("gpt-4o", health.AvailableModels);
    }

    [Fact]
    public async Task StreamChatAsync_YieldsChunks_FromSseStream()
    {
        var sseData = "data: {\"choices\":[{\"delta\":{\"content\":\"Ciao \"}}]}\n\n" +
                      "data: {\"choices\":[{\"delta\":{\"content\":\"dal cloud OpenAI!\"},\"finish_reason\":\"stop\"}]}\n\n" +
                      "data: [DONE]\n\n";

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.EndsWith("/chat/completions", req.RequestUri?.AbsolutePath ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseData, Encoding.UTF8, "text/event-stream")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var profile = new LlmProfileConfig
        {
            Type = "openai",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o"
        };

        using var openAi = new OpenAIClient(profile, secretToken: "sk-test", httpClient: client);
        var messages = new[] { ChatMessage.User("Ciao") };

        var chunks = new List<string>();
        await foreach (var chunk in openAi.StreamChatAsync(messages))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Ciao ", chunks[0]);
        Assert.Equal("dal cloud OpenAI!", chunks[1]);
    }

    [Fact]
    public async Task CompleteStructuredAsync_ReturnsParsedJson()
    {
        var jsonResponse = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"intent\\\":\\\"search\\\"}\"}}]}";

        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var profile = new LlmProfileConfig
        {
            Type = "openai",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o"
        };

        using var openAi = new OpenAIClient(profile, secretToken: "sk-test", httpClient: client);
        var messages = new[] { ChatMessage.User("Trova email") };

        var result = await openAi.CompleteStructuredAsync(messages, "{}");
        Assert.Equal("{\"intent\":\"search\"}", result);
    }

    [Fact]
    public async Task CompleteStructuredAsync_IsStreamed_SoALongAnswerIsNotOneWait()
    {
        var sseData = "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"intent\\\":\"}}],\"usage\":null}\n\n" +
                      "data: {\"choices\":[{\"delta\":{\"content\":\"\\\"search\\\"}\"},\"finish_reason\":\"stop\"}],\"usage\":null}\n\n" +
                      "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":30000,\"completion_tokens\":7}}\n\n" +
                      "data: [DONE]\n\n";
        string? sent = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            sent = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sseData, Encoding.UTF8, "text/event-stream") };
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        using var openAi = new OpenAIClient(new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o" }, secretToken: "sk-test", httpClient: client);

        var result = await openAi.CompleteStructuredAsync(new[] { ChatMessage.User("Trova email") }, "{}");

        Assert.Equal("{\"intent\":\"search\"}", result);
        Assert.Contains("\"stream\":true", sent);
        Assert.Contains("response_format", sent);
        Assert.Equal(30000, openAi.LastTokenUsage!.PromptTokens);
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"Risposta a metà\"}}]}\n\n", "completamento")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"Risposta\"}}]}\n\ndata: {\"error\":{\"message\":\"upstream timeout\"}}\n\n", "upstream timeout")]
    [InlineData("", "flusso di risposta")]
    public async Task StreamChatAsync_CutOrFailedStream_IsReported(string sseData, string error)
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sseData, Encoding.UTF8, "text/event-stream") });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        using var openAi = new OpenAIClient(new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o" }, secretToken: "sk-test", httpClient: client);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in openAi.StreamChatAsync(new[] { ChatMessage.User("x") })) { }
        });
        Assert.Contains(error, ex.Message);
    }

    [Fact]
    public async Task CompleteStructuredAsync_EmptyAnswer_IsReportedNotEmptyObject()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "text/event-stream") });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        using var openAi = new OpenAIClient(new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o" }, secretToken: "sk-test", httpClient: client);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => openAi.CompleteStructuredAsync(new[] { ChatMessage.User("x") }, "{}"));
        Assert.Contains("risposta strutturata", ex.Message);
    }

    [Fact]
    public async Task CompleteStructuredAsync_StreamedRefusal_Throws()
    {
        var sseData = "data: {\"choices\":[{\"delta\":{\"refusal\":\"Non posso \"}}]}\n\n" +
                      "data: {\"choices\":[{\"delta\":{\"refusal\":\"aiutare.\"}}]}\n\ndata: [DONE]\n\n";
        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sseData, Encoding.UTF8, "text/event-stream") });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        using var openAi = new OpenAIClient(new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o" }, secretToken: "sk-test", httpClient: client);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => openAi.CompleteStructuredAsync(new[] { ChatMessage.User("x") }, "{}"));
        Assert.Contains("Non posso aiutare.", ex.Message);
    }

    [Fact]
    public void Factory_CreatesOpenAIClient()
    {
        var profile = new LlmProfileConfig
        {
            Id = "openai-cloud",
            Type = "openai",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o"
        };

        var client = LlmClientFactory.CreateClient(profile, "sk-test");
        Assert.NotNull(client);
        Assert.IsType<OpenAIClient>(client);
    }

    [Fact]
    public async Task StreamChatAsync_CapturesTokenUsage_AndDetectsTruncation()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
                  "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}],\"usage\":{\"prompt_tokens\":80,\"completion_tokens\":40,\"total_tokens\":120}}\n\n" +
                  "data: [DONE]\n\n";

        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var profile = new LlmProfileConfig { Type = "openai", Model = "gpt-4o" };

        using var openAi = new OpenAIClient(profile, secretToken: "sk-test", httpClient: client);
        var messages = new[] { ChatMessage.User("Hello") };

        await foreach (var _ in openAi.StreamChatAsync(messages)) { }

        Assert.NotNull(openAi.LastTokenUsage);
        Assert.Equal(80, openAi.LastTokenUsage!.PromptTokens);
        Assert.Equal(40, openAi.LastTokenUsage.CompletionTokens);
        Assert.True(openAi.LastCompletion!.IsTruncated);
        Assert.Equal("length", openAi.LastCompletion!.Reason);
    }

    [Fact]
    public async Task StreamChatAsync_AcceptsNullUsage_OnEveryChunk()
    {
        // Real api.openai.com shape with stream_options.include_usage.
        var sseData = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}],\"usage\":null}\n\n" +
                      "data: {\"choices\":[{\"delta\":{\"content\":\"Ciao\"},\"finish_reason\":null}],\"usage\":null}\n\n" +
                      "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":null}\n\n" +
                      "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3}}\n\n" +
                      "data: [DONE]\n\n";
        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseData, Encoding.UTF8, "text/event-stream")
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var profile = new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o" };

        using var openAi = new OpenAIClient(profile, secretToken: "sk-test", httpClient: client);
        var text = new StringBuilder();
        await foreach (var chunk in openAi.StreamChatAsync(new[] { ChatMessage.User("Ciao") })) text.Append(chunk);

        Assert.Equal("Ciao", text.ToString());
        Assert.Equal(12, openAi.LastTokenUsage!.PromptTokens);
    }

    [Fact]
    public async Task ReasoningModel_RetriesWithoutTemperature_AndSendsNoOutputLimit()
    {
        var bodies = new List<string>();
        var handler = new MockHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            bodies.Add(body);
            if (body.Contains("\"temperature\""))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"Unsupported value: 'temperature'\",\"param\":\"temperature\"}}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}", Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        // Unique model name: the learned quirks are cached per model for the process.
        var profile = new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com/v1", Model = "test-reasoning-" + Guid.NewGuid().ToString("N") };

        using var openAi = new OpenAIClient(profile, secretToken: "sk-test", httpClient: client);
        await openAi.CompleteStructuredAsync(new[] { ChatMessage.User("Ciao") }, "{}");
        await openAi.CompleteStructuredAsync(new[] { ChatMessage.User("Ciao") }, "{}");

        Assert.Equal(3, bodies.Count); // rejected, retried, then remembered
        Assert.DoesNotContain("\"temperature\"", bodies[1]);
        Assert.DoesNotContain("\"temperature\"", bodies[2]);
        // The provider applies the model's own output limit.
        Assert.DoesNotContain("\"max_completion_tokens\"", bodies[0]);
        Assert.DoesNotContain("\"max_tokens\"", bodies[0]);
    }

    [Fact]
    public async Task StructuredSchema_IsMadeStrict()
    {
        string? sent = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            sent = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}", Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var profile = new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o" };

        using var openAi = new OpenAIClient(profile, secretToken: "sk-test", httpClient: client);
        await openAi.CompleteStructuredAsync(new[] { ChatMessage.User("Ciao") }, OnlyWorkspace.Core.Prompts.PromptCatalog.ActionItemsJsonSchema);

        using var doc = System.Text.Json.JsonDocument.Parse(sent!);
        var schema = doc.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var item = schema.GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(item.GetProperty("properties").EnumerateObject().Count(), item.GetProperty("required").GetArrayLength());
        // Optional before → nullable now; required stays a plain type.
        Assert.Equal(System.Text.Json.JsonValueKind.Array, item.GetProperty("properties").GetProperty("due").GetProperty("type").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.String, item.GetProperty("properties").GetProperty("title").GetProperty("type").ValueKind);
    }

    [Fact]
    public async Task CompatibleServer_GetsTemperatureAndNoOutputLimit()
    {
        string? sent = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            sent = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}", Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://llm.example.test/v1/") };
        var profile = new LlmProfileConfig { Type = "openai", BaseUrl = "https://llm.example.test/v1", Model = "local-model" };

        using var openAi = new OpenAIClient(profile, secretToken: "sk-test", httpClient: client);
        await openAi.CompleteStructuredAsync(new[] { ChatMessage.User("Ciao") }, "{}");

        Assert.DoesNotContain("\"max_tokens\"", sent);
        Assert.Contains("\"temperature\"", sent);
        Assert.DoesNotContain("enable_thinking", sent);
    }

    [Theory]
    [InlineData(true, "\"enable_thinking\":false")]
    [InlineData(false, "\"enable_thinking\":true")]
    public async Task Qwen_GetsEnableThinkingFromTheProfile(bool disableThinking, string expected)
    {
        string? sent = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            sent = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}", Encoding.UTF8, "application/json")
            };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://qwen.example.test/v1/") };
        var profile = new LlmProfileConfig { Type = "qwen", BaseUrl = "https://qwen.example.test/v1", Model = "qwen-test", DisableThinking = disableThinking };

        using var qwen = new OpenAIClient(profile, secretToken: "sk-test", httpClient: client);
        await qwen.CompleteStructuredAsync(new[] { ChatMessage.User("Ciao") }, "{}");

        Assert.Contains(expected, sent);
    }

    [Fact]
    public async Task CompleteStructuredAsync_Throws_OnRefusal_AndStripsCodeFence()
    {
        string body = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":null,\"refusal\":\"I can't help with that.\"}}]}";
        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var profile = new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com/v1", Model = "gpt-test-refusal" };
        using var openAi = new OpenAIClient(profile, secretToken: "sk-test", httpClient: client);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => openAi.CompleteStructuredAsync(new[] { ChatMessage.User("x") }, ""));
        Assert.Contains("I can't help with that.", ex.Message);

        body = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"```json\\n{\\\"a\\\":1}\\n```\"}}]}";
        Assert.Equal("{\"a\":1}", await openAi.CompleteStructuredAsync(new[] { ChatMessage.User("x") }, ""));
    }
}
