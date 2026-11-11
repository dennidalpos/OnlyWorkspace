using System.Net;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

/// <summary>Request payloads: temperature by request kind (#198) and model particularities known up front (#199).</summary>
public sealed class ModelTraitsTests
{
    [Theory]
    [InlineData("anthropic", "claude-opus-4-7", true)]
    [InlineData("anthropic", "claude-opus-4-10", true)]
    [InlineData("anthropic", "claude-sonnet-5-5", true)]
    [InlineData("anthropic", "claude-haiku-5-5", true)]
    [InlineData("anthropic", "claude-fable-5-2", true)]
    [InlineData("anthropic", "claude-opus-4-6", false)]
    [InlineData("anthropic", "claude-sonnet-4-5", false)]
    [InlineData("openai", "gpt-5.1", true)]
    [InlineData("openai", "o3-mini", true)]
    [InlineData("openai", "gpt-4o", false)]
    [InlineData("openai-compatible", "gpt-5-local", false)]
    [InlineData("qwen", "qwen3.8-flash", false)]
    [InlineData("ollama", "o1-local", false)]
    public void Catalog_KnowsWhichModelsRejectTemperature(string type, string model, bool rejects) =>
        Assert.Equal(rejects, ModelTraits.For(type, model).NoTemperature);

    [Fact]
    public void Catalog_KnowsTheOutputCapAndFormatOfOlderClaudeModels()
    {
        var haiku = ModelTraits.For("anthropic", "claude-3-haiku-20240307");
        Assert.Equal(4096, haiku.MaxOutputTokens);
        Assert.True(haiku.NoOutputFormat);
        Assert.Equal(8192, ModelTraits.For("anthropic", "claude-3-5-sonnet-20241022").MaxOutputTokens);
        Assert.Equal(0, ModelTraits.For("anthropic", "claude-sonnet-4-5").MaxOutputTokens);
        Assert.Equal(ModelTraits.None.MaxOutputTokens, ModelTraits.For("anthropic", "").MaxOutputTokens);
    }

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1", "gpt-4o")]
    [InlineData("anthropic", "https://api.anthropic.com", "claude-sonnet-4-5")]
    [InlineData("gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash")]
    [InlineData("ollama", "http://127.0.0.1:11434", "qwen3.5:9b")]
    public async Task StructuredRequests_UseTemperatureZero_AndTheOthersTheProfileValue(string type, string baseUrl, string model)
    {
        double structured = await TemperatureSentAsync(type, baseUrl, model, structured: true);
        double free = await TemperatureSentAsync(type, baseUrl, model, structured: false);
        Assert.Equal(0.0, structured);
        Assert.Equal(0.7, free);
    }

    [Fact]
    public async Task AKnownModel_NeverSendsTheRejectedTemperature_NotEvenOnTheFirstRequest()
    {
        int calls = 0;
        string? body = null;
        using var http = new HttpClient(new Capture(req => { calls++; body = req.Content!.ReadAsStringAsync().Result; return Ok("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}"); }))
        { BaseAddress = new Uri("https://api.openai.com/v1/") };
        using var client = new OpenAIClient(new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com/v1", Model = "gpt-5.1", Temperature = 0.7 }, "sk-test", http);

        await client.CompleteProtocolAsync(new[] { ChatMessage.User("x") }, "{}");

        Assert.Equal(1, calls);
        Assert.DoesNotContain("\"temperature\"", body);
    }

    [Fact]
    public async Task AnthropicKnownModel_SendsNoTemperature_AndTheCatalogOutputCap()
    {
        string? body = null;
        using var http = new HttpClient(new Capture(req => { body = req.Content!.ReadAsStringAsync().Result; return Ok("{\"content\":[{\"type\":\"text\",\"text\":\"{}\"}],\"stop_reason\":\"end_turn\"}"); }))
        { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        using var client = new AnthropicClient(new LlmProfileConfig { Type = "anthropic", BaseUrl = "https://api.anthropic.com", Model = "claude-3-haiku-20240307", Temperature = 0.7 }, "k", http);

        await client.CompleteProtocolAsync(new[] { ChatMessage.User("x") }, "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}");

        using var doc = JsonDocument.Parse(body!);
        Assert.Equal(0.0, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.False(doc.RootElement.TryGetProperty("output_config", out _));
        Assert.Equal(4096, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task AnthropicModelThatRejectsTemperature_SendsNone()
    {
        string? body = null;
        using var http = new HttpClient(new Capture(req => { body = req.Content!.ReadAsStringAsync().Result; return Ok("{\"content\":[{\"type\":\"text\",\"text\":\"{}\"}],\"stop_reason\":\"end_turn\"}"); }))
        { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        using var client = new AnthropicClient(new LlmProfileConfig { Type = "anthropic", BaseUrl = "https://api.anthropic.com", Model = "claude-sonnet-5-5", Temperature = 0.7 }, "k", http);

        await client.CompleteProtocolAsync(new[] { ChatMessage.User("x") }, "{}");

        Assert.DoesNotContain("\"temperature\"", body);
    }

    // Temperature is read from the first request body whatever the answer: only the payload is under test.
    private static async Task<double> TemperatureSentAsync(string type, string baseUrl, string model, bool structured)
    {
        string? body = null;
        using var http = new HttpClient(new Capture(req => { body = req.Content!.ReadAsStringAsync().Result; return Ok("{}"); }))
        { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        var profile = new LlmProfileConfig { Type = type, BaseUrl = baseUrl, Model = model, Temperature = 0.7 };
        var client = (ILlmClient)(type switch
        {
            "openai" => new OpenAIClient(profile, "k", http),
            "anthropic" => new AnthropicClient(profile, "k", http),
            "gemini" => new GeminiClient(profile, "k", http),
            _ => new OllamaClient(profile, null, http)
        });
        try
        {
            if (structured) await client.CompleteProtocolAsync(new[] { ChatMessage.User("x") }, "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}");
            else await foreach (var _ in client.StreamProtocolAsync(new[] { ChatMessage.User("x") })) { }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        finally { (client as IDisposable)?.Dispose(); }
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        var value = type switch
        {
            "gemini" => root.GetProperty("generationConfig").GetProperty("temperature"),
            "ollama" => root.GetProperty("options").GetProperty("temperature"),
            _ => root.GetProperty("temperature")
        };
        return Math.Round(value.GetDouble(), 3);
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Capture : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public Capture(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_reply(request));
    }
}
