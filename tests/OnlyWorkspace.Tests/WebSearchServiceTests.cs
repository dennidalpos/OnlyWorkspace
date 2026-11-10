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

public sealed class WebSearchServiceTests
{


    [Fact]
    public async Task CancelledWebRequest_DoesNotNotifyExternalDispatch()
    {
        int calls = 0, notices = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json("{}")); }));
        using var search = new WebSearchService(Profile("openai"), null, null, http, () => notices++);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.SearchAsync(new[] { ChatMessage.User("Synthetic") }, cts.Token));
        Assert.Equal(0, calls);
        Assert.Equal(0, notices);
    }

    [Fact]
    public async Task LocalQueryFullInstructionsAndToolsOverBudget_SendNothing()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json("{}"));
        }));
        var profile = new LlmProfileConfig { Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "synthetic", ContextBudgetTokens = 100 };
        using var search = new WebSearchService(profile, null, "synthetic-web-key", http);
        await Assert.ThrowsAsync<OnlyWorkspace.Core.Privacy.DataEgressBlockedException>(() => search.SearchAsync(new[] { ChatMessage.User(new string('x', 300)) }));
        Assert.Equal(0, calls);
    }



    [Fact]
    public void Destinations_NameEveryHostThatReceivesSearchData()
    {
        var ollama = new LlmProfileConfig { Type = "ollama", BaseUrl = "http://127.0.0.1:11434" };
        Assert.Equal(new[] { "127.0.0.1", "ollama.com" }, WebSearchService.Destinations(ollama));
        Assert.Contains("ollama.com", WebSearchService.Disclosure(ollama));

        var cloud = new LlmProfileConfig { Type = "openai", BaseUrl = "https://api.openai.com" };
        Assert.Equal(new[] { "api.openai.com" }, WebSearchService.Destinations(cloud));
        Assert.Contains("api.openai.com", WebSearchService.Disclosure(cloud));
    }

    [Theory]
    [InlineData("ollama", "http://127.0.0.1:11434")]
    [InlineData("openai", "https://api.openai.com")]
    public void Disclosure_SaysOnlyTheProbeLeavesAndActionSearchesAreBlocked(string type, string baseUrl)
    {
        var text = WebSearchService.Disclosure(new LlmProfileConfig { Type = type, BaseUrl = baseUrl });
        Assert.Contains("sintetica fissa", text);
        Assert.Contains("bloccate", text);
        Assert.DoesNotContain("6.000", text);
        Assert.DoesNotContain("240", text);
    }

    [Fact]
    public async Task SearchAsync_BlocksWithTheCloudFactsMessageBeforeAnyHttp()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json("{}")); }));
        using var search = new WebSearchService(Profile("openai"), null, null, http);
        var error = await Assert.ThrowsAsync<OnlyWorkspace.Core.Privacy.DataEgressBlockedException>(() => search.SearchAsync(new[] { ChatMessage.User("Synthetic") }));
        Assert.Equal(OnlyWorkspace.Core.Privacy.CloudFactsRequest.BlockedMessage, error.Message);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(OnlyWorkspace.Core.Privacy.ExposureLevel.Standard, true)]
    [InlineData(OnlyWorkspace.Core.Privacy.ExposureLevel.Minimal, true)]
    [InlineData(OnlyWorkspace.Core.Privacy.ExposureLevel.Extended, false)]
    public async Task MaskedSearch_OnlyAtEstesoAndWithAMap(OnlyWorkspace.Core.Privacy.ExposureLevel level, bool blocked)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(OpenAiAnswer("ok"))); }));
        var profile = Profile("openai"); profile.External = true; profile.Exposure = level;
        using var search = new WebSearchService(profile, "secret", httpClient: http);
        var mask = new OnlyWorkspace.Core.Privacy.Pseudonymizer(maskAmounts: true, maskDates: true);
        if (blocked)
        {
            await Assert.ThrowsAsync<OnlyWorkspace.Core.Privacy.DataEgressBlockedException>(() => search.SearchAsync(new[] { ChatMessage.User("Synthetic") }, default, "chat", mask));
            Assert.Equal(0, calls);
        }
        else
        {
            await Assert.ThrowsAsync<OnlyWorkspace.Core.Privacy.DataEgressBlockedException>(() => search.SearchAsync(new[] { ChatMessage.User("Synthetic") }, default, "chat", null));
            Assert.Equal(0, calls);
            // No audit sink: the request must not go out unrecorded.
            await Assert.ThrowsAsync<OnlyWorkspace.Core.Privacy.DataEgressBlockedException>(() => search.SearchAsync(new[] { ChatMessage.User("Synthetic") }, default, "chat", mask));
            Assert.Equal(0, calls);
            await search.SearchAsync(new[] { ChatMessage.User("Synthetic") }, default, "chat", mask, TestPrivacyAudit.Store);
            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public async Task MaskedSearch_AnUnknownActionLabelNeverReachesTheProvider()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async (req, _) => { body = await req.Content!.ReadAsStringAsync(); return Json(OpenAiAnswer("ok")); }));
        var profile = Profile("openai"); profile.External = true; profile.Exposure = OnlyWorkspace.Core.Privacy.ExposureLevel.Extended;
        using var search = new WebSearchService(profile, "secret", httpClient: http);
        var mask = new OnlyWorkspace.Core.Privacy.Pseudonymizer(maskAmounts: true, maskDates: true);
        await search.SearchAsync(new[] { ChatMessage.User("Synthetic") }, default, "mario.rossi@example.org", mask, TestPrivacyAudit.Store);
        Assert.DoesNotContain("mario.rossi@example.org", body);
    }

    [Fact]
    public async Task MaskedSearch_AnUnreadableAnswerLeavesAFailedRecord_NotDispatching()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{\"output\":\"not an array\",\"error\":{\"message\":\"x\"}}"))));
        var profile = Profile("openai"); profile.External = true; profile.Exposure = OnlyWorkspace.Core.Privacy.ExposureLevel.Extended;
        using var search = new WebSearchService(profile, "secret", httpClient: http);
        var mask = new OnlyWorkspace.Core.Privacy.Pseudonymizer(maskAmounts: true, maskDates: true);
        var audit = TestPrivacyAudit.Store;
        await Assert.ThrowsAnyAsync<Exception>(() => search.SearchAsync(new[] { ChatMessage.User("Synthetic") }, default, "chat", mask, audit));
        Assert.Equal("failed", audit.Read().Last().Outcome);
    }

    [Fact]
    public async Task MaskedSearch_AMaskingFailureLeavesABlockedRecordAndSendsNothing()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(OpenAiAnswer("ok"))); }));
        var profile = Profile("openai"); profile.External = true; profile.Exposure = OnlyWorkspace.Core.Privacy.ExposureLevel.Extended;
        using var search = new WebSearchService(profile, "secret", httpClient: http);
        var catalog = OnlyWorkspace.Core.Privacy.PseudonymizationCatalog.Load();
        var mask = new OnlyWorkspace.Core.Privacy.Pseudonymizer(catalog: catalog, strategies: new[] { new ThrowingStrategy() });
        var audit = TestPrivacyAudit.Store;
        await Assert.ThrowsAsync<OnlyWorkspace.Core.Privacy.DataEgressBlockedException>(() => search.SearchAsync(new[] { ChatMessage.User("Synthetic") }, default, "chat", mask, audit));
        var records = audit.Read();
        Assert.Single(records);
        Assert.Equal("blocked", records[0].Outcome);
        Assert.Equal(0, calls);
    }

    private sealed class ThrowingStrategy : OnlyWorkspace.Core.Privacy.IPseudonymizationStrategy
    {
        public string Id => "throwing"; public string Revision => "1"; public int Priority => 1;
        public IEnumerable<OnlyWorkspace.Core.Privacy.PseudonymMatch> Detect(string text, OnlyWorkspace.Core.Privacy.PseudonymizationContext context) =>
            throw new InvalidOperationException("synthetic dictionary failure");
    }

    [Fact]
    public async Task MaskedSearch_SendsPlaceholdersAndRestoresTheAnswerOnThePc()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async (req, _) =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return Json(OpenAiAnswer("Contatto [EMAIL_1] verificato"));
        }));
        var profile = Profile("openai"); profile.External = true; profile.Exposure = OnlyWorkspace.Core.Privacy.ExposureLevel.Extended;
        using var search = new WebSearchService(profile, "secret", httpClient: http);
        var mask = new OnlyWorkspace.Core.Privacy.Pseudonymizer(maskAmounts: true, maskDates: true);
        var result = await search.SearchAsync(new[] { ChatMessage.User("Chi è mario.rossi@example.org?") }, default, "chat", mask, TestPrivacyAudit.Store);
        Assert.NotNull(body);
        Assert.DoesNotContain("mario.rossi@example.org", body);
        Assert.Contains("[EMAIL_1]", body);
        Assert.Equal("Contatto mario.rossi@example.org verificato", result.Text);
    }

    private static string OpenAiAnswer(string text) =>
        "{\"output\":[{\"type\":\"web_search_call\",\"status\":\"completed\",\"action\":{\"sources\":[{\"url\":\"https://example.org/a\"}]}}," +
        "{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\"}]}]}";

    [Fact]
    public void DisplayText_TurnsProviderLinksIntoSourceReferences()
    {
        var result = new WebSearchResult
        {
            Text = "Scadenza il 16 marzo [[31]](https://www.inps.it/cu?x=1). Profili omonimi [[22]](https://www.linkedin.com/pub/dir/x) e " +
                   "[modello](https://example.org/doc) e [W1] e [testo] (nota)."
        };
        result.Sources.Add(new WebSource { Title = "INPS", Url = "https://www.inps.it/cu?x=1" });

        Assert.Equal("Scadenza il 16 marzo [W1]. Profili omonimi e modello e [W1] e [testo] (nota).", result.DisplayText());
    }

    [Fact]
    public void PlainSourceUrls_KeepTheirNumbersAndPunctuation_WithoutPromotingUnknownUrls()
    {
        var result = new WebSearchResult { Text = "Fonte: https://example.org/report. Altra (https://example.org/end.). Ignoto: https://unknown.test/a" };
        result.Sources.Add(new WebSource { Url = "https://example.org/unused" });
        result.Sources.Add(new WebSource { Url = "https://example.org/report" });
        result.Sources.Add(new WebSource { Url = "https://example.org/end." });
        string displayed = result.DisplayText();
        Assert.Equal("Fonte: [W2]. Altra ([W3]). Ignoto: https://unknown.test/a", displayed);
        Assert.Equal(new[] { 2, 3 }, WebCitations.Used(displayed, result.Sources).ConvertAll(s => s.CitationNumber));
    }

    [Fact]
    public void RequestTimeout_LeavesRoomForARealSearchAndFollowsTheProfile()
    {
        Assert.Equal(TimeSpan.FromSeconds(180), WebSearchService.RequestTimeout(new LlmProfileConfig { FirstTokenTimeoutSeconds = 120 }));
        Assert.Equal(TimeSpan.FromSeconds(600), WebSearchService.RequestTimeout(new LlmProfileConfig { FirstTokenTimeoutSeconds = 600 }));
    }

    [Fact]
    public void ItalianNotices_KeepItalianDigitsOnAnEnglishWindows()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Assert.Contains("6.000", WebSearchService.InputCutNotice);
            Assert.Contains("12.345 token", new OnlyWorkspace.Core.Context.ContextTooLongException(12345, 1000).Message);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _reply;
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _reply(request, cancellationToken);
    }

    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private static LlmProfileConfig Profile(string type) => new()
    {
        Type = type,
        Model = "test-model",
        BaseUrl = type switch
        {
            "openai" => "https://api.openai.com/v1",
            "anthropic" => "https://api.anthropic.com/v1",
            "gemini" => "https://generativelanguage.googleapis.com/v1beta",
            _ => "http://localhost:11434"
        }
    };

    [Theory]
    [InlineData("openai", "{\"output\":[{\"type\":\"web_search_call\",\"status\":\"completed\"}]}")]
    [InlineData("gemini", "{\"candidates\":[{\"groundingMetadata\":{\"webSearchQueries\":[\"UTC date\"]}}]}")]
    public async Task CapabilityProbe_UsesSyntheticRequestWithoutMail(string provider, string response)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (req, _) =>
        {
            calls++;
            var body = req.Content == null ? "" : await req.Content.ReadAsStringAsync();
            Assert.DoesNotContain("segreto-email", body);
            Assert.EndsWith(provider == "anthropic" ? "/models/test-model" : provider == "openai" ? "/responses" : "/models/test-model:generateContent", req.RequestUri!.AbsolutePath);
            return Json(response);
        }));
        using var search = new WebSearchService(Profile(provider), "secret", httpClient: http);
        Assert.Equal(WebSearchSupport.Supported, await search.CheckSupportAsync());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("anthropic", "none", "x-api-key")]
    [InlineData("anthropic", "ApiKey", "x-api-key")]
    [InlineData("gemini", "none", "x-goog-api-key")]
    public async Task KeyHeaderProviders_SendTheirKeyHeader_WhateverAuthTypeSays(string provider, string authType, string header)
    {
        var seen = new System.Collections.Generic.List<string>();
        using var http = new HttpClient(new Handler((req, _) =>
        {
            seen.Add((req.Headers.Authorization?.Scheme ?? "-") + "|" + (req.Headers.TryGetValues(header, out var v) ? string.Join(",", v) : "-"));
            return Task.FromResult(Json("{}"));
        }));
        var profile = Profile(provider);
        profile.AuthType = authType;
        using var search = new WebSearchService(profile, "secret", httpClient: http);
        try { await search.CheckSupportAsync(); } catch (Exception) { }

        Assert.NotEmpty(seen);
        Assert.All(seen, s => Assert.Equal("-|secret", s));
    }

    [Fact]
    public async Task Anthropic_CapabilityNeedsSuccessfulSyntheticSearch()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (req, _) =>
        {
            calls++;
            if (calls == 1) return Json("{\"capabilities\":{\"server_tools\":{\"web_search\":{\"supported\":true}}}}");
            Assert.EndsWith("/messages", req.RequestUri!.AbsolutePath);
            var body = await req.Content!.ReadAsStringAsync();
            Assert.Contains("web_search_20250305", body);
            Assert.DoesNotContain("segreto-email", body);
            return Json("{\"content\":[{\"type\":\"web_search_tool_result\",\"content\":[]}]}");
        }));
        using var search = new WebSearchService(Profile("anthropic"), "secret", httpClient: http);
        Assert.Equal(WebSearchSupport.Supported, await search.CheckSupportAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Anthropic_DeclaredUnsupportedModel_IsDisabled()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{\"capabilities\":{\"server_tools\":{\"web_search\":{\"supported\":false}}}}"))));
        using var search = new WebSearchService(Profile("anthropic"), "secret", httpClient: http);
        Assert.Equal(WebSearchSupport.Unsupported, await search.CheckSupportAsync());
    }

    [Fact]
    public async Task Anthropic_ToolErrorAfterModelCapability_IsInconclusive()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(++calls == 1
            ? "{\"capabilities\":{\"server_tools\":{\"web_search\":{\"supported\":true}}}}"
            : "{\"content\":[{\"type\":\"web_search_tool_result\",\"content\":{\"error_code\":\"unavailable\"}}]}"))));
        using var search = new WebSearchService(Profile("anthropic"), "secret", httpClient: http);
        Assert.Equal(WebSearchSupport.Unverified, await search.CheckSupportAsync());
        Assert.Contains("unavailable", search.LastCheckError);
    }

    [Theory]
    [InlineData("openai", "{\"output\":[]}")]
    public async Task CapabilityProbe_WithoutActualSearch_RemainsUnverified(string provider, string response)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(response))));
        using var search = new WebSearchService(Profile(provider), "secret", httpClient: http);
        Assert.Equal(WebSearchSupport.Unverified, await search.CheckSupportAsync());
    }

    [Fact]
    public async Task Gemini_AcceptedToolWithoutExecutedSearchRemainsUnverified()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]}}]}"))));
        using var search = new WebSearchService(Profile("gemini"), "secret", httpClient: http);
        Assert.Equal(WebSearchSupport.Unverified, await search.CheckSupportAsync());
        Assert.Contains("non ha eseguito", search.LastCheckError);
    }

    [Fact]
    public async Task CapabilityProbe_ReportsTransportFailureWithoutClaimingUnsupportedModel()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{\"error\":\"service unavailable\"}", HttpStatusCode.ServiceUnavailable))));
        using var search = new WebSearchService(Profile("openai"), "secret", httpClient: http);
        Assert.Equal(WebSearchSupport.Unverified, await search.CheckSupportAsync());
        Assert.Contains("503", search.LastCheckError);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.OK)]
    [InlineData("wrong", HttpStatusCode.Unauthorized)]
    public async Task Ollama_MissingOrWrongWebKey_RemainsUnverified(string? key, HttpStatusCode status)
    {
        var webCalls = 0;
        using var http = new HttpClient(new Handler((req, _) =>
        {
            if (req.RequestUri!.Host == "ollama.com")
            {
                webCalls++;
                Assert.Equal(key, req.Headers.Authorization?.Parameter);
                return Task.FromResult(Json("{}", status));
            }
            return Task.FromResult(Json("{\"capabilities\":[\"completion\",\"tools\"]}"));
        }));
        using var search = new WebSearchService(Profile("ollama"), null, key, http);
        Assert.Equal(WebSearchSupport.Unverified, await search.CheckSupportAsync());
        Assert.Equal(key == null ? 0 : 1, webCalls);
    }

    [Fact]
    public async Task CapabilityProbe_CancelledByModelChange_DoesNotReturnStaleSupport()
    {
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            await Task.Delay(10000, ct);
            return Json("{}");
        }));
        using var search = new WebSearchService(Profile("openai"), "secret", httpClient: http);
        var check = search.CheckSupportAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }







    [Theory]
    [InlineData("{\"output\":[{\"type\":\"web_search_call\",\"status\":\"failed\",\"action\":{\"sources\":[{\"url\":\"https://example.org/a\"}]}},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Answer\"}]}]}")]
    [InlineData("{\"output\":[{\"type\":\"web_search_call\",\"action\":{\"sources\":[]}},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Answer\"}]}]}")]
    public void QwenCloud_FailedOrUnverifiableSearch_IsRejected(string response)
    {
        using var document = JsonDocument.Parse(response);
        Assert.Throws<HttpRequestException>(() => WebSearchService.ParseResult(WebSearchMode.BasicResponses, document.RootElement));
    }



    /// <summary>Without a forced tool the model may answer directly: a skip, not a failure (actions go on without web).</summary>
    [Theory]
    [InlineData(WebSearchMode.BasicResponses, "{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Answer\"}]}]}")]
    [InlineData(WebSearchMode.AnthropicTool, "{\"content\":[{\"type\":\"text\",\"text\":\"Answer\"}]}")]
    public void AnswerWithoutAnySearch_IsASkip(WebSearchMode mode, string response)
    {
        using var document = JsonDocument.Parse(response);
        Assert.Throws<WebSearchSkippedException>(() => WebSearchService.ParseResult(mode, document.RootElement));
    }

    /// <summary>Where the tool is forced, a missing search is still an error.</summary>
    [Fact]
    public void OpenAI_AnswerWithoutSearch_IsAnError()
    {
        using var document = JsonDocument.Parse("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Answer\"}]}]}");
        Assert.Throws<HttpRequestException>(() => WebSearchService.ParseResult(WebSearchMode.OpenAIResponses, document.RootElement));
    }

    [Fact]
    public void QwenCloud_FailedSearchWithCitations_IsRejected()
    {
        using var response = JsonDocument.Parse("{\"output\":[{\"type\":\"web_search_call\",\"status\":\"failed\",\"action\":{\"type\":\"search\",\"query\":\"UTC date\"}},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Answer\",\"annotations\":[{\"url\":\"https://example.org/a\"}]}]}]}");
        Assert.Throws<HttpRequestException>(() => WebSearchService.ParseResult(WebSearchMode.BasicResponses, response.RootElement));
    }

    [Fact]
    public async Task CapabilityProbe_ReportsFullQuotaMessageWithoutDisablingSupport()
    {
        string message = "Quota exceeded: " + new string('x', 400);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(
            JsonSerializer.Serialize(new { error = new { message } }), (HttpStatusCode)429))));
        using var search = new WebSearchService(Profile("gemini"), "secret", httpClient: http);
        Assert.Equal(WebSearchSupport.Unverified, await search.CheckSupportAsync());
        Assert.Contains("429", search.LastCheckError);
        Assert.Contains(message, search.LastCheckError);
    }

    [Fact]
    public void QwenCloud_OnlyCompletedSearchContributesSources()
    {
        const string response = "{\"output\":[{\"type\":\"web_search_call\",\"status\":\"failed\",\"action\":{\"sources\":[{\"url\":\"https://example.org/failed\"}]}},{\"type\":\"web_search_call\",\"action\":{\"sources\":[{\"url\":\"https://example.org/valid\"}]}},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Answer\"}]}]}";
        using var document = JsonDocument.Parse(response);
        var result = WebSearchService.ParseResult(WebSearchMode.BasicResponses, document.RootElement);
        Assert.Single(result.Sources);
        Assert.Equal("https://example.org/valid", result.Sources[0].Url);
    }

    [Fact]
    public void Gemini_GroundingSourcesAreExtracted()
    {
        using var document = JsonDocument.Parse("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Answer\"}]},\"groundingMetadata\":{\"webSearchQueries\":[\"query\"],\"groundingChunks\":[{\"web\":{\"uri\":\"https://example.org/a\",\"title\":\"Source\"}}]}}]}");
        var result = WebSearchService.ParseResult(WebSearchMode.GeminiGrounding, document.RootElement);
        Assert.Equal("Answer", result.Text);
        Assert.Equal("https://example.org/a", result.Sources[0].Url);
    }

    [Fact]
    public void Anthropic_SearchErrorInSuccessfulHttpResponse_IsNotSilent()
    {
        using var document = JsonDocument.Parse("{\"content\":[{\"type\":\"web_search_tool_result\",\"content\":{\"type\":\"web_search_tool_result_error\",\"error_code\":\"unavailable\"}}]}");
        Assert.Throws<HttpRequestException>(() => WebSearchService.ParseResult(WebSearchMode.AnthropicTool, document.RootElement));
    }

    [Fact]
    public void OpenAI_CitationsAreKeptAndInvalidLinksDropped()
    {
        using var document = JsonDocument.Parse("{\"output\":[{\"type\":\"web_search_call\",\"status\":\"completed\"},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Answer\",\"annotations\":[{\"type\":\"url_citation\",\"url\":\"https://example.com/a\",\"title\":\"Example\"},{\"type\":\"url_citation\",\"url\":\"javascript:alert(1)\",\"title\":\"Bad\"}]}]}]}");
        var result = WebSearchService.ParseResult(WebSearchMode.OpenAIResponses, document.RootElement);
        Assert.Equal("Answer", result.Text);
        Assert.Single(result.Sources);
        Assert.False(result.InputCut);
    }

    [Fact]
    public void CompletedSearchWithoutVerifiableSources_IsRejected()
    {
        using var document = JsonDocument.Parse("{\"output\":[{\"type\":\"web_search_call\",\"status\":\"completed\"},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Answer\"}]}]}");
        Assert.Throws<HttpRequestException>(() => WebSearchService.ParseResult(WebSearchMode.OpenAIResponses, document.RootElement));
    }

    [Fact]
    public async Task ConnectorWithoutWebSearch_IsUnsupportedWithoutContactingTheProvider()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json("{}")); }));
        var profile = new LlmProfileConfig { Type = "deepseek", BaseUrl = "https://api.deepseek.com", Model = "deepseek-flash" };
        using var search = new WebSearchService(profile, "secret", httpClient: http);
        Assert.Equal(WebSearchSupport.Unsupported, await search.CheckSupportAsync());
        Assert.Equal(0, calls);
    }


}
