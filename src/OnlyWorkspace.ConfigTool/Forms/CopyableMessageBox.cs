using System.Drawing;
using System.Windows.Forms;

namespace OnlyWorkspace.ConfigTool.Forms;

internal static class CopyableMessageBox
{
    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        => Show(null, text, caption, buttons, icon);

    /// <param name="labels">Optional button texts, in the order Yes/No/Cancel (or OK).</param>
    /// <remarks>A Yes/No warning confirms something hard to undo: Enter and the initial default answer No, the user must choose Yes.</remarks>
    public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, params string[] labels)
    {
        using var dialog = new Form
        {
            Text = caption,
            Font = new Font("Segoe UI", 9F),
            ClientSize = new Size(580, 260),
            MinimumSize = new Size(420, 200),
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = owner == null,
            BackColor = Theme.Colors.Window,
            ForeColor = Theme.Colors.Text
        };
        dialog.Icon = icon == MessageBoxIcon.Error ? SystemIcons.Error : icon == MessageBoxIcon.Warning
            ? SystemIcons.Warning : icon == MessageBoxIcon.Question ? SystemIcons.Question : SystemIcons.Information;
        var content = new ThemedTextBox
        {
            Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true
        };
        content.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A) { content.SelectAll(); e.SuppressKeyPress = true; }
        };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        body.Controls.Add(content);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            WrapContents = false
        };
        var results = buttons == MessageBoxButtons.YesNoCancel
            ? new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel }
            : buttons == MessageBoxButtons.YesNo ? new[] { DialogResult.Yes, DialogResult.No }
            : new[] { DialogResult.OK };
        bool safeDefault = buttons == MessageBoxButtons.YesNo && icon == MessageBoxIcon.Warning;
        for (int i = results.Length - 1; i >= 0; i--)
        {
            var result = results[i];
            var button = Metrics.DialogButton(new FlatButton
            {
                Text = i < labels.Length ? labels[i] : result == DialogResult.Yes ? "Sì" : result == DialogResult.No ? "No" : result == DialogResult.Cancel ? "Annulla" : "OK",
                DialogResult = result,
                // Laid out right to left: Tab still goes left to right, as the buttons read.
                TabIndex = i + 1
            });
            actions.Controls.Add(button);
            if (safeDefault ? result == DialogResult.No : i == 0) dialog.AcceptButton = button;
            if (result == DialogResult.Cancel || result == DialogResult.OK || (buttons == MessageBoxButtons.YesNo && result == DialogResult.No))
                dialog.CancelButton = button;
        }
        var copy = Metrics.DialogButton(new FlatButton { Text = "Copia messaggio", TabIndex = 0 });
        copy.Click += (_, _) => { content.SelectAll(); content.Copy(); };
        actions.Controls.Add(copy);
        dialog.Controls.Add(body);
        dialog.Controls.Add(actions);
        Ui.ScaleFromDesignDpi(dialog);
        dialog.Shown += (_, _) => { Theme.Apply(dialog); Theme.ApplyWindowChrome(dialog); content.Focus(); content.Select(0, 0); };
        return owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }
}
