using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>
/// A technical authorization with automatic destinations and no manual declarations. The text scrolls; the confirmations stay
/// below it, outside the scrolling area, and unlock once the text has been scrolled to its end (a proof of reading).
/// </summary>
internal sealed class DataEgressConsentForm : Form
{
    internal const string ScrollHint = "Scorri il testo fino in fondo per abilitare le conferme.";
    internal const string TickHint = "Per abilitare «Autorizza e salva» spunta «Ho letto…».";
    internal const string TickBothHint = "Per abilitare «Autorizza e salva» spunta «Ho letto…» e la conferma dell'area.";

    private readonly CheckBox _read;
    private readonly CheckBox? _region;
    private readonly FlatButton _accept;
    private readonly Label _hint;
    private readonly ScrollPanel _scroll;
    private readonly TableLayoutPanel _text;
    private readonly Timer _readWatch = new() { Interval = 200 };
    private readonly Action _unlock;
    internal IReadOnlyList<string> ConfirmedUnspecifiedRegionProfiles { get; private set; } = Array.Empty<string>();
    /// <summary>The text was scrolled to its end, or fits entirely.</summary>
    internal bool ReadToEnd { get; private set; }
    private bool _closeByKeyboardOrX;
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };

    public DataEgressConsentForm(IReadOnlyList<LlmProfileConfig>? profiles = null)
    {
        Text = "Privacy e dati";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        Font = new Font("Segoe UI", 9F);
        // Owner-drawn controls paint with the theme palette: the window must use it too, or the checkbox text vanishes.
        BackColor = Theme.Colors.Window;
        ForeColor = Theme.Colors.Text;
        Size = new Size(650, 560);
        MinimumSize = new Size(380, 360);
        var list = profiles ?? Array.Empty<LlmProfileConfig>();
        var layout = _text = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(Metrics.DialogPadding) };
        var scroll = _scroll = new ScrollPanel { Dock = DockStyle.Fill, Focusable = true, AccessibleName = DataEgressNotice.Title };
        scroll.Controls.Add(layout);
        void TextRow(string text, bool heading = false)
        {
            var row = new SelectableLabel { Text = text, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, Metrics.Gap + 4) };
            if (heading) Ui.SetFont(row, 10.5F, FontStyle.Bold);
            layout.Controls.Add(row);
        }
        TextRow(DataEgressNotice.Title, heading: true);
        TextRow(DataEgressNotice.Text);
        foreach (var service in DataEgress.ByService(list)) TextRow(DataEgress.ServiceSummary(service.ToList()));

        // Confirmations: outside the scrolling text, always in view.
        var confirmations = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(Metrics.DialogPadding, Metrics.Gap, Metrics.DialogPadding, 0)
        };
        var read = _read = new ThemedCheckBox
        {
            Text = "Ho letto e autorizzo gli invii protetti per i servizi indicati",
            AutoSize = true,
            Enabled = false,
            MaximumSize = new Size(560, 0),
            AccessibleDescription = ScrollHint
        };
        confirmations.Controls.Add(read);
        // A cloud profile without a declared area needs its own explicit confirmation.
        var unspecified = list.Where(DataEgress.RequiresRegionConfirmation).ToList();
        var unspecifiedIds = unspecified.Select(p => p.Id).ToArray();
        if (unspecified.Count > 0)
        {
            _region = new ThemedCheckBox
            {
                Text = "Confermo di autorizzare anche l'area non specificata: " + string.Join(", ", unspecified.Select(DataEgress.Name)),
                AutoSize = true,
                Enabled = false,
                MaximumSize = new Size(560, 0),
                AccessibleDescription = ScrollHint
            };
            confirmations.Controls.Add(_region);
        }
        var region = _region;
        var accept = _accept = new FlatButton { Text = "Autorizza e salva", Kind = ButtonKind.Primary, Enabled = false };
        var cancel = new FlatButton { Text = "Annulla", DialogResult = DialogResult.Cancel };
        Metrics.DialogButton(accept);
        Metrics.DialogButton(cancel);
        _tips.SetToolTip(read, "Conferma l'autorizzazione tecnica. Non dichiara adempimenti aziendali o contratti.");
        _tips.SetToolTip(accept, "Salva l'autorizzazione per servizio, destinazione, permessi e chiavi attuali. Richiede diritti di amministratore.");
        _tips.SetToolTip(cancel, "Chiude senza autorizzare nuovi invii.");
        var hint = _hint = new SelectableLabel
        {
            Text = ScrollHint,
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            Margin = new Padding(0, Metrics.Gap, Metrics.Gap + 4, 0)
        };
        bool Ready() => read.Checked && (region == null || region.Checked);
        void SyncButton()
        {
            accept.Enabled = Ready();
            hint.Text = !read.Enabled ? ScrollHint : region == null ? TickHint : TickBothHint;
            hint.Visible = !Ready();
        }
        read.CheckedChanged += (_, _) => SyncButton();
        if (region != null)
        {
            _tips.SetToolTip(region, "Il profilo cloud non dichiara dove il servizio elabora i dati: indicare l'area nel profilo oppure confermare qui.");
            region.CheckedChanged += (_, _) => SyncButton();
        }
        // Scroll and wheel events do not cover every way the view moves (thumb drag, keyboard, a resize that fits the text).
        _unlock = () =>
        {
            read.Enabled = true;
            read.AccessibleDescription = null;
            if (region != null) { region.Enabled = true; region.AccessibleDescription = null; }
            SyncButton();
        };
        _readWatch.Tick += (_, _) => CheckRead();
        scroll.Scroll += (_, _) => CheckRead();
        scroll.MouseWheel += (_, _) => CheckRead();
        accept.Click += (_, _) =>
        {
            if (!Ready()) return;
            ConfirmedUnspecifiedRegionProfiles = unspecifiedIds;
            DialogResult = DialogResult.OK;
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(Metrics.DialogPadding),
            FlowDirection = FlowDirection.RightToLeft
        };
        actions.Controls.AddRange(new Control[] { accept, cancel, hint });
        Controls.Add(scroll);
        Controls.Add(confirmations);
        Controls.Add(actions);
        AcceptButton = accept;
        CancelButton = cancel;
        Ui.ScaleFromDesignDpi(this);
        DisabledControlTips.Attach(this, _tips);
        Shown += (_, _) =>
        {
            Theme.Apply(this);
            Theme.ApplyWindowChrome(this);
            // The text opens at its top with the keyboard on it: PagGiù and the arrows scroll it.
            scroll.AutoScrollPosition = Point.Empty;
            scroll.Focus();
            CheckRead();
            if (!ReadToEnd) _readWatch.Start();
        };
        bool fitting = false;
        void Fit()
        {
            if (fitting) return;
            fitting = true;
            try
            {
                int width = Math.Max(100, scroll.ClientSize.Width - layout.Padding.Horizontal - Ui.Px(this, 20));
                foreach (Control control in layout.Controls)
                    if (control.MaximumSize.Width != width) control.MaximumSize = new Size(width, 0);
                int below = Math.Max(100, confirmations.ClientSize.Width - confirmations.Padding.Horizontal - Ui.Px(this, 8));
                foreach (Control control in confirmations.Controls)
                    if (control.MaximumSize.Width != below) control.MaximumSize = new Size(below, 0);
            }
            finally { fitting = false; }
        }
        scroll.SizeChanged += (_, _) => Fit();
        Load += (_, _) =>
        {
            var area = Screen.FromControl(Owner ?? this).WorkingArea;
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = Placement(area, Owner?.Bounds ?? area, Size); Fit();
        };
    }

    /// <summary>Unlocks the confirmations once the end of the text has been in view.</summary>
    internal void CheckRead()
    {
        if (ReadToEnd || !IsHandleCreated || !_scroll.AtEnd) return;
        ReadToEnd = true;
        _readWatch.Stop();
        _unlock();
    }

    internal static Point Placement(Rectangle area, Rectangle owner, Size size)
    {
        int x = owner.X + (owner.Width - size.Width) / 2;
        int y = owner.Y + (owner.Height - size.Height) / 2;
        return new Point(Math.Max(area.X, Math.Min(x, area.Right - size.Width)), Math.Max(area.Y, Math.Min(y, area.Bottom - size.Height)));
    }

    /// <summary>Reading keys scroll the text wherever the focus is: the checkboxes and buttons do not use them.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        int line = Ui.Px(this, 40), page = Math.Max(line, _scroll.ClientSize.Height - line);
        int? delta = keyData switch
        {
            Keys.Space when ActiveControl == _scroll => page,
            Keys.PageDown => page,
            Keys.PageUp => -page,
            Keys.Down when ActiveControl == _scroll => line,
            Keys.Up when ActiveControl == _scroll => -line,
            Keys.Control | Keys.End => int.MaxValue / 2,
            Keys.Control | Keys.Home => int.MinValue / 2,
            _ => null
        };
        if (delta is int d)
        {
            int y = Math.Max(0, Math.Min(_text.Height, -_scroll.AutoScrollPosition.Y + d));
            _scroll.AutoScrollPosition = new Point(0, y);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private const int WM_SYSCOMMAND = 0x0112, SC_CLOSE = 0xF060;

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) _closeByKeyboardOrX = true;
        return base.ProcessDialogKey(keyData);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_CLOSE) _closeByKeyboardOrX = true;
        base.WndProc(ref m);
    }

    /// <summary>Esc, X and Alt+F4 discard a ticked authorization: ask, while the Annulla button stays an explicit choice.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        bool implicitClose = _closeByKeyboardOrX;
        _closeByKeyboardOrX = false;
        if (implicitClose && _read.Checked && DialogResult != DialogResult.OK && CopyableMessageBox.Show(this,
                "Hai spuntato «Ho letto…» ma non hai autorizzato.\n\nChiudere senza autorizzare nuovi invii?", "Privacy e dati",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            DialogResult = DialogResult.None;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _readWatch.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
