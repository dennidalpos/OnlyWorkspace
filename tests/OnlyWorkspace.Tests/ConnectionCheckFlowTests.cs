using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the connection stage of «Diagnosi» outside MainConfigForm, with a scripted client and no network.</summary>
public sealed class ConnectionCheckFlowTests
{
    private sealed class Host : IConnectionCheckHost
    {
        public bool IsClosed { get; set; }
        public DiagnosisRun Diagnosis { get; } = new();
        public bool Selected = true;
        public List<(string Text, Tone Tone)> Results = new();
        public List<string> Log = new();
        public int LimitsLoaded;
        public Func<Task>? OnLimits;
        public bool IsSelected(LlmProfileConfig profile) => Selected;
        public void SetResult(string text, Tone tone, StatusLight? light = null) => Results.Add((text, tone));
        public async Task<string?> LoadLimitsAsync(LlmProfileConfig profile, CancellationToken cancellationToken)
        {
            LimitsLoaded++;
            if (OnLimits != null) await OnLimits();
            return null;
        }
        public void LogActivity(string message) => Log.Add(message);
    }

    private sealed class Client : ILlmClient, IDisposable
    {
        public Func<CancellationToken, Task<LlmHealthResult>> Health = _ => Task.FromResult(new LlmHealthResult(true, "1", new[] { "a", "b" }));
        public bool Disposed;
        public LlmTokenUsage? LastTokenUsage => null;
        public LlmCompletion? LastCompletion => null;
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => Health(cancellationToken);
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    private static LlmProfileConfig Profile() => new()
    {
        Id = "p1",
        Type = ProviderCatalog.Ollama,
        BaseUrl = "http://127.0.0.1:11434",
        Model = "qwen"
    };

    [Fact]
    public async Task Run_WhenHealthy_ReportsOkLoadsLimitsAndAllowsTheProbes()
    {
        var host = new Host();
        var client = new Client();
        var flow = new ConnectionCheckFlow(host, (_, _) => client);

        bool healthy = await flow.RunAsync(Profile(), "secret");

        Assert.True(healthy);
        Assert.Equal(1, host.LimitsLoaded);
        Assert.StartsWith("Verifica della connessione", host.Results[0].Text, StringComparison.Ordinal);
        Assert.Contains(host.Results, r => r.Tone == Tone.Success && r.Text.StartsWith("OK (", StringComparison.Ordinal) && r.Text.Contains("2 modelli", StringComparison.Ordinal));
        Assert.True(client.Disposed);
        Assert.False(host.Diagnosis.ConnectionRunning);
    }

    [Fact]
    public async Task Run_PassesTheSecretToTheClientFactory()
    {
        string? seen = null;
        var flow = new ConnectionCheckFlow(new Host(), (_, key) => { seen = key; return new Client(); });

        await flow.RunAsync(Profile(), "k-123");

        Assert.Equal("k-123", seen);
    }

    [Fact]
    public async Task Run_WhenUnhealthy_ShowsTheErrorAndSkipsLimitsAndProbes()
    {
        var host = new Host();
        var client = new Client { Health = _ => Task.FromResult(new LlmHealthResult(false, "", new List<string>(), "connessione rifiutata")) };
        var flow = new ConnectionCheckFlow(host, (_, _) => client);

        bool healthy = await flow.RunAsync(Profile(), null);

        Assert.False(healthy);
        Assert.Equal(0, host.LimitsLoaded);
        Assert.Equal(("Fallito: connessione rifiutata", Tone.Danger), host.Results[^1]);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task Run_WhenTheClientThrows_ShowsTheErrorLogsItAndFreesTheSlot()
    {
        var host = new Host();
        var client = new Client { Health = _ => throw new InvalidOperationException("boom") };
        var flow = new ConnectionCheckFlow(host, (_, _) => client);

        bool healthy = await flow.RunAsync(Profile(), null);

        Assert.False(healthy);
        Assert.StartsWith("Errore: ", host.Results[^1].Text, StringComparison.Ordinal);
        Assert.Equal(Tone.Danger, host.Results[^1].Tone);
        Assert.Contains(host.Log, l => l.StartsWith("[TEST ERROR]", StringComparison.Ordinal));
        Assert.False(host.Diagnosis.ConnectionRunning);
    }

    [Fact]
    public async Task Run_WhenCancelledByTheUser_ReportsItAsWarning()
    {
        var host = new Host();
        var client = new Client
        {
            Health = async ct =>
            {
                host.Diagnosis.CancelConnection();
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            }
        };
        var flow = new ConnectionCheckFlow(host, (_, _) => client);

        bool healthy = await flow.RunAsync(Profile(), null);

        Assert.False(healthy);
        Assert.Equal(("Diagnosi annullata.", Tone.Warning), host.Results[^1]);
        Assert.False(host.Diagnosis.ConnectionRunning);
    }

    [Fact]
    public async Task Run_WhenTheProfileIsNoLongerSelectedAfterTheAnswer_DropsTheOutcome()
    {
        var host = new Host();
        var client = new Client
        {
            Health = _ => { host.Selected = false; return Task.FromResult(new LlmHealthResult(true, "1", new[] { "a" })); }
        };
        var flow = new ConnectionCheckFlow(host, (_, _) => client);

        bool healthy = await flow.RunAsync(Profile(), null);

        Assert.False(healthy);
        Assert.Equal(0, host.LimitsLoaded);
        Assert.Single(host.Results);
    }

    [Fact]
    public async Task Run_WhenTheProfileChangesWhileLimitsLoad_DoesNotAllowTheProbes()
    {
        var host = new Host();
        host.OnLimits = () => { host.Selected = false; return Task.CompletedTask; };
        var flow = new ConnectionCheckFlow(host, (_, _) => new Client());

        bool healthy = await flow.RunAsync(Profile(), null);

        Assert.False(healthy);
        Assert.Equal(1, host.LimitsLoaded);
    }

    [Fact]
    public async Task Run_WhenTheWindowIsClosedMeanwhile_ReportsAndLogsNothingMore()
    {
        var host = new Host();
        var client = new Client { Health = _ => { host.IsClosed = true; throw new InvalidOperationException("late"); } };
        var flow = new ConnectionCheckFlow(host, (_, _) => client);

        bool healthy = await flow.RunAsync(Profile(), null);

        Assert.False(healthy);
        Assert.Single(host.Results);
        Assert.Empty(host.Log);
    }

    [Fact]
    public async Task Run_WhenAnotherCheckAlreadyRuns_StillFreesOnlyItsOwnSlot()
    {
        var host = new Host();
        var flow = new ConnectionCheckFlow(host, (_, _) => new Client());

        await flow.RunAsync(Profile(), null);
        await flow.RunAsync(Profile(), null);

        Assert.False(host.Diagnosis.Busy);
    }
}
