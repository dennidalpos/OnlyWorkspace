using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>What the connection check of «Diagnosi» needs from the configuration window (#177).</summary>
internal interface IConnectionCheckHost
{
    /// <summary>True once the window was closed: late answers are dropped.</summary>
    bool IsClosed { get; }
    DiagnosisRun Diagnosis { get; }
    /// <summary>True while this profile is the one selected in the list.</summary>
    bool IsSelected(LlmProfileConfig profile);
    void SetResult(string text, Tone tone, StatusLight? light = null);
    /// <summary>Reads limits and capabilities after a healthy answer; the reason when nothing was read.</summary>
    Task<string?> LoadLimitsAsync(LlmProfileConfig profile, CancellationToken cancellationToken);
    void LogActivity(string message);
}

/// <summary>
/// The connection stage of «Diagnosi» (#177): one health request bounded to 10 seconds, then the limits of the model. The
/// outcome of a run whose profile changed meanwhile (selection, identity) is dropped.
/// </summary>
internal sealed class ConnectionCheckFlow
{
    private readonly IConnectionCheckHost _host;
    private readonly Func<LlmProfileConfig, string?, ILlmClient> _createClient;

    public ConnectionCheckFlow(IConnectionCheckHost host, Func<LlmProfileConfig, string?, ILlmClient>? createClient = null)
    {
        _host = host;
        _createClient = createClient ?? ((profile, key) => LlmClientFactory.CreateClient(profile, secret: key));
    }

    /// <returns>True when the service answered healthy and the profile is still the one shown: the capability probes may follow.</returns>
    public async Task<bool> RunAsync(LlmProfileConfig p, string? secret)
    {
        string identity = ConversationPolicy.Key(p);
        using var run = new InteractiveRequest();
        _host.Diagnosis.BeginConnection(run);
        _host.SetResult("Verifica della connessione in corso (massimo 10 secondi)…", Tone.Warning, StatusLight.Working);
        bool healthy = false;
        bool Current() => !_host.IsClosed && _host.IsSelected(p) && identity == ConversationPolicy.Key(p);
        try
        {
            var client = _createClient(p, secret);
            try
            {
                var sw = Stopwatch.StartNew();
                var health = await client.CheckHealthAsync(run.Token);
                run.Token.ThrowIfCancellationRequested();
                if (!Current()) return false;
                if (health.IsHealthy)
                {
                    _host.SetResult($"OK ({sw.ElapsedMilliseconds} ms, {health.AvailableModels.Count} modelli)", Tone.Success);
                    await _host.LoadLimitsAsync(p, run.Token);
                    run.Token.ThrowIfCancellationRequested();
                    healthy = Current();
                }
                else _host.SetResult("Fallito: " + health.ErrorMessage, Tone.Danger);
            }
            finally { (client as IDisposable)?.Dispose(); }
        }
        catch (OperationCanceledException)
        {
            if (Current()) _host.SetResult(run.TimedOut ? "Tempo massimo di 10 secondi superato." : "Diagnosi annullata.", Tone.Warning);
        }
        catch (Exception ex)
        {
            if (Current()) _host.SetResult("Errore: " + ErrorText.For(ex), Tone.Danger);
            if (!_host.IsClosed) _host.LogActivity("[TEST ERROR] " + ex.Message);
        }
        finally { _host.Diagnosis.EndConnection(run); }
        return healthy;
    }
}
