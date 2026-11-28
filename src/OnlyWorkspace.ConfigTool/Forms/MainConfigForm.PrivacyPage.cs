using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Infrastructure.Storage;

namespace OnlyWorkspace.ConfigTool.Forms;

public sealed partial class MainConfigForm
{
    /// <summary>Owns the controls and refresh operations for the Privacy and data page.</summary>
    private sealed class PrivacyPage
    {
        private readonly MainConfigForm _owner;
        private readonly SelectableLabel _lblDestinations;
        private readonly SelectableLabel _lblEgress;
        private readonly Button _btnEgressAccept;
        private readonly Button _btnEgressRevoke;
        private readonly Button _btnEgressExport;
        private readonly Button _btnRefreshStorage;
        private readonly TableLayoutPanel _storageGrid;
        private readonly SelectableLabel _lblStorageTotal;
        private CancellationTokenSource? _storageMeasure;
        // Measure at each visit, but rebuild the rows only when the values change.
        private string? _storageShown;

        public Control View { get; }

        public PrivacyPage(MainConfigForm owner)
        {
            _owner = owner;
            View = ScrollPage(out var stack);
            var egress = _owner.AddGroup(stack, "Uscita dei dati da Outlook");
            _lblEgress = _owner.AddWide(egress, new SelectableLabel { AutoSize = true, MaximumSize = new Size(Metrics.TextWidthPage, 0), Margin = new Padding(3, 3, 3, 6) },
                "Se e da chi è stata accettata l'informativa che autorizza l'invio di dati di Outlook a servizi esterni. Senza, i profili cloud e la ricerca web esterna sono bloccati per tutti gli utenti di questo PC.");
            _btnEgressAccept = SecondaryButton("Leggi e autorizza…");
            _btnEgressAccept.Click += async (s, e) => await _owner.EgressFlow.AcceptAndSaveAsync();
            _btnEgressRevoke = SecondaryButton("Revoca");
            _btnEgressRevoke.Click += async (s, e) => await _owner.EgressFlow.RevokeAsync();
            _btnEgressExport = SecondaryButton("Esporta PDF…");
            _btnEgressExport.Click += async (s, e) => await ExportDataEgressAsync();
            _owner.AddRow(egress, "", Inline(_btnEgressAccept, _btnEgressRevoke, _btnEgressExport), "Accettazione, revoca ed esportazione dell'informativa sull'uscita dei dati.");
            _owner.Tip(_btnEgressExport, "Salva un PDF A4 con postazione, profili IA, livelli e server autorizzati, dichiarazioni, responsabile che ha abilitato l'uscita dei dati, storico e testo accettato. Funziona anche in sola lettura.");
            _owner.Tip(_btnEgressAccept, "Apre riepilogo dei profili, informativa e dichiarazioni del responsabile. Accetta e salva lega l'autorizzazione a destinazioni, livello, permessi e chiavi; un cambiamento richiede rinnovo. Il normale Salva mantiene bloccati gli invii senza consenso.");
            _owner.Tip(_btnEgressRevoke, "Ritira l'autorizzazione e salva: da quel momento nessun dato di Outlook esce verso servizi esterni. La revoca resta nello storico.");

            var files = _owner.AddGroup(stack, "File sul PC e spazio occupato");
            _storageGrid = _owner.AddWide(files, new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = Padding.Empty },
                "File creati da OnlyWorkspace per il tuo utente Windows e, in fondo, quelli comuni a tutti gli utenti. Passa sul nome di una voce per sapere cosa contiene.");
            foreach (var width in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent }) _storageGrid.ColumnStyles.Add(new ColumnStyle(width, 100F));
            _lblStorageTotal = _owner.AddWide(files, new SelectableLabel { AutoSize = true, MaximumSize = new Size(Metrics.TextWidthPage, 0), Margin = new Padding(3, 8, 3, 3) },
                "Somma delle voci sopra e spazio ancora libero sul disco dell'archivio. Sotto 5 GB liberi l'archivio avvisa, sotto 1 GB smette di acquisire email.");
            _btnRefreshStorage = _owner.AddRow(files, "", SecondaryButton("Aggiorna"),
                "Ricalcola lo spazio occupato. Legge solo le dimensioni dei file: non apre l'archivio e non cancella nulla.");
            _btnRefreshStorage.Click += async (s, e) => await RefreshStorageAsync();
            _owner.AddWide(files, new SelectableLabel
            {
                Text = "File su richiesta, non contati: CSV estratti (Download), ZIP di diagnostica o di backup (Desktop o cartella scelta).",
                AutoSize = true,
                MaximumSize = new Size(Metrics.TextWidthPage, 0),
                Margin = new Padding(3, 6, 3, 3)
            }, "File che crei tu con un comando: restano dove li hai salvati finché non li elimini.");

            var registry = _owner.AddGroup(stack, "Registro di Windows e sistema");
            _owner.AddWide(registry, new SelectableLabel
            {
                Text = "• Add-in: HKLM e HKCU Software\\Microsoft\\Office\\Outlook\\Addins\\OnlyWorkspace.Addin (caricamento in Outlook).\r\n" +
                       "• Avvio con Windows: HKCU Software\\Microsoft\\Windows\\CurrentVersion\\Run, voce OnlyWorkspaceTray.\r\n" +
                       "• Ripara add-in: HKCU Software\\Microsoft\\Office\\16.0\\Outlook\\Resiliency.\r\n" +
                       "• Setup: certificato OnlyApps negli archivi Root e TrustedPublisher del computer; facoltativamente le policy che nascondono «Prova il nuovo Outlook».",
                AutoSize = true,
                MaximumSize = new Size(Metrics.TextWidthPage, 0)
            }, "Impostazioni di Windows scritte da installazione, procedura guidata e «Ripara add-in».");

            var network = _owner.AddGroup(stack, "Server che ricevono dati");
            _lblDestinations = _owner.AddWide(network, new SelectableLabel { AutoSize = true, MaximumSize = new Size(Metrics.TextWidthPage, 0) },
                "A chi arrivano i dati con la configurazione attuale, ricavato dai profili; si aggiorna aprendo questa pagina.");
        }

        public void Refresh()
        {
            RefreshDestinations();
            RefreshEgress();
            _ = RefreshStorageAsync();
        }

        public bool AllowsReadOnlyAction(Control control) => control == _btnEgressExport || control == _btnRefreshStorage;

        private async Task ExportDataEgressAsync()
        {
            if (_owner.ConfirmSavedFilesOnly("Il PDF"))
                await DataEgressPdfExportFlow.RunAsync(_owner, _btnEgressExport, _owner._configDir, WindowsUser, _owner.LogActivity,
                    (message, tone) => _owner.SetStatus(message, tone));
        }

        public void RefreshEgress()
        {
            var status = EgressStatus.Build(_owner._config, _owner._vault.GetSecret);
            _lblEgress.Text = status.Text;
            Theme.SetTone(_lblEgress, status.Tone);
            if (_owner._isLocked) _owner._lockedControls[_btnEgressAccept] = status.CanAccept;
            else _btnEgressAccept.Enabled = status.CanAccept;
            if (_owner._isLocked) _owner._lockedControls[_btnEgressRevoke] = status.CanRevoke;
            else _btnEgressRevoke.Enabled = status.CanRevoke;
        }

        /// <summary>Measures the generated files away from the UI thread; a newer request or closing the window drops an older one.</summary>
        private async Task RefreshStorageAsync()
        {
            _storageMeasure?.Cancel();
            var measure = CancellationTokenSource.CreateLinkedTokenSource(_owner._archiveLifetime.Token);
            _storageMeasure = measure;
            _lblStorageTotal.Text = "Calcolo dello spazio occupato…";
            // The rows are controls: built on the UI thread even when the await resumes elsewhere.
            void OnUi(Action action)
            {
                if (measure.IsCancellationRequested || _owner.IsDisposed) return;
                if (_owner.InvokeRequired) _owner.BeginInvoke(action); else action();
            }
            try
            {
                var report = await Task.Run(() => AppStorageUsage.Measure(measure.Token, _owner._configDir), measure.Token);
                OnUi(() => FillStorage(report));
            }
            catch (Exception ex)
            {
                // Called from async void handlers: nothing may escape.
                OnlyWorkspaceLog.Logger.Warning(ex, "Storage measurement failed");
                try { OnUi(() => _lblStorageTotal.Text = "Spazio non calcolato: " + ErrorText.For(ex)); }
                catch (InvalidOperationException) { }
            }
        }

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
                    var label = new SelectableLabel { Text = text, AutoSize = true, MaximumSize = new Size(Ui.Px(_storageGrid, left == 0 ? Metrics.StorageLabelNarrow : Metrics.StorageLabelWide), 0), Margin = new Padding(Ui.Px(_storageGrid, left), Ui.Px(_storageGrid, 4), Ui.Px(_storageGrid, 3), Ui.Px(_storageGrid, 4)) };
                    return bold ? Ui.SetFont(label, 9F, FontStyle.Bold) : label;
                }
                Row(Cell("Voce", 0, bold: true), Cell("Spazio", bold: true), Cell("Percorso", bold: true));
                foreach (var item in report.Items)
                {
                    string size = StorageText.Size(item);
                    var name = Cell(item.Name, 0);
                    var sizeCell = Cell(size);
                    var path = Cell(item.Path);
                    _owner.Tip(name, item.Note);
                    _owner.Tip(sizeCell, StorageText.SizeTip(item));
                    _owner.Tip(path, "Cartella o file su questo PC. Si può selezionare e copiare.");
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

        private void RefreshDestinations()
        {
            var lines = _owner._config.Profiles.Select(p =>
            {
                var host = Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u) ? u.Host : p.BaseUrl;
                var web = p.WebSearchEnabled ? "; ricerca web: " + string.Join(", ", WebSearchService.Destinations(p)) : string.Empty;
                var kind = p.IsExternal ? "cloud" + (p.SendsAttachmentText ? ", fatti allegati consentiti" : ", fatti allegati esclusi") : "locale";
                return $"• {(string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName)}: {host} ({kind}){web}.";
            }).ToList();
            lines.Add("• Le email vanno a un modello solo per un'azione avviata dall'utente; la posta viene letta solo dalle cassette aperte in Outlook.");
            _lblDestinations.Text = string.Join("\r\n", lines);
        }
    }
}
