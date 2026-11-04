using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>A technical authorization with automatic destinations and no manual declarations.</summary>
internal sealed class DataEgressConsentForm : Form
{
    private readonly CheckBox _read;
    private readonly FlatButton _accept;
    private readonly Label _hint;
    private bool _closeByKeyboardOrX;
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    public DataEgressConsentForm(IReadOnlyList<DataEgressProvider> servers, DataEgressSigner? previous,
        IReadOnlyList<LlmProfileConfig>? profiles = null)
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
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(16) };
        var scroll = new ScrollPanel { Dock = DockStyle.Fill };
        scroll.Controls.Add(layout);
        void TextRow(string text) => layout.Controls.Add(new SelectableLabel { Text = text, AutoSize = true,
            MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 12) });
        TextRow(DataEgressNotice.Title);
        TextRow(DataEgressNotice.Text);
        foreach (var profile in profiles ?? Array.Empty<LlmProfileConfig>()) TextRow(DataEgress.ProfileSummary(profile));
        var read = _read = new ThemedCheckBox { Text = "Ho letto e autorizzo gli invii protetti per i profili indicati", AutoSize = true,
            MaximumSize = new Size(560, 0) };
        layout.Controls.Add(read);
        // A cloud profile without a declared area needs its own explicit confirmation.
        var unspecified = (profiles ?? Array.Empty<LlmProfileConfig>()).Where(p => p.IsExternal && p.ServiceRegion == ServiceRegion.Unspecified)
            .Select(p => p.DisplayName.Length == 0 ? p.Id : p.DisplayName).ToList();
        CheckBox? region = null;
        if (unspecified.Count > 0)
        {
            region = new ThemedCheckBox { Text = "Confermo di autorizzare anche l'area non specificata: " + string.Join(", ", unspecified), AutoSize = true,
                MaximumSize = new Size(560, 0) };
            layout.Controls.Add(region);
        }
        var accept = _accept = new FlatButton { Text = "Autorizza e salva", Kind = ButtonKind.Primary, Enabled = false, AutoSize = true };
        var cancel = new FlatButton { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
        _tips.SetToolTip(read, "Conferma l'autorizzazione tecnica. Non dichiara adempimenti aziendali o contratti.");
        _tips.SetToolTip(accept, "Salva l'autorizzazione per profilo, destinazione, permessi e chiavi attuali. Richiede diritti di amministratore.");
        _tips.SetToolTip(cancel, "Chiude senza autorizzare nuovi invii.");
        // The checkbox can be out of view in the scrolling text: the reason for the disabled button stays beside it.
        var hint = _hint = new SelectableLabel { Text = "Per abilitare «Autorizza e salva» spunta «Ho letto…» in fondo al testo.",
            AutoSize = true, MaximumSize = new Size(300, 0), Margin = new Padding(0, 6, 12, 0) };
        bool Ready() => read.Checked && (region == null || region.Checked);
        void SyncButton() { accept.Enabled = Ready(); hint.Visible = !Ready(); }
        read.CheckedChanged += (_, _) => SyncButton();
        if (region != null)
        {
            _tips.SetToolTip(region, "Il profilo cloud non dichiara dove il servizio elabora i dati: indicare l'area nel profilo oppure confermare qui.");
            region.CheckedChanged += (_, _) => SyncButton();
            hint.Text = "Per abilitare «Autorizza e salva» spunta «Ho letto…» e la conferma dell'area in fondo al testo.";
        }
        accept.Click += (_, _) => { if (Ready()) DialogResult = DialogResult.OK; };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12),
            FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange(new Control[] { accept, cancel, hint });
        Controls.Add(scroll);
        Controls.Add(actions);
        AcceptButton = accept;
        CancelButton = cancel;
        Ui.ScaleFromDesignDpi(this);
        DisabledControlTips.Attach(this, _tips);
        Shown += (_, _) => { Theme.Apply(this); Theme.ApplyWindowChrome(this); read.Focus(); };
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
            }
            finally { fitting = false; }
        }
        scroll.SizeChanged += (_, _) => Fit();
        Load += (_, _) => { var area = Screen.FromControl(Owner ?? this).WorkingArea;
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = Placement(area, Owner?.Bounds ?? area, Size); Fit(); };
    }

    internal static Point Placement(Rectangle area, Rectangle owner, Size size)
    {
        int x = owner.X + (owner.Width - size.Width) / 2;
        int y = owner.Y + (owner.Height - size.Height) / 2;
        return new Point(Math.Max(area.X, Math.Min(x, area.Right - size.Width)), Math.Max(area.Y, Math.Min(y, area.Bottom - size.Height)));
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
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
