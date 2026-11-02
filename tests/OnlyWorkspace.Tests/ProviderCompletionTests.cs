using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

/// <summary>HTTP fixtures, not live provider qualification. Missing usage also covers compatible gateways.</summary>
public sealed class ProviderCompletionTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task MissingUsage_CompletionReachesDispatcherAndConsumers_AndResets(bool gemini, bool sse, bool truncated)
    {
        using var fixture = new Fixture(gemini, request => Response(gemini, sse, request == 1 && truncated));
        using var dispatched = new LlmRequestDispatcher(fixture.Client, fixture.Profile, () => { }, audit: TestPrivacyAudit.Store);
        Assert.Equal("{}", await dispatched.CompleteProtocolAsync(Messages(), "{}"));
        Assert.Null(dispatched.LastTokenUsage);
        Assert.Equal(truncated, dispatched.LastCompletion!.IsTruncated);
        Assert.Equal(Reason(gemini, truncated), dispatched.LastCompletion.Reason);
        if (truncated) Assert.Throws<InvalidModelOutputException>(() => ModelOutput.EnsureComplete(dispatched.LastCompletion));
        else ModelOutput.EnsureComplete(dispatched.LastCompletion);
        Assert.Equal("{}", await dispatched.CompleteProtocolAsync(Messages(), "{}"));
        Assert.False(dispatched.LastCompletion!.IsTruncated);
        Assert.Equal(Reason(gemini, false), dispatched.LastCompletion.Reason);
        Assert.Null(dispatched.LastTokenUsage);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task PartialUsage_DoesNotBecomeAnObservedPair(bool gemini, bool sse, bool inputOnly)
    {
        string usage = gemini
            ? inputOnly ? "{\"promptTokenCount\":10}" : "{\"candidatesTokenCount\":7}"
            : inputOnly ? "{\"input_tokens\":10}" : "{\"output_tokens\":7}";
        using var fixture = new Fixture(gemini, _ => Response(gemini, sse, true, usage));
        await fixture.Client.CompleteProtocolAsync(Messages(), "{}");
        Assert.Null(fixture.Client.LastTokenUsage);
        Assert.Throws<InvalidModelOutputException>(() => ModelOutput.EnsureComplete(fixture.Client.LastCompletion));
    }

    [Theory]
    [InlineData(true, "null")]
    [InlineData(true, "\"7\"")]
    [InlineData(true, "-1")]
    [InlineData(true, "2147483648")]
    [InlineData(false, "null")]
    [InlineData(false, "\"7\"")]
    [InlineData(false, "-1")]
    [InlineData(false, "2147483648")]
    public async Task InvalidTokenCount_IsUnknown_AndDoesNotHideTruncation(bool gemini, string count)
    {
        string usage = gemini ? "{\"promptTokenCount\":10,\"candidatesTokenCount\":" + count + "}"
            : "{\"input_tokens\":10,\"output_tokens\":" + count + "}";
        using var fixture = new Fixture(gemini, _ => Response(gemini, false, true, usage));
        await fixture.Client.CompleteProtocolAsync(Messages(), "{}");
        Assert.Null(fixture.Client.LastTokenUsage);
        Assert.True(fixture.Client.LastCompletion!.IsTruncated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamingFragments_PreserveCompletionAndCumulativeCounts_ThenReset(bool gemini)
    {
        string body = gemini
            ? Response(true, true, true, "{\"promptTokenCount\":10}") +
              Event(new { usageMetadata = new { candidatesTokenCount = 5, thoughtsTokenCount = 20 } }) +
              Event(new { usageMetadata = new { candidatesTokenCount = 7 } })
            : Event(new { type = "message_start", message = new { usage = new { input_tokens = 10 } } }) +
              Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = "{}" } }) +
              Event(new { type = "message_delta", delta = new { stop_reason = "max_tokens" } }) +
              Event(new { type = "message_delta", usage = new { output_tokens = 5 } }) +
              Event(new { type = "message_delta", usage = new { output_tokens = 27 } }) +
              Event(new { type = "message_delta", delta = new { stop_reason = (string?)null }, usage = new { } }) +
              Event(new { type = "message_stop" });
        using var fixture = new Fixture(gemini, request => request == 1 ? body : Response(gemini, true, false));
        var answer = new StringBuilder();
        await foreach (string chunk in fixture.Client.StreamProtocolAsync(Messages())) answer.Append(chunk);
        Assert.Equal("{}", answer.ToString());
        Assert.True(fixture.Client.LastCompletion!.IsTruncated); // final synthesis will request continuation
        Assert.Equal(10, fixture.Client.LastTokenUsage!.PromptTokens);
        Assert.Equal(27, fixture.Client.LastTokenUsage.CompletionTokens);
        await foreach (var _ in fixture.Client.StreamProtocolAsync(Messages())) { }
        Assert.False(fixture.Client.LastCompletion!.IsTruncated);
        Assert.Null(fixture.Client.LastTokenUsage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObservedZero_IsDistinctFromMissingUsage(bool gemini)
    {
        string usage = gemini ? "{\"promptTokenCount\":0,\"candidatesTokenCount\":0}" : "{\"input_tokens\":0,\"output_tokens\":0}";
        using var fixture = new Fixture(gemini, _ => Response(gemini, false, false, usage));
        await fixture.Client.CompleteProtocolAsync(Messages(), "{}");
        Assert.Equal(0, fixture.Client.LastTokenUsage!.PromptTokens);
        Assert.Equal(0, fixture.Client.LastTokenUsage.CompletionTokens);
    }

    [Fact]
    public async Task AnthropicInitialOutput_IsNotAFinalObservedCount()
    {
        string body = Event(new { type = "message_start", message = new { usage = new { input_tokens = 10, output_tokens = 1 } } }) +
            Response(false, true, true);
        using var fixture = new Fixture(false, _ => body);
        await fixture.Client.CompleteProtocolAsync(Messages(), "{}");
        Assert.True(fixture.Client.LastCompletion!.IsTruncated);
        Assert.Null(fixture.Client.LastTokenUsage);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RefusalWithoutUsage_RemainsAnError(bool gemini, bool sse)
    {
        string body = gemini ? "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}"
            : "{\"content\":[],\"stop_reason\":\"refusal\"}";
        if (sse) body = gemini ? "data: " + body + "\n\n"
            : Event(new { type = "message_delta", delta = new { stop_reason = "refusal" } }) + Event(new { type = "message_stop" });
        using var fixture = new Fixture(gemini, _ => body);
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.CompleteProtocolAsync(Messages(), "{}"));
        Assert.Null(fixture.Client.LastTokenUsage);
        Assert.Equal(gemini ? "SAFETY" : "refusal", fixture.Client.LastCompletion!.Reason);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task PrematureEofAfterPreviousRequest_DoesNotReuseItsState(bool gemini, bool structured)
    {
        string usage = gemini ? "{\"promptTokenCount\":10,\"candidatesTokenCount\":7}" : "{\"input_tokens\":10,\"output_tokens\":7}";
        string cut = gemini ? Event(new { candidates = new[] { new { content = new { parts = new[] { new { text = "{}" } } } } } })
            : Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = "{}" } });
        using var fixture = new Fixture(gemini, request => request == 1 ? Response(gemini, true, true, usage) : cut);
        await fixture.Client.CompleteProtocolAsync(Messages(), "{}");
        Assert.NotNull(fixture.Client.LastTokenUsage);
        if (structured) await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.CompleteProtocolAsync(Messages(), "{}"));
        else await Assert.ThrowsAsync<HttpRequestException>(async () => { await foreach (var _ in fixture.Client.StreamProtocolAsync(Messages())) { } });
        Assert.Null(fixture.Client.LastTokenUsage);
        Assert.Null(fixture.Client.LastCompletion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnthropicContextLimit_IsTruncatedWithoutUsage(bool sse)
    {
        using var fixture = new Fixture(false, _ => Response(false, sse, true).Replace("max_tokens", "model_context_window_exceeded"));
        await fixture.Client.CompleteProtocolAsync(Messages(), "{}");
        Assert.True(fixture.Client.LastCompletion!.IsTruncated);
        Assert.Null(fixture.Client.LastTokenUsage);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SuccessfulStructuredRequestWithoutUsage_DoesNotReusePreviousCounts(bool gemini, bool sse)
    {
        string usage = gemini ? "{\"promptTokenCount\":10,\"candidatesTokenCount\":7}" : "{\"input_tokens\":10,\"output_tokens\":7}";
        using var fixture = new Fixture(gemini, request => Response(gemini, sse, false, request == 1 ? usage : null));
        await fixture.Client.CompleteProtocolAsync(Messages(), "{}");
        Assert.NotNull(fixture.Client.LastTokenUsage);
        await fixture.Client.CompleteProtocolAsync(Messages(), "{}");
        Assert.Null(fixture.Client.LastTokenUsage);
        Assert.False(fixture.Client.LastCompletion!.IsTruncated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsageBeforeFinishReason_IsRetainedByStructuredStream(bool gemini)
    {
        string body = gemini
            ? Event(new { usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 7 } }) + Response(true, true, true)
            : Event(new { type = "message_start", message = new { usage = new { input_tokens = 10, output_tokens = 1 } } }) +
              Event(new { type = "message_delta", usage = new { output_tokens = 7 } }) + Response(false, true, true);
        using var fixture = new Fixture(gemini, _ => body);
        await fixture.Client.CompleteProtocolAsync(Messages(), "{}");
        Assert.Equal(10, fixture.Client.LastTokenUsage!.PromptTokens);
        Assert.Equal(7, fixture.Client.LastTokenUsage.CompletionTokens);
        Assert.True(fixture.Client.LastCompletion!.IsTruncated);
    }

    private static ChatMessage[] Messages() => new[] { ChatMessage.User("Synthetic fixture") };
    private static string Reason(bool gemini, bool truncated) => gemini ? truncated ? "MAX_TOKENS" : "STOP" : truncated ? "max_tokens" : "end_turn";
    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";

    private static string Response(bool gemini, bool sse, bool truncated, string? usage = null)
    {
        string json = gemini
            ? JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = "{}" } } }, finishReason = Reason(true, truncated) } } })
            : JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = "{}" } }, stop_reason = Reason(false, truncated) });
        if (!sse || gemini)
        {
            if (usage != null) json = json.Substring(0, json.Length - 1) + (gemini ? ",\"usageMetadata\":" : ",\"usage\":") + usage + "}";
            return sse ? "data: " + json + "\n\n" : json;
        }
        string delta = "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + Reason(false, truncated) + "\"}";
        if (usage != null) delta += ",\"usage\":" + usage;
        return Event(new { type = "content_block_delta", delta = new { type = "text_delta", text = "{}" } }) +
            "data: " + delta + "}\n\n" + Event(new { type = "message_stop" });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        public LlmProfileConfig Profile { get; } = new() { Id = "fixture", Model = "synthetic", BaseUrl = "https://synthetic.example.test/v1" };
        public ILlmClient Client { get; }
        public Fixture(bool gemini, Func<int, string> response)
        {
            _http = new HttpClient(new Handler(response)) { BaseAddress = new Uri(Profile.BaseUrl + "/") };
            Client = gemini ? new GeminiClient(Profile, httpClient: _http) : new AnthropicClient(Profile, httpClient: _http);
        }
        public void Dispose() { (Client as IDisposable)?.Dispose(); _http.Dispose(); }
    }

    private sealed class Handler(Func<int, string> response) : HttpMessageHandler
    {
        private int _requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnbufferedHttpContent(response(++_requests)) });
    }
}
