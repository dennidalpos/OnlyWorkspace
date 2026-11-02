using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>
/// First setup of the PC: AI model, archive permission, start with Windows and add-in state.
/// It edits copies; MainConfigForm applies the result and saves it through the usual validation and UAC path.
/// </summary>
public sealed class SetupWizardForm : Form
{

    private readonly List<Control> _steps = new();
    private readonly string[] _titles = { "Cosa fa OnlyWorkspace", "Modello IA", "Archivio delle email", "Avvio e Outlook", "Riepilogo" };
    private int _step;

    private readonly SelectableLabel _lblHeader = Ui.SetFont(new SelectableLabel { Dock = DockStyle.Top, Height = 34 }, 12F, family: "Segoe UI Semibold");
    private readonly FlatButton _btnBack = new() { Text = "Indietro", AutoSize = true, MinimumSize = new Size(96, 30) };
    private readonly FlatButton _btnNext = new() { Text = "Avanti", Kind = ButtonKind.Primary, AutoSize = true, MinimumSize = new Size(96, 30) };
    private readonly FlatButton _btnCancel = new() { Text = "Chiudi senza applicare", AutoSize = true, MinimumSize = new Size(96, 30) };

    private readonly ThemedComboBox _cmbProvider = new() { Width = 420 };
    private readonly ThemedTextBox _txtEndpoint = new() { Width = 420 };
    private readonly ThemedTextBox _txtModel = new() { Width = 260 };
    private readonly FlatButton _btnDetect = new() { Text = DetectText, AutoSize = true, MinimumSize = new Size(0, 28) };
    private readonly ThemedTextBox _txtKey = new() { Width = 420, UseSystemPasswordChar = true };
    private readonly SelectableLabel _lblKey = new() { Text = "Chiave API:", AutoSize = true };
    private readonly SelectableLabel _lblTest = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly SelectableLabel _lblModelNote = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly ThemedCheckBox _chkArchive = new() { Text = "Consenti l'archivio locale delle email su questo PC", AutoSize = true, Checked = true };
    private readonly ThemedCheckBox _chkAttachments = new() { Text = "Conserva e rendi cercabile il testo degli allegati leggibili", AutoSize = true, Checked = true };
    private readonly ThemedCheckBox _chkAutostart = new() { Text = "Avvia OnlyWorkspace all'accesso a Windows (consigliato)", AutoSize = true, Checked = true };
    private readonly SelectableLabel _lblAddin = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly SelectableLabel _lblOutlook = new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 12, 0, 0) };
    private readonly FlatButton _btnRepair = new() { Text = "Ripara add-in", AutoSize = true, MinimumSize = new Size(0, 28) };
    private readonly SelectableLabel _lblSummary = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100 };

    private InteractiveRequest? _connectionCheck;

    private const string DetectText = "Scegli modello…";

    /// <summary>Source of the «Profilo Outlook» line; tests replace the registry.</summary>
    internal Func<OutlookProfiles> ReadOutlookProfiles = () => OutlookProfiles.Read();
    /// <summary>Client of the model list; tests replace the service.</summary>
    internal Func<LlmProfileConfig, string?, OnlyWorkspace.Core.Interfaces.ILlmClient>? ClientFactory;

    public LlmProfileConfig Profile { get; }
    public string? Secret { get; private set; }
    public bool ArchiveEnabled => _chkArchive.Checked;
    public bool IndexAttachments => _chkAttachments.Checked;

    public SetupWizardForm(LlmProfileConfig profile, string? secret, bool archiveEnabled, bool indexAttachments)
    {
        Profile = Copy(profile);
        Secret = secret;
        Text = "Configurazione guidata di OnlyWorkspace";
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(640, 470);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        Icon = AppIconHelper.GetAppIcon();

        _steps.Add(Page(
            Note("OnlyWorkspace aggiunge a Outlook classico la scheda OnlyWorkspace sulla barra multifunzione e un riquadro laterale: sintesi, bozze di risposta, verifica di sicurezza, attività e appuntamenti, estrazione di dati, ricerca e analisi della posta con un modello IA."),
            Note("Le bozze si aprono in Outlook da rivedere: OnlyWorkspace non invia email automaticamente. I profili IA e Web Search determinano quali dati possono uscire dal PC; gli invii esterni richiedono l'informativa accettata in Privacy e dati."),
            Note("Questa procedura imposta per tutti gli utenti del PC il modello IA e l'autorizzazione dell'archivio, salvati in %ProgramData%\\OnlyWorkspace\\config.json e secrets.bin (chiavi cifrate; il salvataggio può chiedere l'autorizzazione di amministratore). Per l'utente corrente imposta l'avvio automatico e controlla che Outlook carichi l'add-in. Ogni scelta si cambia poi nelle pagine della Configurazione.")));

        _cmbProvider.Items.AddRange(ProviderCatalog.All.Cast<object>().ToArray());
        _cmbProvider.SelectedItem = ProviderCatalog.Get(Profile.Type);
        _cmbProvider.SelectedIndexChanged += (_, _) => { _connectionCheck?.Cancel(); OnProviderChanged(); };
        foreach (var field in new Control[] { _txtEndpoint, _txtModel, _txtKey })
            field.TextChanged += (_, _) => _connectionCheck?.Cancel();
        FormClosed += (_, _) => _connectionCheck?.Cancel();
        _txtEndpoint.Text = Profile.BaseUrl;
        _txtModel.Text = Profile.Model;
        _txtKey.Text = secret ?? string.Empty;
        var test = new FlatButton { Text = "Verifica connessione", AutoSize = true, MinimumSize = new Size(0, 28) };
        test.Click += async (_, _) => await TestAsync(test);
        _btnDetect.Click += async (_, _) => await DetectAsync();
        var model = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _btnDetect.Margin = new Padding(6, 0, 0, 0);
        model.Controls.Add(new InputFrame(_txtModel));
        model.Controls.Add(_btnDetect);
        // The key comes before the model: a cloud service lists its models only to a valid key.
        _steps.Add(Page(
            Row("Servizio:", _cmbProvider),
            Row("Indirizzo (endpoint):", new InputFrame(_txtEndpoint)),
            Row(_lblKey, new InputFrame(_txtKey)),
            Row("Modello:", model),
            test, _lblTest, _lblModelNote));

        _chkArchive.CheckedChanged += (_, _) => _chkAttachments.Enabled = _chkArchive.Checked;
        _chkArchive.Checked = archiveEnabled;
        _chkAttachments.Checked = indexAttachments;
        _steps.Add(Page(
            Note("L'archivio rende cercabile la posta di tutte le cassette aperte in Outlook, anche a Outlook chiuso, e alimenta Controparti e Ricostruisci flusso."),
            _chkArchive, _chkAttachments,
            Note("Il database cifrato in %LOCALAPPDATA%\\OnlyWorkspace\\archive conserva intestazioni, corpo integrale acquisito e, se abilitato, testo degli allegati leggibili. La ricerca usa tutto il testo conservato; l'anteprima mostra solo i primi 255 caratteri. I file allegati originali non vengono archiviati."),
            Note("La raccolta e la ricerca sono locali e non usano modelli IA. Le azioni di analisi seguono il livello di esposizione del profilo e l'informativa accettata."),
            Note("Si attiva per ogni utente su tutte le cassette aperte, comprese quelle aggiunte in seguito; ognuno può restringerlo o spegnerlo in Configurazione → Archivio email → Le mie cassette. La raccolta avviene solo con Outlook aperto e il PC inattivo.")));

        _chkAutostart.Checked = true;
        _btnRepair.Click += (_, _) => RepairAddin();
        _steps.Add(Page(
            _chkAutostart,
            Note("All'accesso parte l'icona OnlyWorkspace nell'area di notifica: ospita il motore dell'archivio, la ricerca a Outlook chiuso e gli avvisi di spazio. Scrive la voce OnlyWorkspaceTray in HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run per il solo utente corrente."),
            Ui.SetFont(new SelectableLabel { Text = "Add-in in Outlook", AutoSize = true, Margin = new Padding(0, 12, 0, 2) }, 9F, family: "Segoe UI Semibold"),
            _lblAddin, _btnRepair, _lblOutlook));

        _steps.Add(Page(_lblSummary));

        foreach (var (control, tip) in new (Control, string)[]
        {
            (_cmbProvider, "Sceglie il servizio IA; la destinazione dipende anche dall'indirizzo configurato. Su loopback e senza Web Search il testo resta su questo PC. Gli invii esterni richiedono l'informativa accettata in Privacy e dati. La scelta si applica al salvataggio."),
            (_txtEndpoint, "Indirizzo a cui il modello riceve il testo. Per Ollama su questo PC è http://127.0.0.1:11434 (loopback); un indirizzo esterno richiede l'informativa accettata. Si applica al salvataggio."),
            (_txtModel, "Nome del modello da usare, come lo chiama il servizio (per esempio qwen3.5:9b). «Scegli modello…» lo fa scegliere dall'elenco del servizio. Si può cambiare dopo in Profili IA."),
            (_btnDetect, "Chiede al servizio, con indirizzo e chiave inseriti, l'elenco dei modelli disponibili e te li fa scegliere. Non invia dati di Outlook."),
            (_txtKey, "Chiave fornita dal servizio cloud. Viene salvata cifrata su questo PC (secrets.bin), mai in chiaro."),
            (test, "Prova indirizzo, chiave e modello con una richiesta di prova, senza dati di Outlook. Senza modello mostra l'elenco da cui sceglierlo."),
            (_chkArchive, "Consente a ogni utente di conservare intestazioni e corpo integrale acquisito nel proprio archivio cifrato, cercabile anche a Outlook chiuso. Richiede Applica e salva, con eventuale UAC."),
            (_chkAttachments, "Conserva nell'archivio cifrato il testo degli allegati leggibili (PDF, Office, testo), per cercarlo e recuperarlo. I file originali non vengono archiviati. Non autorizza da solo invii cloud. Richiede Applica e salva, con eventuale UAC."),
            (_chkAutostart, "All'accesso a Windows parte l'icona OnlyWorkspace nell'area di notifica, necessaria all'archivio. Vale solo per il tuo utente."),
            (_btnRepair, "Se Outlook ha disattivato l'add-in, lo riattiva per il tuo utente e chiede a Outlook di non disattivarlo più. Poi riavvia Outlook."),
            (_btnBack, "Torna al passo precedente senza perdere le scelte."),
            (_btnNext, "Passa al passo successivo; all'ultimo applica le scelte e le salva."),
            (_btnCancel, "Chiude la procedura senza cambiare nulla.")
        })
            _tips.SetToolTip(control, tip);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 6, 20, 6) };
        foreach (var step in _steps) { step.Dock = DockStyle.Fill; step.Visible = false; host.Controls.Add(step); }
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12), WrapContents = false };
        footer.Controls.AddRange(new Control[] { _btnNext, _btnBack, _btnCancel });
        // Laid out right to left: Tab follows the reading order instead.
        _btnCancel.TabIndex = 0;
        _btnBack.TabIndex = 1;
        _btnNext.TabIndex = 2;
        _btnNext.Margin = _btnBack.Margin = new Padding(8, 0, 0, 0);
        _btnBack.Click += (_, _) => ShowStep(_step - 1);
        _btnNext.Click += (_, _) => Next();
        _btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _lblHeader.Padding = new Padding(20, 10, 20, 0);
        Controls.Add(host);
        Controls.Add(_lblHeader);
        Controls.Add(footer);
        CancelButton = _btnCancel;

        Ui.ScaleFromDesignDpi(this);
        DisabledControlTips.Attach(this, _tips);
        Shown += (_, _) => { Theme.Apply(this); Theme.ApplyWindowChrome(this); };
        BackColor = Theme.Colors.Window;
        ForeColor = Theme.Colors.Text;
        OnProviderChanged(keepValues: true);
        RefreshAddinState();
        ShowStep(0);
    }

    private static LlmProfileConfig Copy(LlmProfileConfig p) =>
        System.Text.Json.JsonSerializer.Deserialize<LlmProfileConfig>(System.Text.Json.JsonSerializer.Serialize(p))!;

    private static SelectableLabel Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(580, 0), Margin = new Padding(0, 0, 0, 10) };

    private static Control Row(string label, Control field) => Row(new SelectableLabel { Text = label, AutoSize = true }, field);

    private static Control Row(Label label, Control field)
    {
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        label.Anchor = AnchorStyles.Left;
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(field, 1, 0);
        return row;
    }

    private static Control Page(params Control[] controls)
    {
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        page.Controls.AddRange(controls);
        page.Layout += (_, _) =>
        {
            // Wrap informative text at the available width, including when a vertical scrollbar appears.
            int width = Math.Max(1, page.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
            foreach (Control control in page.Controls)
                if (control is Label || control is CheckBox)
                {
                    var maximum = new Size(Math.Max(1, width - control.Margin.Horizontal), 0);
                    if (control.MaximumSize != maximum) control.MaximumSize = maximum;
                }
        };
        return page;
    }

    private ProviderCapabilities Caps => _cmbProvider.SelectedItem as ProviderCapabilities ?? ProviderCatalog.All[0];

    private void OnProviderChanged(bool keepValues = false)
    {
        var caps = Caps;
        if (!keepValues)
        {
            _txtEndpoint.Text = caps.DefaultBaseUrl;
            _txtModel.Text = caps.DefaultModel;
            _lblTest.Text = string.Empty;
        }
        bool needsKey = caps.RequiresSecret;
        _lblKey.Text = needsKey ? "Chiave API:" : "Token (facoltativo):";
        _lblModelNote.Text = caps.IsExternal
            ? "Cloud: le azioni IA inviano dati al servizio configurato, entro il livello di esposizione del profilo (parte da «1 · Minimo»: fatti locali con segnaposto). Puoi salvare il profilo cloud come predefinito; gli invii restano bloccati fino all'autorizzazione in Privacy e dati. La chiave è salvata cifrata in secrets.bin."
            : "Il modello usa il server indicato. Su loopback (questo PC), senza Web Search, il testo resta sul PC. Un indirizzo esterno o Web Search possono inviare dati fuori dal PC e richiedono l'informativa accettata in Privacy e dati. Il servizio locale e il modello vanno installati a parte.";
    }

    private LlmProfileConfig Edited()
    {
        var caps = Caps;
        var p = Copy(Profile);
        bool switched = p.Type != caps.Type;
        p.Type = caps.Type;
        p.BaseUrl = _txtEndpoint.Text.Trim();
        p.Model = _txtModel.Text.Trim();
        p.External = caps.IsExternal;
        if (!caps.AuthTypes.Contains(p.AuthType)) p.AuthType = caps.AuthTypes[0];
        if (caps.Type == ProviderCatalog.Ollama && !string.IsNullOrWhiteSpace(_txtKey.Text)) p.AuthType = "bearer";
        if (switched)
        {
            p.DisplayName = caps.DisplayName;
            p.WebSearchEnabled = false;
            p.AllowAttachmentUpload = false;
        }
        return p;
    }

    private string? TypedSecret => string.IsNullOrWhiteSpace(_txtKey.Text) ? null : _txtKey.Text.Trim();

    private async System.Threading.Tasks.Task TestAsync(Button button)
    {
        if (_connectionCheck != null) { _connectionCheck.Cancel(); return; }
        var p = Edited();
        using var run = new InteractiveRequest();
        _connectionCheck = run;
        string label = button.Text;
        button.Text = "Annulla verifica";
        _lblTest.Text = "Verifica in corso (massimo 10 secondi)…";
        Theme.SetTone(_lblTest, Tone.Warning);
        try
        {
            var client = (ClientFactory ?? ((profile, key) => LlmClientFactory.CreateClient(profile, key)))(p, TypedSecret);
            try
            {
                if (p.Model.Length == 0)
                {
                    var models = await client.ListModelsAsync(run.Token);
                    run.Token.ThrowIfCancellationRequested();
                    if (IsDisposed) return;
                    _lblTest.Text = models.Count == 0 ? "Connessione riuscita, ma il servizio non elenca modelli: scrivi il nome del modello."
                        : $"Connessione riuscita ({models.Count} modelli disponibili): scegli il modello dall'elenco.";
                    Theme.SetTone(_lblTest, Tone.Success);
                    if (models.Count > 0) ModelPicker.Show(_btnDetect, models, PickModel);
                }
                else
                {
                    var health = await client.CheckHealthAsync(run.Token);
                    run.Token.ThrowIfCancellationRequested();
                    if (IsDisposed) return;
                    bool modelFound = !health.IsModelMissing && (health.AvailableModels.Count == 0 || health.AvailableModels.Any(m => ModelMatches(p, m)));
                    _lblTest.Text = health.IsModelMissing ? MissingModelText(p, health.AvailableModels.Count)
                        : !health.IsHealthy ? "Connessione non riuscita: " + health.ErrorMessage
                        : modelFound ? $"Connessione riuscita ({health.AvailableModels.Count} modelli disponibili)."
                        : MissingModelText(p, health.AvailableModels.Count);
                    Theme.SetTone(_lblTest, health.IsHealthy && modelFound ? Tone.Success : Tone.Danger);
                }
            }
            finally { (client as IDisposable)?.Dispose(); }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) { _lblTest.Text = run.TimedOut ? "Tempo massimo di 10 secondi superato." : "Verifica annullata."; Theme.SetTone(_lblTest, Tone.Warning); }
        }
        catch (Exception ex)
        {
            OnlyWorkspace.Infrastructure.Logging.OnlyWorkspaceLog.Logger.Warning(ex, "Setup connection check failed");
            if (!IsDisposed) { _lblTest.Text = "Errore: " + ErrorText.For(ex); Theme.SetTone(_lblTest, Tone.Danger); }
        }
        finally { _connectionCheck = null; if (!IsDisposed) button.Text = label; }
    }

    private static string MissingModelText(LlmProfileConfig profile, int count) =>
        $"Connessione riuscita, ma il modello «{profile.Model}» non è tra i {count} disponibili. Scegli un modello dall'elenco.";

    private static bool ModelMatches(LlmProfileConfig profile, string model)
    {
        var expected = profile.Model;
        if (profile.Type.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            if (model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) model = model.Substring(7);
            if (expected.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) expected = expected.Substring(7);
        }
        return string.Equals(model, expected, StringComparison.OrdinalIgnoreCase) ||
            profile.Type.Equals("ollama", StringComparison.OrdinalIgnoreCase) && model.StartsWith(expected + ":", StringComparison.OrdinalIgnoreCase);
    }

    private void PickModel(string model)
    {
        _txtModel.Text = model;
        _lblTest.Text = string.Empty;
    }

    private bool KeyMissing()
    {
        if (!Caps.RequiresSecret || TypedSecret != null) return false;
        CopyableMessageBox.Show(this, "Il servizio cloud scelto richiede una chiave API.", "Modello IA", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    /// <summary>«Scegli modello…», also opened by «Avanti» without a model: the service's models in a menu under the button.</summary>
    private async System.Threading.Tasks.Task DetectAsync()
    {
        if (!_btnDetect.Enabled || KeyMissing()) return;
        _btnDetect.Enabled = false;
        _btnDetect.Text = "Rilevamento…";
        try { await ModelPicker.PickAsync(_btnDetect, Edited(), TypedSecret, PickModel, ClientFactory); }
        finally
        {
            if (!IsDisposed)
            {
                _btnDetect.Enabled = true;
                _btnDetect.Text = DetectText;
            }
        }
    }

    /// <summary>The Outlook profile the archive will follow, read again at each showing: Outlook may have been set up meanwhile.</summary>
    private void RefreshOutlookProfile()
    {
        var profiles = ReadOutlookProfiles();
        _lblOutlook.Text = profiles.Describe();
        Theme.SetTone(_lblOutlook, profiles.Names.Count > 0 ? Tone.Success : Tone.Warning);
    }

    private void RefreshAddinState()
    {
        var (state, message) = AddinRegistration.Evaluate(AddinRegistration.Read());
        _lblAddin.Text = message;
        Theme.SetTone(_lblAddin, state == AddinState.Ready ? Tone.Success : Tone.Warning);
        _btnRepair.Visible = state is AddinState.DisabledByOutlook or AddinState.DisabledByUser;
    }

    private void RepairAddin()
    {
        try
        {
            AddinRegistration.Repair();
            RefreshAddinState();
            if (_btnRepair.Visible == false) _lblAddin.Text += " Riavvia Outlook per caricarlo.";
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show(this, "Riparazione non riuscita: " + ErrorText.For(ex), "Add-in Outlook", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Next()
    {
        if (_step == 1 && KeyMissing()) return;
        if (_step == 1 && string.IsNullOrWhiteSpace(_txtModel.Text))
        {
            // Nothing to type from memory: the service's list opens instead of a message (DetectAsync reports its own errors).
            _ = DetectAsync();
            return;
        }
        if (_step < _steps.Count - 1) { ShowStep(_step + 1); return; }

        var edited = Edited();
        Profile.Type = edited.Type;
        Profile.DisplayName = edited.DisplayName;
        Profile.BaseUrl = edited.BaseUrl;
        Profile.Model = edited.Model;
        Profile.AuthType = edited.AuthType;
        Profile.External = edited.External;
        Profile.AllowAttachmentUpload = edited.AllowAttachmentUpload;
        Profile.WebSearchEnabled = edited.WebSearchEnabled;
        Secret = TypedSecret;
        if (!StartupManager.SetRunAtStartup(_chkAutostart.Checked))
            CopyableMessageBox.Show(this, "Impossibile modificare l'avvio automatico di Windows.", "Avvio automatico", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowStep(int index)
    {
        _step = Math.Max(0, Math.Min(_steps.Count - 1, index));
        for (int i = 0; i < _steps.Count; i++) _steps[i].Visible = i == _step;
        _lblHeader.Text = $"Passo {_step + 1} di {_steps.Count} · {_titles[_step]}";
        _btnBack.Enabled = _step > 0;
        _btnNext.Text = _step == _steps.Count - 1 ? "Applica e salva" : "Avanti";
        if (_step >= 3) RefreshOutlookProfile();
        if (_step == _steps.Count - 1) _lblSummary.Text = Summary();
    }

    private string Summary()
    {
        var p = Edited();
        return string.Join("\r\n\r\n", new[]
        {
            $"Modello IA: {p.Model} su {p.BaseUrl} ({(p.IsExternal ? "cloud" : "locale")}).",
            ArchiveEnabled
                ? "Archivio: consentito, con intestazioni e corpo integrale acquisito" + (IndexAttachments ? ", con il testo degli allegati." : ", senza il testo degli allegati.") + " Database cifrato per utente, su tutte le cassette aperte; file allegati originali non archiviati."
                : "Archivio: non consentito; si può abilitare in seguito in Configurazione → Archivio email.",
            "Gli invii esterni delle azioni IA e di Web Search richiedono l'informativa accettata.",
            "Avvio con Windows: " + (_chkAutostart.Checked ? "sì, per l'utente corrente." : "no."),
            // Already a sentence about the add-in: a prefix would repeat it («Add-in: Add-in registrato…»).
            _lblAddin.Text,
            _lblOutlook.Text,
            "«Applica e salva» scrive config.json e secrets.bin (può comparire la richiesta di autorizzazione di amministratore). Poi apri Outlook: nella scheda OnlyWorkspace usa «Riquadro»."
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _connectionCheck?.Cancel(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
