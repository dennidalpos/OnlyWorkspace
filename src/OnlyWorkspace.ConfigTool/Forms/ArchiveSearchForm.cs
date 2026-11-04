using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.ConfigTool.Forms;

public sealed class ArchiveSearchForm : Form
{
    private const int PageSize = 50;
    private readonly ThemedComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    // No label precedes it: the cue banner is not a name for screen readers.
    private readonly ThemedTextBox _query = new() { Width = 320, Anchor = AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Cerca nell'archivio" };
    private readonly ThemedTextBox _amountMin = new() { Width = 105, Name = "FactAmountMin", AccessibleName = "Importo minimo" };
    private readonly ThemedTextBox _amountMax = new() { Width = 105, Name = "FactAmountMax", AccessibleName = "Importo massimo" };
    private readonly ThemedTextBox _currency = new() { Width = 60, Name = "FactCurrency", AccessibleName = "Valuta" };
    private readonly DateTimePicker _dueFrom = new() { Width = 130, Name = "FactDueFrom", AccessibleName = "Scadenza dal", Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
    private readonly DateTimePicker _dueTo = new() { Width = 130, Name = "FactDueTo", AccessibleName = "Scadenza al", Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
    private const int StatusRefreshMs = 5000;
    private readonly DataGridView _results = Grid();
    // Same figures as the Stato screen of the Outlook pane (ArchiveMetrics).
    private readonly DataGridView _metrics = Grid();
    private readonly DataGridView _stores = Grid();
    private readonly Label _status = new SelectableLabel { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 6) };
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = StatusRefreshMs };
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100 };
    private readonly ThemedTextBox _preview = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, WordWrap = true };
    private readonly FlatButton _next = Command("Successivi ›");
    private readonly FlatButton _previous = Command("‹ Precedenti");
    private readonly FlatButton _pause = Command("Pausa");
    private readonly FlatButton _original = Command("Recupera testo originale");
    private readonly FlatButton _flow = Command("Ricostruisci la pratica");
    private readonly FlatButton _cancel = Command("Annulla recupero");
    private readonly FlatButton _sync = Command("Aggiorna da Outlook");
    private readonly Label _searchStatus = new SelectableLabel { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 4, 0, 6), Text = "Scegli un profilo e cerca nell'archivio." };
    private readonly Label _maintenanceStatus = new SelectableLabel { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, 0, 0), Text = "Nessuna manutenzione in corso." };
    // Pages without the system tab strip: the themed bar above switches them.
    private readonly TabControl _tabs = new PageHost { Dock = DockStyle.Fill, Appearance = TabAppearance.Buttons, SizeMode = TabSizeMode.Fixed, ItemSize = new Size(0, 1), TabStop = false, Margin = Padding.Empty };
    private readonly SegmentedBar _sections = new("Ricerca", "Stato e sincronizzazione", "Manutenzione");
    private readonly SplitContainer _searchSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 8 };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Control> _commands = new();
    private readonly string? _archiveDirectory;
    private bool _disposed;
    private CancellationTokenSource? _retrieval;
    private IReadOnlyList<ArchiveHit> _hits = Array.Empty<ArchiveHit>();
    private int _offset;
    private bool _loadingProfiles;
    private bool _busy;
    private bool _paused;
    private bool _outlookConnected;
    private int _searchVersion;
    private ArchiveClient? Client => _profiles.SelectedItem is string profile ? new ArchiveClient(profile, _archiveDirectory, lifetime: _lifetime.Token) : null;

    public ArchiveSearchForm() : this(true) { }

    public ArchiveSearchForm(bool refreshOnShow) : this(refreshOnShow, null) { }

    internal ArchiveSearchForm(bool refreshOnShow, string? archiveDirectory)
    {
        _archiveDirectory = archiveDirectory;
        Text = "OnlyWorkspace – Ricerca e sincronizzazione archivio";
        Icon = Services.AppIconHelper.GetAppIcon();
        Font = new Font("Segoe UI", 9F);
        Size = new Size(1000, 720);
        MinimumSize = new Size(680, 480);
        _sync.AccessibleDescription = "Avvia un nuovo controllo delle cassette quando Outlook è aperto";
        _cancel.Enabled = false;
        var search = Command("Cerca");
        search.Kind = ButtonKind.Primary;
        search.MinimumSize = new Size(96, 28);
        var refresh = Command("Aggiorna stato");
        var optimize = Command("Ottimizza indice");
        // Not destructive (nothing is deleted, the archive only pauses): red is kept for actions that lose data.
        var compact = Command("Compatta database");
        _commands.AddRange(new Control[] { _profiles, _query, search, _previous, _next, refresh, _sync, optimize, compact, _pause, _original, _flow, _cancel });
        foreach (var (control, tip) in new (Control, string)[]
        {
            (_profiles, "Ogni profilo di Outlook ha un proprio archivio: scegli quello in cui cercare e di cui vedere lo stato."),
            (_query, "Parole del testo, dell'oggetto o del mittente. Cerca nell'archivio di questo PC, anche con Outlook chiuso; Invio avvia la ricerca."),
            (search, "Cerca le parole nell'archivio del profilo scelto. Non invia nulla a servizi esterni."),
            (_previous, "Risultati precedenti della stessa ricerca."),
            (_next, "Altri risultati della stessa ricerca."),
            (_results, "Email trovate nell'archivio. Selezionane una per vederne l'anteprima; «Copertura» dice se il testo è stato indicizzato."),
            (_preview, "Anteprima dell'email selezionata o, dopo «Recupera testo originale», il testo integrale acquisito."),
            (_original, "Mostra il testo acquisito anche a Outlook chiuso; se manca lo richiede a Outlook."),
            (_flow, "Mostra in ordine di data le email collegate a quella selezionata (stessa conversazione o risposte)."),
            (_cancel, "Annulla il recupero del testo originale in corso."),
            (refresh, "Rilegge subito i numeri dell'archivio (si aggiornano comunque ogni 5 secondi)."),
            (_sync, "Chiede a Outlook, se aperto, un nuovo controllo delle cassette: email nuove, spostate o eliminate."),
            (_pause, "Sospende o riprende la lettura delle cassette in background. La ricerca resta disponibile."),
            (_metrics, "Numeri dell'archivio: email lette, testi indicizzati, spazio su disco. Gli stessi del pannello Stato del riquadro."),
            (_stores, "Una riga per cassetta di Outlook: quante email sono archiviate, quanto testo è indicizzato e l'ultimo controllo completo."),
            (optimize, "Riorganizza l'indice di ricerca per renderlo più rapido. Non cambia nulla in Outlook; durante l'operazione l'archivio è in pausa."),
            (compact, "Recupera lo spazio libero nel database (per esempio dopo aver tolto una cassetta). Richiede su disco circa il doppio del database e può durare minuti; l'archivio resta in pausa."),
            (_sections, "Ricerca nell'archivio, stato della raccolta delle email e operazioni di manutenzione.")
        })
            _tips.SetToolTip(control, tip);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16, 12, 16, 12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var profileBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        profileBar.Controls.Add(new SelectableLabel { Text = "Archivio del profilo Outlook", AutoSize = true, Margin = new Padding(0, 6, 10, 0) });
        profileBar.Controls.Add(_profiles);
        layout.Controls.Add(profileBar, 0, 0);
        layout.Controls.Add(_sections, 0, 1);
        _sections.SelectedIndexChanged += (_, _) => _tabs.SelectedIndex = _sections.SelectedIndex;
        _tabs.SelectedIndexChanged += (_, _) => _sections.Select(_tabs.SelectedIndex);

        var searchPage = new TabPage("Ricerca") { Name = "ArchiveSearchTab", Padding = new Padding(0, 6, 0, 0), AutoScroll = true };
        var searchLayout = Rows(SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent);
        searchLayout.Dock = DockStyle.Top;
        searchLayout.MinimumSize = new Size(0, 480);
        searchPage.Layout += (_, _) =>
        {
            int height = Math.Max(searchPage.ClientSize.Height - searchPage.Padding.Vertical, searchLayout.MinimumSize.Height);
            if (searchLayout.Height != height) searchLayout.Height = height;
        };
        var searchBar = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchBar.Controls.Add(new InputFrame(_query) { Margin = new Padding(0, 2, 8, 0) }, 0, 0);
        searchBar.Controls.Add(search, 1, 0);
        var factBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty, Name = "FactFilters" };
        foreach (var pair in new (string Text, Control Input)[] { ("Importo da", _amountMin), ("a", _amountMax), ("Valuta", _currency), ("Scadenza dal", _dueFrom), ("al", _dueTo) })
        {
            var field = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 10, 6) };
            field.Controls.Add(new SelectableLabel { Text = pair.Text, AutoSize = true, Margin = new Padding(0, 5, 5, 0) });
            field.Controls.Add(pair.Input); factBar.Controls.Add(field);
        }
        var resetFacts = Command("Azzera filtri");
        resetFacts.Click += async (_, _) => { _amountMin.Clear(); _amountMax.Clear(); _currency.Clear(); _dueFrom.Checked = false; _dueTo.Checked = false; await GuardAsync(() => SearchAsync(true)); };
        factBar.Controls.Add(resetFacts);
        _tips.SetToolTip(_amountMin, "Importo esatto o minimo presente nel testo. Per esempio 1000,00. I valori ambigui non sono inclusi.");
        _tips.SetToolTip(_amountMax, "Importo massimo incluso, nella stessa valuta. Vuoto non imposta un massimo.");
        _tips.SetToolTip(_currency, "Codice come EUR, USD o GBP; vuoto cerca anche importi senza valuta determinabile. Nessuna conversione.");
        _tips.SetToolTip(_dueFrom, "Attiva la casella per filtrare le scadenze nel testo. Non è la data di ricezione dell'email.");
        _tips.SetToolTip(_dueTo, "Ultimo giorno di scadenza incluso; attiva la casella per applicarlo.");
        _tips.SetToolTip(resetFacts, "Toglie i filtri su importi e scadenze e ripete la ricerca locale. Non cambia dati o configurazione.");
        _commands.Add(factBar);
        // Amount and due-date filters only matter for invoices: folded away until asked for, with a hint when one is set.
        var factToggle = Command("Filtri su importi e scadenze ▸");
        factToggle.Name = "FactFiltersToggle";
        factToggle.AccessibleName = "Filtri su importi e scadenze";
        _tips.SetToolTip(factToggle, "Mostra o nasconde i filtri su importi, valuta e scadenze trovati nel testo delle email.");
        factBar.Visible = false;
        void UpdateFactToggle()
        {
            bool active = _amountMin.Text.Length > 0 || _amountMax.Text.Length > 0 || _currency.Text.Length > 0 || _dueFrom.Checked || _dueTo.Checked;
            factToggle.Text = "Filtri su importi e scadenze" + (active ? " (attivi)" : string.Empty) + (factBar.Visible ? " ▾" : " ▸");
            factToggle.AccessibleDescription = factBar.Visible ? "Espanso" : "Compresso";
        }
        factToggle.Click += (_, _) => { factBar.Visible = !factBar.Visible; UpdateFactToggle(); };
        foreach (var box in new[] { _amountMin, _amountMax, _currency }) box.TextChanged += (_, _) => UpdateFactToggle();
        foreach (var picker in new[] { _dueFrom, _dueTo }) picker.ValueChanged += (_, _) => UpdateFactToggle();
        _commands.Add(factToggle);
        var searchControls = Rows(SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize);
        searchControls.Controls.Add(searchBar, 0, 0); searchControls.Controls.Add(factToggle, 0, 1); searchControls.Controls.Add(factBar, 0, 2);
        searchLayout.Controls.Add(searchControls, 0, 0);
        var searchNote = Note("Cerca nell'indice anche a Outlook chiuso. Seleziona un risultato per l'anteprima; il testo acquisito è disponibile anche a Outlook chiuso.");
        searchLayout.Controls.Add(searchNote, 0, 1);
        searchLayout.Layout += (_, _) =>
        {
            int width = Math.Max(1, searchLayout.ClientSize.Width - searchLayout.Padding.Horizontal);
            foreach (Control wrapping in new Control[] { factBar, searchNote, _searchStatus })
                if (wrapping.MaximumSize.Width != width) wrapping.MaximumSize = new Size(width, 0);
        };
        searchLayout.Controls.Add(_searchStatus, 0, 2);
        var resultsCard = new Card("Risultati nell'indice") { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var paging = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        paging.Controls.AddRange(new Control[] { _next, _previous });
        resultsCard.Controls.Add(_results);
        resultsCard.Controls.Add(paging);
        var previewCard = new Card("Anteprima o testo originale") { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var resultActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 0) };
        resultActions.Controls.AddRange(new Control[] { _original, _flow, _cancel });
        previewCard.Controls.Add(_preview);
        previewCard.Controls.Add(resultActions);
        // The results list is what is read first: it gets most of the height once, then the user's own splitter position stays.
        bool splitPlaced = false;
        _searchSplit.SizeChanged += (_, _) =>
        {
            int room = _searchSplit.Height - _searchSplit.SplitterWidth;
            if (splitPlaced || room < Ui.Px(this, 260)) return;
            splitPlaced = true;
            _searchSplit.SplitterDistance = Math.Min((int)(room * 0.62), room - Ui.Px(this, 110));
        };
        _searchSplit.Panel1.Controls.Add(resultsCard);
        _searchSplit.Panel2.Controls.Add(previewCard);
        searchLayout.Controls.Add(_searchSplit, 0, 3);
        searchPage.Controls.Add(searchLayout);

        var statePage = new TabPage("Stato e sincronizzazione") { Name = "ArchiveStateTab", Padding = new Padding(0, 6, 0, 0) };
        var stateLayout = Rows(SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.Percent);
        // The figures first: all their rows fit, the mailbox list scrolls if long.
        stateLayout.RowStyles[3] = new RowStyle(SizeType.Percent, 60);
        stateLayout.RowStyles[4] = new RowStyle(SizeType.Percent, 40);
        stateLayout.Controls.Add(Note("I valori si aggiornano ogni 5 secondi. Aggiorna da Outlook richiede una nuova scansione (Outlook aperto); Pausa sospende la scansione."), 0, 0);
        var stateActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        stateActions.Controls.AddRange(new Control[] { refresh, _sync, _pause });
        stateLayout.Controls.Add(stateActions, 0, 1);
        stateLayout.Controls.Add(_status, 0, 2);
        var statusCard = new Card("Stato dell'archivio") { Dock = DockStyle.Fill };
        statusCard.Controls.Add(_metrics);
        stateLayout.Controls.Add(statusCard, 0, 3);
        var coverageCard = new Card("Cassette") { Dock = DockStyle.Fill, Margin = Padding.Empty };
        coverageCard.Controls.Add(_stores);
        stateLayout.Controls.Add(coverageCard, 0, 4);
        statePage.Controls.Add(stateLayout);

        var maintenancePage = new TabPage("Manutenzione") { Name = "ArchiveMaintenanceTab", Padding = new Padding(0, 6, 0, 0) };
        var maintenanceLayout = Rows(SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent);
        maintenanceLayout.AutoScroll = true;
        maintenanceLayout.Controls.Add(ActionCard("Ottimizza indice", "Aggiorna le strutture di ricerca dell'archivio. Le email in Outlook non vengono modificate.", optimize), 0, 0);
        maintenanceLayout.Controls.Add(ActionCard("Compatta database", "Riduce lo spazio occupato dal database. Può richiedere tempo e spazio temporaneo; nel frattempo l'archivio risulta in pausa.", compact), 0, 1);
        maintenanceLayout.Controls.Add(_maintenanceStatus, 0, 2);
        maintenancePage.Controls.Add(maintenanceLayout);

        _tabs.TabPages.AddRange(new[] { searchPage, statePage, maintenancePage });
        layout.Controls.Add(_tabs, 0, 2);
        Controls.Add(layout);
        foreach (var name in new[] { "Data", "Oggetto", "Mittente", "Copertura" }) _results.Columns.Add(name, name);
        _results.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        _results.Columns[0].FillWeight = 25;
        _results.Columns[1].FillWeight = 90;
        _results.Columns[2].FillWeight = 50;
        _results.Columns[3].FillWeight = 55;
        Columns(_metrics, ("Voce", 70, false), ("Fatti", 35, true), ("Totale", 35, true), ("%", 25, true), ("Restano", 35, true), ("Non letti", 45, true));
        _metrics.Columns[3].HeaderCell.ToolTipText = "Percentuale sugli elementi leggibili; non include la colonna Non letti.";
        Columns(_stores, ("Cassetta", 70, false), ("Stato", 40, false), ("Email", 30, true), ("Testi %", 28, true), ("Restano", 30, true), ("Allegati", 32, true),
            ("Cartelle", 30, true), ("Ultimo inventario", 50, true), ("Errore", 110, false));
        // The reason of a failed mailbox is the point of the column: it wraps (rows grow) and the full text is in the tooltip.
        _stores.Columns[8].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        Ui.ScaleFromDesignDpi(this);
        void MatchMinimumSize() => MinimumSize = new Size(Ui.Px(this, 680), Ui.Px(this, 480));
        Load += (_, _) => MatchMinimumSize();
        DpiChanged += (_, _) => MatchMinimumSize();
        FontChanged += (_, _) => MatchMinimumSize();
        DisabledControlTips.Attach(this, _tips);
        search.Click += async (_, _) => await GuardAsync(() => SearchAsync(reset: true));
        _query.HandleCreated += (_, _) => SendMessage(_query.Handle, EmSetCueBanner, (IntPtr)1, "Cerca nell'archivio: parole, mittente, oggetto…");
        _query.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await GuardAsync(() => SearchAsync(reset: true)); } };
        _profiles.SelectedIndexChanged += async (_, _) => { if (!_loadingProfiles) await GuardAsync(() => SearchAsync(reset: true)); };
        _previous.Click += async (_, _) => { _offset = Math.Max(0, _offset - PageSize); await GuardAsync(() => SearchAsync()); };
        _next.Click += async (_, _) => { _offset += PageSize; await GuardAsync(() => SearchAsync()); };
        refresh.Click += async (_, _) => await GuardAsync(RefreshAsync);
        _sync.Click += async (_, _) => await GuardAsync(async () =>
        {
            if (Client is { } client && _outlookConnected)
            {
                await client.SyncNowAsync();
                await RefreshAsync();
                _status.Text = "Aggiornamento richiesto a Outlook. La copertura cambia quando il nuovo inventario termina." + Environment.NewLine + _status.Text;
            }
        });
        optimize.Click += async (_, _) => await GuardAsync(async () =>
        {
            if (Client is { } client)
            {
                _status.Text = "Ottimizzazione indice in corso…";
                _maintenanceStatus.Text = _status.Text;
                await client.OptimizeIndexAsync(_lifetime.Token);
                await RefreshAsync();
                _maintenanceStatus.Text = "Ottimizzazione completata.";
            }
        });
        compact.Click += async (_, _) => await GuardAsync(async () =>
        {
            // Minutes of work with the archive paused and twice its size on disk: asked first.
            if (CopyableMessageBox.Show(this, "Compattare il database dell'archivio?\n\nPuò richiedere alcuni minuti, durante i quali l'archivio è in pausa, " +
                    "e serve spazio libero pari a circa il doppio della sua dimensione.", "Compatta database",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, "Compatta", "Annulla") != DialogResult.Yes) return;
            if (Client is { } client)
            {
                _status.Text = "Compattazione database in corso…";
                _maintenanceStatus.Text = _status.Text;
                await client.CompactAsync(_lifetime.Token);
                await RefreshAsync();
                _maintenanceStatus.Text = "Compattazione completata.";
            }
        });
        _pause.Click += async (_, _) => await GuardAsync(async () =>
        {
            if (Client is { } client) { await client.SetMetaAsync("paused", _paused ? null : "1"); await RefreshAsync(); }
        });
        _results.SelectionChanged += (_, _) => ShowPreview();
        _original.Click += async (_, _) => await GuardAsync(RetrieveOriginalAsync);
        _flow.Click += async (_, _) => await GuardAsync(ShowFlowAsync);
        // Live figures while the state page is on screen; a running command is never interrupted.
        _statusTimer.Tick += async (_, _) =>
        {
            if (_busy || !Visible || _tabs.SelectedIndex != 1 || Client is not { } client) return;
            try { await ShowStatusAsync(client); }
            catch (Exception ex) { OnlyWorkspaceLog.Logger.Debug(ex, "Archive status refresh failed"); }
        };
        _statusTimer.Start();
        _cancel.Click += (_, _) => _retrieval?.Cancel();
        if (refreshOnShow) Shown += async (_, _) => await GuardAsync(RefreshAsync);
        Theme.Watch();
        Theme.Changed += OnThemeChanged;
        ApplyTheme();
        SetPaging();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        _busy = true;
        foreach (Control control in _commands) if (!ReferenceEquals(control, _cancel)) control.Enabled = false;
        try { await action(); }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) { _status.Text = "Recupero annullato."; _searchStatus.Text = _status.Text; } }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Archive search operation failed");
            if (!_lifetime.IsCancellationRequested) _status.Text = "Operazione non riuscita: " + ErrorText.For(ex);
            if (!_lifetime.IsCancellationRequested && _tabs.SelectedIndex == 0) _searchStatus.Text = _status.Text;
            if (!_lifetime.IsCancellationRequested && _tabs.SelectedIndex == 2) _maintenanceStatus.Text = _status.Text;
        }
        finally
        {
            _busy = false;
            if (!_lifetime.IsCancellationRequested)
            {
                foreach (Control control in _commands) if (!ReferenceEquals(control, _cancel)) control.Enabled = true;
                SetPaging();
            }
        }
    }

    private async Task RefreshAsync()
    {
        var statuses = await new ArchiveClient(string.Empty, _archiveDirectory, lifetime: _lifetime.Token).ProfilesAsync();
        if (_lifetime.IsCancellationRequested) return;
        string? chosen = _profiles.SelectedItem as string;
        _loadingProfiles = true;
        try
        {
            _profiles.Items.Clear();
            foreach (var state in statuses.Where(s => s.Phase != "error" || s.LastOutlookUtc.HasValue)) _profiles.Items.Add(state.Profile);
            if (_profiles.Items.Count > 0) _profiles.SelectedItem = chosen != null && _profiles.Items.Contains(chosen) ? chosen : _profiles.Items[0];
        }
        finally { _loadingProfiles = false; }
        if (_profiles.Items.Count == 0)
        {
            _outlookConnected = false;
            _status.Text = statuses.Count == 0 ? "Nessun archivio collegato. Apri Outlook: l'archivio si crea da sé quando è consentito." : string.Join(Environment.NewLine, statuses.Select(s => s.Error));
            _searchStatus.Text = _status.Text;
            _hits = Array.Empty<ArchiveHit>();
            _results.Rows.Clear();
            _preview.Clear();
            _metrics.Rows.Clear();
            _stores.Rows.Clear();
            SetPaging();
            return;
        }
        await SearchAsync(reset: chosen != _profiles.SelectedItem as string);
    }

    private async Task SearchAsync(bool reset = false)
    {
        if (Client is not { } client) return;
        if (reset) _offset = 0;
        int version = ++_searchVersion;
        var hits = await client.SearchAsync(CreateQuery());
        if (_lifetime.IsCancellationRequested || version != _searchVersion) return;
        _hits = hits;
        _results.Rows.Clear();
        foreach (var hit in hits) _results.Rows.Add(hit.ReceivedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), hit.Subject, hit.SenderAddress, Coverage(hit));
        await ShowStatusAsync(client);
        if (_lifetime.IsCancellationRequested || version != _searchVersion) return;
        var facts = await client.GetFactProgressAsync(_lifetime.Token);
        if (_lifetime.IsCancellationRequested || version != _searchVersion) return;
        _searchStatus.Text = hits.Count + " risultati nella pagina · " + (_outlookConnected ? "Outlook collegato" : "Ricerca offline") + FactCoverage(facts);
        ShowPreview();
    }

    internal ArchiveQuery CreateQuery()
    {
        decimal? Amount(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string? normalized = ArchiveFactExtractor.NormalizeAmount(value.Trim());
            if (normalized == null) throw new ArgumentException("Importo non valido o ambiguo. Usa per esempio 1000,00.");
            return decimal.Parse(normalized, CultureInfo.InvariantCulture);
        }
        var query = new ArchiveQuery { Text = _query.Text, Max = PageSize, Offset = _offset, IncludeFacts = true };
        decimal? min = Amount(_amountMin.Text), max = Amount(_amountMax.Text);
        if (min > max) throw new ArgumentException("L'importo minimo supera il massimo.");
        string currency = _currency.Text.Trim().ToUpperInvariant();
        if (currency.Length > 0 && (currency.Length != 3 || currency.Any(c => c < 'A' || c > 'Z'))) throw new ArgumentException("Indica la valuta con tre lettere, per esempio EUR.");
        if (min.HasValue || max.HasValue || currency.Length > 0)
            query.FactFilters.Add(new ArchiveFactFilter { Kind = ArchiveFactKind.Amount, AmountMin = min, AmountMax = max, Currency = currency.Length == 0 ? null : currency });
        if (_dueFrom.Checked && _dueTo.Checked && _dueFrom.Value.Date > _dueTo.Value.Date) throw new ArgumentException("La prima scadenza supera l'ultima.");
        if (_dueFrom.Checked || _dueTo.Checked)
            query.FactFilters.Add(new ArchiveFactFilter { Kind = ArchiveFactKind.Deadline, DateFrom = _dueFrom.Checked ? _dueFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                DateTo = _dueTo.Checked ? _dueTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null });
        query.Text = ArchiveSearchText.Parse(query.Text, query.FactFilters);
        return query;
    }

    private static string FactCoverage(ArchiveFactProgress progress) => " · Fatti: " + progress.Done + " email elaborate" +
        (progress.Pending > 0 ? " · recupero incompleto: " + progress.Pending + " in attesa; ripeti la ricerca al termine" : "") +
        (progress.Failed > 0 ? " · " + progress.Failed + " email non elaborate" : "");

    /// <summary>Status page: fixed columns of figures, no sentences.</summary>
    private async Task ShowStatusAsync(ArchiveClient client)
    {
        var state = await client.EngineStatusAsync();
        var coverage = await client.GetStoreCoverageAsync(_lifetime.Token);
        if (_lifetime.IsCancellationRequested) return;
        _paused = state.Paused;
        _outlookConnected = state.OutlookConnected;
        var facts = await client.GetFactProgressAsync(_lifetime.Token);
        if (_lifetime.IsCancellationRequested) return;
        _pause.Text = _paused ? "Riprendi" : "Pausa";
        var stats = state.Stats;
        // Same reason as the pane and the tray: a failed folder is an error even while the phase is idle.
        string? reason = StatusLights.ArchiveReason(state.Phase, state.Error, stats.FoldersFailed);
        _status.Text = "Fase: " + ArchiveMetrics.PhaseLabel(state.Paused ? "paused" : state.Phase) +
            " · Outlook: " + (state.OutlookConnected ? "collegato" : "non collegato") +
            " · Ultimo inventario: " + (state.LastCheckUtc.HasValue ? state.LastCheckUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "mai") +
            (reason == null ? string.Empty : " · Errore: " + reason) + FactCoverage(facts);
        Theme.SetTone(_status, reason == null ? Tone.Normal : Tone.Danger);

        _metrics.Rows.Clear();
        foreach (var m in ArchiveMetrics.Build(stats))
            _metrics.Rows.Add(m.Name, m.Total == null ? Figure(m.Unit, m.Done) : m.Done.ToString("N0"), m.Total?.ToString("N0") ?? string.Empty,
                m.Total == null ? string.Empty : Pct(m.Percent), m.Total == null ? string.Empty : m.Remaining.ToString("N0"), m.Total == null ? string.Empty : m.Excluded.ToString("N0"));

        _stores.Rows.Clear();
        foreach (var c in coverage)
        {
            var s = ArchiveMetrics.Store(StoreLabel(c), !c.Closed, true, c);
            int index = _stores.Rows.Add(s.Name, s.State, s.Emails.ToString("N0"), Pct(s.BodiesPercent), s.BodiesRemaining.ToString("N0"),
                s.AttachmentsDone.ToString("N0") + "/" + s.AttachmentsTotal.ToString("N0"), s.FoldersDone.ToString("N0") + "/" + s.Folders.ToString("N0"),
                s.LastInventoryUtc.HasValue ? s.LastInventoryUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "mai", s.Error);
            if (!string.IsNullOrEmpty(s.Error)) _stores.Rows[index].Cells[8].ToolTipText = s.Error;
        }
        // Read-only figures: no row looks selected.
        _metrics.ClearSelection();
        _stores.ClearSelection();
    }

    private static string Pct(int? percent) => percent.HasValue ? percent.Value + "%" : "–";

    private static string Figure(string unit, long value) => unit switch
    {
        "bytes" => value >= 1073741824 ? (value / 1073741824.0).ToString("N1") + " GB" : Math.Max(value > 0 ? 1 : 0, value / 1048576).ToString("N0") + " MB",
        _ => value.ToString("N0")
    };

    private static string Coverage(ArchiveHit hit)
    {
        string text = hit.BodyState switch { ArchiveBodyState.Indexed => "Testo indicizzato", ArchiveBodyState.Unavailable => "Testo escluso", ArchiveBodyState.Retryable => "Testo da riprovare", _ => "Solo intestazioni" };
        return text + (hit.TextLimited ? "; testo parziale" : string.Empty) + (hit.RecipientsLimited ? "; destinatari parziali" : string.Empty) + (hit.AttachmentsLimited ? "; allegati parziali" : string.Empty);
    }

    private ArchiveHit? Selected => _results.CurrentRow is { Index: >= 0 } row && row.Index < _hits.Count ? _hits[row.Index] : null;

    private void ShowPreview()
    {
        ClearCopyMenu();
        if (Selected is not { } hit) { _preview.Clear(); return; }
        _preview.Text = hit.Subject + Environment.NewLine + hit.SenderAddress + Environment.NewLine + Coverage(hit) + Environment.NewLine +
            string.Join(Environment.NewLine, hit.Attachments.Select(a => a.Name + " · " + ArchiveAttachment.StateLabel(a.State) + (a.TextLimited ? " · testo indicizzato parziale" : ""))) +
            Environment.NewLine + hit.Preview + Environment.NewLine + Environment.NewLine + "Anteprima dell'indice. Il testo completo acquisito è disponibile con «Recupera testo originale»." +
            Environment.NewLine + Environment.NewLine + "Fatti locali · valori riconosciuti nel testo, senza attestazione di autenticità" + Environment.NewLine +
            string.Join(Environment.NewLine + Environment.NewLine, hit.Facts.Select(f => FactLabel(f.Kind) + (f.Label.Length > 0 ? " (" + f.Label + ")" : "") + " · " + f.SourceName + ": " +
                (f.Resolved ? f.Value + (f.Currency == null ? "" : " " + f.Currency) : "non risolto: " + f.Raw) + Environment.NewLine + "«" + f.Context + "»"));
    }

    private static string FactLabel(ArchiveFactKind kind) => kind switch
    {
        ArchiveFactKind.Date => "Data", ArchiveFactKind.Deadline => "Scadenza", ArchiveFactKind.Amount => "Importo", ArchiveFactKind.Order => "Ordine",
        ArchiveFactKind.Invoice => "Fattura", ArchiveFactKind.Protocol => "Protocollo", ArchiveFactKind.Iban => "IBAN", ArchiveFactKind.Vat => "P.IVA",
        ArchiveFactKind.TaxCode => "Codice fiscale", ArchiveFactKind.PecReceipt => "Ricevuta PEC", ArchiveFactKind.Calendar => "Invito", _ => "Parte"
    };

    private void ClearCopyMenu()
    {
        _preview.ContextMenuStrip?.Dispose(); _preview.ContextMenuStrip = null;
    }

    private async Task ShowFlowAsync()
    {
        if (Selected is not { } hit || Client is not { } client) return;
        var flow = await client.GetFlowAsync(new ArchiveFlowSeed { Id = hit.Id, EntryId = hit.EntryId, StoreId = hit.StoreId });
        var names = (await client.GetStoreCoverageAsync(_lifetime.Token)).ToDictionary(s => s.StoreId, StoreLabel, StringComparer.OrdinalIgnoreCase);
        string Mailbox(string storeId) => names.TryGetValue(storeId, out var name) ? name : "cassetta non più inventariata";
        var lines = new List<string>
        {
            $"Cronologia: {flow.Timeline.Count} email" + (flow.LimitReached ? " · limite di 20.000 raggiunto: restringi l'ambito" : string.Empty),
            "Con Outlook chiuso sono disponibili anche i testi integrali già acquisiti.",
            "Una voce per Message-ID, con tutte le posizioni delle copie. Nelle cassette IMAP/OST le assenze si verificano al controllo completo, con Outlook aperto.", string.Empty
        };
        if (flow.MetadataIncomplete) lines.Add("Intestazioni della catena incomplete.");
        if (flow.FactsIncomplete) lines.Add("Estrazione dei fatti incompleta.");
        foreach (var mail in flow.Timeline)
        {
            lines.Add($"{(mail.SentUtc ?? mail.ReceivedUtc).ToLocalTime():dd/MM/yyyy HH:mm} · {mail.Subject} · {mail.Sender} · {Mailbox(mail.StoreId)}" +
                (mail.ParentEntryId.Length > 0 ? " · risposta" : string.Empty) +
                (mail.BodyState == ArchiveBodyState.Indexed ? string.Empty : " · corpo non indicizzato"));
            if (mail.Copies.Count > 1) lines.Add("  Copie: " + string.Join(", ", mail.Copies.Select(copy => copy.FolderPath.Length > 0 ? copy.FolderPath : Mailbox(copy.StoreId))));
            foreach (var reason in mail.LinkReasons) lines.Add("  " + reason);
            foreach (var fact in mail.LinkFacts) lines.Add("  " + fact.SourceName + ": " + fact.Context);
            if (mail.Attachments.Count > 0) lines.Add("  Allegati: " + string.Join(", ", mail.Attachments));
        }
        if (flow.PossibleLinks.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Possibili collegamenti (oggetto o mittente entro 30 giorni, non uniti automaticamente):");
            lines.AddRange(flow.PossibleLinks.Select(mail => $"{(mail.SentUtc ?? mail.ReceivedUtc).ToLocalTime():dd/MM/yyyy HH:mm} · {mail.Subject} · {Mailbox(mail.StoreId)}"));
        }
        ClearCopyMenu();
        var copies = new ContextMenuStrip();
        foreach (var mail in flow.Timeline)
            foreach (var copy in mail.Copies)
            {
                var chosen = copy;
                var item = copies.Items.Add("Leggi copia · " + (chosen.FolderPath.Length > 0 ? chosen.FolderPath : Mailbox(chosen.StoreId)) + " · " + mail.Subject);
                item.Click += async (_, _) => await GuardAsync(async () =>
                {
                    var original = await client.GetOriginalAsync(chosen.EntryId, chosen.StoreId, _lifetime.Token);
                    if (!_lifetime.IsCancellationRequested) _preview.Text = original == null ? "Testo non acquisito per questa copia." : ArchiveAttachmentReader.ReadableText(original);
                });
            }
        _preview.ContextMenuStrip = copies;
        if (copies.Items.Count > 0) lines.Add("Tasto destro nell'anteprima: leggi il testo acquisito di una copia.");
        _preview.Text = string.Join(Environment.NewLine, lines);
    }

    private static string StoreLabel(ArchiveStoreCoverage s) => s.DisplayName.Length > 0 ? s.DisplayName : "Cassetta senza nome";

    private async Task RetrieveOriginalAsync()
    {
        if (Selected is not { } hit || Client is not { } client) return;
        using var retrieval = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        retrieval.CancelAfter(TimeSpan.FromMinutes(3));
        _retrieval = retrieval;
        _cancel.Enabled = true;
        _original.Enabled = false;
        string? requestId = null;
        try
        {
            var stored = await client.GetOriginalAsync(hit.EntryId, hit.StoreId, retrieval.Token);
            if (stored != null)
            {
                _preview.Text = ArchiveAttachmentReader.ReadableText(stored);
                _status.Text = "Testo recuperato dall'archivio cifrato locale.";
                return;
            }
            requestId = await client.RequestOriginalAsync(hit.EntryId, hit.StoreId, retrieval.Token);
            _status.Text = "Recupero del testo originale dalla sorgente tramite Outlook…";
            while (true)
            {
                retrieval.Token.ThrowIfCancellationRequested();
                var result = await client.OriginalResultAsync(requestId, retrieval.Token);
                if (result.Ready)
                {
                    if (result.Error != null) throw new InvalidOperationException(result.Error);
                    _preview.Text = result.Text ?? string.Empty;
                    _status.Text = "Testo originale recuperato da Outlook.";
                    requestId = null;
                    return;
                }
                await Task.Delay(500, retrieval.Token);
            }
        }
        finally
        {
            _retrieval = null;
            if (!_lifetime.IsCancellationRequested) { _cancel.Enabled = false; _original.Enabled = true; }
            if (requestId != null && !_lifetime.IsCancellationRequested)
            {
                try { await client.CancelOriginalAsync(requestId); }
                catch (Exception ex) { OnlyWorkspaceLog.Logger.Warning("Original text cleanup failed: {Kind}", ex.GetType().Name); }
            }
        }
    }

    private void SetPaging()
    {
        _previous.Enabled = _offset > 0;
        _next.Enabled = _hits.Count == PageSize;
        _original.Enabled = _retrieval == null && Selected != null;
        _flow.Enabled = Selected != null;
        _pause.Enabled = Client != null;
        _sync.Enabled = Client != null && _outlookConnected;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(ApplyTheme));
    }

    private void ApplyTheme()
    {
        var c = Theme.Colors;
        BackColor = c.Window;
        ForeColor = c.Text;
        Theme.Apply(this);
        foreach (var grid in new[] { _results, _metrics, _stores })
        {
            grid.BackgroundColor = c.Card;
            grid.DefaultCellStyle.BackColor = c.Card;
            grid.DefaultCellStyle.ForeColor = c.Text;
            grid.GridColor = c.Border;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = c.Card;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = c.Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = c.Card;
            grid.DefaultCellStyle.SelectionBackColor = c.Accent;
            grid.DefaultCellStyle.SelectionForeColor = c.OnAccent;
            grid.DefaultCellStyle.Padding = grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 3, 4, 3);
            Theme.ApplyScrollBars(grid);
        }
        // Native date pickers: only the drop-down calendar takes the theme; the edit box of the native control stays light
        // (BackColor is ignored with visual styles), which is why the filters are folded away until asked for.
        foreach (var picker in new[] { _dueFrom, _dueTo })
        {
            picker.CalendarMonthBackground = c.Card;
            picker.CalendarForeColor = c.Text;
            picker.CalendarTitleBackColor = c.Border;
            picker.CalendarTitleForeColor = c.Text;
            picker.CalendarTrailingForeColor = c.Muted;
        }
        _searchSplit.BackColor = c.Window;
        foreach (TabPage page in _tabs.TabPages) page.BackColor = c.Window;
        Theme.ApplyWindowChrome(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowChrome(this);
    }

    private static FlatButton Command(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(0, 28),
        Padding = new Padding(8, 0, 8, 0),
        Margin = new Padding(0, 0, 6, 6)
    };

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
        BorderStyle = BorderStyle.None, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None
    };

    /// <summary>Figures right-aligned, text left; each header lines up with its cells.</summary>
    private static void Columns(DataGridView grid, params (string Name, int Weight, bool Figure)[] columns)
    {
        foreach (var (name, weight, figure) in columns)
        {
            int index = grid.Columns.Add(name, name);
            var column = grid.Columns[index];
            column.FillWeight = weight;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            column.DefaultCellStyle.Alignment = column.HeaderCell.Style.Alignment =
                figure ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft;
        }
    }

    private static TableLayoutPanel Rows(params SizeType[] rows)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in rows) table.RowStyles.Add(row == SizeType.Percent ? new RowStyle(SizeType.Percent, 100) : new RowStyle(row));
        return table;
    }

    /// <summary>Muted help text that wraps to the column width.</summary>
    private static Label Note(string text)
    {
        var note = new SelectableLabel { Text = text, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 6) };
        Theme.SetTone(note, Tone.Muted);
        return note;
    }

    private static Card ActionCard(string title, string text, Button action)
    {
        var card = new Card(title) { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var note = Note(text);
        note.Margin = new Padding(0, 0, 12, 0);
        grid.Controls.Add(note, 0, 0);
        action.Anchor = AnchorStyles.Right;
        action.Margin = Padding.Empty;
        grid.Controls.Add(action, 1, 0);
        card.Controls.Add(grid);
        return card;
    }

    /// <summary>Tab pages without strip or frame: each page fills the control.</summary>
    private sealed class PageHost : TabControl
    {
        public override Rectangle DisplayRectangle => ClientRectangle;
    }

    private const int EmSetCueBanner = 0x1501;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Theme.Changed -= OnThemeChanged;
            _statusTimer.Dispose();
            _tips.Dispose();
            ClearCopyMenu();
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
