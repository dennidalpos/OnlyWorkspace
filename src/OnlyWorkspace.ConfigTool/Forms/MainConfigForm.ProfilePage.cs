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
            "Cosa può uscire dal PC: solo fatti strutturati protetti; testo libero e query web non verificabili sono bloccati.",
            "Diagnostica del modello: capacità con esito, provenienza e data; Aggiorna capacità e Prova funzioni."
        };
        for (int i = 0; i < sections.Controls.Count && i < sectionTips.Length; i++) Tip(sections.Controls[i], sectionTips[i]);
        sections.SelectedIndexChanged += (s, e) =>
        {
            for (int i = 0; i < grids.Length; i++) grids[i].Visible = i == sections.SelectedIndex;
            // The scrolling host otherwise keeps the extent of a taller section shown before (empty scroll bar).
            grids[0].Parent?.PerformLayout();
        };
        var host = new ScrollPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
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
            "Nome tecnico del profilo, unico, usato nei file di configurazione (per esempio qwen-cloud). Gli utenti non lo vedono. La modifica vale quando premi Invio o lasci il campo.");
        _txtProfileId.Leave += (s, e) => ApplyProfileIdChange();
        _txtProfileId.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ApplyProfileIdChange(); e.SuppressKeyPress = true; } };

        _txtDisplayName = AddRow(conn, "Nome visualizzato:", StretchTextBox(),
            "Nome che gli utenti vedono nel riquadro di Outlook, nel suggerimento sul nome OnlyWorkspace e in Stato (per esempio «Qwen cloud»).");
        _txtDisplayName.TextChanged += (s, e) =>
        {
            OnProfileFieldChanged();
            if (!_isLoading) RefreshProfileNames();
        };

        _cmbProvider = AddRow(conn, "Servizio IA:", Combo(240),
            "Sceglie tra sei servizi preimpostati. Compatibile OpenAI richiede un indirizzo e non offre ricerca web o lettura delle capacità specifiche. I servizi precedenti particolari restano disponibili solo nei rispettivi profili. La modifica si applica al salvataggio.");
        _cmbProvider.Items.AddRange(ProviderCatalog.All.Cast<object>().ToArray());
        _cmbProvider.SelectedIndexChanged += (s, e) => OnProviderChanged();

        _txtEndpoint = AddRow(conn, "Indirizzo del servizio:", StretchTextBox(),
            "Indirizzo del servizio, di solito già proposto. Per un servizio su questo PC basta http://127.0.0.1:…; per un server in rete o in cloud serve https://.");
        _txtEndpoint.TextChanged += (s, e) => { OnProfileFieldChanged(); ApplyCapabilities(); InvalidateWebSupport(); };

        _txtModel = new ThemedTextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _txtModel.TextChanged += (s, e) =>
        {
            if (!_isLoading && _lstProfiles.SelectedItem is ProfileItem selected &&
                !string.Equals(selected.Profile.Model, _txtModel.Text.Trim(), StringComparison.Ordinal))
            {
                _chkWebSearch.Checked = false;
                selected.Profile.WebSearchEnabled = false;
            }
            OnProfileFieldChanged();
            if (!_isLoading) { ShowModelLimits(); InvalidateWebSupport(); }
        };
        Tip(_txtModel, "Nome esatto del modello (per esempio qwen3.5:9b). Con un servizio cloud inserisci prima la chiave e poi usa «Scegli modello…» per sceglierlo dall'elenco.");
        _btnDetectModels = SecondaryButton(DetectModelsText);
        _btnDetectModels.MinimumSize = new Size(0, 23);
        _btnDetectModels.Margin = new Padding(3);
        _btnDetectModels.Click += BtnDetectModels_Click;
        Tip(_btnDetectModels, "Chiede al servizio, con la chiave inserita, l'elenco dei modelli disponibili e te li fa scegliere. Non invia dati di Outlook e non verifica le capacità del modello: per quelle c'è «Avanzate».");
        AddRow(conn, "Modello:", FillRow(new InputFrame(_txtModel), _btnDetectModels), "Nome del modello invocato.");

        _lblModelLimits = AddRow(conn, "", new SelectableLabel { AutoSize = true, Margin = new Padding(3, 0, 3, 6) },
            "Quanto testo il modello può leggere e scrivere, come dichiarato dal servizio. Non tutti i servizi lo pubblicano.");
        Theme.SetTone(_lblModelLimits, Tone.Muted);

        _cmbAuthType = AddRow(conn, "Autenticazione:", Combo(180),
            "Come il servizio riconosce OnlyWorkspace: nessuna (servizio locale aperto), token (Bearer) o chiave API del fornitore cloud. Di solito va lasciato il valore proposto.");
        _cmbAuthType.SelectedIndexChanged += (s, e) => { OnProfileFieldChanged(); ApplyCapabilities(); InvalidateWebSupport(); };

        _txtToken = new ThemedTextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, UseSystemPasswordChar = true };
        _txtToken.TextChanged += (s, e) =>
        {
            if (!_isLoading && _lstProfiles.SelectedItem is ProfileItem item)
            {
                _connectionCheck?.Cancel();
                _capabilityRefresh?.Cancel();
                _inMemorySecrets[item.Profile.Id] = _txtToken.Text;
                MarkDirty();
                InvalidateWebSupport();
                InvalidateProbes(item.Profile);
            }
        };
        Tip(_txtToken, "Chiave o token rilasciato dal servizio. Viene salvato cifrato su questo PC (secrets.bin) e mai mostrato agli utenti. Lascia vuoto per toglierla.");
        _chkShowToken = new ThemedCheckBox { Text = "Mostra", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 3, 3, 3) };
        _chkShowToken.CheckedChanged += (s, e) => _txtToken.UseSystemPasswordChar = !_chkShowToken.Checked;
        Tip(_chkShowToken, "Mostra la chiave in chiaro per controllarla; non viene salvato nulla.");
        _tokenRow = AddRow(conn, "Chiave di accesso:", FillRow(new InputFrame(_txtToken), _chkShowToken), "Token o API key del profilo.");


        _numFirstTokenTimeout = AddRow(conn, "Timeout primo token (s):", Numeric(5, 3600, 120, 5),
            "Quanti secondi aspettare l'inizio della risposta, compreso il caricamento del modello. Aumentalo se un modello locale grande risulta «non ha risposto in tempo».");
        _numFirstTokenTimeout.ValueChanged += (s, e) => OnProfileFieldChanged();

        _numIdleTimeout = AddRow(conn, "Timeout inattività (s):", Numeric(0, 3600, 60, 5),
            "Quanti secondi di silenzio tollerare a risposta già iniziata prima di fermarla. 0 = aspetta sempre.");
        _numIdleTimeout.ValueChanged += (s, e) => OnProfileFieldChanged();

        _chkUseProxy = AddWide(conn, new ThemedCheckBox { Text = "Usa il proxy di sistema", AutoSize = true },
            "Usa il proxy impostato in Windows per raggiungere il servizio. Serve di solito in azienda per i servizi cloud; lascialo spento per un servizio su questo PC.");
        _chkUseProxy.CheckedChanged += (s, e) => OnProfileFieldChanged();
    }

    /// <summary>Contesto: Ollama context, output and budget, temperature, keep alive, reasoning.</summary>
    private void BuildContextRows(TableLayoutPanel ctx)
    {
        _cmbPreset = AddRow(ctx, "Preset contesto:", Combo(210),
            "Solo modelli locali: combinazioni pronte di quanto testo il modello legge e scrive. Più testo richiede più memoria sul PC. Con i servizi cloud non serve: decide il fornitore.");
        _cmbPreset.SelectedIndexChanged += (s, e) => OnPresetChanged();

        _numNumCtx = AddRow(ctx, "NumCtx (Ollama):", Numeric(2048, 262144, 16384, 1024),
            "Solo Ollama: quanto testo (in token, circa 3,5 caratteri l'uno) il modello tiene in memoria tra domanda e risposta. Cambiarlo spesso rallenta, perché il modello si ricarica.");
        _numNumCtx.ValueChanged += (s, e) => OnContextValueChanged(recomputeBudget: true);

        _numMaxTokens = AddRow(ctx, "Max output token:", Numeric(256, 128000, 2048, 256),
            "Solo modelli locali: lunghezza massima di una risposta, in token (circa 3,5 caratteri l'uno).");
        _numMaxTokens.ValueChanged += (s, e) => OnContextValueChanged(recomputeBudget: true);

        _numBudget = Numeric(1000, 1000000, 12698, 500);
        _numBudget.ValueChanged += (s, e) => OnContextValueChanged(recomputeBudget: false);
        Tip(_numBudget, "Solo modelli locali: quanto testo di email e allegati può entrare in una richiesta (in token, circa 3,5 caratteri l'uno). Un'email o un allegato più lungo non viene letto a pezzi: il riquadro avvisa.");
        AddRow(ctx, "Budget token (prompt):", _numBudget, "Solo modelli locali: quanto testo di email e allegati può entrare in una richiesta.");
        _lblBudgetHint = AddRow(ctx, "", new SelectableLabel { AutoSize = true, Margin = new Padding(3, 0, 3, 6) },
            "Valore consigliato per le impostazioni scelte sopra.");
        Theme.SetTone(_lblBudgetHint, Tone.Muted);
        DescriptionHelp(_lblBudgetHint, "Budget del contesto");

        _numTemp = AddRow(ctx, "Temperatura:", new ThemedNumeric { Width = 100, Minimum = 0M, Maximum = 2M, DecimalPlaces = 2, Increment = 0.05M, Value = 0.3M, Anchor = AnchorStyles.Left },
            "Quanto il modello varia le risposte: 0 = sempre uguali e prudenti, valori alti = più creative ma meno precise. 0,3 va bene per sintesi e bozze.");
        _numTemp.ValueChanged += (s, e) => OnProfileFieldChanged();

        _txtKeepAlive = AddRow(ctx, "Mantieni in memoria (Ollama):", new ThemedTextBox { Width = 100, Anchor = AnchorStyles.Left },
            "Solo Ollama: quanto il modello resta caricato dopo l'ultima richiesta (per esempio 30m, 1h, 90s; 0 = libera subito la memoria). Più a lungo = risposte successive più rapide.");
        _txtKeepAlive.TextChanged += (s, e) => OnProfileFieldChanged();

        _chkDisableThinking = AddWide(ctx, new ThemedCheckBox { Text = "Disattiva ragionamento del modello", AutoSize = true },
            "Per i modelli che «ragionano» prima di rispondere (Ollama, Qwen): spunta per risposte più rapide e meno token pagati; il ragionamento non verrebbe comunque mostrato.");
        _chkDisableThinking.CheckedChanged += (s, e) => OnProfileFieldChanged();
    }

    /// <summary>Autorizzazioni: cloud service, attachment text and Ricerca web.</summary>
    private void BuildPermissionRows(TableLayoutPanel net)
    {
        _chkExternal = AddWide(net, new ThemedCheckBox { Text = "Servizio esterno: invii soggetti alla protezione dei dati", AutoSize = true },
            "Il servizio è fuori da questo PC. Solo fatti strutturati protetti possono essere inviati, dopo l'autorizzazione in Privacy e dati. Testo libero non verificabile bloccato. Sempre attivo per servizi cloud e indirizzi esterni.");
        _chkExternal.CheckedChanged += (s, e) =>
        {
            if (!_isLoading && _chkExternal.Enabled && _lstProfiles.SelectedItem is ProfileItem item)
                item.Profile.External = _chkExternal.Checked;
            OnProfileFieldChanged(); ApplyCapabilities();
        };

        _cmbExposure = AddRow(net, "Esposizione dati:", new ThemedComboBox { Width = 200, Anchor = AnchorStyles.Left },
            "0 blocca ogni invio. Minimo e Standard consentono solo fatti strutturati protetti; Esteso permette anche quelli degli allegati con permesso distinto. Testo libero, importi e date originali non escono. Si applica con Salva.");
        foreach (var level in ExposurePolicy.All) _cmbExposure.Items.Add(new ExposureItem(level));
        _cmbExposure.SelectedIndexChanged += (s, e) => { ShowExposure(); OnProfileFieldChanged(); ApplyCapabilities(); UpdateWebActionsEnabled(); };
        _lblExposure = AddRow(net, "", new SelectableLabel { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 0, 3, 6) },
            "Cosa esce dal PC al livello scelto.");
        Theme.SetTone(_lblExposure, Tone.Muted);
        DescriptionHelp(_lblExposure, "Protezione dei dati");
        net.Layout += (_, _) =>
        {
            // Wrapped at the visible width of the field column: at 440 px it went past the card at 100% (A159).
            Control cell = _lblExposure.Parent is FlowLayoutPanel inline && inline.Parent == net ? inline : _lblExposure;
            int width = Math.Max(Ui.Px(this, 160), net.ClientSize.Width - cell.Left - cell.Margin.Right - cell.Padding.Horizontal - _lblExposure.Margin.Horizontal);
            if (_lblExposure.MaximumSize.Width != width) _lblExposure.MaximumSize = new Size(width, 0);
        };

        _chkAllowAttachments = AddWide(net, new ThemedCheckBox { Text = "Fatti protetti dagli allegati", AutoSize = true },
            "Con Esteso autorizza solo fatti strutturati protetti dagli allegati. Testo libero e nomi dei file restano sul PC. Si applica con Salva.");
        _chkAllowAttachments.CheckedChanged += (s, e) => OnProfileFieldChanged();
        _chkPseudonymizeAmounts = AddWide(net, new ThemedCheckBox { Text = "Sostituisci gli importi con segnaposto", AutoSize = true },
            "Negli invii esterni sostituisce gli importi riconosciuti; li ripristina nella risposta su questo PC. Si applica con Salva.");
        _chkPseudonymizeDates = AddWide(net, new ThemedCheckBox { Text = "Sostituisci le date con segnaposto", AutoSize = true },
            "Negli invii esterni sostituisce le date riconosciute; le ripristina nella risposta su questo PC. Si applica con Salva.");
        _chkPseudonymizeAmounts.Visible = _chkPseudonymizeDates.Visible = false;
        _chkPseudonymizeAmounts.CheckedChanged += (s, e) => OnProfileFieldChanged();
        _chkPseudonymizeDates.CheckedChanged += (s, e) => OnProfileFieldChanged();

        _lblWebSupport = AddRow(net, "Ricerca web:", new SelectableLabel { AutoSize = true, Text = WebNotChecked, Margin = new Padding(3, 5, 3, 6) },
            "Se il modello scelto sa cercare sul web, secondo il servizio o secondo la prova fatta con il pulsante.");
        _btnWebRetry = AddRow(net, "", SecondaryButton("Verifica Ricerca web"),
            "Fa una vera ricerca di prova (senza dati di Outlook) per capire se il modello sa cercare sul web. Il fornitore può addebitarla.");
        _btnWebRetry.Click += async (s, e) => await CheckWebSupportAsync();
        _chkWebSearch = AddRow(net, "Abilitazione:", new ThemedCheckBox { Text = "Ricerca web attiva", AutoSize = true },
            "Permette alle azioni spuntate sotto di cercare sul web, senza chiedere conferma. Le ricerche possono contenere parole prese dalle email; con Ollama vanno a ollama.com. Richiede l'informativa accettata.");
        _chkWebSearch.CheckedChanged += (s, e) => { OnProfileFieldChanged(); UpdateWebActionsEnabled(); };
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
        _txtWebSearchKey = AddRow(net, "Chiave Ollama Web:", new ThemedTextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, UseSystemPasswordChar = true },
            "Solo Ollama: chiave del servizio di ricerca web di ollama.com (diversa da quella del modello). Salvata cifrata su questo PC.");
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
        _lblExposure.Text = _chkExternal.Checked ? ExposurePolicy.Describe(SelectedExposure) : "Profilo locale: nessun dato lascia il PC.";

    /// <summary>Verifica connessione with its result, always visible under the sections.</summary>
    private TableLayoutPanel BuildProfileTestRow()
    {
        _btnTestProfile = SecondaryButton("Verifica connessione");
        _btnTestProfile.MinimumSize = new Size(0, 30);
        _btnTestProfile.Click += BtnTestProfile_Click;
        Tip(_btnTestProfile, "Prova che il servizio risponda, che la chiave sia accettata e che il modello esista. Invia solo una richiesta di prova, senza dati di Outlook.");
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
