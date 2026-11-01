using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>
/// The notice on data leaving Outlook, with the responsible person's details, the company's statements and one
/// block of declarations per server: «Accetta» stays off until everything is valid and the reading is confirmed.
/// The caller records the acceptance (DataEgress.Accept) and saves.
/// </summary>
internal sealed class DataEgressConsentForm : Form
{
    private const int TextWidth = 560;
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100 };
    private readonly TextBox _company = Field();
    private readonly TextBox _firstName = Field();
    private readonly TextBox _lastName = Field();
    private readonly TextBox _role = Field();
    private readonly TextBox _email = Field();
    private readonly DateTimePicker _date = new() { Format = DateTimePickerFormat.Short, Width = 140, MaxDate = DateTime.Today, Value = DateTime.Today, Anchor = AnchorStyles.Left };
    private readonly TextBox _privacyContact = Field();
    private readonly ComboBox _legalBasis = Choice(DataEgress.LegalBases);
    private readonly TextBox _dpia = Field();
    private readonly CheckBox _workers = new ThemedCheckBox { Text = "I lavoratori sono stati informati dell'uso dell'IA (L. 132/2025 art. 11)", AutoSize = true };
    private readonly CheckBox _literacy = new ThemedCheckBox { Text = "Chi usa OnlyWorkspace ha ricevuto indicazioni sull'uso dell'IA (AI Act art. 4)", AutoSize = true };
    private readonly List<ProviderFields> _providers = new();
    private readonly CheckBox _read = new ThemedCheckBox { Text = "Ho letto l'informativa, le dichiarazioni sono veritiere e autorizzo l'uscita dei dati descritta", AutoSize = true };
    private readonly FlatButton _accept = new() { Text = "Accetta e salva", Kind = ButtonKind.Primary, MinimumSize = new Size(120, 30), Margin = new Padding(8, 0, 0, 0) };
    private readonly Label _problem = new SelectableLabel { AutoSize = true, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(0, 6, 0, 0) };

    /// <summary>One server's declarations.</summary>
    private sealed class ProviderFields
    {
        public string Host = string.Empty;
        public ExposureLevel Required;
        public readonly TextBox Provider = Field();
        public readonly ComboBox Level = new ThemedComboBox { Width = 200, Anchor = AnchorStyles.Left };
        public readonly CheckBox Agreement = new ThemedCheckBox { Text = "Nomina a responsabile del trattamento firmata (GDPR art. 28)", AutoSize = true };
        public readonly TextBox Region = Field();
        public readonly ComboBox Transfer = Choice(DataEgress.TransferBases);
        public readonly CheckBox NoTraining = new ThemedCheckBox { Text = "Il contratto esclude l'uso dei dati per l'addestramento", AutoSize = true };
        public readonly TextBox Retention = Field();

        public DataEgressProvider Value => new()
        {
            Host = Host, Provider = Provider.Text, Level = Level.SelectedItem is LevelItem item ? item.Level : Required,
            ProcessingAgreement = Agreement.Checked, Region = Region.Text, TransferBasis = Transfer.SelectedItem as string ?? string.Empty,
            NoTraining = NoTraining.Checked, Retention = Retention.Text
        };
    }

    private sealed class LevelItem
    {
        public LevelItem(ExposureLevel level) => Level = level;
        public ExposureLevel Level { get; }
        public override string ToString() => ExposurePolicy.Label(Level);
    }

    public DataEgressConsentForm(IReadOnlyList<DataEgressProvider> servers, DataEgressSigner? previous,
        IReadOnlyList<LlmProfileConfig>? profiles = null)
    {
        Text = "Uscita dei dati da Outlook";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F);
        BackColor = Theme.Colors.Window;
        ForeColor = Theme.Colors.Text;

        // GrowAndShrink: with the default GrowOnly the grid kept its 150% width after moving to a 100% screen (A159).
        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(16, 14, 16, 8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Wide(Control control)
        {
            int row = layout.RowCount++;
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }
        void Row(string label, Control field, string tip)
        {
            int row = layout.RowCount++;
            var caption = new SelectableLabel { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 3) };
            layout.Controls.Add(caption, 0, row);
            layout.Controls.Add(field is TextBox box ? new InputFrame(box) { Margin = new Padding(0, 3, 0, 3) } : field, 1, row);
            _tips.SetToolTip(caption, tip);
            _tips.SetToolTip(field, tip);
        }
        void Check(CheckBox box, string tip)
        {
            box.Margin = new Padding(0, 4, 0, 2);
            _tips.SetToolTip(box, tip);
            Wide(box);
        }
        void Heading(string text, string tip)
        {
            var label = Ui.SetFont(new SelectableLabel { Text = text, AutoSize = true, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(0, 12, 0, 4) }, 9.5F, family: "Segoe UI Semibold");
            _tips.SetToolTip(label, tip);
            Wide(label);
        }

        Heading("Autorizzazioni per profilo", "Un'unica accettazione registra separatamente ogni profilo elencato, con destinazione, permessi e chiavi attuali.");
        foreach (var profile in profiles ?? Array.Empty<LlmProfileConfig>())
            Wide(new SelectableLabel { Text = DataEgress.ProfileSummary(profile), AutoSize = true, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(0, 4, 0, 8) });
        Wide(new SelectableLabel
        {
            Text = "Vale fino a un cambio di destinazione, livello, permessi o chiavi. Nessuna conferma per le singole azioni. Puoi annullare e salvare il profilo con gli invii bloccati.",
            AutoSize = true, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(0, 0, 0, 8)
        });
        Wide(Ui.SetFont(new SelectableLabel { Text = DataEgressNotice.Title, AutoSize = true, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(0, 0, 0, 8) }, 10.5F, family: "Segoe UI Semibold"));
        var notice = new ThemedTextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = TextWidth, Height = 200,
            Text = DataEgressNotice.Text, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 4)
        };
        _tips.SetToolTip(notice, $"Testo dell'informativa, versione {DataEgressNotice.Version}. Se il testo cambia in un aggiornamento, va accettato di nuovo.");
        Wide(notice);

        Heading("Responsabile che autorizza", "Chi autorizza l'uscita dei dati per conto dell'azienda.");
        Row("Azienda:", _company, "Ragione sociale dell'azienda titolare del trattamento.");
        Row("Nome:", _firstName, "Nome del responsabile che autorizza.");
        Row("Cognome:", _lastName, "Cognome del responsabile che autorizza.");
        Row("Ruolo:", _role, "Ruolo in azienda del responsabile (per esempio titolare, DPO, responsabile IT).");
        Row("Email:", _email, "Indirizzo email del responsabile, per ritrovare chi ha autorizzato.");
        Row("Data:", _date, "Data dell'autorizzazione indicata dal responsabile; non può essere futura.");

        Heading("Dichiarazioni dell'azienda", "Gli adempimenti che il titolare del trattamento dichiara di aver svolto.");
        Row("Referente privacy:", _privacyContact, "DPO o referente privacy dell'azienda (facoltativo).");
        Row("Base giuridica:", _legalBasis, "Base giuridica del trattamento dei dati delle email con l'IA (GDPR art. 6).");
        Row("Valutazione d'impatto:", _dpia, "Riferimento della valutazione d'impatto (GDPR art. 35), o il motivo per cui non è necessaria.");
        Check(_workers, "L'azienda ha informato i lavoratori dell'uso dell'intelligenza artificiale, come chiede la legge 132/2025, art. 11.");
        Check(_literacy, "L'azienda ha adottato misure perché chi usa OnlyWorkspace conosca possibilità e limiti dell'IA (Regolamento UE 2024/1689, art. 4).");

        if (servers.Count == 0)
        {
            Heading("Server che riceveranno dati", "Ricavati dai profili cloud e dalla ricerca web configurati.");
            var none = new SelectableLabel
            {
                Text = "Nessuno con la configurazione attuale: l'accettazione varrà per i server presenti al momento.",
                AutoSize = true, MaximumSize = new Size(TextWidth, 0)
            };
            _tips.SetToolTip(none, "Un server aggiunto in seguito richiede una nuova accettazione.");
            Wide(none);
        }
        foreach (var server in servers)
        {
            var fields = new ProviderFields { Host = server.Host, Required = server.Level };
            Heading($"Server {server.Host}", "Dichiarazioni sul contratto con il fornitore che gestisce questo server; vanno verificate sul contratto.");
            Row("Fornitore:", fields.Provider, "Società che gestisce il server e riceve i dati (responsabile del trattamento).");
            foreach (var level in ExposurePolicy.All.Where(l => l >= server.Level && l != ExposureLevel.LocalOnly)) fields.Level.Items.Add(new LevelItem(level));
            fields.Level.SelectedIndex = 0;
            Row("Livello massimo:", fields.Level, "Livello di esposizione più alto autorizzato per questo server. Il minimo è quello chiesto dai profili che lo usano.");
            Check(fields.Agreement, "Senza la nomina a responsabile del trattamento (GDPR art. 28) il server non può ricevere dati.");
            Row("Dove sono trattati:", fields.Region, "Paese o regione dei server indicati dal contratto (per esempio «UE, Francoforte»).");
            Row("Trasferimento:", fields.Transfer, "Base dell'eventuale trasferimento fuori dallo Spazio economico europeo (GDPR capo V).");
            Check(fields.NoTraining, "Il contratto o le condizioni del piano escludono l'uso dei dati per addestrare i modelli: requisito per autorizzare.");
            Row("Conservazione:", fields.Retention, "Per quanto tempo il fornitore conserva richieste e risposte, secondo il contratto (per esempio «nessuna» o «30 giorni»).");
            fields.Provider.Text = server.Provider;
            fields.Agreement.Checked = server.ProcessingAgreement;
            fields.Region.Text = server.Region;
            fields.Transfer.SelectedItem = DataEgress.TransferBases.Contains(server.TransferBasis) ? server.TransferBasis : null;
            fields.NoTraining.Checked = server.NoTraining;
            fields.Retention.Text = server.Retention;
            _providers.Add(fields);
        }

        _read.Margin = new Padding(0, 12, 0, 2);
        _tips.SetToolTip(_read, "Il responsabile conferma di aver letto l'informativa e autorizza, per conto dell'azienda, l'uscita dei dati verso i server elencati. Senza questa conferma l'autorizzazione non si registra.");
        Wide(_read);
        Theme.SetTone(_problem, Tone.Warning);
        _tips.SetToolTip(_problem, "Cosa manca per poter accettare.");
        Wide(_problem);

        var scroll = new ScrollPanel { Dock = DockStyle.Fill };
        scroll.Controls.Add(layout);
        var cancel = new FlatButton { Text = "Annulla", DialogResult = DialogResult.Cancel, MinimumSize = new Size(88, 30), Margin = Padding.Empty };
        _tips.SetToolTip(cancel, "Chiude senza registrare nulla: i profili cloud e la ricerca web esterna restano bloccati.");
        _tips.SetToolTip(_accept, "Registra l'autorizzazione in config.json, con data, ora, utente Windows e testo accettato, e salva la configurazione (richiede i diritti di amministratore).");
        _accept.Click += (s, e) => { if (Problem() == null) DialogResult = DialogResult.OK; };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(16, 8, 16, 12)
        };
        actions.Controls.AddRange(new Control[] { _accept, cancel });
        // Laid out right to left: Tab follows the reading order instead.
        cancel.TabIndex = 0;
        _accept.TabIndex = 1;
        Controls.Add(scroll);
        Controls.Add(actions);
        CancelButton = cancel;

        if (previous != null)
        {
            _company.Text = previous.Company;
            _firstName.Text = previous.FirstName;
            _lastName.Text = previous.LastName;
            _role.Text = previous.Role;
            _email.Text = previous.Email;
            _privacyContact.Text = previous.PrivacyContact;
            _legalBasis.SelectedItem = DataEgress.LegalBases.Contains(previous.LegalBasis) ? previous.LegalBasis : null;
            _dpia.Text = previous.Dpia;
            _workers.Checked = previous.WorkersInformed;
            _literacy.Checked = previous.AiLiteracy;
        }
        foreach (var box in new[] { _company, _firstName, _lastName, _role, _email, _dpia }) box.TextChanged += (s, e) => UpdateAccept();
        foreach (var fields in _providers)
        {
            foreach (var box in new[] { fields.Provider, fields.Region, fields.Retention }) box.TextChanged += (s, e) => UpdateAccept();
            foreach (var combo in new[] { fields.Level, fields.Transfer }) combo.SelectedIndexChanged += (s, e) => UpdateAccept();
            foreach (var check in new[] { fields.Agreement, fields.NoTraining }) check.CheckedChanged += (s, e) => UpdateAccept();
        }
        _legalBasis.SelectedIndexChanged += (s, e) => UpdateAccept();
        foreach (var check in new[] { _workers, _literacy, _read }) check.CheckedChanged += (s, e) => UpdateAccept();
        _date.ValueChanged += (s, e) => UpdateAccept();
        UpdateAccept();

        Ui.ScaleFromDesignDpi(this);
        DisabledControlTips.Attach(this, _tips);
        Load += (s, e) =>
        {
            // As tall as the content, within the screen; the declarations scroll when they do not fit.
            var area = Screen.FromControl(Owner ?? this).WorkingArea;
            var preferred = layout.GetPreferredSize(Size.Empty);
            int chrome = Height - ClientSize.Height;
            int width = preferred.Width + SystemInformation.VerticalScrollBarWidth + (Width - ClientSize.Width);
            Size = new Size(Math.Min(width, area.Width), Math.Min(preferred.Height + actions.Height + chrome, area.Height - Ui.Px(this, 40)));
            MinimumSize = new Size(Math.Min(width, area.Width), Ui.Px(this, 360));
            // Centered on the owner as it was before the resize, then moved inside the screen: a dialog that grew
            // below the owner hid its buttons under the taskbar (A159, 1080p at 100%).
            Location = Placement(area, Owner?.Bounds ?? area, Size);
        };
        Shown += (s, e) => { Theme.Apply(this); Theme.ApplyWindowChrome(this); _company.Focus(); FitWidth(); };
        // Measured at the primary screen's scale and shown on a 100% screen, the text came out a few pixels wider than
        // the rescaled window and a horizontal scrollbar appeared (A159): widen by what is missing.
        DpiChanged += (s, e) => BeginInvoke(new Action(FitWidth));
        void FitWidth()
        {
            if (IsDisposed) return;
            int missing = layout.Width + layout.Margin.Horizontal - scroll.ClientSize.Width;
            if (missing <= 0) return;
            var area = Screen.FromControl(this).WorkingArea;
            Width = Math.Min(Width + missing, area.Width);
            Location = Placement(area, Bounds, Size);
        }
    }

    /// <summary>Top-left corner of a window of <paramref name="size"/> centered on <paramref name="owner"/> and kept inside <paramref name="area"/>.</summary>
    internal static Point Placement(Rectangle area, Rectangle owner, Size size)
    {
        int x = owner.X + (owner.Width - size.Width) / 2;
        int y = owner.Y + (owner.Height - size.Height) / 2;
        return new Point(Math.Max(area.X, Math.Min(x, area.Right - size.Width)), Math.Max(area.Y, Math.Min(y, area.Bottom - size.Height)));
    }

    private static TextBox Field() => new ThemedTextBox { Width = 320, MaxLength = 200 };

    private static ComboBox Choice(IEnumerable<string> items)
    {
        var combo = new ThemedComboBox { Width = 320, Anchor = AnchorStyles.Left };
        foreach (var item in items) combo.Items.Add(item);
        return combo;
    }

    public DataEgressSigner Signer => new()
    {
        Company = _company.Text, FirstName = _firstName.Text, LastName = _lastName.Text, Role = _role.Text, Email = _email.Text,
        Date = _date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), PrivacyContact = _privacyContact.Text,
        LegalBasis = _legalBasis.SelectedItem as string ?? string.Empty, Dpia = _dpia.Text,
        WorkersInformed = _workers.Checked, AiLiteracy = _literacy.Checked
    };

    public IReadOnlyList<DataEgressProvider> Providers => _providers.Select(p => p.Value).ToList();

    private string? Problem() =>
        DataEgress.Problem(Signer, DateTime.Today) ?? Providers.Select(DataEgress.Problem).FirstOrDefault(p => p != null) ??
        (_read.Checked ? null : "Confermare la lettura dell'informativa.");

    private void UpdateAccept()
    {
        var problem = Problem();
        _accept.Enabled = problem == null;
        _problem.Text = problem ?? string.Empty;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
