using System.Net;
using System.Net.Http;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

public sealed class UsageObservationTests
{
    public static IEnumerable<object[]> InvalidUsage()
    {
        foreach (string provider in new[] { "openai-json", "openai-sse", "ollama", "ollama-cloud" })
            foreach (string pair in new[] { "", "\"INPUT\":10", "\"OUTPUT\":7", "\"INPUT\":10,\"OUTPUT\":-1", "\"INPUT\":null,\"OUTPUT\":7", "\"INPUT\":\"10\",\"OUTPUT\":7", "\"INPUT\":2147483648,\"OUTPUT\":7", "\"INPUT\":1.5,\"OUTPUT\":7" })
                yield return new object[] { provider, pair };
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Func<string> Response = null!;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response()) });
        }
    }

    private static string Response(string provider, string pair, bool length = false)
    {
        bool ollama = provider.StartsWith("ollama");
        pair = pair.Replace("INPUT", ollama ? "prompt_eval_count" : "prompt_tokens").Replace("OUTPUT", ollama ? "eval_count" : "completion_tokens");
        string reason = length ? "length" : "stop";
        if (ollama) return "{\"message\":{\"content\":\"{}\"},\"done\":true,\"done_reason\":\"" + reason + "\"" + (pair.Length > 0 ? "," + pair : "") + "}\n";
        if (provider.EndsWith("json")) return "{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"" + reason + "\"}],\"usage\":{" + pair + "}}";
        return "data: {\"choices\":[{\"delta\":{\"content\":\"{}\"},\"finish_reason\":\"" + reason + "\"}]}\n\ndata: {\"choices\":[],\"usage\":{" + pair + "}}\n\ndata: [DONE]\n\n";
    }

    private static ILlmClient Client(string provider, HttpClient http, LlmProfileConfig? profile = null) =>
        provider.StartsWith("ollama") ? new OllamaClient(profile ?? new() { Type = provider, Model = "synthetic" }, httpClient: http)
            : new OpenAIClient(profile ?? new() { Type = "openai", Model = "synthetic" }, httpClient: http);

    [Theory]
    [MemberData(nameof(InvalidUsage))]
    public async Task InvalidOrPartialCounts_AreUnknown_IndependentlyOfCompletion(string provider, string pair)
    {
        var handler = new Handler { Response = () => Response(provider, pair, true) };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.example.test/") };
        var client = Client(provider, http);
        using var lifetime = (IDisposable)client;
        await client.CompleteProtocolAsync(new[] { ChatMessage.User("synthetic") }, "{}");
        Assert.Null(client.LastTokenUsage);
        Assert.True(client.LastCompletion!.IsTruncated);
        handler.Response = () => Response(provider, "\"INPUT\":0,\"OUTPUT\":0");
        await client.CompleteProtocolAsync(new[] { ChatMessage.User("synthetic") }, "{}");
        Assert.NotNull(client.LastTokenUsage);
        Assert.Equal(0, client.LastTokenUsage!.PromptTokens);
        Assert.Equal(0, client.LastTokenUsage.CompletionTokens);
        handler.Response = () => Response(provider, "");
        await client.CompleteProtocolAsync(new[] { ChatMessage.User("synthetic") }, "{}");
        Assert.Null(client.LastTokenUsage);
        Assert.False(client.LastCompletion!.IsTruncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CumulativeFragments_AreMergedWithoutSumming(bool ollama)
    {
        string body = ollama
            ? "{\"message\":{\"content\":\"{}\"},\"done\":false,\"prompt_eval_count\":10}\n{\"done\":true,\"eval_count\":7}\n"
            : "data: {\"choices\":[{\"delta\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10}}\n\ndata: {\"choices\":[],\"usage\":{\"completion_tokens\":7}}\n\ndata: [DONE]\n\n";
        using var http = new HttpClient(new Handler { Response = () => body }) { BaseAddress = new Uri("https://fixture.example.test/") };
        var client = Client(ollama ? "ollama" : "openai-sse", http);
        using var lifetime = (IDisposable)client;
        await client.CompleteProtocolAsync(new[] { ChatMessage.User("synthetic") }, "{}");
        Assert.Equal(10, client.LastTokenUsage!.PromptTokens);
        Assert.Equal(7, client.LastTokenUsage.CompletionTokens);
    }


}
