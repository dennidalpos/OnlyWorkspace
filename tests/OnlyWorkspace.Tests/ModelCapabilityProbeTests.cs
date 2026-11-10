using System.Net;
using System.Net.Http;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

/// <summary>«Prova funzioni» and «Capacità e verifiche»: outcomes with provenance, never a guess from a failure.</summary>
public sealed class ModelCapabilityProbeTests
{
    private static readonly ProbeKind[] Model = { ProbeKind.Text, ProbeKind.Json, ProbeKind.Reasoning };

    [Fact]
    public async Task Probes_RunInOrderWithSyntheticDataAndReportEachOutcome()
    {
        var client = new ScriptedClient { Answer = "PRONTO", Json = "{\"colore\":\"verde\",\"numero\":7}", Reason = true };
        var reported = new List<CapabilityProbeResult>();
        var results = await ModelCapabilityProbe.RunAsync(client, client, Model, null, new SyncProgress(reported.Add), CancellationToken.None);

        Assert.Equal(Model, results.Select(r => r.Kind));
        Assert.All(results, r => Assert.Equal(ProbeOutcome.Supported, r.Outcome));
        Assert.Equal(results, reported);
        Assert.Equal(3, client.Requests.Count);
        // Nothing but the probe's own invented text goes out.
        Assert.All(client.Requests.SelectMany(m => m), m => Assert.DoesNotContain("@", m.Content));
        Assert.Contains(client.Requests[1], m => m.Content.Contains("numero della prova è 7"));
        Assert.Contains("non mostrato né salvato", results[2].Detail);
    }

    [Fact]
    public async Task UnconformingJsonIsUnsupported_ButAServiceErrorOrTimeoutIsInconclusive()
    {
        var client = new ScriptedClient { Answer = "", Json = "{\"colore\":\"blu\",\"numero\":7}" };
        var results = await ModelCapabilityProbe.RunAsync(client, client, new[] { ProbeKind.Text, ProbeKind.Json }, null, null, CancellationToken.None);
        Assert.Equal(ProbeOutcome.Unsupported, results[0].Outcome);
        Assert.Equal(ProbeOutcome.Unsupported, results[1].Outcome);
        Assert.Contains("colore", results[1].Detail);

        client = new ScriptedClient { Fail = new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).") };
        results = await ModelCapabilityProbe.RunAsync(client, client, new[] { ProbeKind.Text, ProbeKind.Json }, null, null, CancellationToken.None);
        Assert.All(results, r => Assert.Equal(ProbeOutcome.Inconclusive, r.Outcome));
        Assert.Contains("credenziali", results[0].Detail);

        client = new ScriptedClient { Hang = true };
        results = await ModelCapabilityProbe.RunAsync(client, client, new[] { ProbeKind.Text }, null, null, CancellationToken.None, TimeSpan.FromMilliseconds(200));
        Assert.Equal(ProbeOutcome.Inconclusive, results[0].Outcome);
        Assert.Contains("Nessuna risposta entro", results[0].Detail);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData("{\"colore\":\"verde\",\"numero\":7}", null)]
    [InlineData("[1]", "oggetto")]
    [InlineData("{\"colore\":\"verde\",\"numero\":7,\"extra\":1}", "non previsti")]
    [InlineData("{\"colore\":\"verde\",\"numero\":7.5}", "numero")]
    [InlineData("non json", "JSON valido")]
    public void Schema_IsCheckedOnTheAnswer(string answer, string? problem)
    {
        var found = ModelCapabilityProbe.SchemaProblem(answer);
        if (problem == null) Assert.Null(found);
        else Assert.Contains(problem, found);
    }

    [Fact]
    public async Task Reasoning_SupportedOnlyWhenObserved_AndStoppedAsSoonAsSeen()
    {
        var thinking = new ScriptedClient { Answer = "95", Reason = true, ReasonThenHang = true };
        var results = await ModelCapabilityProbe.RunAsync(thinking, thinking, new[] { ProbeKind.Reasoning }, null, null, CancellationToken.None, TimeSpan.FromSeconds(10));
        Assert.Equal(ProbeOutcome.Supported, results[0].Outcome);

        var quiet = new ScriptedClient { Answer = "95" };
        results = await ModelCapabilityProbe.RunAsync(quiet, quiet, new[] { ProbeKind.Reasoning }, null, null, CancellationToken.None);
        Assert.Equal(ProbeOutcome.Inconclusive, results[0].Outcome);

        var opaque = new OpaqueClient();
        results = await ModelCapabilityProbe.RunAsync(opaque, opaque, new[] { ProbeKind.Reasoning }, null, null, CancellationToken.None);
        Assert.Equal(ProbeOutcome.Inconclusive, results[0].Outcome);
        Assert.Contains("non rende osservabile", results[0].Detail);
    }

    [Fact]
    public async Task Web_UsesTheExistingCheck_AndCancellingKeepsThePartialResults()
    {
        var client = new ScriptedClient { Answer = "PRONTO", Json = "{\"colore\":\"verde\",\"numero\":7}" };
        var results = await ModelCapabilityProbe.RunAsync(client, client, new[] { ProbeKind.Web }, _ => Task.FromResult<(bool?, string)>((null, "quota")), null, CancellationToken.None);
        Assert.Equal(ProbeOutcome.Inconclusive, results[0].Outcome);
        results = await ModelCapabilityProbe.RunAsync(client, client, new[] { ProbeKind.Web }, null, null, CancellationToken.None);
        Assert.Equal(ProbeOutcome.Unsupported, results[0].Outcome);

        using var stop = new CancellationTokenSource();
        var reported = new List<CapabilityProbeResult>();
        var progress = new SyncProgress(r => { reported.Add(r); stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelCapabilityProbe.RunAsync(client, client, Model, null, progress, stop.Token));
        Assert.Single(reported);
        Assert.Single(client.Requests);
    }

    [Fact]
    public void Report_ProbeWinsInconclusiveKeepsTheDeclarationAndVisionToolsAreOnlyDeclared()
    {
        var profile = new LlmProfileConfig { Type = "qwen", AuthType = "bearer", Model = "m1", BaseUrl = "https://cloud.example.test/v1" };
        var cache = ModelCapabilitiesCache.For(profile);
        var rows = CapabilityReport.Rows(cache, null, webAvailable: true);
        Assert.Equal(new[] { "Risposta testuale", "JSON strutturato", "Ragionamento", "Ricerca web", "Strumenti", "Visione" }, rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.Equal(CapabilityOrigin.NotDeclared, r.Origin));
        Assert.Equal("non dichiarata", rows[0].OutcomeText);

        cache.CheckedUtc = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        cache.Capabilities = new ModelCapabilities { Reasoning = CapabilityState.Supported, Vision = CapabilityState.Unsupported, Source = "/models" };
        cache.Record(new CapabilityProbeResult { Kind = ProbeKind.Json, Outcome = ProbeOutcome.Supported, Detail = "ok", CheckedUtc = DateTime.UtcNow });
        cache.Record(new CapabilityProbeResult { Kind = ProbeKind.Reasoning, Outcome = ProbeOutcome.Inconclusive, Detail = "nulla", CheckedUtc = DateTime.UtcNow });
        cache.Record(new CapabilityProbeResult { Kind = ProbeKind.Text, Outcome = ProbeOutcome.Inconclusive, Detail = "timeout", CheckedUtc = DateTime.UtcNow });
        cache.Record(new CapabilityProbeResult { Kind = ProbeKind.Json, Outcome = ProbeOutcome.Unsupported, Detail = "blu", CheckedUtc = DateTime.UtcNow });
        Assert.Equal(3, cache.Probes.Count);

        rows = CapabilityReport.Rows(cache, null, webAvailable: false);
        Assert.Equal((CapabilityOrigin.NotVerifiable, "non verificabile"), (rows[0].Origin, rows[0].OutcomeText));
        Assert.Equal((CapabilityOrigin.Verified, "no", "verificata dall'app"), (rows[1].Origin, rows[1].OutcomeText, rows[1].OriginText));
        Assert.Equal((CapabilityOrigin.Declared, CapabilityState.Supported), (rows[2].Origin, rows[2].State));
        Assert.Contains("Prova inconcludente: nulla", rows[2].Detail);
        Assert.Equal(cache.CheckedUtc, rows[2].CheckedUtc);
        Assert.Equal(CapabilityOrigin.NotAvailable, rows[3].Origin);
        Assert.Equal(CapabilityOrigin.NotDeclared, rows[4].Origin);
        Assert.Equal((CapabilityOrigin.Declared, "no"), (rows[5].Origin, rows[5].OutcomeText));
    }

    [Fact]
    public void Cache_AppliesOnlyToTheSameServiceAuthenticationModelAndEndpoint()
    {
        var profile = new LlmProfileConfig { Type = "qwen", AuthType = "bearer", Model = "m1", BaseUrl = "https://api.example.com/v1" };
        var cache = ModelCapabilitiesCache.For(profile);
        profile.BaseUrl = "https://api.example.com/v1/";
        Assert.True(cache.Matches(profile));
        foreach (var change in new Action<LlmProfileConfig>[] { p => p.Model = "m2", p => p.Type = "openai", p => p.AuthType = "none", p => p.BaseUrl = "https://other.example.com/v1" })
        {
            var changed = new LlmProfileConfig { Type = "qwen", AuthType = "bearer", Model = "m1", BaseUrl = "https://api.example.com/v1" };
            change(changed);
            Assert.False(cache.Matches(changed));
        }
        // Probes and declaration survive the configuration round trip; no secret is part of them.
        cache.Record(new CapabilityProbeResult { Kind = ProbeKind.Text, Outcome = ProbeOutcome.Supported, Detail = "ok", CheckedUtc = DateTime.UtcNow });
        var json = System.Text.Json.JsonSerializer.Serialize(cache);
        Assert.Contains("\"Kind\":\"Text\"", json);
        Assert.Equal(ProbeOutcome.Supported, System.Text.Json.JsonSerializer.Deserialize<ModelCapabilitiesCache>(json)!.Probe(ProbeKind.Text)!.Outcome);
    }

    [Theory]
    [InlineData("{\"reasoning_content\":\"penso\"}", true)]
    [InlineData("{\"reasoning\":\"penso\"}", true)]
    [InlineData("{\"content\":\"95\"}", false)]
    public async Task OpenAIClient_ObservesReasoningWithoutShowingIt(string delta, bool observed)
    {
        string body = "data: {\"choices\":[{\"delta\":" + delta + "}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"95\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        using var http = new HttpClient(new Responder(body)) { BaseAddress = new Uri("https://fixture.example.test/v1/") };
        using var client = new OpenAIClient(new LlmProfileConfig { Type = "qwen", Model = "synthetic", BaseUrl = "https://fixture.example.test/v1" }, secretToken: "synthetic", httpClient: http);
        var text = new List<string>();
        await foreach (var chunk in client.StreamProtocolAsync(new[] { ChatMessage.User("prova") })) text.Add(chunk);
        Assert.Equal(observed, client.ReasoningObserved);
        Assert.DoesNotContain("penso", string.Concat(text));

        // Hidden reasoning still counts when the service reports reasoning tokens.
        body = "data: {\"choices\":[{\"delta\":{\"content\":\"95\"},\"finish_reason\":\"stop\"}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":9,\"completion_tokens_details\":{\"reasoning_tokens\":7}}}\n\ndata: [DONE]\n\n";
        using var http2 = new HttpClient(new Responder(body)) { BaseAddress = new Uri("https://fixture.example.test/v1/") };
        using var hidden = new OpenAIClient(new LlmProfileConfig { Type = "openai", Model = "synthetic", BaseUrl = "https://fixture.example.test/v1" }, secretToken: "synthetic", httpClient: http2);
        await foreach (var _ in hidden.StreamProtocolAsync(new[] { ChatMessage.User("prova") })) { }
        Assert.True(hidden.ReasoningObserved);
    }

    [Fact]
    public async Task OllamaClient_ObservesThinking()
    {
        string body = "{\"message\":{\"role\":\"assistant\",\"thinking\":\"penso\",\"content\":\"\"},\"done\":false}\n{\"message\":{\"role\":\"assistant\",\"content\":\"95\"},\"done\":true,\"done_reason\":\"stop\"}\n";
        using var http = new HttpClient(new Responder(body)) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        using var client = new OllamaClient(new LlmProfileConfig { Type = "ollama", Model = "synthetic" }, httpClient: http);
        var text = new List<string>();
        await foreach (var chunk in client.StreamProtocolAsync(new[] { ChatMessage.User("prova") })) text.Add(chunk);
        Assert.True(client.ReasoningObserved);
        Assert.Equal("95", string.Concat(text));
    }

    private sealed class SyncProgress(Action<CapabilityProbeResult> report) : IProgress<CapabilityProbeResult>
    {
        public void Report(CapabilityProbeResult value) => report(value);
    }

    private sealed class Responder(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private class ScriptedClient : ILlmClient, IReasoningObserver
    {
        public string Answer = "PRONTO";
        public string Json = "{}";
        public bool Reason;
        public bool ReasonThenHang;
        public bool Hang;
        public Exception? Fail;
        public readonly List<List<ChatMessage>> Requests = new();
        public bool ReasoningObserved { get; private set; }
        public LlmTokenUsage? LastTokenUsage => null;
        public LlmCompletion? LastCompletion => null;
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            return Fail != null ? Task.FromException<string>(Fail) : Task.FromResult(Json);
        }

        public async IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            ReasoningObserved = false;
            if (Fail != null) throw Fail;
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Reason) ReasoningObserved = true;
            // A long reasoning phase: no answer text until the probe stops reading.
            if (ReasonThenHang) await Task.Delay(Timeout.Infinite, cancellationToken);
            await Task.Yield();
            if (Answer.Length > 0) yield return Answer;
        }
    }

    /// <summary>A connector that does not expose reasoning (Anthropic, Gemini in this app).</summary>
    private sealed class OpaqueClient : ILlmClient
    {
        public LlmTokenUsage? LastTokenUsage => null;
        public LlmCompletion? LastCompletion => null;
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return "95";
        }
    }
}
