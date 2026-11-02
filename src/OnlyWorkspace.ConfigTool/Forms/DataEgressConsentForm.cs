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
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    public DataEgressConsentForm(IReadOnlyList<DataEgressProvider> servers, DataEgressSigner? previous,
        IReadOnlyList<LlmProfileConfig>? profiles = null)
    {
        Text = "Privacy e dati";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        Font = new Font("Segoe UI", 9F);
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
        var accept = _accept = new FlatButton { Text = "Autorizza e salva", Kind = ButtonKind.Primary, Enabled = false, AutoSize = true };
        var cancel = new FlatButton { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
        _tips.SetToolTip(read, "Conferma l'autorizzazione tecnica. Non dichiara adempimenti aziendali o contratti.");
        _tips.SetToolTip(accept, "Salva l'autorizzazione per profilo, destinazione, permessi e chiavi attuali. Richiede diritti di amministratore.");
        _tips.SetToolTip(cancel, "Chiude senza autorizzare nuovi invii.");
        read.CheckedChanged += (_, _) => accept.Enabled = read.Checked;
        accept.Click += (_, _) => { if (read.Checked) DialogResult = DialogResult.OK; };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12),
            FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange(new Control[] { accept, cancel });
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
