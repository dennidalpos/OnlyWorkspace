using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.ConfigTool.Tray;
using OnlyWorkspace.Core;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Validation;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Security;
using OnlyWorkspace.Infrastructure.Storage;

namespace OnlyWorkspace.ConfigTool.Forms;

public sealed partial class MainConfigForm : Form
{
    private readonly string _configDir;
    private readonly FileConfigurationService _configService;
    private static readonly string WindowTitle = $"OnlyWorkspace {OnlyWorkspace.Core.ProductInfo.Version} - Gestione e Configurazione";

    private readonly DpapiSecretVault _vault;
    private readonly AdminPasswordService _adminPassword;
    private readonly TrayApplicationContext? _trayContext;
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100 };
    private WorkspaceConfig _config = new();
    private readonly Dictionary<string, string> _inMemorySecrets = new();
    private readonly Dictionary<string, string> _inMemoryWebSecrets = new();
    private ProfileEditor? _profileEditor;
    private ProfileEditor Profiles => _profileEditor ??= new ProfileEditor(() => _config, _inMemorySecrets, _inMemoryWebSecrets);
    private System.Threading.CancellationTokenSource? _webSupportCheck;
    private bool _isDirty;
    private bool _isLocked;
    private readonly Dictionary<Control, bool> _lockedControls = new();
    private bool _isLoading;
    // Set while code rewrites context fields, so their change events don't re-derive the preset.
    private bool _syncingContext;
    // Set while list entries are re-assigned to refresh their text: the selection event must not reload the editor.
    private bool _refreshingList;
    // Limits and capabilities read from the provider for _modelLimitsKey ("profileId|type|url|model").
    private ModelInfo? _modelInfo;
    private string? _modelLimitsKey;
    // Set when config.json or secrets.bin exist but cannot be read: saving would overwrite them.
    private string? _loadProblem;
    private bool LoadBlocked => _loadProblem != null;
    /// <summary>Load failed but config.json still parsed: its values are on screen, read-only.</summary>
    private bool _fileShown;

    private void MarkDirty()
    {
        if (_isLoading) return;
        if (!_isDirty)
        {
            _isDirty = true;
            UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        Text = _isDirty ? WindowTitle + " *" : WindowTitle;
    }

    private const string DetectModelsText = "Scegli modello…";

    private Panel _sidebar = null!;
    private TableLayoutPanel _footer = null!;
    private Panel _pageHost = null!;
    private Label _lblPageTitle = null!;
    private readonly List<NavButton> _navButtons = new();
    private readonly List<Control> _pages = new();
    private ListBox _lstProfiles = null!;
    private TextBox _txtProfileId = null!;
    private TextBox _txtDisplayName = null!;
    private ComboBox _cmbProvider = null!;
    private ComboBox _cmbCloudPreset = null!;
    private bool _syncingPreset;
    private TextBox _txtEndpoint = null!;
    private TextBox _txtModel = null!;
    private Button _btnDetectModels = null!;
    private ComboBox _cmbAuthType = null!;
    private TextBox _txtToken = null!;
    private CheckBox _chkShowToken = null!;
    private TableLayoutPanel _tokenRow = null!;
    private ComboBox _cmbPreset = null!;
    private NumericUpDown _numBudget = null!;
    private Label _lblBudgetHint = null!;
    private NumericUpDown _numNumCtx = null!;
    private NumericUpDown _numMaxTokens = null!;
    private NumericUpDown _numTemp = null!;
    private CheckBox _chkDisableThinking = null!;
    private TextBox _txtKeepAlive = null!;
    private NumericUpDown _numFirstTokenTimeout = null!;
    private NumericUpDown _numIdleTimeout = null!;
    private CheckBox _chkUseProxy = null!;
    private CheckBox _chkExternal = null!;
    private TableLayoutPanel _webActions = null!;
    private CheckBox _chkAllowAttachments = null!;
    private CheckBox _chkPseudonymizeAmounts = null!;
    private CheckBox _chkPseudonymizeDates = null!;
    private ComboBox _cmbExposure = null!;
    private ComboBox _cmbRegion = null!;
    private SelectableLabel _lblExposure = null!;
    private SelectableLabel _lblAttachmentsNote = null!;
    private SelectableLabel _lblWebActionsNote = null!;
    private CheckBox _chkWebSearch = null!;
    private TextBox _txtWebSearchKey = null!;
    private Label _lblWebSupport = null!;
    private Button _btnWebRetry = null!;
    private Label _lblModelLimits = null!;
    private OperationalStatusLabel _lblTestResult = null!;
    private Button _btnTestProfile = null!;

    private ComboBox _cmbDefaultProfile = null!;
    private NumericUpDown _numMaxSearch = null!;
    private NumericUpDown _numFollowUpDays = null!;
    private NumericUpDown _numMaxAnalysis = null!;
    private ComboBox _cmbLogLevel = null!;
    private CheckBox _chkHistoryEnabled = null!;
    private CheckBox _chkArchiveEnabled = null!;
    private CheckBox _chkIndexAttachments = null!;
    private CheckBox _chkAttachmentOcr = null!;
    private NumericUpDown _numRetentionDays = null!;
    private NumericUpDown _numMaxConversations = null!;
    private CheckBox _chkAutostart = null!;
    private Label _lblAddinState = null!;
    private Button _btnRepairAddin = null!;
    private Button _btnRefreshStorage = null!;
    private Button _btnOpenDataFolder = null!;

    private TextBox _txtLog = null!;

    private OperationalStatusLabel _lblStatus = null!;
    private Button _btnSave = null!;
    private Button _btnUnlock = null!;
    private Button _btnResetPassword = null!;
    private Button _btnManagePassword = null!;
    private bool _requiresElevation;
    private List<TableLayoutPanel>? _batchGrids;

    public MainConfigForm(string? configDir = null) : this(configDir, null)
    {
    }

    public MainConfigForm(string? configDir, TrayApplicationContext? trayContext, string? preferencesDir = null)
    {
        _configDir = configDir ?? OnlyWorkspace.Core.AppPaths.ConfigDirectory;
        _configService = new FileConfigurationService(_configDir, preferencesDir);
        _vault = new DpapiSecretVault();
        _adminPassword = new AdminPasswordService(_configDir);
        _trayContext = trayContext;

        InitializeFormComponents();
        Ui.ScaleFromDesignDpi(this);
        DisabledControlTips.Attach(this, _tips);
        // Watch first: it reads a theme changed since the tray started, before the window takes its colours.
        Theme.Watch();
        ApplyTheme();
        Theme.Changed += OnThemeChanged;
        LoadActiveConfiguration();
    }

    private void InitializeFormComponents()
    {
        Text = WindowTitle;
        Icon = AppIconHelper.GetAppIcon();
        // Fits 1366x768 at 100% and 1920x1080 at 150%; FitToScreen handles anything smaller.
        ClientSize = new Size(900, 600);
        MinimumSize = new Size(820, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        _footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 5,
            Padding = new Padding(16, 10, 16, 10)
        };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 4; i++) _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.Paint += (s, e) =>
        {
            using var pen = new Pen(Theme.Colors.Border);
            e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
        };
        _footer.Resize += (s, e) => _footer.Invalidate();

        _lblStatus = new OperationalStatusLabel
        {
            Text = "Pronto",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        Theme.SetTone(_lblStatus, Tone.Muted);
        _btnSave = PrimaryButton("Salva");
        _btnSave.Click += async (s, e) =>
        {
            ApplyProfileIdChange();
            await SaveActiveConfiguration();
        };
        Tip(_btnSave, "Controlla e salva le impostazioni per tutti gli utenti di questo PC (config.json e chiavi cifrate in %ProgramData%\\OnlyWorkspace). " +
            "Serve un amministratore (richiesta UAC). Il riquadro di Outlook applica le modifiche entro pochi secondi.");

        _btnUnlock = SecondaryButton("Sblocca");
        _btnUnlock.Click += (s, e) => UnlockConfiguration();
        Tip(_btnUnlock, "La configurazione è protetta da password: inseriscila per modificare i campi in questa finestra. Le scelte personali (Le mie cassette) restano sempre modificabili.");
        _btnResetPassword = SecondaryButton("Reimposta password (UAC)");
        _btnResetPassword.Click += async (s, e) => await ResetAdminPassword();
        Tip(_btnResetPassword, "Password dimenticata: un amministratore di Windows (richiesta UAC) ne imposta una nuova o la toglie.");

        var btnClose = SecondaryButton("Chiudi");
        Tip(btnClose, "Chiude la finestra; OnlyWorkspace resta attivo nell'area di notifica. Le modifiche non salvate vengono chieste prima di chiudere.");
        btnClose.MinimumSize = new Size(90, 32);
        btnClose.Margin = new Padding(8, 0, 0, 0);
        btnClose.Click += (s, e) => Close();

        _footer.Controls.Add(_lblStatus, 0, 0);
        _footer.Controls.Add(_btnUnlock, 1, 0);
        _footer.Controls.Add(_btnResetPassword, 2, 0);
        _footer.Controls.Add(_btnSave, 3, 0);
        _footer.Controls.Add(btnClose, 4, 0);

        _sidebar = new Panel { Dock = DockStyle.Left, Width = 176, Padding = new Padding(8, 14, 8, 8) };
        var nav = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        AddStackRow(nav, Ui.SetFont(new SelectableLabel
        {
            Text = "OnlyWorkspace",
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0)
        }, 11F, family: "Segoe UI Semibold"));
        var lblVersion = Ui.SetFont(new SelectableLabel
        {
            Text = "Configurazione · " + OnlyWorkspace.Core.ProductInfo.Version,
            AutoSize = true,
            Margin = new Padding(9, 0, 0, 14)
        }, 8.25F);
        Theme.SetTone(lblVersion, Tone.Muted);
        AddStackRow(nav, lblVersion);
        _sidebar.Controls.Add(nav);

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 10) };
        _pageHost = new Panel { Dock = DockStyle.Fill };
        _lblPageTitle = Ui.SetFont(new SelectableLabel
        {
            Dock = DockStyle.Top,
            Height = 40,
            TextAlign = ContentAlignment.TopLeft
        }, 14F, family: "Segoe UI Semibold");
        content.Controls.Add(_pageHost);
        content.Controls.Add(_lblPageTitle);

        // Fill must be added first so the docked sidebar/footer claim their edges before it.
        Controls.Add(content);
        Controls.Add(_sidebar);
        Controls.Add(_footer);
        // Docking follows the add order; Tab goes sidebar, page, footer.
        _sidebar.TabIndex = 0;
        content.TabIndex = 1;
        _footer.TabIndex = 2;

        BuildBatched(() =>
        {
            AddPage(nav, "Profili IA", "\uE99A", BuildProfilesPage(),
                "Quali modelli IA usa l'add-in, dove si trovano (sul PC o in cloud), con quale chiave e cosa possono ricevere.");
            AddPage(nav, "Generale", "\uE713", BuildGeneralPage(),
                "Profilo predefinito, limiti delle ricerche, storico delle chat, password amministrativa, avvio con Windows e stato dell'add-in in Outlook.");
            AddPage(nav, "Archivio email", "\uE715", BuildArchivePage(),
                "Archivio locale cifrato della posta e, per il tuo utente, quali cassette archiviare.");
            AddPage(nav, "Manutenzione e backup", "\uE74E", BuildBackupPage(),
                "Copia e ripristino di configurazione e chiavi, cartella dei dati e registro delle operazioni di questa sessione.");
            AddPage(nav, "Privacy e dati", "\uE72E", BuildPrivacyPage(),
                "Informativa sull'uscita dei dati, spazio occupato dai file dell'app, chiavi di registro e server che ricevono dati.");
            AddPage(nav, "Informazioni", "\uE897", BuildAboutPage(),
                "Versione, autore, licenza e componenti di terze parti con le loro licenze.");
        });
        // Filler row: without it the last entry would take the leftover height.
        nav.RowCount++;
        nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        ShowPage(0);
    }

    private readonly List<string> _pageDescriptions = new();

    private void AddPage(TableLayoutPanel nav, string title, string glyph, Control page, string description)
    {
        int index = _pages.Count;
        _pageDescriptions.Add(description);
        var button = new NavButton
        {
            Text = title,
            Glyph = glyph,
            Height = 36,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 1, 0, 1)
        };
        button.Click += (s, e) => ShowPage(index);
        Tip(button, description);
        AddStackRow(nav, button);
        _navButtons.Add(button);

        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pageHost.Controls.Add(page);
        _pages.Add(page);
    }

    // Area each page was last laid out for: showing a page again at the same size needs no new layout.
    private readonly Dictionary<Control, Size> _laidOutFor = new();
    // Pages built at their first visit, before they show.
    private readonly Dictionary<Control, Func<Action>> _fillOnFirstShow = new();

    /// <summary>
    /// Shows a section. Becoming visible relaid out the whole page, every auto-size grid measuring its labels again:
    /// up to 1.3 s for Informazioni at each visit (measured 26/10/2026). Layout is held back while the page appears
    /// and runs only when the area changed size since the page was last laid out (window resized, DPI changed);
    /// then it runs once per container, innermost first, instead of each grid relaying out its parents as it appears.
    /// </summary>
    private void ShowPage(int index)
    {
        var page = _pages[index];
        Action? afterLayout = null;
        if (_fillOnFirstShow.TryGetValue(page, out var fill))
        {
            _fillOnFirstShow.Remove(page);
            afterLayout = fill();
        }
        bool sameArea = _laidOutFor.TryGetValue(page, out var area) && area == _pageHost.ClientSize;
        var containers = Containers(page).ToList();
        foreach (var container in containers) container.SuspendLayout();
        try
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                if (i != index) _pages[i].Visible = false;
                _navButtons[i].Selected = i == index;
            }
            page.Visible = true;
        }
        finally
        {
            // Pre-order list reversed: children resume before their parents, which are still suspended.
            for (int i = containers.Count - 1; i >= 0; i--) containers[i].ResumeLayout(!sameArea);
        }
        afterLayout?.Invoke();
        _laidOutFor[page] = _pageHost.ClientSize;

        static IEnumerable<Control> Containers(Control root)
        {
            yield return root;
            foreach (Control child in root.Controls)
                if (child.HasChildren)
                    foreach (var nested in Containers(child)) yield return nested;
        }
        _lblPageTitle.Text = _navButtons[index].Text;
        if (_navButtons[index].Text == "Privacy e dati")
        {
            RefreshDestinations();
            RefreshEgress();
            _ = RefreshStorageAsync();
        }
        Tip(_lblPageTitle, _pageDescriptions[index]);
    }

    private void ApplyTheme()
    {
        var c = Theme.Colors;
        BackColor = c.Window;
        ForeColor = c.Text;
        _sidebar.BackColor = c.Sidebar;
        Theme.Apply(this);
        Theme.ApplyWindowChrome(this);
        Invalidate(true);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(new Action(ApplyTheme));
        else ApplyTheme();
    }

    /// <summary>Keeps the window inside the working area (small screens, high scaling).</summary>
    private void FitToScreen(bool center)
    {
        if (WindowState != FormWindowState.Normal) return;

        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        var bounds = new Rectangle(Location, new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height)));
        if (center)
        {
            bounds.X = area.X + (area.Width - bounds.Width) / 2;
            bounds.Y = area.Y + (area.Height - bounds.Height) / 2;
        }
        else if (bounds.Size == Size)
        {
            return;
        }
        else
        {
            bounds.X = Math.Max(area.X, Math.Min(bounds.X, area.Right - bounds.Width));
            bounds.Y = Math.Max(area.Y, Math.Min(bounds.Y, area.Bottom - bounds.Height));
        }
        Bounds = bounds;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowChrome(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToScreen(center: true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (IsHandleCreated) BeginInvoke(new Action(() => FitToScreen(center: false)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _webSupportCheck?.Cancel();
            _probeRun?.Cancel();
            _connectionCheck?.Cancel();
            _capabilityRefresh?.Cancel();
            // Dispose can run twice (tray exit, then the form's own close): cancel only once.
            if (!_archiveLifetime.IsCancellationRequested) _archiveLifetime.Cancel();
            Theme.Changed -= OnThemeChanged;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class ExposureItem
    {
        public ExposureItem(ExposureLevel level) => Level = level;
        public ExposureLevel Level { get; }
        public override string ToString() => ExposurePolicy.Label(Level);
    }

    private ExposureLevel SelectedExposure => _cmbExposure.SelectedItem is ExposureItem item ? item.Level : ExposureLevel.Minimal;

    private sealed class RegionItem
    {
        public RegionItem(ServiceRegion region) => Region = region;
        public ServiceRegion Region { get; }
        public override string ToString() => ServiceRegions.Label(Region);
    }

    private ServiceRegion SelectedRegion => _cmbRegion.SelectedItem is RegionItem item ? item.Region : ServiceRegion.Unspecified;

    private static readonly Dictionary<string, string> WebActionTips = new()
    {
        [WebSearchAction.Chat] = "Chat: la risposta può includere notizie e fatti recenti trovati sul web.",
        [WebSearchAction.Summary] = "Sintesi di un'email: aggiunge informazioni pubbliche su aziende e argomenti citati.",
        [WebSearchAction.Reply] = "Bozze di risposta: la bozza può tenere conto di informazioni pubbliche aggiornate.",
        [WebSearchAction.FollowUp] = "Follow-up: verifica sul web novità utili prima di proporre il sollecito.",
        [WebSearchAction.Security] = "Verifica sicurezza: la ricerca web riceve l'intera richiesta pseudonimizzata, con il testo dell'email (solo a Esteso).",
        [WebSearchAction.Actions] = "Attività e scadenze: aggiunge, a parte, avvisi e scadenze pubbliche delle aziende dei mittenti.",
        [WebSearchAction.Extract] = "Estrai dati: può integrare i dati estratti con informazioni pubbliche trovate sul web.",
        [WebSearchAction.Briefing] = "Briefing: aggiunge notizie recenti sugli interlocutori e sugli argomenti della giornata.",
        [WebSearchAction.Analysis] = "Analisi della posta (Cerca e analizza): integra la risposta con fonti web, citate come [W1], [W2]…"
    };

    private Control BuildGeneralPage()
    {
        var page = ScrollPage(out var stack);

        var behavior = AddGroup(stack, "Add-in Outlook");

        _cmbDefaultProfile = AddRow(behavior, "Profilo predefinito:", Combo(240),
            "Modello IA usato da tutti gli utenti nel riquadro di Outlook.");
        _cmbDefaultProfile.SelectedIndexChanged += (s, e) =>
        {
            if (!_refreshingList && _cmbDefaultProfile.SelectedItem is DefaultChoice choice)
            {
                _config.DefaultProfile = choice.Profile.Id;
                RefreshProfileNames(combo: false);
                MarkDirty();
            }
        };

        _chkIndexAttachments = AddWide(behavior, new ThemedCheckBox { Text = "Testo degli allegati nell'indice dell'archivio", AutoSize = true },
            "Rende cercabile anche il testo degli allegati (PDF, Office, testo); i file non vengono copiati. Spegnendola si cancellano quelle parole e i fatti derivati (i nomi dei file restano); riaccendendola Outlook le rilegge.");
        _chkIndexAttachments.CheckedChanged += (s, e) =>
        {
            _config.Archive.IndexAttachmentText = _chkIndexAttachments.Checked;
            MarkDirty();
        };

        _chkAttachmentOcr = AddWide(behavior, new ThemedCheckBox { Text = "OCR di scansioni e immagini allegate", AutoSize = true },
            "Legge sul PC, con l'OCR di Windows, PDF scansionati e immagini allegate. Spenta: restano non letti e i testi già riconosciuti restano; riaccendendola vengono riletti.");
        _chkAttachmentOcr.CheckedChanged += (s, e) => { _config.AttachmentOcr = _chkAttachmentOcr.Checked; MarkDirty(); };

        _btnManagePassword = AddRow(behavior, "Password amministrativa:", SecondaryButton("Imposta / cambia / disattiva…"),
            "Facoltativa: chi apre questa finestra vede le impostazioni ma può cambiarle solo con la password. Se la dimentichi, «Reimposta password» la reimposta con i diritti di amministratore.");
        _btnManagePassword.Click += async (s, e) => await ManageAdminPassword();

        _numMaxSearch = Numeric(10, 200, 50, 10);
        _numMaxSearch.ValueChanged += (s, e) => { _config.Limits.MaxSearchItems = (int)_numMaxSearch.Value; MarkDirty(); };
        _numFollowUpDays = Numeric(1, 30, 5, 1);
        _numFollowUpDays.ValueChanged += (s, e) => { _config.Limits.FollowUpDays = (int)_numFollowUpDays.Value; MarkDirty(); };
        AddPair(behavior,
            "Max agenda/contatti:", _numMaxSearch, "Quanti appuntamenti o contatti al massimo mostra una ricerca nel riquadro. Non limita la ricerca nella posta.",
            "Giorni per follow-up:", _numFollowUpDays, "In Cerca e analizza → Email senza risposta: dopo quanti giorni senza risposta un'email inviata viene proposta per un sollecito.");

        _numMaxAnalysis = Numeric(50, 2000, 300, 50);
        _numMaxAnalysis.ValueChanged += (s, e) => { _config.Limits.MaxAnalysisEmails = (int)_numMaxAnalysis.Value; MarkDirty(); };
        _cmbLogLevel = Combo(130);
        _cmbLogLevel.Items.AddRange(new object[] {
            new LogLevelItem("Verbose", "Traccia dettagliata"), new LogLevelItem("Debug", "Diagnostica"),
            new LogLevelItem("Information", "Informazioni"), new LogLevelItem("Warning", "Avvisi"),
            new LogLevelItem("Error", "Errori"), new LogLevelItem("Fatal", "Errori critici") });
        _cmbLogLevel.SelectedIndexChanged += (s, e) =>
        {
            if (_cmbLogLevel.SelectedItem != null)
            {
                _config.Logging.Level = ((LogLevelItem)_cmbLogLevel.SelectedItem).Value;
                MarkDirty();
            }
        };
        // Own rows: with a «?» the log level did not fit the pair's second column and its button was cut at the card edge.
        AddRow(behavior, "Lotto analisi email:", _numMaxAnalysis,
            "In Cerca e analizza: quante email leggere a ogni passaggio prima di chiedere se continuare. Non limita la ricerca. Con un modello locale ogni email può richiedere alcuni secondi.");
        AddRow(behavior, "Livello di log:", _cmbLogLevel,
            "Quanto scrive il registro tecnico dell'add-in. Informazioni per l'uso normale; Diagnostica o Traccia dettagliata solo per cercare un problema: scrivono molto di più.");

        var history = AddGroup(stack, "Storico chat");

        _chkHistoryEnabled = AddWide(history, new ThemedCheckBox { Text = "Abilita memorizzazione locale dello storico chat (%LOCALAPPDATA%, cifrato)", AutoSize = true },
            "Conserva chat e risultati delle azioni per riaprirli dallo Storico del riquadro. Sono cifrati e leggibili solo dall'utente Windows che li ha creati.");
        _chkHistoryEnabled.CheckedChanged += (s, e) => { _config.History.Enabled = _chkHistoryEnabled.Checked; MarkDirty(); };

        _numRetentionDays = Numeric(1, 365, 90, 1);
        _numRetentionDays.ValueChanged += (s, e) => { _config.History.RetentionDays = (int)_numRetentionDays.Value; MarkDirty(); };
        _numMaxConversations = Numeric(10, 1000, 200, 10);
        _numMaxConversations.ValueChanged += (s, e) => { _config.History.MaxConversations = (int)_numMaxConversations.Value; MarkDirty(); };
        AddPair(history,
            "Giorni di ritenzione:", _numRetentionDays, "Dopo quanti giorni le conversazioni salvate si cancellano da sole.",
            "Max conversazioni:", _numMaxConversations, "Quante conversazioni tenere al massimo per utente: oltre, le più vecchie si cancellano.");

        var setup = AddGroup(stack, "Primo avvio");
        var btnWizard = AddRow(setup, "Procedura guidata:", SecondaryButton("Configurazione guidata…"),
            "Riapre la procedura guidata del primo avvio, in cinque passi: modello IA, archivio, avvio con Windows e controllo dell'add-in in Outlook.");
        btnWizard.Click += (s, e) => RunSetupWizard();
        _lblAddinState = AddRow(setup, "Add-in Outlook:", new SelectableLabel { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(3, 6, 3, 6) },
            "Se Outlook carica OnlyWorkspace per il tuo utente, letto dalle impostazioni di Windows.");
        _btnRepairAddin = AddRow(setup, "", SecondaryButton("Ripara add-in"),
            "Se la scheda OnlyWorkspace non compare in Outlook: la riattiva per il tuo utente e chiede a Outlook di non disattivarla più. Poi chiudi e riapri Outlook.");
        _btnRepairAddin.Click += (s, e) =>
        {
            try { AddinRegistration.Repair(); }
            catch (Exception ex) { LogActivity("Riparazione dell'add-in non riuscita: " + ex.Message); CopyableMessageBox.Show(this, "Riparazione non riuscita: " + ErrorText.For(ex), "Add-in Outlook", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            RefreshAddinState();
        };

        var tray = AddGroup(stack, "Area di notifica (utente corrente)");
        _chkAutostart = AddWide(tray, new ThemedCheckBox
        {
            Text = "Avvia OnlyWorkspace all'accesso a Windows, ridotto nell'area di notifica",
            AutoSize = true,
            Checked = StartupManager.IsRunAtStartupEnabled()
        }, "L'icona nell'area di notifica ospita il motore dell'archivio: ricerca e avvisi di spazio senza aprire questa finestra. Scrive OnlyWorkspaceTray in HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run (solo utente corrente); si applica subito, senza Salva. Chiudere la finestra la nasconde nell'area di notifica.");
        _chkAutostart.CheckedChanged += (s, e) =>
        {
            if (!_isLoading && !StartupManager.SetRunAtStartup(_chkAutostart.Checked)) SyncAutostart();
        };

        return page;
    }

    private Control BuildArchivePage()
    {
        var page = ScrollPage(out var stack);
        _archivePage = page;

        var archive = AddGroup(stack, "Archivio locale");

        _chkArchiveEnabled = AddWide(archive, new ThemedCheckBox { Text = "Archivio locale delle email", AutoSize = true },
            "Attiva l'archivio cifrato per utente; ognuno sceglie le cassette\nin «Le mie cassette».\nConserva corpi integrali e testi degli allegati consentiti, con ricerca\nsull'intero testo, non i file originali.\nLa raccolta richiede Outlook aperto e PC inattivo.\nLa modifica richiede Salva.");
        _chkArchiveEnabled.CheckedChanged += (s, e) => { _config.Archive.Enabled = _chkArchiveEnabled.Checked; MarkDirty(); };

        var mine = AddGroup(stack, "Le mie cassette");
        _mailboxes = new MailboxAuthorizationPanel(_configService, _tips, _archiveLifetime.Token);
        AddWide(mine, _mailboxes, "Scelte solo per il tuo utente Windows: quali cassette archiviare. Si salvano subito, senza Salva e senza diritti di amministratore.");
        page.VisibleChanged += async (s, e) => { if (page.Visible) await _mailboxes.ShowAsync(); };

        return page;
    }

    private readonly System.Threading.CancellationTokenSource _archiveLifetime = new();
    private MailboxAuthorizationPanel _mailboxes = null!;
    private Panel _archivePage = null!;

    /// <summary>Archivio email → Le mie cassette, asked from the pane when no mailbox is authorized.</summary>
    public void ShowMailboxes()
    {
        ShowPage(_pages.IndexOf(_archivePage));
        _archivePage.ScrollControlIntoView(_mailboxes);
    }

    private Control BuildBackupPage()
    {
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var btnExportBackup = PrimaryButton("Esegui backup…");
        btnExportBackup.Click += BtnExportBackup_Click;
        Tip(btnExportBackup, "Salva in un file la configurazione e le chiavi API. Le chiavi sono cifrate per questo PC: il file si ripristina solo qui. Non contiene archivio né storico.");
        layout.Controls.Add(ActionCard("Backup di configurazione e credenziali", "Configurazione e credenziali in un unico archivio, valido solo su questo PC.", btnExportBackup), 0, 0);

        var btnImportRestore = PrimaryButton("Ripristina backup...");
        btnImportRestore.Kind = ButtonKind.Danger;
        btnImportRestore.Click += BtnImportRestore_Click;
        Tip(btnImportRestore, "Sostituisce configurazione e chiavi attuali con quelle di un backup fatto su questo PC. Serve un amministratore di Windows (richiesta UAC).");
        layout.Controls.Add(ActionCard("Ripristino da backup", "Sostituisce configurazione e credenziali attuali con quelle di un backup.", btnImportRestore), 0, 1);

        var btnOpenFolder = _btnOpenDataFolder = SecondaryButton("Apri cartella dati");
        btnOpenFolder.MinimumSize = new Size(0, 30);
        btnOpenFolder.Click += (s, e) =>
        {
            // Never a click that does nothing: a missing folder or a failed start is said in the status bar.
            if (!Directory.Exists(_configDir))
            {
                SetStatus($"La cartella dei dati non esiste ancora: si crea al primo salvataggio ({_configDir}).", Tone.Warning);
                return;
            }
            try { Process.Start("explorer.exe", _configDir); }
            catch (Exception ex)
            {
                SetStatus("Impossibile aprire la cartella dei dati: " + _configDir, Tone.Danger);
                LogActivity("Apertura della cartella dati non riuscita: " + ex.Message);
            }
        };
        Tip(btnOpenFolder, $"Configurazione: {Path.Combine(_configDir, "config.json")}\nVault segreti: {Path.Combine(_configDir, "secrets.bin")}");
        layout.Controls.Add(ActionCard("Percorsi dei dati", "Cartella con config.json e il vault cifrato secrets.bin.", btnOpenFolder), 0, 2);

        var logCard = new Card("Log attività") { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _txtLog = Ui.SetFont(new ThemedTextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None
        }, 8.5F, family: "Consolas");
        Tip(_txtLog, "Cosa è successo da quando hai aperto questa finestra: salvataggi, prove, errori. Si può copiare; non viene salvato.");
        logCard.Controls.Add(_txtLog);
        layout.Controls.Add(logCard, 0, 3);

        return layout;
    }

    private SelectableLabel _lblDestinations = null!;
    private SelectableLabel _lblEgress = null!;
    private Button _btnEgressAccept = null!;
    private Button _btnEgressRevoke = null!;
    private Button _btnEgressExport = null!;

    private static string WindowsUser => Environment.UserDomainName + "\\" + Environment.UserName;

    /// <summary>Opens the notice; an acceptance is recorded in the configuration being edited (saved by the caller).</summary>
    private bool AskDataEgress()
    {
        if (_isLocked || LoadBlocked || _elevationBusy) return false;
        UpdateSelectedProfileFromUi();
        PrepareProfileSecrets();
        var profiles = DataEgress.EgressProfiles(_config).ToList();
        if (profiles.Count == 0) return false;
        using var dialog = new DataEgressConsentForm(profiles);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        var consent = DataEgress.Accept(_config, WindowsUser, Environment.MachineName, DateTime.UtcNow, _vault.GetSecret,
            dialog.ConfirmedUnspecifiedRegionProfiles);
        LogActivity($"Informativa accettata, in attesa di salvataggio, da {consent.Describe()} per: " +
            string.Join(", ", consent.Providers.Select(p => $"{p.Host} ({ExposurePolicy.Label(p.Level)})")) + ".");
        MarkDirty();
        RefreshEgress();
        return true;
    }

    private async Task<bool> AcceptAndSaveDataEgressAsync()
    {
        var previous = _config.DataEgress?.Consent;
        int events = _config.DataEgress?.History.Count ?? 0;
        bool dirty = _isDirty;
        if (dirty && CopyableMessageBox.Show(this, "«Leggi e autorizza» salva anche tutte le altre modifiche non ancora salvate.\n\nProseguire?",
                "Modifiche non salvate", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        if (!AskDataEgress()) return false;
        if (await SaveActiveConfiguration())
        {
            LogActivity("Autorizzazioni per profilo registrate e salvate.");
            return true;
        }
        RestoreDataEgress(previous, events, dirty);
        return false;
    }

    private void RestoreDataEgress(DataEgressConsent? previous, int events, bool dirty)
    {
        _config.DataEgress.Consent = previous;
        _config.DataEgress.History.RemoveRange(events, _config.DataEgress.History.Count - events);
        DataEgress.BindProfiles(_config, _vault.GetSecret);
        _isDirty = dirty;
        UpdateTitle();
        RefreshEgress();
    }

    /// <summary>The saved configuration (what is in force) as a printable PDF; edits not yet saved are not in it.</summary>
    private async Task ExportDataEgressAsync()
    {
        if (!ConfirmSavedFilesOnly("Il PDF")) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Esporta l'informativa sull'uscita dei dati",
            Filter = "Documento PDF (*.pdf)|*.pdf",
            DefaultExt = "pdf",
            AddExtension = true,
            FileName = $"OnlyWorkspace-informativa-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.pdf"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _btnEgressExport.Enabled = false;
        try
        {
            var savedService = new FileConfigurationService(_configDir);
            var saved = await Task.Run(() => savedService.LoadConfiguration());
            var workstation = new DataEgressWorkstation
            {
                Machine = Environment.MachineName,
                ExportedBy = WindowsUser,
                ProductVersion = ProductInfo.Version,
                Windows = $"{WorkstationInfo.WindowsProductName()} {WorkstationInfo.WindowsDisplayVersion()} ({Environment.OSVersion.Version})".Trim(),
                Outlook = $"{WorkstationInfo.OutlookVersion()} {WorkstationInfo.OutlookBitness()}",
                WebView2 = WorkstationInfo.WebView2Version(),
                ConfigPath = Path.Combine(_configDir, "config.json"),
                GeneratedUtc = DateTime.UtcNow
            };
            var privacyAudit = await Task.Run(() => new OnlyWorkspace.Infrastructure.Security.PseudonymizationAuditStore().Read());
            string html = DataEgressReport.RenderHtml(saved, workstation, privacyAudit, savedService.ResolveSecret);
            var consent = saved.DataEgress?.Consent;
            string footer = consent == null ? "Informativa non accettata" : $"Informativa v{consent.NoticeVersion} · SHA-256 {consent.NoticeHash}";
            await DataEgressPdfExporter.ExportAsync(this, html, dialog.FileName, footer);
            LogActivity("Informativa esportata in PDF: " + dialog.FileName);
            SetStatus("Informativa esportata: " + dialog.FileName, Tone.Success);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            LogActivity("Esportazione informativa non riuscita: " + ex);
            SetStatus("Esportazione non riuscita: " + ErrorText.For(ex), Tone.Danger);
            CopyableMessageBox.Show(this, "Esportazione non riuscita: " + ErrorText.For(ex) +
                (ex is WebView2RuntimeNotFoundException ? "\n\nServe il runtime Microsoft Edge WebView2, lo stesso richiesto dal riquadro di Outlook." : ""),
                "Uscita dei dati", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnEgressExport.Enabled = true;
        }
    }

    private async void RevokeDataEgress()
    {
        if (_isLocked || LoadBlocked || _config.DataEgress?.Consent == null) return;
        if (CopyableMessageBox.Show(this, "Revocare l'autorizzazione all'uscita dei dati?\n\nDopo il salvataggio i profili cloud e la ricerca web esterna si bloccano per tutti gli utenti di questo PC. " +
                "Le richieste già ricevute dai fornitori non si possono ritirare.", "Uscita dei dati", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, "Revoca", "Annulla") != DialogResult.Yes) return;
        await ApplyRevocation();
    }

    /// <summary>Revokes and saves; a save that does not go through (UAC refused, write error) leaves the notice as it was.</summary>
    private async Task<bool> ApplyRevocation()
    {
        var consent = _config.DataEgress!.Consent;
        int events = _config.DataEgress.History.Count;
        bool dirty = _isDirty;
        DataEgress.Revoke(_config, WindowsUser, Environment.MachineName, DateTime.UtcNow);
        MarkDirty();
        bool saved = await SaveActiveConfiguration();
        if (saved) LogActivity("Informativa sull'uscita dei dati revocata.");
        else
        {
            RestoreDataEgress(consent, events, dirty);
        }
        RefreshEgress();
        return saved;
    }

    private void RefreshEgress()
    {
        var status = EgressStatus.Build(_config, _vault.GetSecret);
        _lblEgress.Text = status.Text;
        Theme.SetTone(_lblEgress, status.Tone);
        if (_isLocked) _lockedControls[_btnEgressAccept] = status.CanAccept;
        else _btnEgressAccept.Enabled = status.CanAccept;
        if (_isLocked) _lockedControls[_btnEgressRevoke] = status.CanRevoke;
        else _btnEgressRevoke.Enabled = status.CanRevoke;
    }
    private TableLayoutPanel _storageGrid = null!;
    private SelectableLabel _lblStorageTotal = null!;
    private System.Threading.CancellationTokenSource? _storageMeasure;

    /// <summary>Measures the generated files away from the UI thread; a newer request or closing the window drops an older one.</summary>
    private async System.Threading.Tasks.Task RefreshStorageAsync()
    {
        _storageMeasure?.Cancel();
        var measure = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_archiveLifetime.Token);
        _storageMeasure = measure;
        _lblStorageTotal.Text = "Calcolo dello spazio occupato…";
        // The rows are controls: built on the UI thread even when the await resumes elsewhere.
        void OnUi(Action action)
        {
            if (measure.IsCancellationRequested || IsDisposed) return;
            if (InvokeRequired) BeginInvoke(action); else action();
        }
        try
        {
            var report = await System.Threading.Tasks.Task.Run(() => AppStorageUsage.Measure(measure.Token, _configDir), measure.Token);
            OnUi(() => FillStorage(report));
        }
        catch (Exception ex)
        {
            // Called from async void handlers: nothing may escape.
            OnlyWorkspace.Infrastructure.Logging.OnlyWorkspaceLog.Logger.Warning(ex, "Storage measurement failed");
            try { OnUi(() => _lblStorageTotal.Text = "Spazio non calcolato: " + ErrorText.For(ex)); }
            catch (InvalidOperationException) { }
        }
    }

    // Rows last shown: measured again at every visit of Privacy e dati, rebuilt only when a value changed.
    private string? _storageShown;

    private void FillStorage(StorageReport report)
    {
        string shown = StorageText.Signature(report);
        if (shown == _storageShown && _storageGrid.Controls.Count > 0)
        {
            SetStorageTotal(report);
            return;
        }
        _storageShown = shown;
        _storageGrid.SuspendLayout();
        try
        {
            foreach (Control old in _storageGrid.Controls.Cast<Control>().ToList()) old.Dispose();
            _storageGrid.Controls.Clear();
            _storageGrid.RowStyles.Clear();
            _storageGrid.RowCount = 0;
            void Row(Control name, Control size, Control path)
            {
                int row = _storageGrid.RowCount++;
                _storageGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _storageGrid.Controls.Add(name, 0, row);
                _storageGrid.Controls.Add(size, 1, row);
                _storageGrid.Controls.Add(path, 2, row);
            }
            SelectableLabel Cell(string text, int left = 12, bool bold = false)
            {
                var label = new SelectableLabel { Text = text, AutoSize = true, MaximumSize = new Size(Ui.Px(_storageGrid, left == 0 ? 190 : 330), 0), Margin = new Padding(Ui.Px(_storageGrid, left), Ui.Px(_storageGrid, 4), Ui.Px(_storageGrid, 3), Ui.Px(_storageGrid, 4)) };
                return bold ? Ui.SetFont(label, 9F, FontStyle.Bold) : label;
            }
            Row(Cell("Voce", 0, bold: true), Cell("Spazio", bold: true), Cell("Percorso", bold: true));
            foreach (var item in report.Items)
            {
                string size = StorageText.Size(item);
                var name = Cell(item.Name, 0);
                var sizeCell = Cell(size);
                var path = Cell(item.Path);
                Tip(name, item.Note);
                Tip(sizeCell, StorageText.SizeTip(item));
                Tip(path, "Cartella o file su questo PC. Si può selezionare e copiare.");
                Row(name, sizeCell, path);
            }
        }
        finally
        {
            _storageGrid.ResumeLayout();
        }
        Theme.Apply(_storageGrid);
        SetStorageTotal(report);
    }

    private void SetStorageTotal(StorageReport report) => _lblStorageTotal.Text = StorageText.Total(report, DateTime.Now);


    /// <summary>One place that says what the app writes on the PC and which servers receive data.</summary>
    private Control BuildPrivacyPage()
    {
        var page = ScrollPage(out var stack);
        var egress = AddGroup(stack, "Uscita dei dati da Outlook");
        _lblEgress = AddWide(egress, new SelectableLabel { AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(3, 3, 3, 6) },
            "Se e da chi è stata accettata l'informativa che autorizza l'invio di dati di Outlook a servizi esterni. Senza, i profili cloud e la ricerca web esterna sono bloccati per tutti gli utenti di questo PC.");
        _btnEgressAccept = SecondaryButton("Leggi e autorizza…");
        _btnEgressAccept.Click += async (s, e) => await AcceptAndSaveDataEgressAsync();
        _btnEgressRevoke = SecondaryButton("Revoca");
        _btnEgressRevoke.Margin = new Padding(8, 3, 3, 3);
        _btnEgressRevoke.Click += (s, e) => RevokeDataEgress();
        _btnEgressExport = SecondaryButton("Esporta PDF…");
        _btnEgressExport.Margin = new Padding(8, 3, 3, 3);
        _btnEgressExport.Click += async (s, e) => await ExportDataEgressAsync();
        AddRow(egress, "", Inline(_btnEgressAccept, _btnEgressRevoke, _btnEgressExport), "Accettazione, revoca ed esportazione dell'informativa sull'uscita dei dati.");
        Tip(_btnEgressExport, "Salva un PDF A4 con postazione, profili IA, livelli e server autorizzati, dichiarazioni, responsabile che ha abilitato l'uscita dei dati, storico e testo accettato. Funziona anche in sola lettura.");
        Tip(_btnEgressAccept, "Apre riepilogo dei profili, informativa e dichiarazioni del responsabile. Accetta e salva lega l'autorizzazione a destinazioni, livello, permessi e chiavi; un cambiamento richiede rinnovo. Il normale Salva mantiene bloccati gli invii senza consenso.");
        Tip(_btnEgressRevoke, "Ritira l'autorizzazione e salva: da quel momento nessun dato di Outlook esce verso servizi esterni. La revoca resta nello storico.");

        var files = AddGroup(stack, "File sul PC e spazio occupato");
        _storageGrid = AddWide(files, new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = Padding.Empty },
            "File creati da OnlyWorkspace per il tuo utente Windows e, in fondo, quelli comuni a tutti gli utenti. Passa sul nome di una voce per sapere cosa contiene.");
        foreach (var width in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent }) _storageGrid.ColumnStyles.Add(new ColumnStyle(width, 100F));
        _lblStorageTotal = AddWide(files, new SelectableLabel { AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(3, 8, 3, 3) },
            "Somma delle voci sopra e spazio ancora libero sul disco dell'archivio. Sotto 5 GB liberi l'archivio avvisa, sotto 1 GB smette di acquisire email.");
        _btnRefreshStorage = AddRow(files, "", SecondaryButton("Aggiorna"),
            "Ricalcola lo spazio occupato. Legge solo le dimensioni dei file: non apre l'archivio e non cancella nulla.");
        _btnRefreshStorage.Click += async (s, e) => await RefreshStorageAsync();
        AddWide(files, new SelectableLabel
        {
            Text = "File su richiesta, non contati: CSV estratti (Download), ZIP di diagnostica o di backup (Desktop o cartella scelta).",
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Margin = new Padding(3, 6, 3, 3)
        }, "File che crei tu con un comando: restano dove li hai salvati finché non li elimini.");

        var registry = AddGroup(stack, "Registro di Windows e sistema");
        AddWide(registry, new SelectableLabel
        {
            Text = "• Add-in: HKLM e HKCU Software\\Microsoft\\Office\\Outlook\\Addins\\OnlyWorkspace.Addin (caricamento in Outlook).\r\n" +
                   "• Avvio con Windows: HKCU Software\\Microsoft\\Windows\\CurrentVersion\\Run, voce OnlyWorkspaceTray.\r\n" +
                   "• Ripara add-in: HKCU Software\\Microsoft\\Office\\16.0\\Outlook\\Resiliency.\r\n" +
                   "• Setup: certificato OnlyApps negli archivi Root e TrustedPublisher del computer; facoltativamente le policy che nascondono «Prova il nuovo Outlook».",
            AutoSize = true,
            MaximumSize = new Size(600, 0)
        }, "Impostazioni di Windows scritte da installazione, procedura guidata e «Ripara add-in».");

        var network = AddGroup(stack, "Server che ricevono dati");
        _lblDestinations = AddWide(network, new SelectableLabel { AutoSize = true, MaximumSize = new Size(600, 0) },
            "A chi arrivano i dati con la configurazione attuale, ricavato dai profili; si aggiorna aprendo questa pagina.");
        return page;
    }

    /// <summary>Author, version, license and the third-party components with their licenses («?» of the ConfigTool).</summary>
    private Control BuildAboutPage()
    {
        // Built on first display: its many selectable rows would add seconds to every ConfigTool start.
        var page = new ScrollPanel();
        _fillOnFirstShow[page] = () =>
        {
            // Filled detached and batched, before the page shows: rows added to a shown page relayout it each time.
            var stack = NewStack();
            BuildBatched(() => FillAboutPage(stack));
            Ui.ScaleDynamic(stack, page);
            Theme.Apply(stack);
            page.Controls.Add(stack);
            // Column widths exist only after the page's first layout.
            return () => AlignLabelColumns(stack);
        };
        return page;
    }

    private void FillAboutPage(TableLayoutPanel stack)
    {
        var about = AddGroup(stack, "OnlyWorkspace");
        AddRow(about, "Autore:", new SelectableLabel { Text = ThirdPartyComponents.Author + " · " + ThirdPartyComponents.Publisher, AutoSize = true, Margin = new Padding(3, 6, 3, 6) }, "Autore e editore.");
        AddRow(about, "Versione:", new SelectableLabel { Text = ProductInfo.Version, AutoSize = true, Margin = new Padding(3, 6, 3, 6) }, "Versione installata.");
        AddRow(about, "Licenza:", new SelectableLabel { Text = ThirdPartyComponents.License, AutoSize = true, Margin = new Padding(3, 6, 3, 6) }, "Le licenze dei componenti di terze parti sono in THIRD-PARTY-NOTICES.txt nella cartella di installazione.");
        AddRow(about, "GitHub:", Link(ThirdPartyComponents.GitHubUrl, ThirdPartyComponents.GitHubUrl), "Apre il profilo GitHub dell'autore nel browser.");

        var credits = AddGroup(stack, "Contributi");
        foreach (var component in ThirdPartyComponents.All)
        {
            string meta = (component.Version != "-" ? component.Version + " · " : string.Empty) + component.License +
                          (component.Note.Length > 0 ? " · " + component.Note : string.Empty) + (component.Distributed ? string.Empty : " · solo sviluppo");
            AddRow(credits, component.Name, new SelectableLabel { Text = meta, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(3, 6, 3, 6) }, component.Url);
        }
    }

    /// <summary>
    /// Component names are wider than the usual label column: every group of the page takes the widest one,
    /// so the values start at the same column in each group.
    /// </summary>
    private static void AlignLabelColumns(Control page)
    {
        var grids = Grids(page).ToList();
        int width = grids.Max(g => g.GetColumnWidths()[0]);
        // One layout at the end: each MinimumSize set on a laid-out page relaid out the whole page (seconds for Informazioni).
        page.SuspendLayout();
        foreach (var grid in grids) grid.SuspendLayout();
        try
        {
            foreach (var grid in grids)
                foreach (Control control in grid.Controls)
                    if (grid.GetColumn(control) == 0 && control is SelectableLabel label)
                        label.MinimumSize = new Size(width - label.Margin.Horizontal, 0);
        }
        finally
        {
            foreach (var grid in grids) grid.ResumeLayout(true);
            page.ResumeLayout(true);
        }

        static IEnumerable<TableLayoutPanel> Grids(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (child is TableLayoutPanel { ColumnCount: 4 } grid) yield return grid;
                foreach (var nested in Grids(child)) yield return nested;
            }
        }
    }

    private LinkLabel Link(string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 6), LinkColor = Theme.Colors.Accent, ActiveLinkColor = Theme.Colors.Accent };
        link.LinkClicked += (s, e) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { LogActivity("Apertura collegamento non riuscita: " + ex); SetStatus("Impossibile aprire il collegamento: " + ErrorText.For(ex), Tone.Danger); }
        };
        return link;
    }

    private void RefreshDestinations()
    {
        var lines = _config.Profiles.Select(p =>
        {
            var host = Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u) ? u.Host : p.BaseUrl;
            var web = p.WebSearchEnabled ? "; ricerca web: " + string.Join(", ", WebSearchService.Destinations(p)) : string.Empty;
            var kind = p.IsExternal ? "cloud" + (p.SendsAttachmentText ? ", fatti allegati consentiti" : ", fatti allegati esclusi") : "locale";
            return $"• {(string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName)}: {host} ({kind}){web}.";
        }).ToList();
        lines.Add("• Le email vanno a un modello solo per un'azione avviata dall'utente; la posta viene letta solo dalle cassette aperte in Outlook.");
        _lblDestinations.Text = string.Join("\r\n", lines);
    }

    private static Card ActionCard(string title, string note, Button action)
    {
        var card = new Card(title) { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var text = new SelectableLabel { Text = note, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) };
        Theme.SetTone(text, Tone.Muted);
        grid.Controls.Add(text, 0, 0);
        action.Anchor = AnchorStyles.Right;
        action.Margin = new Padding(8, 0, 0, 0);
        grid.Controls.Add(action, 1, 0);
        card.Controls.Add(grid);
        return card;
    }

    private void LoadActiveConfiguration()
    {
        _loadProblem = null;
        _fileShown = false;
        _inMemorySecrets.Clear();
        _inMemoryWebSecrets.Clear();
        _modelInfo = null;
        _modelLimitsKey = null;
        try
        {
            _config = _configService.LoadConfiguration();
            LogActivity("Configurazione caricata con successo da: " + Path.Combine(_configDir, "config.json"));
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // The service reads the vault together with the configuration.
            _loadProblem = "secrets.bin (chiavi API cifrate) non è leggibile su questo PC: " + ErrorText.For(ex);
            LogActivity(_loadProblem + "\r\nDettaglio tecnico: " + ex);
            _config = FileOrDefaultConfiguration();
        }
        catch (FileNotFoundException)
        {
            LogActivity("config.json assente: proposta una configurazione iniziale con Ollama locale.");
            _config = DefaultConfiguration();
        }
        catch (Exception ex)
        {
            _loadProblem = "config.json non è leggibile o non è valido: " + ErrorText.For(ex);
            LogActivity(_loadProblem + "\r\nDettaglio tecnico: " + ex);
            _config = FileOrDefaultConfiguration();
        }

        string secretsFile = Path.Combine(AppPaths.SecretsDirectoryFor(_configDir), "secrets.bin");
        if (AppPaths.IsStandardConfigDirectory(_configDir) && File.Exists(AppPaths.LegacySecretsFile))
            LogActivity("[AVVISO] " + AppPaths.LegacySecretsFile + " è il vault di una versione precedente: non viene più letto ed è leggibile da tutti gli utenti del PC. Reinserisci le chiavi API e fai eliminare il file a un amministratore.");
        if (File.Exists(secretsFile))
        {
            try
            {
                _vault.Load(secretsFile);
                foreach (var p in _config.Profiles)
                {
                    if (!string.IsNullOrEmpty(p.SecretRef))
                    {
                        var sec = _vault.GetSecret(p.SecretRef);
                        if (sec != null) _inMemorySecrets[p.Id] = sec;
                    }
                    if (!string.IsNullOrEmpty(p.WebSearchSecretRef))
                    {
                        var webSecret = _vault.GetSecret(p.WebSearchSecretRef);
                        if (webSecret != null) _inMemoryWebSecrets[p.Id] = webSecret;
                    }
                }
            }
            catch (Exception ex)
            {
                _loadProblem ??= "secrets.bin (chiavi API cifrate) non è leggibile su questo PC: " + ErrorText.For(ex);
                LogActivity("Vault non leggibile: " + ex.Message);
            }
        }

        PopulateUiFromConfig();
        SetLocked(_adminPassword.Enabled);
        // What is on screen is the file again: no pending edits (discarded on close, replaced by a restore).
        _isDirty = false;
        UpdateTitle();

        _requiresElevation = !ElevatedConfigWriter.CanWriteConfig(_configDir);
        if (_requiresElevation) _btnSave.Text = "Salva (Amministratore)";
        if (LoadBlocked)
        {
            SetStatus("Configurazione non leggibile: salvataggio bloccato per non sovrascriverla. " +
                (_fileShown ? "I valori mostrati sono quelli del file." : "Nessun valore del file è leggibile: quelli mostrati sono predefiniti."), Tone.Danger);
        }
        else if (!_isLocked)
        {
            SetStatus(_requiresElevation ? "Configurazione caricata. Il salvataggio richiede l'autorizzazione di amministratore (UAC)." : "Configurazione caricata.");
        }
    }

    /// <summary>After a failed load: the file's own values when it still parses, so read-only never shows defaults as the configuration.</summary>
    private WorkspaceConfig FileOrDefaultConfiguration()
    {
        var shown = _configService.ReadForDisplay();
        _fileShown = shown != null;
        return shown ?? DefaultConfiguration();
    }

    private static WorkspaceConfig DefaultConfiguration() => new()
    {
        Profiles = new List<LlmProfileConfig> { new() { Id = "ollama-lan", DisplayName = "Ollama locale" } }
    };

    /// <summary>preferences.json of the Windows user: the guided setup was completed or closed once.</summary>
    private const string GuidedSetupDone = "guidedSetupDone";

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (LoadBlocked) BeginInvoke(new Action(ResolveLoadProblem));
        else if (!_isLocked && !SetupAlreadyOffered()) BeginInvoke(new Action(RunSetupWizard));
    }

    private bool SetupAlreadyOffered()
    {
        try { return _configService.GetUserFlag(GuidedSetupDone); }
        catch (Exception) { return true; }
    }

    /// <summary>Guided first setup; also reachable from the tray and from Generale.</summary>
    public async void RunSetupWizard()
    {
        if (IsDisposed) return;
        if (SetupWizardApplier.BlockedReason(LoadBlocked, _isLocked) is { } blocked)
        {
            CopyableMessageBox.Show(this, blocked, "Configurazione guidata", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var profile = SetupWizardApplier.StartingProfile(_config, Profiles);
        using var wizard = new SetupWizardForm(profile, _inMemorySecrets.TryGetValue(profile.Id, out var secret) ? secret : null,
            _config.Archive.Enabled, _config.Archive.IndexAttachmentText);
        var result = wizard.ShowDialog(this);
        try { _configService.SetUserFlag(GuidedSetupDone, true); }
        catch (Exception ex) { LogActivity("Preferenza della procedura guidata non salvata: " + ex.Message); }
        SyncAutostart();
        if (result != DialogResult.OK) return;

        SetupWizardApplier.Apply(_config, _inMemorySecrets, profile, new SetupWizardResult
        {
            Profile = wizard.Profile,
            Secret = wizard.Secret,
            ArchiveEnabled = wizard.ArchiveEnabled,
            IndexAttachments = wizard.IndexAttachments
        });
        PopulateUiFromConfig();
        MarkDirty();
        LogActivity("Procedura guidata completata: profilo '" + wizard.Profile.Id + "' su " + wizard.Profile.BaseUrl + ".");
        if (!await SaveActiveConfiguration())
        {
            SetStatus("Procedura guidata applicata ma non salvata: l'avvio automatico di Windows resta invariato. Premi Salva quando pronto.", Tone.Warning);
            return;
        }
        if (!StartupManager.SetRunAtStartup(wizard.RunAtStartup))
            CopyableMessageBox.Show(this, "Impossibile modificare l'avvio automatico di Windows.", "Avvio automatico", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        SyncAutostart();
    }

    /// <summary>Unreadable config or vault: the user chooses; nothing is overwritten silently.</summary>
    private void ResolveLoadProblem()
    {
        if (!LoadBlocked || IsDisposed) return;
        var choice = CopyableMessageBox.Show(this,
            _loadProblem + "\n\nPer non perdere la configurazione esistente il salvataggio è bloccato. Cartella: " + _configDir +
            "\n\n• Ripristina backup: sostituisce i file con un backup valido.\n• Nuova configurazione: copia i file attuali in " + InvalidCopyRoot() +
            " e consente di salvarne una nuova (le chiavi API vanno reinserite).\n• Sola lettura: lascia tutto invariato.",
            "Configurazione non leggibile", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
            "Ripristina backup…", "Nuova configurazione", "Sola lettura");
        if (choice == DialogResult.Yes) BtnImportRestore_Click(this, EventArgs.Empty);
        else if (choice == DialogResult.No) StartNewConfiguration();
    }

    private static string InvalidCopyRoot() =>
        AppPaths.UnreadableConfigDirectory;

    private void StartNewConfiguration()
    {
        try
        {
            var copy = Path.Combine(InvalidCopyRoot(), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(copy);
            foreach (var (name, folder) in new[] { ("config.json", _configDir), ("secrets.bin", AppPaths.SecretsDirectoryFor(_configDir)) })
            {
                var source = Path.Combine(folder, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(copy, name), overwrite: true);
            }
            _loadProblem = null;
            _vault.Clear();
            if (_fileShown)
            {
                _fileShown = false;
                _inMemorySecrets.Clear();
                _inMemoryWebSecrets.Clear();
                _config = DefaultConfiguration();
                PopulateUiFromConfig();
            }
            _btnSave.Enabled = !_isLocked;
            MarkDirty();
            SetStatus("Nuova configurazione: i file precedenti sono copiati in " + copy + ". Salva per applicarla.", Tone.Warning);
            LogActivity("File non leggibili copiati in: " + copy);
        }
        catch (Exception ex)
        {
            LogActivity("Copia configurazione non riuscita: " + ex);
            CopyableMessageBox.Show(this, "Impossibile copiare i file attuali: " + ErrorText.For(ex), "Nuova configurazione", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PopulateUiFromConfig()
    {
        _isLoading = true;
        try
        {
            _lstProfiles.Items.Clear();
            foreach (var p in _config.Profiles)
            {
                _lstProfiles.Items.Add(new ProfileItem(p, IsDefaultProfile));
            }

            if (_lstProfiles.Items.Count > 0)
            {
                _lstProfiles.SelectedIndex = 0;
            }

            RefreshProfileNames();
            _numMaxSearch.Value = Math.Max(10, Math.Min(200, _config.Limits.MaxSearchItems));
            SetNumeric(_numFollowUpDays, _config.Limits.FollowUpDays);
            SetNumeric(_numMaxAnalysis, _config.Limits.MaxAnalysisEmails);
            _cmbLogLevel.SelectedItem = _cmbLogLevel.Items.Cast<LogLevelItem>().FirstOrDefault(i => i.Value == _config.Logging.Level)
                ?? _cmbLogLevel.Items.Cast<LogLevelItem>().First(i => i.Value == "Information");

            _chkHistoryEnabled.Checked = _config.History.Enabled;
            _numRetentionDays.Value = Math.Max(1, Math.Min(365, _config.History.RetentionDays));
            _numMaxConversations.Value = Math.Max(10, Math.Min(1000, _config.History.MaxConversations));

            _config.Archive ??= new WorkspaceArchiveConfig();
            _chkArchiveEnabled.Checked = _config.Archive.Enabled;
            _chkIndexAttachments.Checked = _config.Archive.IndexAttachmentText;
            _chkAttachmentOcr.Checked = _config.AttachmentOcr;
        }
        finally
        {
            _isLoading = false;
            _isDirty = false;
            UpdateTitle();
        }
    }

    private const string WebNotChecked = "Da verificare";

    private InteractiveRequest? _connectionCheck;
    internal Func<LlmProfileConfig, string?, ILlmClient>? ConnectionClientFactory;

    private async Task<bool> SaveActiveConfiguration()
    {
        if (_isLocked || LoadBlocked || _elevationBusy) return false;
        UpdateSelectedProfileFromUi();

        PrepareProfileSecrets();

        if (!await SaveValidated()) return false;
        RefreshEgress();
        var blocked = DataEgress.Missing(_config, _vault.GetSecret);
        if (blocked.Count > 0)
            SetStatus("Salvato. Bloccati finché non si accetta l'informativa sull'uscita dei dati (Privacy e dati): " + string.Join(", ", blocked) + ".", Tone.Warning);
        return true;
    }


    private void PrepareProfileSecrets() =>
        ProfileSecretSync.Apply(_config, _inMemorySecrets, _inMemoryWebSecrets, _vault, () => _vault.Keys);

    private async Task<bool> SaveValidated()
    {
        var validation = ConfigurationValidator.Validate(_config, _vault.GetSecret);
        if (!validation.IsValid)
        {
            CopyableMessageBox.Show(this, "Validazione fallita:\n\n- " + string.Join("\n- ", validation.Errors), "Validazione non riuscita", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        foreach (var warning in validation.Warnings) LogActivity("[AVVISO] " + warning);

        string configPath = Path.Combine(_configDir, "config.json");
        try
        {
            if (!_requiresElevation)
            {
                try
                {
                    WriteConfigFiles(_configDir);
                }
                catch (UnauthorizedAccessException)
                {
                    _requiresElevation = true;
                    _btnSave.Text = "Salva (Amministratore)";
                }
            }

            if (_requiresElevation && !await SaveWithElevation())
            {
                return false;
            }

            _isDirty = false;
            UpdateTitle();
            _trayContext?.UpdateProfileStatus();

            // Warnings (missing key or model, cards profile gone) belong in the status, not only in the log of another page.
            var warnings = validation.Warnings;
            SetStatus(warnings.Count == 0 ? $"Salvato alle {DateTime.Now:HH:mm}. Le modifiche sono attive per l'Add-in Outlook."
                : $"Salvato alle {DateTime.Now:HH:mm}, " + (warnings.Count == 1 ? "con un avviso: " : $"con {warnings.Count} avvisi (tutti nel log di Manutenzione e backup): ") + warnings[0],
                warnings.Count == 0 ? Tone.Success : Tone.Warning);
            LogActivity("Configurazione salvata con successo in: " + configPath);
            return true;
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show(this, "Salvataggio non riuscito: " + ErrorText.For(ex), "Salvataggio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Salvataggio non riuscito.", Tone.Danger);
            LogActivity("Errore salvataggio: " + ex.Message);
            return false;
        }
    }

    private void WriteConfigFiles(string targetDir)
    {
        // For the standard folder the vault goes to this user's %LOCALAPPDATA%; the staging folder only gets config.json.
        ConfigurationBackupService.SaveConfiguration(targetDir, _config, _vault, _configDir);
    }

    /// <summary>
    /// %ProgramData%\OnlyWorkspace is admin-only: stage the files and apply them from an elevated instance (UAC).
    /// </summary>
    private async Task<bool> SaveWithElevation()
    {
        string? stagingDir = null;
        try
        {
            stagingDir = ElevatedConfigWriter.CreateStagingDirectory();
            WriteConfigFiles(stagingDir);

            SetStatus("In attesa dell'autorizzazione di amministratore...");
            var result = await RunElevatedAsync(
                "apply-staged " + ElevatedConfigWriter.Quote(stagingDir) + " " + ElevatedConfigWriter.Quote(_configDir) + " " +
                ConfigurationBackupService.StagedDigest(Path.Combine(stagingDir, "config.json")) + " " +
                ConfigurationBackupService.StagedDigest(Path.Combine(stagingDir, "secrets.bin")),
                stagingDir);

            if (result.Success) return true;

            if (result.Cancelled)
            {
                SetStatus("Salvataggio annullato: autorizzazione amministratore non concessa.", Tone.Warning);
                LogActivity("Salvataggio annullato dall'utente (UAC).");
            }
            else
            {
                CopyableMessageBox.Show(this, "Errore durante il salvataggio:\n" + result.Message, "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("Salvataggio non riuscito.", Tone.Danger);
                LogActivity("Errore salvataggio elevato: " + result.Message);
            }
            return false;
        }
        finally
        {
            ElevatedConfigWriter.DeleteStagingDirectory(stagingDir);
        }
    }

    private sealed class LogLevelItem
    {
        public string Value { get; }
        private readonly string _label;
        public LogLevelItem(string value, string label) { Value = value; _label = label; }
        public override string ToString() => _label;
    }

    private bool _elevationBusy;
    private bool _closeSavePending;
    private Func<string, string?, Task<ElevatedConfigWriter.ElevationResult>> _elevatedWriter = ElevatedConfigWriter.RunElevatedAsync;

    private async Task<ElevatedConfigWriter.ElevationResult> RunElevatedAsync(string arguments, string? resultDir)
    {
        if (_elevationBusy) return new ElevatedConfigWriter.ElevationResult { Message = "Un'operazione amministrativa è già in corso." };
        _elevationBusy = true;
        var enabled = new Dictionary<Control, bool>();
        void Disable(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is ButtonBase || child is TextBoxBase || child is ComboBox || child is ListBox || child is NumericUpDown)
                { enabled[child] = child.Enabled; child.Enabled = false; }
                else Disable(child);
            }
        }
        Disable(this);
        UseWaitCursor = true;
        SetStatus("In attesa dell'autorizzazione di amministratore…", Tone.Warning, StatusLight.Waiting);
        try
        {
            var result = await _elevatedWriter(arguments, resultDir);
            if (!IsDisposed && !string.IsNullOrEmpty(result.TechnicalDetail))
                LogActivity("Dettaglio tecnico dell'operazione amministrativa: " + result.TechnicalDetail);
            return result;
        }
        finally
        {
            _elevationBusy = false;
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                foreach (var pair in enabled) if (!pair.Key.IsDisposed) pair.Key.Enabled = pair.Value;
            }
        }
    }

    private void SetLocked(bool locked)
    {
        _isLocked = locked;
        if (locked)
        {
            foreach (var page in _pages) LockInputs(page);
        }
        else
        {
            foreach (var entry in _lockedControls)
                if (!entry.Key.IsDisposed) entry.Key.Enabled = entry.Value;
            _lockedControls.Clear();
        }
        _btnSave.Enabled = !locked && !LoadBlocked;
        _btnUnlock.Visible = locked;
        _btnResetPassword.Visible = _adminPassword.Enabled;
        if (locked) SetStatus("Configurazione in sola lettura: inserire la password amministrativa.", Tone.Warning);
    }

    private void LockInputs(Control root)
    {
        // Per-user choices: the administrator password protects config.json, not the user's own archive.
        if (root is SelectableLabel || root is TextBoxBase { ReadOnly: true } || root is MailboxAuthorizationPanel) return;
        // Browsing stays possible: profile list and section tabs only change what is shown.
        if (root is NavButton || root == _lstProfiles) return;
        // Read-only actions and per-user settings: the PDF export of the notice, the user's own autostart and add-in repair
        // (HKCU), storage figures and the data folder write nothing the password protects.
        if (root == _btnEgressExport || root == _chkAutostart || root == _btnRepairAddin || root == _btnRefreshStorage || root == _btnOpenDataFolder) return;
        if (root is TextBoxBase || root is ButtonBase || root is ComboBox || root is ListBox || root is NumericUpDown)
        {
            if (!_lockedControls.ContainsKey(root)) _lockedControls[root] = root.Enabled;
            root.Enabled = false;
            return;
        }
        foreach (Control child in root.Controls) LockInputs(child);
    }

    private static string? AskPassword(IWin32Window owner, string title, string label)
    {
        using var dialog = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Segoe UI", 9F),
            BackColor = Theme.Colors.Window,
            ForeColor = Theme.Colors.Text
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16, 14, 16, 12) };
        var caption = new SelectableLabel { Text = label, AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(0, 0, 0, 6) };
        var input = new ThemedTextBox { Width = 360, UseSystemPasswordChar = true };
        var ok = new FlatButton { Text = "OK", Kind = ButtonKind.Primary, DialogResult = DialogResult.OK, MinimumSize = new Size(88, 30), Margin = new Padding(8, 0, 0, 0) };
        var cancel = new FlatButton { Text = "Annulla", DialogResult = DialogResult.Cancel, MinimumSize = new Size(88, 30), Margin = Padding.Empty };
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
        actions.Controls.AddRange(new Control[] { ok, cancel });
        // Laid out right to left: Tab follows the reading order instead.
        cancel.TabIndex = 0;
        ok.TabIndex = 1;
        layout.Controls.Add(caption, 0, 0);
        layout.Controls.Add(new InputFrame(input) { Margin = Padding.Empty }, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        Ui.ScaleFromDesignDpi(dialog);
        dialog.Shown += (s, e) => { Theme.Apply(dialog); Theme.ApplyWindowChrome(dialog); input.Focus(); };
        var result = dialog.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
        input.Clear();
        return result;
    }

    private void UnlockConfiguration()
    {
        var password = AskPassword(this, "Sblocca configurazione", "Password amministrativa:");
        if (password == null) return;
        if (!_adminPassword.Verify(password))
        {
            CopyableMessageBox.Show(this, "Password non corretta.", "Configurazione bloccata", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SetLocked(false);
        SetStatus("Configurazione sbloccata per questa finestra.", Tone.Success);
    }

    private string? AskNewPassword()
    {
        var next = AskPassword(this, "Password amministrativa", "Nuova password (vuota per disattivarla, minimo 8 caratteri):");
        if (next == null) return null;
        if (next.Length > 0 && next.Length < AdminPasswordService.MinLength)
        {
            CopyableMessageBox.Show(this, "La password deve contenere almeno " + AdminPasswordService.MinLength + " caratteri.", "Password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        var confirm = AskPassword(this, "Conferma password", "Ripeti la nuova password:");
        if (confirm == null) return null;
        if (next != confirm)
        {
            CopyableMessageBox.Show(this, "Le password non coincidono.", "Password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        return next;
    }

    private async Task ManageAdminPassword()
    {
        if (_isLocked) return;
        if (_adminPassword.Enabled)
        {
            var current = AskPassword(this, "Modifica password", "Password amministrativa corrente:");
            if (current == null) return;
            if (!_adminPassword.Verify(current))
            {
                CopyableMessageBox.Show(this, "Password non corretta.", "Password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        var next = AskNewPassword();
        if (next == null) return;
        await ApplyAdminPassword(next);
    }

    private async Task ResetAdminPassword()
    {
        var next = AskNewPassword();
        if (next == null) return;
        await ApplyAdminPassword(next, requireUac: true);
    }

    private async Task ApplyAdminPassword(string next, bool requireUac = false)
    {
        string? staging = null;
        try
        {
            byte[]? record = next.Length == 0 ? null : AdminPasswordService.CreateRecord(next);
            if (!requireUac && ElevatedConfigWriter.CanWriteConfig(_configDir))
            {
                _adminPassword.ApplyRecord(record);
            }
            else
            {
                staging = ElevatedConfigWriter.CreateStagingDirectory();
                if (record != null) File.WriteAllBytes(Path.Combine(staging, "verifier.json"), record);
                var applied = await RunElevatedAsync(
                    "admin-password-apply " + ElevatedConfigWriter.Quote(staging) + " " + ElevatedConfigWriter.Quote(_configDir), staging);
                if (applied.Cancelled)
                {
                    SetStatus("Password non modificata: autorizzazione amministratore non concessa.", Tone.Warning);
                    LogActivity("Password amministrativa non applicata: autorizzazione annullata dall'utente (UAC).");
                    return;
                }
                if (!applied.Success) throw new InvalidOperationException(applied.Message);
            }
            SetLocked(false);
            SetStatus(_adminPassword.Enabled ? "Password amministrativa aggiornata." : "Password amministrativa disattivata.", Tone.Success);
        }
        catch (Exception ex)
        {
            LogActivity("Password amministrativa non applicata: " + ex.Message);
            CopyableMessageBox.Show(this, ErrorText.For(ex), "Password amministrativa", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { ElevatedConfigWriter.DeleteStagingDirectory(staging); }
    }

    private bool _closingForExit;

    /// <summary>Exit from the tray: asks about unsaved changes; false when the user cancels.</summary>
    public bool TryCloseForExit()
    {
        _closingForExit = true;
        Close();
        // A modeless form disposes itself once closed; a cancelled close leaves it alive.
        if (IsDisposed || !IsHandleCreated) return true;
        _closingForExit = false;
        return false;
    }

    /// <summary>The tray menu can change the same Run entry.</summary>
    public void SyncAutostart()
    {
        bool loading = _isLoading;
        _isLoading = true;
        _chkAutostart.Checked = StartupManager.IsRunAtStartupEnabled();
        _isLoading = loading;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        SyncAutostart();
        RefreshAddinState();
    }

    private void RefreshAddinState()
    {
        var (state, message) = AddinRegistration.Evaluate(AddinRegistration.Read());
        _lblAddinState.Text = message;
        Theme.SetTone(_lblAddinState, state == AddinState.Ready ? Tone.Success : Tone.Warning);
        _btnRepairAddin.Visible = state is AddinState.DisabledByOutlook or AddinState.DisabledByUser;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var action = ClosePolicy.Decide(_elevationBusy || _closeSavePending, _isDirty, _trayContext != null, _closingForExit, e.CloseReason);
        if (action == CloseAction.Block) { e.Cancel = true; return; }
        if (action == CloseAction.AskUnsaved)
        {
            var res = CopyableMessageBox.Show(
                this,
                "Ci sono modifiche non salvate alla configurazione.\n\nVuoi salvarle prima di chiudere la finestra? «Non salvare» le annulla.",
                "Modifiche non salvate",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question, "Salva", "Non salvare", "Annulla");

            if (res == DialogResult.Yes)
            {
                e.Cancel = true;
                _closeSavePending = true;
                // Observed task, not an async void: the close is repeated only after persistence, outside this callback.
                _ = SaveThenCloseAsync(_closingForExit);
                return;
            }
            if (res == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            // The window only hides in the tray and is shown again later: discarded edits must not come back with it.
            if (_trayContext != null && !_closingForExit) LoadActiveConfiguration();
            action = ClosePolicy.Decide(false, false, _trayContext != null, _closingForExit, e.CloseReason);
        }

        if (action == CloseAction.HideToTray)
        {
            e.Cancel = true;
            Hide();
            _trayContext!.NotifyMinimizedToTray();
            return;
        }

        base.OnFormClosing(e);
    }

    private async Task SaveThenCloseAsync(bool exiting)
    {
        bool saved = false;
        try { saved = await SaveActiveConfiguration(); }
        catch (Exception ex)
        {
            // Nothing may escape a fire-and-forget task: the window stays open with its edits and the reason is logged.
            if (!IsDisposed) { SetStatus("Salvataggio non riuscito.", Tone.Danger); LogActivity("Errore salvataggio alla chiusura: " + ex.Message); }
        }
        finally { _closeSavePending = false; }
        if (saved && !IsDisposed)
            BeginInvoke(new Action(() => { if (exiting && _trayContext != null) _trayContext.ExitApplication(); else Close(); }));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_trayContext != null && WindowState == FormWindowState.Minimized)
        {
            Hide();
            _trayContext.NotifyMinimizedToTray();
        }
    }

    /// <summary>PDF and backup read the saved files: with edits pending, say they are left out.</summary>
    private bool ConfirmSavedFilesOnly(string what) => !_isDirty || CopyableMessageBox.Show(this,
        what + " usa i file salvati: le modifiche non ancora salvate non sono incluse.\n\nProseguire comunque?",
        "Modifiche non salvate", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    private void BtnExportBackup_Click(object? sender, EventArgs e)
    {
        if (!ConfirmSavedFilesOnly("Il backup")) return;
        try
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Esporta il backup della configurazione OnlyWorkspace",
                Filter = "Archivio Backup OnlyWorkspace (*.owbak)|*.owbak|Archivio ZIP (*.zip)|*.zip|Tutti i file (*.*)|*.*",
                FileName = $"OnlyWorkspace_Backup_{DateTime.Now:yyyyMMdd_HHmm}.owbak",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                RestoreDirectory = true,
                AutoUpgradeEnabled = true,
                OverwritePrompt = true
            };

            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                ConfigurationBackupService.CreateBackup(_configDir, sfd.FileName);
                LogActivity("Backup esportato con successo in: " + sfd.FileName);
                CopyableMessageBox.Show(this, "Backup completato con successo!\n\nFile: " + sfd.FileName, "Backup completato", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show(this, "Backup non riuscito: " + ErrorText.For(ex), "Backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            LogActivity("Errore creazione backup: " + ex.Message);
        }
    }

    private async void BtnImportRestore_Click(object? sender, EventArgs e)
    {
        try
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Seleziona Archivio Backup OnlyWorkspace",
                Filter = "Archivio Backup (*.owbak;*.zip)|*.owbak;*.zip|Tutti i file (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                RestoreDirectory = true,
                AutoUpgradeEnabled = true,
                CheckFileExists = true
            };

            if (_isDirty && CopyableMessageBox.Show(this, "Le modifiche non salvate andranno perse con il ripristino. Continuare?",
                    "Modifiche non salvate", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                // Checked here so the reason is shown even when the restore runs elevated.
                ConfigurationBackupService.ValidateBackup(ofd.FileName, _configDir);

                var confirm = CopyableMessageBox.Show(
                    this,
                    "Il ripristino sovrascriverà la configurazione e i segreti attuali.\nVuoi procedere?",
                    "Conferma ripristino",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (confirm == DialogResult.Yes)
                {
                    var restore = await BackupRestoreFlow.Create(_configDir, RunElevatedAsync).RestoreAsync(ofd.FileName);
                    if (restore.Outcome != RestoreOutcome.Completed)
                    {
                        if (restore.Outcome == RestoreOutcome.Cancelled)
                            SetStatus("Ripristino annullato: autorizzazione amministratore non concessa.", Tone.Warning);
                        else
                            CopyableMessageBox.Show(this, "Ripristino non riuscito: " + restore.Message, "Ripristino", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        LogActivity("Ripristino non eseguito: " + restore.Message);
                        return;
                    }
                    LogActivity("Ripristino completato con successo da: " + ofd.FileName);
                    LoadActiveConfiguration();
                    CopyableMessageBox.Show(this, "Ripristino completato con successo!\nLa configurazione attiva è stata aggiornata.", "Ripristino completato", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show(this, "Ripristino non riuscito: " + ErrorText.For(ex), "Ripristino", MessageBoxButtons.OK, MessageBoxIcon.Error);
            LogActivity("Errore ripristino backup: " + ex.Message);
        }
    }

    private static StatusLight StateForTone(Tone tone) => tone == Tone.Success ? StatusLight.Ok : tone == Tone.Danger ? StatusLight.Error : tone == Tone.Warning ? StatusLight.Paused : StatusLight.Off;

    private void SetStatus(string msg, Tone tone = Tone.Muted, StatusLight? light = null)
    {
        _lblStatus.SetState(light ?? StateForTone(tone));
        _lblStatus.Text = msg;
        Theme.SetTone(_lblStatus, tone);
    }

    private void SetTestResult(string text, Tone tone, StatusLight? light = null)
    {
        _lblTestResult.SetState(light ?? StateForTone(tone));
        _lblTestResult.Text = text;
        Theme.SetTone(_lblTestResult, tone);
        // The edit surface retains the complete message even in a narrow layout.
        Tip(_lblTestResult, text);
    }

    private void LogActivity(string msg)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n";
        _txtLog.AppendText(entry);
    }

    private void Tip(Control control, string text)
    {
        _tips.SetToolTip(control, text);
        if (control is SelectableLabel)
            foreach (Control child in control.Controls) _tips.SetToolTip(child, text);
    }

    /// <summary>Single-column auto-height stack of cards; lives in a scrolling panel.</summary>
    private static TableLayoutPanel NewStack()
    {
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        return stack;
    }

    /// <summary>Page that scrolls only when the window is too short for its cards.</summary>
    private static Panel ScrollPage(out TableLayoutPanel stack)
    {
        var page = new ScrollPanel();
        stack = NewStack();
        page.Controls.Add(stack);
        return page;
    }

    private static void AddStackRow(TableLayoutPanel stack, Control control)
    {
        int row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(control, 0, row);
    }

    /// <summary>Grid of label | field | label | field: a row holds one field (spanning) or a pair.</summary>
    private TableLayoutPanel NewGrid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        if (_batchGrids != null) { grid.SuspendLayout(); _batchGrids.Add(grid); }
        return grid;
    }

    /// <summary>Lays out the grids created by <paramref name="build"/> once at the end: each added row would otherwise relayout its whole grid (seconds per page).</summary>
    private void BuildBatched(Action build)
    {
        _batchGrids = new List<TableLayoutPanel>();
        try { build(); }
        finally
        {
            var grids = _batchGrids;
            _batchGrids = null;
            foreach (var grid in grids) grid.ResumeLayout(true);
        }
    }

    /// <summary>Titled card with a field grid, appended to the stack; returns the grid.</summary>
    private TableLayoutPanel AddGroup(TableLayoutPanel stack, string title)
    {
        var card = new Card(title) { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var grid = NewGrid();
        card.Controls.Add(grid);
        AddStackRow(stack, card);
        return grid;
    }

    /// <summary>
    /// Keeps a long description behind a «?» but never alone: the first sentence stays visible beside it
    /// (same pattern as the notes of the guided setup). A description that is one short sentence needs no «?».
    /// </summary>
    private void DescriptionHelp(Control description, string title)
    {
        var parent = description.Parent;
        if (parent == null) return;
        description.Visible = false;
        var lead = new SelectableLabel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) };
        Theme.SetTone(lead, Tone.Muted);
        var button = ContextHelp.Button(this, title, () => description.Text, _tips);
        void Refresh()
        {
            lead.Text = LeadOf(description.Text);
            lead.Visible = lead.Text.Length > 0;
            button.Visible = description.Text.Trim().Length > lead.Text.Length;
        }
        description.TextChanged += (_, _) => Refresh();
        Refresh();
        Tip(lead, description.Text.Length > 0 ? description.Text : title);
        parent.Controls.Add(lead);
        parent.Controls.Add(button);
        FitLead(parent, lead, button);
    }

    /// <summary>First sentence of a text, cut at a word when still long: the visible line beside a «?».</summary>
    internal static string LeadOf(string text)
    {
        text = text.Trim();
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        string first = end > 0 ? text.Substring(0, end + 1) : text;
        if (first.Length <= 90) return first;
        int cut = first.LastIndexOf(' ', 87);
        return first.Substring(0, cut > 40 ? cut : 87).TrimEnd(',', ';', ':', ' ') + "…";
    }

    /// <summary>A visible lead with its «?» in one row: <paramref name="lead"/> wraps in what the grid leaves beside the button.</summary>
    private FlowLayoutPanel LeadWithHelp(string lead, string title, Func<string> detail)
    {
        var label = new SelectableLabel { Text = lead, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) };
        Theme.SetTone(label, Tone.Muted);
        var button = ContextHelp.Button(this, title, detail, _tips);
        var row = Inline(label, button);
        Tip(label, detail());
        FitLead(row, label, button);
        return row;
    }

    private void FitLead(Control row, Label lead, Control button)
    {
        void Fit(object? sender, LayoutEventArgs e)
        {
            if (row.Parent is not TableLayoutPanel grid) return;
            int room = Math.Max(Ui.Px(this, 120), grid.ClientSize.Width - row.Left - row.Margin.Horizontal
                - (button.Visible ? button.Width + button.Margin.Horizontal : 0) - lead.Margin.Horizontal - Ui.Px(this, 8));
            if (lead.MaximumSize.Width != room) lead.MaximumSize = new Size(room, 0);
        }
        if (row.Parent is TableLayoutPanel now) now.Layout += Fit;
        else row.ParentChanged += (_, _) => { if (row.Parent is TableLayoutPanel grid) grid.Layout += Fit; };
    }

    private T AddRow<T>(TableLayoutPanel grid, string label, T field, string tip) where T : Control
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddCell(grid, label, field, tip, 0, row, 3);
        return field;
    }

    /// <summary>Two short fields on one row.</summary>
    private void AddPair(TableLayoutPanel grid, string label1, Control field1, string tip1, string label2, Control field2, string tip2)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddCell(grid, label1, field1, tip1, 0, row, 1);
        AddCell(grid, label2, field2, tip2, 2, row, 1);
    }

    /// <summary>Shows or hides a grid row made by AddRow: its label and its field cell.</summary>
    private static void SetRowVisible(Control field, bool visible)
    {
        Control cell = field;
        while (cell.Parent is not TableLayoutPanel && cell.Parent != null) cell = cell.Parent;
        if (cell.Parent is not TableLayoutPanel grid) return;
        int row = grid.GetRow(cell);
        foreach (Control control in grid.Controls)
            if (grid.GetRow(control) == row) control.Visible = visible;
    }

    private void AddCell(TableLayoutPanel grid, string label, Control field, string tip, int column, int row, int span)
    {
        // An empty label is a note under the field above: no label cell, so a hidden note leaves no gap.
        if (label.Length > 0)
        {
            var lbl = new SelectableLabel { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(column == 0 ? 0 : 24, 6, 10, 3) };
            // Same minimum label width in every group keeps the field column aligned across groups.
            // The second label of a pair gets one too: its field then lines up across groups as well.
            lbl.MinimumSize = new Size(column == 0 ? 160 : 140, 0);
            grid.Controls.Add(lbl, column, row);
            Tip(lbl, tip);
        }
        Tip(field, tip);

        Control cell = field is ThemedTextBox box ? new InputFrame(box) : field;
        // A fixed-width field placed directly in the cell is clamped to the cell width, which is 0 while
        // the AutoSize grid is measured, and never grows back: an AutoSize flow wrapper keeps its width.
        bool stretches = (cell.Anchor & AnchorStyles.Right) != 0;
        if (!stretches && cell is not FlowLayoutPanel) cell = Inline(cell);
        if (cell is FlowLayoutPanel helpCell && label.Length > 0 && tip.Length > 100)
            helpCell.Controls.Add(ContextHelp.Button(this, label.TrimEnd(':').Length > 0 ? label.TrimEnd(':') : "Dettagli", () => tip, _tips));
        grid.Controls.Add(cell, column + 1, row);
        if (span > 1) grid.SetColumnSpan(cell, span);
    }

    private T AddWide<T>(TableLayoutPanel grid, T field, string tip, string? lead = null) where T : Control
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Control shown = field;
        // A long label stays behind a «?», with a short line visible beside it.
        if (field is SelectableLabel && field.Text.Length > 100)
            shown = LeadWithHelp(lead ?? LeadOf(field.Text), "Informazioni", () => field.Text);
        grid.Controls.Add(shown, 0, row);
        grid.SetColumnSpan(shown, grid.ColumnCount);
        Tip(field, tip);
        return field;
    }

    private static FlowLayoutPanel Inline(params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Left
        };
        flow.Controls.AddRange(controls);
        return flow;
    }

    /// <summary>Row whose first control takes the free width, with a fixed control at its right.</summary>
    private static TableLayoutPanel FillRow(Control fill, Control right)
    {
        var row = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = Padding.Empty, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(fill, 0, 0);
        row.Controls.Add(right, 1, 0);
        return row;
    }

    private static ThemedTextBox StretchTextBox() => new() { Width = 320, Anchor = AnchorStyles.Left | AnchorStyles.Right };

    private static ThemedComboBox Combo(int width) => new() { Width = width, Anchor = AnchorStyles.Left };

    private static ThemedNumeric Numeric(int min, int max, int value, int increment) =>
        new() { Width = 100, Minimum = min, Maximum = max, Value = value, Increment = increment, ThousandsSeparator = true, Anchor = AnchorStyles.Left };

    private static void SetNumeric(NumericUpDown numeric, decimal value) =>
        numeric.Value = Math.Max(numeric.Minimum, Math.Min(numeric.Maximum, value));

    private static FlatButton PrimaryButton(string text) => new()
    {
        Text = text,
        Kind = ButtonKind.Primary,
        // GrowAndShrink: a GrowOnly button keeps the size DPI scaling gave it on top of its own.
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(120, 32),
        Padding = new Padding(12, 0, 12, 0),
        Margin = new Padding(8, 0, 0, 0)
    };

    private static FlatButton SecondaryButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(0, 28),
        Padding = new Padding(8, 0, 8, 0),
        Margin = Padding.Empty
    };

    private sealed class ProfileItem
    {
        private readonly Func<LlmProfileConfig, bool> _isDefault;
        public LlmProfileConfig Profile { get; }

        public ProfileItem(LlmProfileConfig profile, Func<LlmProfileConfig, bool> isDefault)
        {
            Profile = profile;
            _isDefault = isDefault;
        }

        public string Name => string.IsNullOrWhiteSpace(Profile.DisplayName) ? Profile.Id : Profile.DisplayName;
        public bool IsDefault => _isDefault(Profile);

        // Read by screen readers; the list draws Name and a star.
        public override string ToString() => Name + (IsDefault ? "  ★ predefinito" : "");
    }

    /// <summary>Entry of the default-profile combo: "Name (id)".</summary>
    private sealed class DefaultChoice
    {
        public LlmProfileConfig Profile { get; }
        public DefaultChoice(LlmProfileConfig profile) => Profile = profile;
        public override string ToString() =>
            string.IsNullOrWhiteSpace(Profile.DisplayName) || Profile.DisplayName == Profile.Id ? Profile.Id : $"{Profile.DisplayName} ({Profile.Id})";
    }
}
