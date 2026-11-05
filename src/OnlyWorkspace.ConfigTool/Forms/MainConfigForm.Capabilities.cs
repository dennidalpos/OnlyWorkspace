using OnlyWorkspace.Core.Archive;
using System;
using OnlyWorkspace.ConfigTool.Services;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>
/// «Avanzate» of a profile: each capability with outcome, provenance and date, «Aggiorna capacità»
/// (what the service declares, no generation) and «Prova funzioni» (short requests with synthetic data, may cost
/// tokens). Results are saved with the normal Salva and dropped when service, model, endpoint or key change.
/// </summary>
public sealed partial class MainConfigForm
{
    private TableLayoutPanel _capabilityTable = null!;
    private FlatButton _btnRefreshCapabilities = null!;
    private FlatButton _btnProbe = null!;
    private FlatButton _btnProbeCancel = null!;
    private ThemedCheckBox _chkProbeWeb = null!;
    private OperationalStatusLabel _lblProbeStatus = null!;
    private CancellationTokenSource? _probeRun;
    private int _capabilityOriginWidth;

    private IEnumerable<Control> CapabilityOriginCells() =>
        _capabilityTable.Controls.Cast<Control>().Where(c => _capabilityTable.GetColumn(c) == 2);

    private const string ProbeCost =
        "«Prova funzioni» invia al servizio brevi richieste con dati inventati (nessun dato di Outlook): risposta testuale, JSON strutturato " +
        "e ragionamento, una alla volta, al massimo 60 secondi ciascuna e senza ripetizioni. Con un servizio a pagamento consuma pochi token; " +
        "la Ricerca web, se selezionata, esegue una vera ricerca che il fornitore può addebitare.";

    private const string ProbeIdle = "Prove non ancora eseguite per questo profilo.";

    private void BuildCapabilityRows(TableLayoutPanel grid)
    {
        var intro = new SelectableLabel
        {
            AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 6, 3, 8),
            Text = "«Scegli modello…» elenca i modelli senza provare nulla. «Verifica connessione» controlla indirizzo, chiave e modello. " +
                   "«Aggiorna capacità» rilegge ciò che il servizio dichiara, senza costi. «Prova funzioni» verifica davvero le funzioni usate dall'app."
        };
        Theme.SetTone(intro, Tone.Muted);
        AddWide(grid, intro, "Differenza fra i comandi del profilo.",
            lead: "Cosa fanno Scegli modello, Verifica connessione, Aggiorna capacità e Prova funzioni.");

        _capabilityTable = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(3, 0, 3, 6) };
        for (int i = 0; i < 3; i++) _capabilityTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddWide(grid, _capabilityTable,
            "Per ogni funzione: esito (sì, no, non dichiarata, non verificabile) e origine (dichiarata dal servizio, verificata con una prova o prova inconcludente). " +
            "Visione e Strumenti sono solo dichiarati: l'app non li usa né li prova.");

        _btnRefreshCapabilities = SecondaryButton("Aggiorna capacità");
        _btnRefreshCapabilities.Click += async (s, e) => await RefreshDeclaredCapabilitiesAsync();
        Tip(_btnRefreshCapabilities, "Rilegge limiti e capacità dichiarati, senza generazione né costi. Alcuni servizi non li pubblicano. Salva conserva gli esiti.");
        _btnProbe = SecondaryButton("Prova funzioni");
        _btnProbe.Click += async (s, e) => await RunProbesAsync();
        Tip(_btnProbe, ProbeCost);
        _btnProbeCancel = SecondaryButton("Annulla");
        _btnProbeCancel.Enabled = false;
        _btnProbeCancel.Click += (s, e) => { _probeRun?.Cancel(); _capabilityRefresh?.Cancel(); };
        Tip(_btnProbeCancel, "Ferma le prove: gli esiti già ottenuti restano.");
        foreach (var b in new[] { _btnRefreshCapabilities, _btnProbe, _btnProbeCancel }) b.Margin = new Padding(0, 0, 6, 6);
        var commands = Inline(_btnRefreshCapabilities, _btnProbe, _btnProbeCancel);
        commands.WrapContents = true;
        commands.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        AddWide(grid, commands, "Aggiorna capacità, Prova funzioni e Annulla.");

        // The cost note sits behind a «?» beside the checkbox; the status label below stays visible, short, and shows progress.
        _chkProbeWeb = new ThemedCheckBox { Text = "Prova anche la Ricerca web", AutoSize = true };
        Tip(_chkProbeWeb, "Aggiunge una ricerca sintetica, senza dati di Outlook, con possibili addebiti. Spento per impostazione predefinita.");
        var probeWebRow = Inline(_chkProbeWeb, ContextHelp.Button(this, "Costo delle prove", () => ProbeCost, _tips));
        int probeWebIndex = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(probeWebRow, 0, probeWebIndex);
        grid.SetColumnSpan(probeWebRow, grid.ColumnCount);
        _lblProbeStatus = AddWide(grid, new OperationalStatusLabel { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 4, 3, 6), Text = ProbeIdle },
            "Avanzamento ed esito delle prove; i risultati si salvano con Salva.");
        Theme.SetTone(_lblProbeStatus, Tone.Muted);
        // Wrapped at the visible width of the section, like the exposure note: a fixed width was cut off at 100%.
        grid.Layout += (_, _) =>
        {
            int width = Math.Max(Ui.Px(this, 160), grid.ClientSize.Width - Ui.Px(this, 32));
            if (commands.MaximumSize.Width != width) commands.MaximumSize = new Size(width, 0);
            foreach (var label in new[] { intro, _lblProbeStatus })
                if (label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
            // Provenance and date wrap in what the first two columns leave: at 150% the date was cut off.
            int[] columns = _capabilityTable.GetColumnWidths();
            if (columns.Length < 3) return;
            int origin = Math.Max(Ui.Px(this, 120), width - columns[0] - columns[1] - _capabilityTable.Margin.Horizontal - Ui.Px(this, 16));
            if (origin == _capabilityOriginWidth) return;
            _capabilityOriginWidth = origin;
            foreach (var label in CapabilityOriginCells()) label.MaximumSize = new Size(origin, 0);
        };
    }

    /// <summary>Same service, authentication, endpoint and model: the outcomes still apply.</summary>
    private static string CapabilityKey(LlmProfileConfig p) => LimitsKey(p) + "|" + p.AuthType;

    /// <summary>Cache of the selected profile, created for its current identity when absent or stale (declarations of the same model kept).</summary>
    private static ModelCapabilitiesCache CacheFor(LlmProfileConfig p) =>
        p.ModelCapabilities is { } cache && cache.Matches(p) ? cache : p.ModelCapabilities = ModelCapabilitiesCache.For(p);

    private void RefreshCapabilityRows()
    {
        if (_capabilityTable == null) return;
        var item = _lstProfiles.SelectedItem as ProfileItem;
        var cache = item?.Profile.ModelCapabilities is { } saved && saved.Matches(item.Profile) ? saved : null;
        var rows = CapabilityReport.Rows(cache, CurrentCapabilities(), CurrentCaps().WebSearch != WebSearchMode.None);

        _capabilityTable.SuspendLayout();
        try
        {
            foreach (Control c in _capabilityTable.Controls.Cast<Control>().ToList()) c.Dispose();
            _capabilityTable.Controls.Clear();
            _capabilityTable.RowStyles.Clear();
            _capabilityTable.RowCount = 0;
            AddCapabilityCells("Funzione", "Esito", "Provenienza e data", null, header: true);
            foreach (var row in rows)
            {
                string when = row.CheckedUtc.HasValue ? " · " + row.CheckedUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : string.Empty;
                string symbol = row.Origin is CapabilityOrigin.NotDeclared or CapabilityOrigin.NotVerifiable ? "? "
                    : row.State == CapabilityState.Supported ? "✓ " : row.State == CapabilityState.Unsupported ? "✕ " : "? ";
                AddCapabilityCells(row.Name, symbol + row.OutcomeText, row.OriginText + when, row, header: false);
            }
        }
        finally
        {
            _capabilityTable.ResumeLayout(true);
        }
    }

    private void AddCapabilityCells(string name, string outcome, string origin, CapabilityRow? row, bool header)
    {
        int r = _capabilityTable.RowCount++;
        _capabilityTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var cells = new[] { name, outcome, origin }.Select(t => new SelectableLabel { Text = t, AutoSize = true, Margin = new Padding(0, 2, 16, 2) }).ToArray();
        if (_capabilityOriginWidth > 0) cells[2].MaximumSize = new Size(_capabilityOriginWidth, 0);
        for (int c = 0; c < cells.Length; c++)
        {
            if (header) Ui.SetFont(cells[c], 9F, family: "Segoe UI Semibold");
            _capabilityTable.Controls.Add(cells[c], c, r);
        }
        if (row == null) return;
        // Tone follows the outcome; the symbol and the words carry it too, never the colour alone.
        Theme.SetTone(cells[1], row.Origin is CapabilityOrigin.NotDeclared or CapabilityOrigin.NotVerifiable or CapabilityOrigin.NotAvailable ? Tone.Muted
            : row.State == CapabilityState.Supported ? Tone.Success : Tone.Muted);
        string tip = row.Name + ": " + row.OutcomeText + ", " + row.OriginText + (row.Detail.Length > 0 ? ".\n" + row.Detail : ".");
        foreach (var cell in cells)
        {
            cell.AccessibleName = tip;
            Tip(cell, tip);
        }
    }

    /// <summary>«Aggiorna capacità»: the service's declaration, read again and reported in words.</summary>
    private InteractiveRequest? _capabilityRefresh;

    private async Task RefreshDeclaredCapabilitiesAsync()
    {
        if (_lstProfiles.SelectedItem is not ProfileItem item || _isLoading || _probeRun != null || _capabilityRefresh != null) return;
        UpdateSelectedProfileFromUi();
        var p = item.Profile;
        if (ProviderCatalog.Get(p.Type).Capabilities == CapabilitySource.None)
        {
            SetProbeStatus("Questo servizio non pubblica le capacità dei modelli: usa «Prova funzioni» per verificarle.", Tone.Warning);
            return;
        }
        using var run = new InteractiveRequest();
        _capabilityRefresh = run;
        string identity = CapabilityKey(p);
        SetProbeRunning(true);
        SetProbeStatus("Lettura delle capacità dichiarate (massimo 10 secondi)…", Tone.Warning, StatusLight.Working);
        try
        {
            string? problem = await LoadModelLimitsCoreAsync(p, run.Token);
            run.Token.ThrowIfCancellationRequested();
            if (!IsDisposed && ReferenceEquals(_lstProfiles.SelectedItem, item) && CapabilityKey(p) == identity)
                SetProbeStatus(problem ?? "Capacità dichiarate rilette; si salvano con Salva.", problem == null ? Tone.Success : Tone.Danger);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && ReferenceEquals(_lstProfiles.SelectedItem, item) && CapabilityKey(p) == identity)
                SetProbeStatus(run.TimedOut ? "Tempo massimo di 10 secondi superato." : "Aggiornamento annullato.", Tone.Warning);
        }
        finally { _capabilityRefresh = null; if (!IsDisposed) SetProbeRunning(false); }
    }

    private void SetProbeStatus(string text, Tone tone, StatusLight? light = null)
    {
        _lblProbeStatus.SetState(light ?? StateForTone(tone));
        _lblProbeStatus.Text = text;
        Theme.SetTone(_lblProbeStatus, tone);
        Tip(_lblProbeStatus, text);
    }

    private void SetProbeRunning(bool running)
    {
        void Set(Control c, bool enabled)
        {
            if (_isLocked) _lockedControls[c] = enabled;
            else c.Enabled = enabled;
        }
        Set(_btnProbe, !running);
        Set(_btnRefreshCapabilities, !running);
        Set(_chkProbeWeb, !running);
        _btnProbeCancel.Enabled = running;
    }

    /// <summary>
    /// «Prova funzioni». The results of a run whose profile changed meanwhile (selection, model, endpoint, key)
    /// are dropped: a late answer never paints another configuration.
    /// </summary>
    private async Task RunProbesAsync()
    {
        if (_lstProfiles.SelectedItem is not ProfileItem item || _isLoading || _probeRun != null || _capabilityRefresh != null) return;
        UpdateSelectedProfileFromUi();
        var p = item.Profile;
        if (string.IsNullOrWhiteSpace(p.Model) || string.IsNullOrWhiteSpace(p.BaseUrl))
        {
            SetProbeStatus("Inserisci indirizzo e modello prima delle prove.", Tone.Warning);
            return;
        }

        string key = CapabilityKey(p);
        string? secret = _inMemorySecrets.TryGetValue(p.Id, out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        string? webSecret = _inMemoryWebSecrets.TryGetValue(p.Id, out var w) && !string.IsNullOrWhiteSpace(w) ? w : null;
        bool webAvailable = ProviderCatalog.Get(p.Type).WebSearch != WebSearchMode.None;
        var kinds = new List<ProbeKind> { ProbeKind.Text, ProbeKind.Json, ProbeKind.Reasoning };
        if (webAvailable && _chkProbeWeb.Checked) kinds.Add(ProbeKind.Web);

        bool Current() => !IsDisposed && ReferenceEquals(_lstProfiles.SelectedItem, item) && CapabilityKey(p) == key &&
                          secret == (_inMemorySecrets.TryGetValue(p.Id, out var now) && !string.IsNullOrWhiteSpace(now) ? now : null);

        var run = new CancellationTokenSource();
        _probeRun = run;
        SetProbeRunning(true);
        int done = 0;
        var progress = new Progress<CapabilityProbeResult>(result =>
        {
            done++;
            if (!Current()) return;
            CacheFor(p).Record(result);
            MarkDirty();
            RefreshCapabilityRows();
            if (result.Kind == ProbeKind.Web) ShowWebSupport(p, result.Outcome, result.Detail);
            // Posted: a callback may land after the run ended, and must not overwrite its final status.
            if (_probeRun == run) SetProbeStatus($"Prova {done} di {kinds.Count} completata: {ProbeName(result.Kind)} {OutcomeWord(result.Outcome)}.", Tone.Warning, StatusLight.Working);
        });

        var plain = Probing(p, reasoning: false);
        var thinking = Probing(p, reasoning: true);
        ILlmClient? client = null, reasoningClient = null;
        try
        {
            client = LlmClientFactory.CreateClient(plain, secret);
            reasoningClient = LlmClientFactory.CreateClient(thinking, secret);
            SetProbeStatus($"Prove in corso (0 di {kinds.Count})…", Tone.Warning, StatusLight.Working);
            Func<CancellationToken, Task<(bool?, string)>>? web = webAvailable ? ct => WebProbeAsync(p, secret, webSecret, ct) : null;
            await ModelCapabilityProbe.RunAsync(client, reasoningClient, kinds, web, progress, run.Token);
            if (Current())
            {
                SetProbeStatus("Prove completate. Gli esiti si salvano con Salva.", Tone.Success);
                LogActivity($"[CAPACITÀ] Profilo '{p.Id}', modello '{p.Model}': prove completate.");
            }
        }
        catch (OperationCanceledException)
        {
            if (Current()) SetProbeStatus($"Prove interrotte: {done} di {kinds.Count} esiti conservati.", Tone.Warning);
        }
        catch (Exception ex)
        {
            if (Current()) SetProbeStatus("Prove non avviate: " + ErrorText.For(ex), Tone.Danger);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            (reasoningClient as IDisposable)?.Dispose();
            if (_probeRun == run) _probeRun = null;
            run.Dispose();
            if (!IsDisposed) SetProbeRunning(false);
        }
    }

    private static string ProbeName(ProbeKind kind) => kind switch
    {
        ProbeKind.Text => "risposta testuale",
        ProbeKind.Json => "JSON strutturato",
        ProbeKind.Reasoning => "ragionamento",
        _ => "Ricerca web"
    };

    private static string OutcomeWord(ProbeOutcome outcome) => outcome switch
    {
        ProbeOutcome.Supported => "riuscita",
        ProbeOutcome.Unsupported => "non supportata",
        _ => "inconcludente"
    };

    /// <summary>
    /// The profile as configured, without the cached capabilities; the reasoning probe allows thinking, the
    /// others do not, so they stay short. Timeouts are bounded by the probe's own 60 seconds.
    /// </summary>
    private static LlmProfileConfig Probing(LlmProfileConfig p, bool reasoning)
    {
        var copy = JsonSerializer.Deserialize<LlmProfileConfig>(JsonSerializer.Serialize(p))!;
        copy.ModelCapabilities = null;
        copy.DisableThinking = !reasoning;
        copy.Temperature = 0;
        return copy;
    }

    /// <summary>The real web check, with only what it needs of the profile (no exposure, actions or cached capabilities).</summary>
    private static async Task<(bool?, string)> WebProbeAsync(LlmProfileConfig profile, string? secret, string? webSecret, CancellationToken cancellationToken)
    {
        var check = new LlmProfileConfig
        {
            Type = profile.Type, BaseUrl = profile.BaseUrl, Model = profile.Model, AuthType = profile.AuthType,
            External = profile.External, UseSystemProxy = profile.UseSystemProxy
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

    /// <summary>A probe of the profile in Avanzate, a manual change in Connessione: the earlier outcomes no longer apply.</summary>
    private void InvalidateProbes(LlmProfileConfig p, bool webOnly = false)
    {
        _probeRun?.Cancel();
        if (p.ModelCapabilities is { } cache)
        {
            if (webOnly) cache.Probes.RemoveAll(r => r.Kind == ProbeKind.Web);
            else cache.Probes.Clear();
        }
        RefreshCapabilityRows();
    }
}
