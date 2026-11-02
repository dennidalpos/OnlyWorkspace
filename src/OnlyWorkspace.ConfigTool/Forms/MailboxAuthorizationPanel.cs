using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Archive;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>
/// Choices of the current Windows user for one Outlook profile: which mailboxes enter the archive.
/// Written straight into that archive (no administrator
/// rights, no save button); the Outlook indexer applies them at its next step.
/// </summary>
internal sealed class MailboxAuthorizationPanel : TableLayoutPanel
{
    private const string AllRowId = "*";

    private readonly IConfigurationService _config;
    private readonly ToolTip _tips;
    private readonly CancellationToken _lifetime;
    private readonly ThemedComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Anchor = AnchorStyles.Left };
    // GrowAndShrink like the other buttons: GrowOnly keeps the size of an intermediate DPI layout (twice as tall at 150%).
    private readonly FlatButton _refresh = new() { Text = "Aggiorna", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 28), Padding = new Padding(8, 0, 8, 0) };
    private readonly ThemedCheckBox _mine = new() { Text = "Archivio attivo per me", AutoSize = true, Margin = new Padding(3, 6, 3, 6) };
    private readonly TableLayoutPanel _grid = new() { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 4) };
    private readonly SelectableLabel _status = new() { AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(3, 4, 3, 0) };
    private readonly Dictionary<string, CheckBox> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    // Profiles with an archive; the combo may instead show the Outlook profile whose archive does not exist yet.
    private readonly HashSet<string> _archived = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _archives;
    private readonly Func<OutlookProfiles> _outlook;
    // While mailboxes are missing and the page is shown: read again, so they appear once Outlook has created the archive.
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 5000 };
    private bool _building;
    private bool _scopeUnknown;
    private IReadOnlyList<string> _persistedStores = Array.Empty<string>();
    internal Func<ArchiveClient, string, CancellationToken, Task>? WriteStores;
    internal Func<ArchiveClient, CancellationToken, Task<string?>>? ReadStores;

    private static IReadOnlyList<string> ParseSavedStores(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        using var doc = JsonDocument.Parse(json!);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())))
            throw new JsonException("Selezione delle cassette non valida.");
        return doc.RootElement.EnumerateArray().Select(v => v.GetString()!).ToList();
    }
    private void ApplySavedStores(IReadOnlyList<string> chosen)
    {
        _building = true;
        try
        {
            _persistedStores = chosen.ToArray();
            _scopeUnknown = false;
            bool all = chosen.Count == 0;
            foreach (var row in _rows) { row.Value.ThreeState = false; row.Value.Checked = row.Key == AllRowId ? all : all || chosen.Contains(row.Key, StringComparer.Ordinal); }
            SyncAllRow();
        }
        finally { _building = false; }
    }

    private void MarkStoresUnknown()
    {
        _scopeUnknown = true;
        _building = true;
        try
        {
            foreach (var box in _rows.Values) { box.ThreeState = true; box.CheckState = CheckState.Indeterminate; }
            _grid.Enabled = false;
        }
        finally { _building = false; }
    }

    public MailboxAuthorizationPanel(IConfigurationService config, ToolTip tips, CancellationToken lifetime)
        : this(config, tips, lifetime, null, null) { }

    /// <param name="archives">Profiles with an archive (default: the engine); <paramref name="outlook"/> the user's Outlook profiles (default: the registry).</param>
    internal MailboxAuthorizationPanel(IConfigurationService config, ToolTip tips, CancellationToken lifetime,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? archives, Func<OutlookProfiles>? outlook)
    {
        _config = config;
        _tips = tips;
        _lifetime = lifetime;
        _archives = archives ?? (async ct => (await new ArchiveClient(string.Empty, lifetime: _lifetime).ProfilesAsync(ct)).Select(s => s.Profile).ToList());
        _outlook = outlook ?? (() => OutlookProfiles.Read());
        AutoSize = true;
        // No margin of its own: labels and boxes line up with the other groups of the page.
        Margin = Padding.Empty;
        ColumnCount = 1;
        Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        top.Controls.Add(new SelectableLabel { Text = "Profilo Outlook:", AutoSize = true, Margin = new Padding(0, 8, 6, 3) });
        top.Controls.Add(_profiles);
        top.Controls.Add(_refresh);
        Controls.Add(top);
        Controls.Add(_mine);
        Controls.Add(_grid);
        Controls.Add(_status);
        for (int i = 0; i < 2; i++) _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _tips.SetToolTip(_profiles, "Ogni profilo di Outlook ha un proprio archivio. Le cassette compaiono dopo il primo avvio di Outlook con l'archivio attivo.");
        _tips.SetToolTip(_mine, "Vale solo per il tuo utente Windows. Spento: Outlook non crea né aggiorna il tuo archivio; quello esistente resta finché non lo elimini.");
        _tips.SetToolTip(_refresh, "Rilegge cassette e scelte dall'archivio.");

        RefreshUserPreference();
        _mine.CheckStateChanged += (_, _) =>
        {
            if (_building) return;
            try
            {
                _config.SetUserFlag(ArchiveScope.UserOffFlag, !_mine.Checked);
                RefreshUserPreference();
                SetStatus(_mine.Checked ? "Archivio attivo: Outlook lo aggiorna quando il PC è inattivo." : "Archivio spento per il tuo utente.", Tone.Success);
            }
            catch (Exception ex)
            {
                RefreshUserPreference();
                OnlyWorkspace.Infrastructure.Logging.OnlyWorkspaceLog.Logger.Warning(ex, "Archive preference save failed");
                SetStatus("Scelta non salvata: " + ErrorText.For(ex), Tone.Danger);
            }
            _grid.Enabled = _mine.CheckState == CheckState.Checked && !_scopeUnknown && _busy == 0;
        };
        _refresh.Click += async (_, _) => await LoadAsync();
        _profiles.SelectedIndexChanged += async (_, _) =>
        {
            if (_building) return;
            await LoadSelectedAsync();
            UpdatePoll();
        };
        // A hidden page (another section, the form closed) stops it: WinForms does not tell a child when its parent hides.
        _poll.Tick += async (_, _) =>
        {
            if (!Visible || Closing) _poll.Stop();
            else await LoadAsync();
        };
    }

    private string? _preferenceError;

    private void RefreshUserPreference()
    {
        bool building = _building;
        _building = true;
        try
        {
            _mine.Checked = !_config.GetUserFlag(ArchiveScope.UserOffFlag);
            _preferenceError = null;
        }
        catch (Exception ex)
        {
            _mine.CheckState = CheckState.Indeterminate;
            OnlyWorkspace.Infrastructure.Logging.OnlyWorkspaceLog.Logger.Warning(ex, "Archive preference read failed");
            _preferenceError = "Scelta archivio non leggibile; raccolta sospesa: " + ErrorText.For(ex);
        }
        finally { _building = building; }
        _grid.Enabled = _mine.CheckState == CheckState.Checked && !_scopeUnknown && _busy == 0;
    }

    private ArchiveClient? Client => _profiles.SelectedItem is string profile && _archived.Contains(profile) ? new ArchiveClient(profile, lifetime: _lifetime) : null;

    /// <summary>
    /// The saved configuration, null when unreadable. Read away from the UI thread: loading decrypts the keys with DPAPI,
    /// and it ran at every visit of the page.
    /// </summary>
    private Task<WorkspaceConfig?> SavedConfigAsync() => Task.Run<WorkspaceConfig?>(() =>
    {
        try { return _config.LoadConfiguration(); }
        catch { return null; }
    });


    // A write or a removal is running: coming back to the page must not rebuild the grid under it.
    private int _busy;
    private bool _loading;
    private DateTime _loadedUtc;
    // The last reading listed the mailboxes: no polling, and a quick return to the page keeps it.
    private bool _complete;

    internal const string ArchiveDisabled = "L'archivio non è abilitato su questo PC: l'amministratore lo attiva qui sopra con «Archivio locale delle email» e Salva.";
    internal const string ArchiveNotCreated = "Archivio non ancora creato: apri Outlook, le cassette compaiono qui da sole.";

    /// <summary>
    /// The page is shown again: read the archive only when nothing runs and the last reading, with its mailboxes, is recent,
    /// so a removal in progress, its messages and the addresses being typed stay as they are («Aggiorna» always reads).
    /// </summary>
    public Task ShowAsync() => _busy > 0 || _complete && DateTime.UtcNow - _loadedUtc < TimeSpan.FromMinutes(1) ? Task.CompletedTask : LoadAsync();

    public async Task LoadAsync()
    {
        if (_busy > 0 || _loading) return;
        _loading = true;
        _loadedUtc = DateTime.UtcNow;
        _complete = false;
        try
        {
            RefreshUserPreference();
            if (Closing) return;
            // The engine refuses every request then: say why instead of «non raggiungibile»; an unreadable file is left to the engine to report.
            var saved = await SavedConfigAsync();
            // Hidden while the configuration was read (another section, a poll's last tick): the engine is not asked.
            if (Closing || !Visible) return;
            if (saved?.Archive.Enabled == false)
            {
                ShowOutlookProfile(ArchiveDisabled, Tone.Warning);
                return;
            }
            var archived = await _archives(_lifetime);
            if (Closing) return;
            string? chosen = _profiles.SelectedItem as string;
            _building = true;
            try
            {
                _profiles.Items.Clear();
                _archived.Clear();
                foreach (var profile in archived)
                {
                    _profiles.Items.Add(profile);
                    _archived.Add(profile);
                }
                if (_profiles.Items.Count > 0) _profiles.SelectedItem = chosen != null && _profiles.Items.Contains(chosen) ? chosen : _profiles.Items[0];
            }
            finally { _building = false; }
            if (_profiles.Items.Count == 0)
            {
                ShowOutlookProfile(ArchiveNotCreated, Tone.Muted);
                return;
            }
            await LoadSelectedAsync(saved);
        }
        catch (Exception ex)
        {
            // Called from async void UI handlers: nothing may escape, or the ConfigTool process ends (closing while loading cancels the request).
            if (!Closing)
            {
                if (_profiles.Items.Count == 0) ShowOutlookProfile(null, Tone.Warning);
                SetStatus("Motore dell'archivio non raggiungibile: " + ErrorText.For(ex), Tone.Warning);
            }
        }
        finally
        {
            _loading = false;
            UpdatePoll();
        }
    }

    /// <summary>
    /// No archive to show: the combo holds the Outlook profile the archive will belong to, read from the registry, so the
    /// page names it before Outlook has run with the add-in. Without any Outlook profile that is what the status says.
    /// </summary>
    private void ShowOutlookProfile(string? status, Tone tone)
    {
        var outlook = _outlook();
        _building = true;
        try
        {
            _profiles.Items.Clear();
            _archived.Clear();
            if ((outlook.Default ?? outlook.Names.FirstOrDefault()) is { } name)
            {
                _profiles.Items.Add(name);
                _profiles.SelectedIndex = 0;
            }
        }
        finally { _building = false; }
        ClearGrid();
        if (status != null) SetStatus(outlook.Names.Count == 0 && status == ArchiveNotCreated ? OutlookProfiles.NotConfigured : status, tone);
    }

    private void ClearGrid()
    {
        // Controls.Clear only detaches: each rebuild of the grid would keep the old rows' windows alive.
        var old = _grid.Controls.Cast<Control>().ToList();
        _grid.Controls.Clear();
        foreach (var control in old) control.Dispose();
        _grid.RowStyles.Clear();
        _grid.RowCount = 0;
        _rows.Clear();
        _open.Clear();
    }

    private void UpdatePoll()
    {
        if (!Closing) _poll.Enabled = !_complete && Visible;
    }

    private bool Closing => _lifetime.IsCancellationRequested || IsDisposed;

    private async Task LoadSelectedAsync(WorkspaceConfig? saved = null)
    {
        if (Client is not { } client) return;
        saved ??= await SavedConfigAsync();
        try
        {
            var stores = ArchiveScope.ParseOpenStores(await client.GetMetaAsync(ArchiveScope.OpenStoresKey, _lifetime)).ToList();
            // A PST taken out of the Outlook profile is no longer listed by Outlook, but its emails stay in the archive.
            var closed = (await client.GetStoreCoverageAsync(_lifetime))
                .Where(c => c.Closed && !stores.Any(s => s.Open && string.Equals(s.Id, c.StoreId, StringComparison.OrdinalIgnoreCase))).ToList();
            stores.AddRange(closed.Where(c => !stores.Any(s => string.Equals(s.Id, c.StoreId, StringComparison.OrdinalIgnoreCase)))
                .Select(c => new ArchiveOpenStore { Id = c.StoreId, Name = c.DisplayName.Length > 0 ? c.DisplayName : c.StoreId, Open = false }));
            var chosen = ParseSavedStores(await client.GetMetaAsync(ArchiveScope.StoresKey, _lifetime));
            var own = StoreOwnAddresses.Parse(await client.GetMetaAsync(StoreOwnAddresses.MetaKey, _lifetime));
            BuildGrid(stores, chosen, own, closed.Select(c => c.StoreId).ToList());
            _complete = stores.Count > 0;
            SetStatus(stores.Count == 0 ? "Cassette non ancora rilevate: apri Outlook, compaiono qui da sole." : string.Empty, Tone.Muted);
        }
        catch (Exception ex)
        {
            if (!Closing)
            {
                MarkStoresUnknown();
                SetStatus("Archivio non leggibile: " + ErrorText.For(ex) + " Premi Aggiorna prima di modificare le scelte.", Tone.Warning);
            }
        }
    }

    /// <param name="removable">Closed mailboxes with emails in the archive: each gets «Togli dall'archivio».</param>
    private void BuildGrid(IReadOnlyList<ArchiveOpenStore> stores, IReadOnlyList<string> chosen, Dictionary<string, StoreOwnEntry> own, IReadOnlyCollection<string> removable)
    {
        _building = true;
        try
        {
            _grid.SuspendLayout();
            ClearGrid();
            AddRow(Ui.SetFont(new SelectableLabel { Text = "Cassetta", AutoSize = true }, 9F, FontStyle.Bold),
                Header("Archivio", "Le email della cassetta entrano nell'archivio locale cifrato di questo PC."));

            _scopeUnknown = false;
            _persistedStores = chosen.ToArray();
            bool allArchive = chosen.Count == 0;
            AddStore(AllRowId, "Tutte le cassette, anche future", true, allArchive, null);
            foreach (var store in stores)
            {
                bool archived = allArchive || chosen.Contains(store.Id, StringComparer.Ordinal);
                AddStore(store.Id, store.Name + (store.Open ? string.Empty : " (chiusa)"), store.Open, archived,
                    !store.Open && removable.Contains(store.Id, StringComparer.OrdinalIgnoreCase) ? store.Name : null);
                if (store.IsDataFileStore && !store.HasAccount) AddOwnAddresses(store, own.TryGetValue(store.Id, out var entry) ? entry : null);
            }
            SyncAllRow();
            _grid.Enabled = _mine.CheckState == CheckState.Checked && !_scopeUnknown && _busy == 0;
        }
        finally
        {
            _grid.ResumeLayout();
            _building = false;
        }
        Theme.Apply(_grid);
    }

    private Label Header(string text, string tip)
    {
        var label = Ui.SetFont(new SelectableLabel { Text = text, AutoSize = true, Margin = new Padding(12, 3, 3, 3) }, 9F, FontStyle.Bold);
        _tips.SetToolTip(label, tip);
        return label;
    }

    private void AddRow(Control a, Control b)
    {
        int row = _grid.RowCount++;
        _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.Controls.Add(a, 0, row);
        _grid.Controls.Add(b, 1, row);
    }

    /// <param name="removableName">Name of a closed mailbox that can be taken out of the archive; null otherwise.</param>
    private void AddStore(string id, string name, bool open, bool archived, string? removableName)
    {
        var archive = new ThemedCheckBox { AutoSize = true, Checked = archived, Enabled = open, Margin = new Padding(24, 3, 3, 3), AccessibleName = "Archivio: " + name };
        Control label = new SelectableLabel { Text = name, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        if (removableName != null)
        {
            var remove = new FlatButton { Text = "Togli dall'archivio", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 26), Padding = new Padding(8, 0, 8, 0), AccessibleName = "Togli dall'archivio: " + name };
            _tips.SetToolTip(remove, "Elimina dall'archivio di questo PC le email della cassetta chiusa, con dati e scelte. Non si possono recuperare.");
            remove.Click += async (_, _) => await RemoveClosedAsync(id, removableName);
            var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            line.Controls.Add(label);
            line.Controls.Add(remove);
            label = line;
        }
        AddRow(label, archive);
        _tips.SetToolTip(archive, "Sceglie se conservare le email nell’archivio locale. Con Tutte le cassette attivo le singole scelte sono bloccate; le cassette chiuse si modificano dopo averle riaperte in Outlook.");
        _rows[id] = archive;
        if (open) _open.Add(id);
        archive.CheckedChanged += async (_, _) => { if (!_building) await ApplyAsync(archive); };
    }

    /// <summary>
    /// A PST without account: the addresses the user sent from in it, guessed by Outlook from its Sent Items. They are the
    /// user's own (never a counterparty); the user corrects the guess here.
    /// </summary>
    private void AddOwnAddresses(ArchiveOpenStore store, StoreOwnEntry? entry)
    {
        var box = new ThemedTextBox { Width = 360, Text = string.Join("; ", entry?.Effective ?? Array.Empty<string>()), AccessibleName = "Indirizzi miei: " + store.Name };
        var save = new FlatButton { Text = "Salva", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 26), Padding = new Padding(8, 0, 8, 0), AccessibleName = "Salva gli indirizzi miei: " + store.Name };
        // Its own Save, unlike the ticks that apply at once: said where the user looks.
        _tips.SetToolTip(save, "Salva subito questi indirizzi per il tuo archivio (non serve il Salva della finestra).");
        var line = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(18, 0, 0, 6) };
        line.Controls.Add(new SelectableLabel { Text = "Indirizzi miei:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
        line.Controls.Add(new InputFrame(box));
        line.Controls.Add(save);
        Ui.ScaleDynamic(line, _grid);
        _tips.SetToolTip(box, entry == null
            ? "Outlook non ha ancora letto la Posta inviata di questo file. Separa più indirizzi con «;»."
            : "Ipotesi dalla Posta inviata del file, modificabile: aggiungi o togli indirizzi separati da «;». Non diventano mai controparti.");
        int row = _grid.RowCount++;
        _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.Controls.Add(line, 0, row);
        _grid.SetColumnSpan(line, 2);
        save.Click += async (_, _) => await SaveOwnAddressesAsync(store.Id, box.Text);
    }

    private async Task SaveOwnAddressesAsync(string storeId, string text)
    {
        if (Client is not { } client) return;
        var mine = text.Split(new[] { ';', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var invalid = mine.Where(a => CounterpartyResolver.Normalize(a).IndexOf('@') <= 0).ToList();
        if (invalid.Count > 0)
        {
            SetStatus("Indirizzo non valido: " + invalid[0], Tone.Warning);
            return;
        }
        _busy++;
        try
        {
            // Read again: Outlook may have written a new guess meanwhile.
            var json = await client.GetMetaAsync(StoreOwnAddresses.MetaKey, _lifetime);
            await client.SetMetaAsync(StoreOwnAddresses.MetaKey, StoreOwnAddresses.WithChoice(json, storeId, mine), _lifetime);
            SetStatus("Indirizzi salvati: Outlook li applica al prossimo passaggio.", Tone.Success);
        }
        catch (Exception ex)
        {
            if (!Closing) SetStatus("Indirizzi non salvati: " + ErrorText.For(ex), Tone.Danger);
        }
        finally { _busy--; }
    }

    /// <summary>
    /// Deletes a closed mailbox's emails from the archive, a slice per call, after a confirmation: under «all» too, which
    /// stays as it is. The mailbox is archived again if it is opened in Outlook once more.
    /// </summary>
    private async Task RemoveClosedAsync(string storeId, string name)
    {
        if (Client is not { } client) return;
        if (CopyableMessageBox.Show(FindForm(), $"Togliere dall'archivio di questo PC le email di «{name}», con i dati e le scelte sulla cassetta?\n\n" +
                "Non si possono recuperare. Se la riapri in Outlook, la cassetta viene archiviata di nuovo.",
                "Togli dall'archivio", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, "Togli", "Annulla") != DialogResult.Yes) return;
        _grid.Enabled = false;
        _refresh.Enabled = false;
        _busy++;
        try
        {
            int total = 0, removed;
            do
            {
                total += removed = await client.RemoveClosedStoreAsync(storeId, 500, _lifetime);
                SetStatus($"Rimozione di «{name}»: {total} elementi tolti…", Tone.Muted);
            }
            while (removed > 0);
            _busy--;
            await LoadSelectedAsync();
            _busy++;
            SetStatus($"«{name}» tolta dall'archivio.", Tone.Success);
        }
        catch (Exception ex)
        {
            if (!Closing) SetStatus("Rimozione non riuscita: " + ErrorText.For(ex), Tone.Danger);
        }
        finally
        {
            _busy--;
            if (!Closing)
            {
                _grid.Enabled = _mine.CheckState == CheckState.Checked && !_scopeUnknown && _busy == 0;
                _refresh.Enabled = true;
            }
        }
    }

    /// <summary>With «all» ticked every mailbox follows it and its own box is shown ticked and locked.</summary>
    private void SyncAllRow()
    {
        if (!_rows.TryGetValue(AllRowId, out var all)) return;
        foreach (var row in _rows.Where(r => r.Key != AllRowId))
        {
            if (all.Checked) row.Value.Checked = true;
            row.Value.Enabled = !all.Checked && _open.Contains(row.Key);
            _tips.SetToolTip(row.Value, all.Checked ? "Tutte le cassette è attivo: cambia prima questa riga per scegliere le singole cassette." : !_open.Contains(row.Key) ? "Cassetta chiusa in Outlook: riaprila per cambiarne la scelta." : "Sceglie se conservare le email di questa cassetta nell’archivio locale.");
        }
    }

    /// <param name="changed">The box the user just changed: put back when the choice is refused, so the grid shows what is saved.</param>
    private async Task ApplyAsync(CheckBox? changed = null)
    {
        if (_busy > 0 || _scopeUnknown || Client is not { } client) return;
        _building = true;
        try { SyncAllRow(); }
        finally { _building = false; }

        var all = _rows[AllRowId];
        var stores = _rows.Where(r => r.Key != AllRowId).ToList();
        var archived = stores.Where(r => r.Value.Checked).Select(r => r.Key).ToList();
        if (!all.Checked && archived.Count == 0)
        {
            ApplySavedStores(_persistedStores);
            SetStatus("Scegli almeno una cassetta oppure spegni l'archivio per il tuo utente.", Tone.Warning);
            return;
        }
        _busy++;
        _grid.Enabled = _profiles.Enabled = _refresh.Enabled = false;
        try
        {
            string json = JsonSerializer.Serialize(all.Checked ? new List<string>() : archived);
            if (WriteStores != null) await WriteStores(client, json, _lifetime);
            else await client.SetMetaAsync(ArchiveScope.StoresKey, json, _lifetime);
            if (Closing) return;
            ApplySavedStores(ParseSavedStores(json));
            SetStatus("Salvato: Outlook applica la scelta al prossimo passaggio.", Tone.Success);
        }
        catch (Exception ex)
        {
            if (Closing) return;
            try
            {
                string? saved = ReadStores != null ? await ReadStores(client, _lifetime) : await client.GetMetaAsync(ArchiveScope.StoresKey, _lifetime);
                if (Closing) return;
                ApplySavedStores(ParseSavedStores(saved));
                SetStatus("Salvataggio non confermato: " + ErrorText.For(ex) + " Sono mostrate le scelte rilette dall'archivio.", Tone.Danger);
            }
            catch (Exception readError)
            {
                if (Closing) return;
                MarkStoresUnknown();
                SetStatus("Stato delle cassette non verificabile: " + ErrorText.For(readError) + " Premi Aggiorna prima di modificare le scelte.", Tone.Danger);
            }
        }
        finally
        {
            _busy--;
            if (!Closing)
            {
                _profiles.Enabled = _refresh.Enabled = true;
                _grid.Enabled = !_scopeUnknown && _mine.CheckState == CheckState.Checked;
            }
        }
    }

    private void SetStatus(string text, Tone tone)
    {
        if (_preferenceError != null)
        {
            text = _preferenceError + (text.Length > 0 ? "\n" + text : "");
            tone = Tone.Warning;
        }
        _status.Text = text;
        _status.Visible = text.Length > 0;
        Theme.SetTone(_status, tone);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _poll.Dispose();
        base.Dispose(disposing);
    }
}
