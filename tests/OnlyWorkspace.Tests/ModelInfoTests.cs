using System;
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

/// <summary>Limits and capabilities each provider declares for a model, read without generating text.</summary>
public class ModelInfoTests
{
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _json;
        public Handler(Func<HttpRequestMessage, string> json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_json(request), Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task Anthropic_ReadsLimitsAndDeclaredCapabilities()
    {
        var http = new HttpClient(new Handler(req =>
        {
            Assert.EndsWith("/v1/models/claude-test", req.RequestUri!.AbsolutePath);
            return "{\"id\":\"claude-test\",\"max_input_tokens\":200000,\"max_tokens\":64000,\"capabilities\":{" +
                   "\"thinking\":{\"supported\":true},\"effort\":{\"supported\":true,\"low\":{\"supported\":true},\"high\":{\"supported\":true},\"max\":{\"supported\":false}}," +
                   "\"server_tools\":{\"supported\":true,\"web_search\":{\"supported\":true}},\"image_input\":{\"supported\":false},\"structured_outputs\":{\"supported\":true}}}";
        }))
        { BaseAddress = new Uri("https://api.anthropic.com/") };
        using var client = new AnthropicClient(new LlmProfileConfig { Type = "anthropic", BaseUrl = "https://api.anthropic.com", Model = "claude-test" }, "key", http);

        var info = await client.GetModelInfoAsync("claude-test");

        Assert.Equal(200000, info!.ContextWindow);
        Assert.Equal(64000, info.MaxOutputTokens);
        var c = info.Capabilities;
        Assert.Equal(CapabilityState.Supported, c.Tools);
        Assert.Equal(CapabilityState.Supported, c.Reasoning);
        Assert.Equal(new[] { "low", "high" }, c.ReasoningLevels);
        Assert.Equal(CapabilityState.Supported, c.WebSearch);
        Assert.Equal(CapabilityState.Unsupported, c.Vision);
        Assert.Equal(CapabilityState.Supported, c.StructuredOutput);
    }

    [Fact]
    public void Anthropic_WithoutCapabilitiesLeavesThemUnknown()
    {
        using var doc = JsonDocument.Parse("{\"id\":\"claude-x\",\"max_input_tokens\":null,\"max_tokens\":null}");
        var info = AnthropicClient.ParseModelInfo(doc.RootElement);
        Assert.Equal(0, info.ContextWindow);
        Assert.False(info.Capabilities.AnyKnown);
    }

    [Fact]
    public async Task Gemini_ReadsTokenLimitsAndThinking()
    {
        var http = new HttpClient(new Handler(req =>
        {
            Assert.EndsWith("/v1beta/models/gemini-test", req.RequestUri!.AbsolutePath);
            return "{\"name\":\"models/gemini-test\",\"inputTokenLimit\":1048576,\"outputTokenLimit\":65536,\"thinking\":true}";
        }))
        { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
        using var client = new GeminiClient(new LlmProfileConfig { Type = "gemini", BaseUrl = "https://generativelanguage.googleapis.com", Model = "gemini-test" }, "key", http);

        var info = await client.GetModelInfoAsync("models/gemini-test");

        Assert.Equal(1048576, info!.ContextWindow);
        Assert.Equal(65536, info.MaxOutputTokens);
        Assert.Equal(CapabilityState.Supported, info.Capabilities.Reasoning);
        Assert.Equal(CapabilityState.Unknown, info.Capabilities.Tools);
    }

    [Fact]
    public async Task Ollama_ReadsContextLengthAndCapabilityList()
    {
        var http = new HttpClient(new Handler(req =>
        {
            Assert.EndsWith("/api/show", req.RequestUri!.AbsolutePath);
            return "{\"model_info\":{\"general.architecture\":\"qwen3\",\"qwen3.context_length\":262144},\"capabilities\":[\"completion\",\"tools\",\"thinking\"]}";
        }))
        { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        using var client = new OllamaClient(new LlmProfileConfig { Model = "qwen3.5:9b" }, null, http);

        var info = await client.GetModelInfoAsync("qwen3.5:9b");

        Assert.Equal(262144, info!.ContextWindow);
        Assert.Equal(CapabilityState.Supported, info.Capabilities.Tools);
        Assert.Equal(CapabilityState.Supported, info.Capabilities.Reasoning);
        Assert.Equal(CapabilityState.Unsupported, info.Capabilities.Vision);
        Assert.Equal(CapabilityState.Unknown, info.Capabilities.WebSearch);
    }

    [Fact]
    public void Ollama_ModelWithoutToolsCannotSearchTheWeb()
    {
        using var doc = JsonDocument.Parse("{\"capabilities\":[\"completion\"]}");
        Assert.Equal(CapabilityState.Unsupported, OllamaClient.ParseModelInfo(doc.RootElement).Capabilities.WebSearch);
    }

    [Fact]
    public async Task OpenAI_DoesNotExposeLimitsOrCapabilities()
    {
        var calls = 0;
        var http = new HttpClient(new Handler(_ => { calls++; return "{}"; })) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        using var client = new OpenAIClient(new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com/v1", Model = "gpt-test" }, "key", http);

        Assert.Null(await client.GetModelInfoAsync("gpt-test"));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("openai-compatible")]
    [InlineData("lmstudio")]
    [InlineData("openrouter")]
    [InlineData("mistral")]
    [InlineData("llamacpp")]
    [InlineData("vllm")]
    public async Task CompatibleConnections_DoNotReadProprietaryCapabilities(string type)
    {
        int calls = 0;
        var http = new HttpClient(new Handler(_ => { calls++; return "{}"; })) { BaseAddress = new Uri("http://127.0.0.1:1234/v1/") };
        using var client = new OpenAIClient(new LlmProfileConfig { Type = type, BaseUrl = "http://127.0.0.1:1234/v1", Model = "synthetic" }, null, http);
        Assert.Null(await client.GetModelInfoAsync("synthetic"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void CapabilitiesCache_AppliesOnlyToTheSameModelAndEndpoint()
    {
        var profile = new LlmProfileConfig { Model = "m1", BaseUrl = "https://api.example.com/v1" };
        var cache = ModelCapabilitiesCache.For(new LlmProfileConfig { Model = "m1", BaseUrl = "https://api.example.com/v1/" });
        Assert.True(cache.Matches(profile));
        profile.Model = "m2";
        Assert.False(cache.Matches(profile));
    }
}
