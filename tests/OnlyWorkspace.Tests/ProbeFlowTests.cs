using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the capability probes of «Diagnosi» outside MainConfigForm, with scripted probes and no network.</summary>
public sealed class ProbeFlowTests
{
    private sealed class Host : IProbeHost
    {
        public bool IsLoading { get; set; }
        public bool IsClosed { get; set; }
        public DiagnosisRun Diagnosis { get; } = new();
        public bool Selected = true;
        public string Key = "k1";
        public string? Secret = "s";
        public List<(string Text, Tone Tone)> Status = new();
        public List<CapabilityProbeResult> Recorded = new();
        public List<ProbeOutcome> WebShown = new();
        public List<string> Log = new();
        public bool IsSelected(LlmProfileConfig profile) => Selected;
        public string CapabilityKey(LlmProfileConfig profile) => Key;
        public string? SecretFor(string profileId) => Secret;
        public string? WebSecretFor(string profileId) => null;
        public void SetStatus(string text, Tone tone, StatusLight? light = null) => Status.Add((text, tone));
        public void RecordResult(LlmProfileConfig profile, CapabilityProbeResult result) => Recorded.Add(result);
        public void ShowWebSupport(LlmProfileConfig profile, ProbeOutcome outcome, string detail) => WebShown.Add(outcome);
        public void LogActivity(string message) => Log.Add(message);
    }

    private sealed class FakeClient : ILlmClient, IDisposable
    {
        public bool Disposed;
        public LlmTokenUsage? LastTokenUsage => null;
        public LlmCompletion? LastCompletion => null;
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    private static LlmProfileConfig Profile() => new()
    {
        Id = "p1", Type = ProviderCatalog.Ollama, BaseUrl = "http://127.0.0.1:11434", Model = "qwen"
    };

    private static CapabilityProbeResult Result(ProbeKind kind, ProbeOutcome outcome = ProbeOutcome.Supported) =>
        new() { Kind = kind, Outcome = outcome, Detail = "d", CheckedUtc = DateTime.UtcNow };

    private static ProbeFlow.ProbeRunner Script(params CapabilityProbeResult[] results) => (c, r, k, w, progress, ct) =>
    {
        foreach (var result in results) progress.Report(result);
        return Task.CompletedTask;
    };

    /// <summary>Progress&lt;T&gt; posts to the thread pool without a synchronization context: let the callbacks land.</summary>
    private static async Task SettleAsync()
    {
        for (int i = 0; i < 20; i++) await Task.Delay(10);
    }

    [Fact]
    public async Task Run_WithoutModelOrAddress_AsksForThemAndRunsNothing()
    {
        var host = new Host();
        var p = Profile();
        p.Model = " ";
        int clients = 0;
        var flow = new ProbeFlow(host, (_, _) => { clients++; return new FakeClient(); }, Script());

        await flow.RunAsync(p);

        Assert.Equal(0, clients);
        Assert.Equal(Tone.Warning, Assert.Single(host.Status).Tone);
        Assert.False(host.Diagnosis.ProbeRunning);
    }

    [Fact]
    public async Task Run_WhileLoadingOrAlreadyRunning_DoesNothing()
    {
        var host = new Host { IsLoading = true };
        var flow = new ProbeFlow(host, (_, _) => new FakeClient(), Script(Result(ProbeKind.Text)));
        await flow.RunAsync(Profile());
        Assert.Empty(host.Status);

        host.IsLoading = false;
        using var running = new CancellationTokenSource();
        host.Diagnosis.BeginProbe(running);
        await flow.RunAsync(Profile());
        Assert.Empty(host.Status);
    }

    [Fact]
    public async Task Run_Completes_RecordsEachResultAndReportsSuccess()
    {
        var host = new Host();
        var disposed = new List<FakeClient>();
        var flow = new ProbeFlow(host, (_, _) => { var c = new FakeClient(); disposed.Add(c); return c; },
            Script(Result(ProbeKind.Text), Result(ProbeKind.Json), Result(ProbeKind.Reasoning), Result(ProbeKind.Web, ProbeOutcome.Unsupported)));

        await flow.RunAsync(Profile());
        await SettleAsync();

        Assert.Equal(4, host.Recorded.Count);
        Assert.Equal(new[] { ProbeOutcome.Unsupported }, host.WebShown);
        Assert.Contains(host.Status, s => s.Text.StartsWith("Diagnosi completata", StringComparison.Ordinal) && s.Tone == Tone.Success);
        Assert.Contains(host.Log, l => l.Contains("diagnosi completata", StringComparison.Ordinal));
        Assert.All(disposed, c => Assert.True(c.Disposed));
        Assert.Equal(2, disposed.Count);
        Assert.False(host.Diagnosis.ProbeRunning);
    }

    [Fact]
    public async Task Run_WhenProfileChangesMeanwhile_DropsLateResultsAndStatus()
    {
        var host = new Host();
        var flow = new ProbeFlow(host, (_, _) => new FakeClient(), (c, r, k, w, progress, ct) =>
        {
            host.Key = "k2";
            progress.Report(Result(ProbeKind.Text));
            return Task.CompletedTask;
        });

        await flow.RunAsync(Profile());
        await SettleAsync();

        Assert.Empty(host.Recorded);
        Assert.DoesNotContain(host.Status, s => s.Text.StartsWith("Diagnosi completata", StringComparison.Ordinal));
        Assert.False(host.Diagnosis.ProbeRunning);
    }

    [Fact]
    public async Task Run_WhenSecretChangesMeanwhile_DropsTheOutcome()
    {
        var host = new Host();
        var flow = new ProbeFlow(host, (_, _) => new FakeClient(), (c, r, k, w, progress, ct) => { host.Secret = "other"; return Task.CompletedTask; });

        await flow.RunAsync(Profile());

        Assert.DoesNotContain(host.Status, s => s.Tone == Tone.Success);
    }

    [Fact]
    public async Task Run_WhenCancelled_KeepsTheCountOfResultsAlreadyDone()
    {
        var host = new Host();
        var flow = new ProbeFlow(host, (_, _) => new FakeClient(), (c, r, k, w, progress, ct) =>
        {
            progress.Report(Result(ProbeKind.Text));
            throw new OperationCanceledException();
        });

        await flow.RunAsync(Profile());
        await SettleAsync();

        Assert.Single(host.Recorded);
        var last = host.Status[^1];
        Assert.StartsWith("Diagnosi interrotta", last.Text, StringComparison.Ordinal);
        Assert.Equal(Tone.Warning, last.Tone);
        Assert.False(host.Diagnosis.ProbeRunning);
    }

    [Fact]
    public async Task Run_WhenTheClientCannotBeCreated_ReportsAnErrorAndFreesTheSlot()
    {
        var host = new Host();
        var flow = new ProbeFlow(host, (_, _) => throw new InvalidOperationException("boom"), Script());

        await flow.RunAsync(Profile());

        var last = host.Status[^1];
        Assert.StartsWith("Diagnosi non avviata", last.Text, StringComparison.Ordinal);
        Assert.Equal(Tone.Danger, last.Tone);
        Assert.False(host.Diagnosis.ProbeRunning);
    }

    [Fact]
    public async Task Run_WhenTheWindowIsClosed_ReportsNothingAtTheEnd()
    {
        var host = new Host();
        var flow = new ProbeFlow(host, (_, _) => new FakeClient(), (c, r, k, w, progress, ct) => { host.IsClosed = true; return Task.CompletedTask; });

        await flow.RunAsync(Profile());

        Assert.DoesNotContain(host.Status, s => s.Tone == Tone.Success);
        Assert.Empty(host.Log);
    }

    [Fact]
    public async Task Run_PassesAWebProbeOnlyWhenTheServiceHasWebSearch()
    {
        var host = new Host();
        Func<CancellationToken, Task<(bool?, string)>>? passed = null;
        var flow = new ProbeFlow(host, (_, _) => new FakeClient(), (c, r, k, w, progress, ct) => { passed = w; return Task.CompletedTask; },
            (p, s, ws, ct) => Task.FromResult<(bool?, string)>((true, "ok")));

        await flow.RunAsync(Profile());

        Assert.NotNull(passed);
        Assert.Equal((true, "ok"), await passed!(CancellationToken.None));
    }

    [Fact]
    public void Probing_CopiesTheProfileWithoutCachedCapabilitiesAndTogglesThinking()
    {
        var p = Profile();
        p.ModelCapabilities = ModelCapabilitiesCache.For(p);

        var plain = ProbeFlow.Probing(p, reasoning: false);
        var thinking = ProbeFlow.Probing(p, reasoning: true);

        Assert.Null(plain.ModelCapabilities);
        Assert.True(plain.DisableThinking);
        Assert.False(thinking.DisableThinking);
        Assert.NotNull(p.ModelCapabilities);
        Assert.NotSame(p, plain);
    }

    [Theory]
    [InlineData(ProbeKind.Text, "risposta testuale")]
    [InlineData(ProbeKind.Json, "JSON strutturato")]
    [InlineData(ProbeKind.Reasoning, "ragionamento")]
    [InlineData(ProbeKind.Web, "Ricerca web")]
    public void ProbeName_NamesEachKind(ProbeKind kind, string expected) => Assert.Equal(expected, ProbeFlow.ProbeName(kind));

    [Theory]
    [InlineData(ProbeOutcome.Supported, "riuscita")]
    [InlineData(ProbeOutcome.Unsupported, "non supportata")]
    [InlineData(ProbeOutcome.Inconclusive, "inconcludente")]
    public void OutcomeWord_NamesEachOutcome(ProbeOutcome outcome, string expected) => Assert.Equal(expected, ProbeFlow.OutcomeWord(outcome));
}
