using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>What the capability probes need from the configuration window (#177).</summary>
internal interface IProbeHost
{
    /// <summary>True while the window fills its controls from a profile.</summary>
    bool IsLoading { get; }
    /// <summary>True once the window was closed: late answers are dropped.</summary>
    bool IsClosed { get; }
    DiagnosisRun Diagnosis { get; }
    /// <summary>True while this profile is the one selected in the list.</summary>
    bool IsSelected(LlmProfileConfig profile);
    /// <summary>Service, authentication, endpoint and model: the outcomes still apply while it is unchanged.</summary>
    string CapabilityKey(LlmProfileConfig profile);
    string? SecretFor(string profileId);
    string? WebSecretFor(string profileId);
    void SetStatus(string text, Tone tone, StatusLight? light = null);
    /// <summary>Keeps one result in the profile's cache, marks the window dirty and redraws the capability rows.</summary>
    void RecordResult(LlmProfileConfig profile, CapabilityProbeResult result);
    void ShowWebSupport(LlmProfileConfig profile, ProbeOutcome outcome, string detail);
    void LogActivity(string message);
}

/// <summary>
/// The probing stage of «Diagnosi» (#177), after the connection answered. The results of a run whose profile changed
/// meanwhile (selection, model, endpoint, key) are dropped: a late answer never paints another configuration.
/// </summary>
internal sealed class ProbeFlow
{
    /// <summary>Runs the probes; replaceable so a test needs no network.</summary>
    internal delegate Task ProbeRunner(ILlmClient client, ILlmClient reasoningClient, IEnumerable<ProbeKind> kinds,
        Func<CancellationToken, Task<(bool?, string)>>? web, IProgress<CapabilityProbeResult> progress, CancellationToken cancellationToken);

    private readonly IProbeHost _host;
    private readonly Func<LlmProfileConfig, string?, ILlmClient> _createClient;
    private readonly ProbeRunner _run;
    private readonly Func<LlmProfileConfig, string?, string?, CancellationToken, Task<(bool?, string)>> _webProbe;

    public ProbeFlow(IProbeHost host, Func<LlmProfileConfig, string?, ILlmClient>? createClient = null, ProbeRunner? run = null,
        Func<LlmProfileConfig, string?, string?, CancellationToken, Task<(bool?, string)>>? webProbe = null)
    {
        _host = host;
        _createClient = createClient ?? ((p, secret) => LlmClientFactory.CreateClient(p, secret));
        _run = run ?? ((c, r, k, w, pr, ct) => ModelCapabilityProbe.RunAsync(c, r, k, w, pr, ct));
        _webProbe = webProbe ?? WebProbeAsync;
    }

    public async Task RunAsync(LlmProfileConfig p)
    {
        if (_host.IsLoading || _host.Diagnosis.ProbeRunning) return;
        if (string.IsNullOrWhiteSpace(p.Model) || string.IsNullOrWhiteSpace(p.BaseUrl))
        {
            _host.SetStatus("Inserisci indirizzo e modello prima della diagnosi.", Tone.Warning);
            return;
        }

        string key = _host.CapabilityKey(p);
        string? secret = _host.SecretFor(p.Id);
        string? webSecret = _host.WebSecretFor(p.Id);
        bool webAvailable = ProviderCatalog.Get(p.Type).WebSearch != WebSearchMode.None;
        var kinds = new List<ProbeKind> { ProbeKind.Text, ProbeKind.Json, ProbeKind.Reasoning };
        if (webAvailable) kinds.Add(ProbeKind.Web);

        bool Current() => !_host.IsClosed && _host.IsSelected(p) && _host.CapabilityKey(p) == key && secret == _host.SecretFor(p.Id);

        var run = new CancellationTokenSource();
        _host.Diagnosis.BeginProbe(run);
        int done = 0;
        var progress = new Progress<CapabilityProbeResult>(result =>
        {
            done++;
            if (!Current()) return;
            _host.RecordResult(p, result);
            if (result.Kind == ProbeKind.Web) _host.ShowWebSupport(p, result.Outcome, result.Detail);
            // Posted: a callback may land after the run ended, and must not overwrite its final status.
            if (_host.Diagnosis.IsProbe(run)) _host.SetStatus($"Prova {done} di {kinds.Count}: {ProbeName(result.Kind)} {OutcomeWord(result.Outcome)}.", Tone.Warning, StatusLight.Working);
        });

        var plain = Probing(p, reasoning: false);
        var thinking = Probing(p, reasoning: true);
        ILlmClient? client = null, reasoningClient = null;
        try
        {
            client = _createClient(plain, secret);
            reasoningClient = _createClient(thinking, secret);
            _host.SetStatus($"Prove in corso (0 di {kinds.Count})…", Tone.Warning, StatusLight.Working);
            Func<CancellationToken, Task<(bool?, string)>>? web = webAvailable ? ct => _webProbe(p, secret, webSecret, ct) : null;
            await _run(client, reasoningClient, kinds, web, progress, run.Token);
            if (Current())
            {
                _host.SetStatus("Diagnosi completata. Gli esiti si salvano con Salva.", Tone.Success);
                _host.LogActivity($"[CAPACITÀ] Profilo '{p.Id}', modello '{p.Model}': diagnosi completata.");
            }
        }
        catch (OperationCanceledException)
        {
            if (Current()) _host.SetStatus($"Diagnosi interrotta: {done} di {kinds.Count} esiti conservati.", Tone.Warning);
        }
        catch (Exception ex)
        {
            if (Current()) _host.SetStatus("Diagnosi non avviata: " + ErrorText.For(ex), Tone.Danger);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            (reasoningClient as IDisposable)?.Dispose();
            _host.Diagnosis.EndProbe(run);
            run.Dispose();
        }
    }

    internal static string ProbeName(ProbeKind kind) => kind switch
    {
        ProbeKind.Text => "risposta testuale",
        ProbeKind.Json => "JSON strutturato",
        ProbeKind.Reasoning => "ragionamento",
        _ => "Ricerca web"
    };

    internal static string OutcomeWord(ProbeOutcome outcome) => outcome switch
    {
        ProbeOutcome.Supported => "riuscita",
        ProbeOutcome.Unsupported => "non supportata",
        _ => "inconcludente"
    };

    /// <summary>
    /// The profile as configured, without the cached capabilities; the reasoning probe allows thinking, the
    /// others do not, so they stay short. Timeouts are bounded by the probe's own 60 seconds.
    /// </summary>
    internal static LlmProfileConfig Probing(LlmProfileConfig p, bool reasoning)
    {
        var copy = JsonSerializer.Deserialize<LlmProfileConfig>(JsonSerializer.Serialize(p))!;
        copy.ModelCapabilities = null;
        copy.DisableThinking = !reasoning;
        // Same temperature as real use: the profile's for text and reasoning, 0 for the structured probe (set by the client).
        return copy;
    }

    /// <summary>The real web check, with only what it needs of the profile (no exposure, actions or cached capabilities).</summary>
    internal static async Task<(bool?, string)> WebProbeAsync(LlmProfileConfig profile, string? secret, string? webSecret, CancellationToken cancellationToken)
    {
        var check = new LlmProfileConfig
        {
            Type = profile.Type,
            BaseUrl = profile.BaseUrl,
            Model = profile.Model,
            AuthType = profile.AuthType,
            External = profile.External,
            UseSystemProxy = profile.UseSystemProxy
        };
        using var search = WebSearchService.ForCheck(check, secret, webSecret);
        var support = await search.CheckSupportAsync(cancellationToken);
        return support switch
        {
            WebSearchSupport.Supported => (true, "Ricerca di prova eseguita dal servizio."),
            WebSearchSupport.Unsupported => (false, "Il modello ha risposto senza cercare sul web."),
            _ => (null, "Verifica non conclusa: " + (search.LastCheckError ?? "nessuna ricerca eseguita"))
        };
    }
}
