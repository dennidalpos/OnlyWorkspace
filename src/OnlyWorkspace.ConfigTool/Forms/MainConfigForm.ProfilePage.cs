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

public sealed partial class MainConfigForm
{

    private Control BuildProfilesPage()
    {
        var layout = new TableLayoutPanel { ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        layout.Controls.Add(BuildProfileList(), 0, 0);

        // Profile details: one section at a time (scrolls only when the window is short)
        var editCard = new Card { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var editLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        editLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        editLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        editLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var conn = NewGrid();
        var ctx = NewGrid();
        var net = NewGrid();
        var capsGrid = NewGrid();
        var grids = new[] { conn, ctx, net, capsGrid };
        ctx.Visible = net.Visible = capsGrid.Visible = false;
        // Diagnostics last and never the section shown on opening.
        var sections = new SegmentedBar("Connessione", "Contesto", "Autorizzazioni", "Avanzate");
        var sectionTips = new[]
        {
            "Servizio, indirizzo, modello, chiave e tempi di attesa del profilo selezionato.",
            "Quanto testo il modello legge e scrive (solo per i modelli locali) e quanto è creativo nelle risposte.",
            "Cosa può uscire dal PC: con Minimo solo fatti protetti; con Standard ed Esteso anche testo mascherato. Query web non verificabili bloccate.",
            "Diagnostica del modello: capacità con esito, provenienza e data; si aggiornano con il pulsante Diagnosi."
        };
        for (int i = 0; i < sections.Controls.Count && i < sectionTips.Length; i++) Tip(sections.Controls[i], sectionTips[i]);
        var host = _profileHost = new ScrollPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        sections.SelectedIndexChanged += (s, e) =>
        {
            for (int i = 0; i < grids.Length; i++) grids[i].Visible = i == sections.SelectedIndex;
            // The scrolling host otherwise keeps the extent of a taller section shown before (empty scroll bar).
            host.PerformLayout();
            Ui.ShowView(host, grids, sections.SelectedIndex);
        };
        host.Controls.AddRange(grids);
        editLayout.Controls.Add(sections, 0, 0);
        editLayout.Controls.Add(host, 0, 1);
        editCard.Controls.Add(editLayout);
        layout.Controls.Add(editCard, 1, 0);

        BuildConnectionRows(conn);
        BuildCapabilityRows(capsGrid);
        BuildContextRows(ctx);
        BuildPermissionRows(net);
        editLayout.Controls.Add(BuildProfileTestRow(), 0, 2);

        return layout;
    }

    /// <summary>Profile list with add, remove and default.</summary>
    private Card BuildProfileList()
    {
        var listCard = new Card("Profili") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) };
        var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        listLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        listLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lstProfiles = new ProfileListBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            Caption = o => ((ProfileItem)o).Name,
            IsMarked = o => ((ProfileItem)o).IsDefault
        };
        _lstProfiles.SelectedIndexChanged += LstProfiles_SelectedIndexChanged;
        Tip(_lstProfiles, "Modelli IA configurati. Selezionane uno per vederne e cambiarne le impostazioni a destra. La stella indica quello usato di default dagli utenti.");
        listLayout.Controls.Add(_lstProfiles, 0, 0);
        listLayout.SetColumnSpan(_lstProfiles, 2);

        var btnAdd = SecondaryButton("Aggiungi");
        btnAdd.Glyph = "\uE710";
        btnAdd.Dock = DockStyle.Fill;
        btnAdd.Margin = new Padding(0, 0, 3, 6);
        btnAdd.Click += BtnAddProfile_Click;
        Tip(btnAdd, "Aggiunge un profilo nuovo, partendo da un modello locale (Ollama su questo PC); poi scegli servizio, modello e chiave. Si applica con Salva.");
        var btnRemove = SecondaryButton("Rimuovi");
        btnRemove.Glyph = "\uE738";
        btnRemove.Dock = DockStyle.Fill;
        btnRemove.Margin = new Padding(3, 0, 0, 6);
        btnRemove.Click += BtnRemoveProfile_Click;
        Tip(btnRemove, "Toglie il profilo selezionato e la sua chiave salvata. Ne deve restare almeno uno. Si applica con Salva.");
        listLayout.Controls.Add(btnAdd, 0, 1);
        listLayout.Controls.Add(btnRemove, 1, 1);

        var btnSetDefault = SecondaryButton("Imposta predefinito");
        btnSetDefault.Glyph = "\uE734";
        btnSetDefault.Dock = DockStyle.Fill;
        btnSetDefault.Click += (s, e) =>
        {
            if (_lstProfiles.SelectedItem is ProfileItem item)
            {
                _config.DefaultProfile = item.Profile.Id;
                RefreshProfileNames();
                SetStatus($"Profilo '{item.Profile.Id}' impostato come predefinito.");
                MarkDirty();
            }
        };
        Tip(btnSetDefault, "Il profilo selezionato diventa quello usato da tutti gli utenti nel riquadro di Outlook. Si applica con Salva.");
        listLayout.Controls.Add(btnSetDefault, 0, 2);
        listLayout.SetColumnSpan(btnSetDefault, 2);

        listCard.Controls.Add(listLayout);
        return listCard;
    }

    /// <summary>Connessione: identity, provider, endpoint, model, credentials, timeouts and proxy.</summary>
    private void BuildConnectionRows(TableLayoutPanel conn)
    {
        _txtProfileId = AddRow(conn, "ID profilo:", new ThemedTextBox { Width = 180, Anchor = AnchorStyles.Left },
            "ID univoco nei file di configurazione. Invio o uscita dal campo aggiorna il profilo; Salva applica la modifica.");
        _txtProfileId.Leave += (s, e) => ApplyProfileIdChange();
        _txtProfileId.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ApplyProfileIdChange(); e.SuppressKeyPress = true; } };

        _txtDisplayName = AddRow(conn, "Nome visualizzato:", StretchTextBox(),
            "Nome mostrato in Stato e nel suggerimento del pallino del riquadro. Si applica con Salva.");
        _txtDisplayName.TextChanged += (s, e) =>
        {
            OnProfileFieldChanged();
            if (!_isLoading) RefreshProfileNames();
        };

        _cmbProvider = AddRow(conn, "Servizio IA:", Combo(240),
            "Sei servizi preimpostati. Compatibile OpenAI richiede un indirizzo, senza ricerca web né lettura capacità. I precedenti restano nei loro profili. «non provato» indica un servizio senza prova con un account reale. Si applica con Salva.");
        _cmbProvider.Items.AddRange(ProviderCatalog.All.Cast<object>().ToArray());
        _cmbProvider.SelectedIndexChanged += (s, e) => OnProviderChanged();

        _cmbCloudPreset = AddRow(conn, "Preset cloud:", Combo(240),
            "Compila l'indirizzo di un servizio compatibile OpenAI con dati dichiarati nell'UE (dichiarazione del fornitore, non attestazione di conformità) e lo segna come cloud: le azioni IA inviano dati al servizio, entro il livello di esposizione del profilo. Si applica con Salva.");
        _cmbCloudPreset.Items.Add(CloudPresetNone);
        _cmbCloudPreset.Items.AddRange(CloudProviderPresets.All.Cast<object>().ToArray());
        _cmbCloudPreset.SelectedIndex = 0;
        _cmbCloudPreset.SelectedIndexChanged += (s, e) => OnCloudPresetChosen();

        _txtEndpoint = AddRow(conn, "Indirizzo del servizio:", StretchTextBox(),
            "Indirizzo del servizio: HTTP solo su loopback (127.0.0.1), HTTPS per rete e cloud. Si applica con Salva.");
        _txtEndpoint.TextChanged += (s, e) => { SyncCloudPreset(CurrentCaps()); OnProfileFieldChanged(); ApplyCapabilities(); InvalidateWebSupport(); };

        _txtModel = new ThemedTextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _txtModel.TextChanged += (s, e) =>
        {
            if (!_isLoading && _lstProfiles.SelectedItem is ProfileItem selected &&
                !string.Equals(selected.Profile.Model, _txtModel.Text.Trim(), StringComparison.Ordinal))
            {
                if (_chkWebSearch.Checked) _webOffReason = "il modello è cambiato";
                _chkWebSearch.Checked = false;
                selected.Profile.WebSearchEnabled = false;
            }
            OnProfileFieldChanged();
            if (!_isLoading) { ShowModelLimits(); InvalidateWebSupport(); }
        };
        _txtModel.HandleCreated += (s, e) => ApplyModelHint();
        Tip(_txtModel, "Nome esatto del modello. Per il cloud inserisci prima la chiave, poi usa «Scegli modello…». Si applica con Salva.");
        _btnDetectModels = SecondaryButton(DetectModelsText);
        Metrics.CompactButton(_btnDetectModels);
        _btnDetectModels.Click += BtnDetectModels_Click;
        Tip(_btnDetectModels, "Elenca i modelli inviando la chiave inserita al servizio, senza dati di Outlook. Per le capacità usa Avanzate; Salva applica la scelta.");
        AddRow(conn, "Modello:", FillRow(new InputFrame(_txtModel), _btnDetectModels), "Nome del modello invocato.");

        _lblModelLimits = AddRow(conn, "", new SelectableLabel { AutoSize = true, Margin = new Padding(3, 0, 3, 6) },
            "Quanto testo il modello può leggere e scrivere, come dichiarato dal servizio. Non tutti i servizi lo pubblicano.");
        Theme.SetTone(_lblModelLimits, Tone.Muted);

        _cmbAuthType = AddRow(conn, "Autenticazione:", Combo(180),
            "Autenticazione del servizio: nessuna, token Bearer o chiave API. Mantieni il valore proposto se corretto. Si applica con Salva.");
        _cmbAuthType.SelectedIndexChanged += (s, e) => { OnProfileFieldChanged(); ApplyCapabilities(); InvalidateWebSupport(); };

        _txtToken = new ThemedTextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, UseSystemPasswordChar = true };
        _txtToken.TextChanged += (s, e) =>
        {
            if (!_isLoading && _lstProfiles.SelectedItem is ProfileItem item)
            {
                _diagnosis.CancelConnection();
                _inMemorySecrets[item.Profile.Id] = _txtToken.Text;
                MarkDirty();
                InvalidateWebSupport();
                InvalidateProbes(item.Profile);
            }
        };
        Tip(_txtToken, "Chiave del servizio, cifrata su questo PC con Salva. Lascia vuoto e salva per rimuoverla.");
        _chkShowToken = new ThemedCheckBox { Text = "Mostra", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 3, 3, 3) };
        _chkShowToken.CheckedChanged += (s, e) => _txtToken.UseSystemPasswordChar = !_chkShowToken.Checked;
        Tip(_chkShowToken, "Mostra la chiave in chiaro per controllarla; non viene salvato nulla.");
        _tokenRow = AddRow(conn, "Chiave di accesso:", FillRow(new InputFrame(_txtToken), _chkShowToken), "Token o API key del profilo.");


        _numFirstTokenTimeout = AddRow(conn, "Timeout primo token (s):", Numeric(5, 3600, 120, 5),
            "Secondi per l’inizio della risposta, incluso il caricamento del modello. Si applica con Salva.");
        _numFirstTokenTimeout.ValueChanged += (s, e) => OnProfileFieldChanged();

        _numIdleTimeout = AddRow(conn, "Timeout inattività (s):", Numeric(0, 3600, 60, 5),
            "Secondi senza nuovi dati prima di fermare la risposta iniziata; 0 disattiva il limite. Si applica con Salva.");
        _numIdleTimeout.ValueChanged += (s, e) => OnProfileFieldChanged();

        _chkUseProxy = AddWide(conn, new ThemedCheckBox { Text = "Usa il proxy di sistema", AutoSize = true },
            "Usa il proxy di Windows per raggiungere il servizio; lascialo spento sul PC locale. Si applica con Salva.");
        _chkUseProxy.CheckedChanged += (s, e) => OnProfileFieldChanged();
    }

    /// <summary>Contesto: Ollama context, output and budget, temperature, keep alive, reasoning.</summary>
    private void BuildContextRows(TableLayoutPanel ctx)
    {
        _cmbPreset = AddRow(ctx, "Preset contesto:", Combo(210),
            "Solo locale: imposta contesto e risposta; più testo richiede più memoria. Nel cloud decide il servizio. Si applica con Salva.");
        _cmbPreset.SelectedIndexChanged += (s, e) => OnPresetChanged();

        _numNumCtx = AddRow(ctx, "NumCtx (Ollama):", Numeric(2048, 262144, 16384, 1024),
            "Solo Ollama: token disponibili per domanda e risposta. Cambiarli può ricaricare il modello. Si applica con Salva.");
        _numNumCtx.ValueChanged += (s, e) => OnContextValueChanged(recomputeBudget: true);

        _numMaxTokens = AddRow(ctx, "Max output token:", Numeric(256, 128000, 2048, 256),
            "Solo locale: massimo di token della risposta. Si applica con Salva.");
        _numMaxTokens.ValueChanged += (s, e) => OnContextValueChanged(recomputeBudget: true);

        _numBudget = Numeric(1000, 1000000, 12698, 500);
        _numBudget.ValueChanged += (s, e) => OnContextValueChanged(recomputeBudget: false);
        Tip(_numBudget, "Solo locale: token per email e allegati. Se il testo supera il budget, il riquadro si ferma e avvisa. Si applica con Salva.");
        AddRow(ctx, "Budget token (prompt):", _numBudget, "Solo locale: token per email e allegati. Se il testo supera il budget, il riquadro si ferma e avvisa. Si applica con Salva.");
        _lblBudgetHint = AddRow(ctx, "", new SelectableLabel { AutoSize = true, Margin = new Padding(3, 0, 3, 6) },
            "Valore consigliato per le impostazioni scelte sopra.");
        Theme.SetTone(_lblBudgetHint, Tone.Muted);
        // A warning shown only when budget + output overflow NumCtx (UpdateBudgetHint): no standing «?» for a hidden label.
        _lblBudgetHint.Visible = false;

        _numTemp = AddRow(ctx, "Temperatura:", new ThemedNumeric { Width = 100, Minimum = 0M, Maximum = 2M, DecimalPlaces = 2, Increment = 0.05M, Value = 0.3M, Anchor = AnchorStyles.Left },
            "Variazione delle risposte: valori bassi la riducono, valori alti la aumentano. Si applica con Salva.");
        _numTemp.ValueChanged += (s, e) => OnProfileFieldChanged();

        _txtKeepAlive = AddRow(ctx, "Mantieni in memoria (Ollama):", new ThemedTextBox { Width = 100, Anchor = AnchorStyles.Left },
            "Solo Ollama: durata in memoria dopo la richiesta (30m, 1h, 90s); 0 libera subito la memoria. Si applica con Salva.");
        _txtKeepAlive.TextChanged += (s, e) => OnProfileFieldChanged();

        _chkDisableThinking = AddWide(ctx, new ThemedCheckBox { Text = "Disattiva ragionamento del modello", AutoSize = true },
            "Disattiva il ragionamento nei modelli supportati (Ollama, Qwen), riducendo tempi e token. Non viene mostrato. Si applica con Salva.");
        _chkDisableThinking.CheckedChanged += (s, e) => OnProfileFieldChanged();
    }

    /// <summary>
    /// Notes wrap at the visible width of the field column: at 440 px one went past the card at 100% (A159), and the
    /// reason Ricerca web went off, set while the section was hidden, ran past the edge (#226).
    /// </summary>
    private void WrapPermissionNotes()
    {
        var net = _permissionGrid;
        if (net == null || net.ClientSize.Width == 0) return;
        foreach (var note in new[] { _lblExposure, _lblAttachmentsNote, _lblWebActionsNote })
        {
            if (note == null) continue;
            Control cell = note.Parent is FlowLayoutPanel inline && inline.Parent == net ? inline : note;
            int width = Math.Max(Ui.Px(this, 160), net.ClientSize.Width - cell.Left - cell.Margin.Right - cell.Padding.Horizontal - note.Margin.Horizontal);
            if (note.MaximumSize.Width != width) note.MaximumSize = new Size(width, 0);
        }
    }

    /// <summary>Autorizzazioni: cloud service, attachment text and Ricerca web.</summary>
    private void BuildPermissionRows(TableLayoutPanel net)
    {
        AddSubheading(net, "Servizio e livello di esposizione");
        _chkExternal = AddWide(net, new ThemedCheckBox { Text = "Servizio esterno: invii soggetti alla protezione dei dati", AutoSize = true },
            "Il servizio è fuori da questo PC. Gli invii, già mascherati, richiedono l'autorizzazione in Privacy e dati. Sempre attivo per servizi cloud e indirizzi esterni.");
        _chkExternal.CheckedChanged += (s, e) =>
        {
            if (!_isLoading && _chkExternal.Enabled && _lstProfiles.SelectedItem is ProfileItem item)
                item.Profile.External = _chkExternal.Checked;
            OnProfileFieldChanged(); ApplyCapabilities();
        };

        _cmbExposure = AddRow(net, "Esposizione dati:", new ThemedComboBox { Width = 200, Anchor = AnchorStyles.Left },
            "0 blocca ogni invio. Minimo invia solo fatti estratti in locale; Standard anche email e testo libero mascherati; Esteso aggiunge gli allegati con permesso distinto e la ricerca web con il testo pseudonimizzato. Si applica con Salva.");
        foreach (var level in ExposurePolicy.All) _cmbExposure.Items.Add(new ExposureItem(level));
        _cmbExposure.SelectedIndexChanged += (s, e) => { ShowExposure(); OnProfileFieldChanged(); ApplyCapabilities(); UpdateWebActionsEnabled(); };
        _lblExposure = AddRow(net, "", new SelectableLabel { AutoSize = true, MaximumSize = new Size(Metrics.TextWidthNarrow, 0), Margin = new Padding(3, 0, 3, 6) },
            "Cosa esce dal PC al livello scelto.");
        Theme.SetTone(_lblExposure, Tone.Muted);
        DescriptionHelp(_lblExposure, "Protezione dei dati");
        _permissionGrid = net;
        net.Layout += (_, _) => WrapPermissionNotes();
        net.VisibleChanged += (_, _) => WrapPermissionNotes();

        _cmbRegion = AddRow(net, "Area del servizio:", new ThemedComboBox { Width = 200, Anchor = AnchorStyles.Left },
            "Dove il servizio elabora i dati, secondo il contratto: UE, non UE o non specificata. Non è dedotta dall'indirizzo; se non specificata, l'informativa chiede una conferma. Si applica con Salva.");
        foreach (var region in new[] { ServiceRegion.Unspecified, ServiceRegion.Eu, ServiceRegion.NonEu }) _cmbRegion.Items.Add(new RegionItem(region));
        _cmbRegion.SelectedIndexChanged += (s, e) => OnProfileFieldChanged();

        AddSubheading(net, "Cosa può partire verso il servizio");
        _chkAllowAttachments = AddWide(net, new ThemedCheckBox { Text = "Includi allegati verso il cloud", AutoSize = true },
            "Disponibile con Esteso: include nelle richieste al servizio il testo estratto in locale dagli allegati, pseudonimizzato sul PC. I nomi dei file e i file restano sul PC. Si applica con Salva.");
        _chkAllowAttachments.CheckedChanged += (s, e) => OnProfileFieldChanged();
        _lblAttachmentsNote = AddRow(net, "", new SelectableLabel { AutoSize = true, Text = "Disponibile solo con Esteso.", Margin = new Padding(3, 0, 3, 6) },
            "Perché «Includi allegati verso il cloud» non è selezionabile.");
        Theme.SetTone(_lblAttachmentsNote, Tone.Muted);
        _chkPseudonymizeAmounts = AddWide(net, new ThemedCheckBox { Text = "Sostituisci gli importi con segnaposto", AutoSize = true },
            "Sostituisce gli importi riconosciuti prima che il testo arrivi al modello e li ripristina nella risposta su questo PC. Nei profili cloud è sempre attiva. Si applica con Salva.");
        _chkPseudonymizeDates = AddWide(net, new ThemedCheckBox { Text = "Sostituisci le date con segnaposto", AutoSize = true },
            "Sostituisce le date riconosciute prima che il testo arrivi al modello e le ripristina nella risposta su questo PC. Nei profili cloud è sempre attiva. Si applica con Salva.");
        _chkPseudonymizeAmounts.CheckedChanged += (s, e) => OnProfileFieldChanged();
        _chkPseudonymizeDates.CheckedChanged += (s, e) => OnProfileFieldChanged();

        AddSubheading(net, "Ricerca web");
        _lblWebSupport = AddRow(net, "Ricerca web:", new SelectableLabel { AutoSize = true, Text = WebNotChecked, Margin = new Padding(3, 5, 3, 6) },
            "Se il modello scelto sa cercare sul web, secondo il servizio o secondo la prova fatta con il pulsante.");
        _chkWebSearch = AddRow(net, "Abilitazione:", new ThemedCheckBox { Text = "Ricerca web attiva", AutoSize = true },
            "Abilita la ricerca web per le azioni spuntate sotto. Con Esteso la ricerca invia l'intera richiesta (testo di email e allegati compreso) pseudonimizzata sul PC; a Minimo e Standard nessuna ricerca; le query generate dal modello restano bloccate. Richiede l'informativa accettata.");
        _chkWebSearch.CheckedChanged += (s, e) =>
        {
            if (_chkWebSearch.Checked) _webOffReason = null;
            OnProfileFieldChanged();
            UpdateWebActionsEnabled();
        };
        // Two columns as wide as their longest action: the boxes line up from row to row, and a third column
        // did not fit the field at the default window width (the last actions were cut off).
        _webActions = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, GrowStyle = TableLayoutPanelGrowStyle.AddRows, Margin = Padding.Empty };
        for (int i = 0; i < _webActions.ColumnCount; i++) _webActions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var action in WebSearchAction.All)
        {
            var box = new ThemedCheckBox { Text = action.Value, Tag = action.Key, AutoSize = true, Margin = new Padding(3, 3, 8, 3) };
            box.CheckedChanged += (s, e) => OnProfileFieldChanged();
            Tip(box, WebActionTips.TryGetValue(action.Key, out var tip) ? tip : "Questa azione del riquadro può cercare sul web.");
            _webActions.Controls.Add(box);
        }
        AddRow(net, "Azioni con ricerca:", _webActions, "Quali azioni del riquadro integrano la risposta con una ricerca sul web.");
        _lblWebActionsNote = AddRow(net, "", new SelectableLabel { AutoSize = true, Margin = new Padding(3, 0, 3, 6) },
            "Perché le azioni con ricerca non sono selezionabili.");
        Theme.SetTone(_lblWebActionsNote, Tone.Muted);
        _txtWebSearchKey = AddRow(net, "Chiave Ollama Web:", new ThemedTextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, UseSystemPasswordChar = true },
            "Solo Ollama Web: chiave distinta da quella del modello. Salva la conserva cifrata su questo PC.");
        _txtWebSearchKey.TextChanged += (s, e) =>
        {
            if (_isLoading || _lstProfiles.SelectedItem is not ProfileItem selected) return;
            _inMemoryWebSecrets[selected.Profile.Id] = _txtWebSearchKey.Text;
            MarkDirty();
            InvalidateWebSupport();
            InvalidateProbes(selected.Profile, webOnly: true);
        };
    }

    private void ShowExposure() =>
        _lblExposure.Text = _chkExternal.Checked ? ExposurePolicy.Describe(SelectedExposure) : "Profilo locale: nessun dato lascia il PC; importi e date si sostituiscono se lo scegli.";

    /// <summary>Diagnosi with its result, always visible under the sections.</summary>
    private TableLayoutPanel BuildProfileTestRow()
    {
        _btnTestProfile = SecondaryButton("Diagnosi");
        _btnTestProfile.Click += BtnTestProfile_Click;
        Tip(_btnTestProfile, ProbeCost + " Invia la chiave al servizio. Non salva le modifiche.");
        // AutoSize with side anchors wraps to the column: a long error grows the row instead of being clipped.
        _lblTestResult = Ui.SetFont(new OperationalStatusLabel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(10, 0, 0, 0)
        }, 9F, family: "Segoe UI Semibold");
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        actions.Controls.Add(_btnTestProfile, 0, 0);
        actions.Controls.Add(_lblTestResult, 1, 0);
        return actions;
    }
}
