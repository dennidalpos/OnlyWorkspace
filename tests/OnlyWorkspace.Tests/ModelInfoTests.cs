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
        })) { BaseAddress = new Uri("https://api.anthropic.com/") };
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
        })) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
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
        })) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
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

    [Fact]
    public async Task LmStudio_ReadsRootModelsEndpoint()
    {
        var http = new HttpClient(new Handler(req =>
        {
            Assert.Equal("/api/v1/models", req.RequestUri!.AbsolutePath);
            return "{\"models\":[{\"key\":\"other\"},{\"key\":\"qwen/qwen3-8b\",\"max_context_length\":32768,\"capabilities\":{\"vision\":false,\"trained_for_tool_use\":true,\"reasoning\":{\"allowed_options\":[\"off\",\"low\",\"high\"],\"default\":\"low\"}}}]}";
        })) { BaseAddress = new Uri("http://127.0.0.1:1234/v1/") };
        using var client = new OpenAIClient(new LlmProfileConfig { Type = "lmstudio", BaseUrl = "http://127.0.0.1:1234/v1", Model = "qwen/qwen3-8b" }, null, http);

        var info = await client.GetModelInfoAsync("qwen/qwen3-8b");

        Assert.Equal(32768, info!.ContextWindow);
        Assert.Equal(CapabilityState.Supported, info.Capabilities.Tools);
        Assert.Equal(CapabilityState.Supported, info.Capabilities.Reasoning);
        Assert.Equal(new[] { "low", "high" }, info.Capabilities.ReasoningLevels);
        Assert.Equal(CapabilityState.Unsupported, info.Capabilities.Vision);
    }

    [Fact]
    public void OpenRouter_ReadsSupportedParameters()
    {
        using var doc = JsonDocument.Parse("{\"data\":[{\"id\":\"vendor/model\",\"context_length\":131072,\"top_provider\":{\"max_completion_tokens\":8192}," +
            "\"supported_parameters\":[\"tools\",\"reasoning\",\"response_format\"],\"architecture\":{\"input_modalities\":[\"text\",\"image\"]},\"reasoning\":{\"supported_efforts\":[\"low\",\"high\"]}}]}");
        var info = OpenAIClient.ParseModelInfo(CapabilitySource.OpenRouter, doc.RootElement, "vendor/model")!;
        Assert.Equal(131072, info.ContextWindow);
        Assert.Equal(8192, info.MaxOutputTokens);
        Assert.Equal(CapabilityState.Supported, info.Capabilities.Tools);
        Assert.Equal(CapabilityState.Supported, info.Capabilities.Reasoning);
        Assert.Equal(CapabilityState.Supported, info.Capabilities.StructuredOutput);
        Assert.Equal(CapabilityState.Supported, info.Capabilities.Vision);
        Assert.Equal(new[] { "low", "high" }, info.Capabilities.ReasoningLevels);
        Assert.Null(OpenAIClient.ParseModelInfo(CapabilitySource.OpenRouter, doc.RootElement, "missing"));
    }

    [Fact]
    public void Mistral_AndLlamaCpp_ReadTheirOwnFields()
    {
        using var mistral = JsonDocument.Parse("{\"id\":\"mistral-small\",\"max_context_length\":32768,\"capabilities\":{\"completion_chat\":true,\"function_calling\":true,\"vision\":false}}");
        var m = OpenAIClient.ParseModelInfo(CapabilitySource.Mistral, mistral.RootElement, "mistral-small")!;
        Assert.Equal(32768, m.ContextWindow);
        Assert.Equal(CapabilityState.Supported, m.Capabilities.Tools);
        Assert.Equal(CapabilityState.Unsupported, m.Capabilities.Vision);

        using var props = JsonDocument.Parse("{\"default_generation_settings\":{\"n_ctx\":65536},\"chat_template_caps\":{\"supports_tools\":true},\"modalities\":{\"vision\":true}}");
        var l = OpenAIClient.ParseModelInfo(CapabilitySource.LlamaCpp, props.RootElement, "any")!;
        Assert.Equal(65536, l.ContextWindow);
        Assert.Equal(CapabilityState.Supported, l.Capabilities.Tools);
        Assert.Equal(CapabilityState.Supported, l.Capabilities.Vision);
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
