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
/// «Avanzate» of a profile: each capability with outcome, provenance and date. The single «Diagnosi» button (under the
/// sections) checks the connection, reads what the service declares and runs short requests with synthetic data (may
/// cost tokens). Results are saved with the normal Salva and dropped when service, model, endpoint or key change.
/// </summary>
public sealed partial class MainConfigForm
{
    private TableLayoutPanel _capabilityTable = null!;
    private OperationalStatusLabel _lblProbeStatus = null!;
    private CancellationTokenSource? _probeRun;
    private int _capabilityOriginWidth;

    private IEnumerable<Control> CapabilityOriginCells() =>
        _capabilityTable.Controls.Cast<Control>().Where(c => _capabilityTable.GetColumn(c) == 2);

    internal const string ProbeCost =
        "«Diagnosi» controlla indirizzo, chiave e modello, rilegge le capacità dichiarate dal servizio e invia brevi richieste con dati inventati " +
        "(nessun dato di Outlook): risposta testuale, JSON strutturato, ragionamento e, se il servizio la offre, Ricerca web. Una alla volta, " +
        "al massimo 60 secondi ciascuna e senza ripetizioni. Con un servizio a pagamento consuma pochi token; la ricerca web è una vera ricerca " +
        "che il fornitore può addebitare.";

    private const string ProbeIdle = "Diagnosi non ancora eseguita per questo profilo.";

    private void BuildCapabilityRows(TableLayoutPanel grid)
    {
        var intro = new SelectableLabel
        {
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Margin = new Padding(3, 6, 3, 8),
            Text = "«Scegli modello…» elenca i modelli senza provare nulla. «Diagnosi» controlla la connessione, rilegge ciò che il servizio dichiara " +
                   "e verifica davvero le funzioni usate dall'app."
        };
        Theme.SetTone(intro, Tone.Muted);
        AddWide(grid, intro, "Differenza fra i comandi del profilo.",
            lead: "Cosa fanno Scegli modello e Diagnosi.");

        _capabilityTable = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(3, 0, 3, 6) };
        for (int i = 0; i < 3; i++) _capabilityTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddWide(grid, _capabilityTable,
            "Per ogni funzione: esito (sì, no, non dichiarata, non verificabile) e origine (dichiarata dal servizio, verificata con una prova o prova inconcludente). " +
            "Visione e Strumenti sono solo dichiarati: l'app non li usa né li prova.");

        // One test command: «Diagnosi» sits under the sections; its progress shows here, short, with the cost note behind a «?».
        int statusIndex = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _lblProbeStatus = new OperationalStatusLabel { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 4, 3, 6), Text = ProbeIdle };
        Tip(_lblProbeStatus, "Avanzamento ed esito della diagnosi; i risultati si salvano con Salva.");
        var statusRow = Inline(_lblProbeStatus, ContextHelp.Button(this, "Costo della diagnosi", () => ProbeCost, _tips));
        grid.Controls.Add(statusRow, 0, statusIndex);
        grid.SetColumnSpan(statusRow, grid.ColumnCount);
        Theme.SetTone(_lblProbeStatus, Tone.Muted);
        // Wrapped at the visible width of the section, like the exposure note: a fixed width was cut off at 100%.
        grid.Layout += (_, _) =>
        {
            int width = Math.Max(Ui.Px(this, 160), grid.ClientSize.Width - Ui.Px(this, 32));
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

    private void SetProbeStatus(string text, Tone tone, StatusLight? light = null)
    {
        _lblProbeStatus.SetState(light ?? StateForTone(tone));
        _lblProbeStatus.Text = text;
        Theme.SetTone(_lblProbeStatus, tone);
        Tip(_lblProbeStatus, text);
        // The status sits in Avanzate, which may be out of view: while «Diagnosi» runs the line beside it carries the same words.
        if (_probeRun != null) SetTestResult(text, tone, light ?? StateForTone(tone));
    }

    /// <summary>
    /// The probing stage of «Diagnosi», after the connection answered. The results of a run whose profile changed meanwhile
    /// (selection, model, endpoint, key) are dropped: a late answer never paints another configuration.
    /// </summary>
    private async Task RunProbesAsync(ProfileItem item)
    {
        if (_isLoading || _probeRun != null) return;
        var p = item.Profile;
        if (string.IsNullOrWhiteSpace(p.Model) || string.IsNullOrWhiteSpace(p.BaseUrl))
        {
            SetProbeStatus("Inserisci indirizzo e modello prima della diagnosi.", Tone.Warning);
            return;
        }

        string key = CapabilityKey(p);
        string? secret = Profiles.SecretFor(p.Id);
        string? webSecret = Profiles.WebSecretFor(p.Id);
        bool webAvailable = ProviderCatalog.Get(p.Type).WebSearch != WebSearchMode.None;
        var kinds = new List<ProbeKind> { ProbeKind.Text, ProbeKind.Json, ProbeKind.Reasoning };
        if (webAvailable) kinds.Add(ProbeKind.Web);

        bool Current() => !IsDisposed && ReferenceEquals(_lstProfiles.SelectedItem, item) && CapabilityKey(p) == key &&
                          secret == Profiles.SecretFor(p.Id);

        var run = new CancellationTokenSource();
        _probeRun = run;
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
            if (_probeRun == run) SetProbeStatus($"Prova {done} di {kinds.Count}: {ProbeName(result.Kind)} {OutcomeWord(result.Outcome)}.", Tone.Warning, StatusLight.Working);
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
                SetProbeStatus("Diagnosi completata. Gli esiti si salvano con Salva.", Tone.Success);
                LogActivity($"[CAPACITÀ] Profilo '{p.Id}', modello '{p.Model}': diagnosi completata.");
            }
        }
        catch (OperationCanceledException)
        {
            if (Current()) SetProbeStatus($"Diagnosi interrotta: {done} di {kinds.Count} esiti conservati.", Tone.Warning);
        }
        catch (Exception ex)
        {
            if (Current()) SetProbeStatus("Diagnosi non avviata: " + ErrorText.For(ex), Tone.Danger);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            (reasoningClient as IDisposable)?.Dispose();
            if (_probeRun == run) _probeRun = null;
            run.Dispose();
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

    /// <summary>A diagnosis of the profile, a manual change in Connessione: the earlier outcomes no longer apply.</summary>
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
