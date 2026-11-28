using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Validation;
using OnlyWorkspace.Infrastructure.Llm;
using Xunit;

namespace OnlyWorkspace.Tests;

public class ProviderCatalogTests
{
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _reply;
        public Handler(Func<HttpRequestMessage, string> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_reply(request), Encoding.UTF8, "text/event-stream") });
    }

    [Fact]
    public void EveryConnectorIsUniqueAndUsable()
    {
        Assert.Equal(ProviderCatalog.All.Count, ProviderCatalog.All.Select(p => p.Type).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var p in ProviderCatalog.All)
        {
            string endpoint = p.Type == ProviderCatalog.OpenAICompatible ? "http://127.0.0.1:1234/v1" : p.DefaultBaseUrl;
            Assert.True(Uri.TryCreate(endpoint, UriKind.Absolute, out var url), p.Type);
            // Local connectors stay on loopback over HTTP; every remote one uses HTTPS.
            Assert.True(p.IsExternal ? url!.Scheme == Uri.UriSchemeHttps : ConfigurationValidator.IsLoopback(url!), p.Type);
            Assert.StartsWith("https://", p.DocsUrl);
            Assert.NotEmpty(p.AuthTypes);
            if (p.AuthTypes.Contains("apiKey")) Assert.NotEmpty(p.ApiKeyHeader);
            Assert.IsAssignableFrom(p.Family switch
            {
                ApiFamily.Ollama => typeof(OllamaClient),
                ApiFamily.Anthropic => typeof(AnthropicClient),
                ApiFamily.Gemini => typeof(GeminiClient),
                _ => typeof(OpenAIClient)
            }, LlmClientFactory.CreateClient(new LlmProfileConfig { Type = p.Type, BaseUrl = endpoint, External = p.IsExternal }));
        }
    }

    [Fact]
    public void ValidatorAcceptsCatalogTypesOnly()
    {
        Assert.False(ProviderCatalog.IsKnown("openrouter"));
        Assert.True(ProviderCatalog.IsKnown(ProviderCatalog.OpenAICompatible));
        Assert.False(ProviderCatalog.IsKnown("unknown"));
        Assert.Throws<NotSupportedException>(() => LlmClientFactory.CreateClient(new LlmProfileConfig { Type = "unknown", BaseUrl = "https://example.com" }));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("bearer")]
    public async Task Generic_UsesChatCompletionsAndTheConfiguredAuthentication(string authentication)
    {
        HttpRequestMessage? sent = null;
        using var http = new HttpClient(new Handler(req => { sent = req; return "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"; }))
        { BaseAddress = new Uri("http://127.0.0.1:1234/v1/") };
        var profile = new LlmProfileConfig { Type = ProviderCatalog.OpenAICompatible, BaseUrl = "http://127.0.0.1:1234/v1", Model = "synthetic", AuthType = authentication };
        using var client = new OpenAIClient(profile, "synthetic-key", http);
        await foreach (var _ in client.StreamProtocolAsync(new[] { ChatMessage.User("synthetic") })) { }
        Assert.Equal("/v1/chat/completions", sent!.RequestUri!.AbsolutePath);
        if (authentication == "none") Assert.Null(sent.Headers.Authorization);
        else Assert.Equal("synthetic-key", sent.Headers.Authorization!.Parameter);
    }

    [Fact]
    public void Presets_HaveUniformLabelsAndFitTheModel()
    {
        var sixteen = ProviderCatalog.Presets.Single(p => p.ContextWindow == 16384);
        Assert.Equal("16K · risposta 2K · prompt 12,4K", sixteen.Label);
        Assert.All(ProviderCatalog.Presets.Where(p => !p.IsCustom), p => Assert.Matches(@"^\d+K · risposta \d+K · prompt [\d,]+K$", p.Label));
        Assert.DoesNotContain(ProviderCatalog.PresetsFor(32768), p => p.ContextWindow > 32768);
        Assert.Contains(ProviderCatalog.PresetsFor(32768), p => p.IsCustom);
        Assert.Equal(ProviderCatalog.Presets.Count, ProviderCatalog.PresetsFor(0).Count());
    }

    [Fact]
    public void OnlyLocalOllamaUsesClientContext()
    {
        Assert.Equal(new[] { "ollama" }, ProviderCatalog.All.Where(p => p.SupportsNumCtx).Select(p => p.Type));
    }
}
