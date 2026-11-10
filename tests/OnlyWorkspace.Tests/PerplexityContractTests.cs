using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

/// <summary>Offline Sonar wire fixtures, not a qualification of an authenticated provider account.
/// https://docs.perplexity.ai/docs/agent-api/migrate-from-sonar/overview
/// https://docs.perplexity.ai/docs/agent-api/migrate-from-sonar/how-to</summary>
public sealed class PerplexityContractTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StructuredSonarUsesMessagesAndSchemaAndReadsPlainOrStreamedChoices(bool plain, bool versionedBase)
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}";
        const string output = "{\"answer\":\"Synthetic finding [1]\"}";
        var profile = new LlmProfileConfig { Type = "perplexity", BaseUrl = "https://api.perplexity.ai" + (versionedBase ? "/v1" : ""), Model = "sonar", AuthType = "bearer" };
        int requests = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/sonar", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-test-key", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal("sonar", root.GetProperty("model").GetString());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("Synthetic question", root.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.False(root.TryGetProperty("input", out _));
            var format = root.GetProperty("response_format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
            Assert.Equal("string", format.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());
            var sources = new[] { "https://example.test/source" };
            string response = plain
                ? JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = output }, finish_reason = "stop" } }, citations = sources, search_results = new[] { new { title = "Synthetic source", url = sources[0] } }, usage = new { prompt_tokens = 13, completion_tokens = 8 } })
                : "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = output }, finish_reason = "stop" } }, citations = sources, search_results = new[] { new { title = "Synthetic source", url = sources[0] } } }) + "\n\n" +
                    "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":13,\"completion_tokens\":8}}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, plain ? "application/json" : "text/event-stream") };
        }))
        { BaseAddress = new Uri(profile.BaseUrl + "/") };
        using var client = new OpenAIClient(profile, "synthetic-test-key", http);
        string result = await client.CompleteProtocolAsync(new[] { ChatMessage.System("Return synthetic JSON"), ChatMessage.User("Synthetic question") }, schema);
        Assert.Equal(output, result);
        Assert.Equal(1, requests);
        Assert.False(client.LastCompletion!.IsTruncated);
        Assert.Equal("stop", client.LastCompletion.Reason);
        Assert.Equal((13, 8), (client.LastTokenUsage!.PromptTokens, client.LastTokenUsage.CompletionTokens));
        // URL metadata is not promoted to answer text or to a fabricated source list by this client.
        Assert.DoesNotContain("https://example.test/source", result);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("length")]
    public async Task ChatPreservesNumberedMarkersAndCompletionWithoutTurningSourceMetadataIntoText(string reason)
    {
        const string text = "Synthetic answer [1].";
        string sse = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text }, finish_reason = reason } }, citations = new[] { "https://example.test/source" } }) + "\n\ndata: [DONE]\n\n";
        var profile = new LlmProfileConfig { Type = "perplexity", BaseUrl = "https://api.perplexity.ai", Model = "sonar" };
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") }))) { BaseAddress = new Uri(profile.BaseUrl + "/") };
        using var client = new OpenAIClient(profile, "synthetic-test-key", http);
        var output = new StringBuilder();
        await foreach (var chunk in client.StreamProtocolAsync(new[] { ChatMessage.User("Synthetic question") })) output.Append(chunk);
        Assert.Equal(text, output.ToString());
        Assert.Equal(reason == "length", client.LastCompletion!.IsTruncated);
        Assert.Null(client.LastTokenUsage);
    }
}
