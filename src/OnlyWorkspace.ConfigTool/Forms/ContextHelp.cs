using System.Drawing;
using System.Windows.Forms;

namespace OnlyWorkspace.ConfigTool.Forms;

internal static class ContextHelp
{
    internal static Button Button(Control owner, string title, System.Func<string> text, ToolTip tips)
    {
        var button = new FlatButton { Text = "?", AccessibleName = "Informazioni: " + title,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(28, 28), Margin = new Padding(4, 2, 4, 2) };
        tips.SetToolTip(button, "Apre la descrizione di «" + title + "».");
        button.Click += (_, _) =>
        {
            Show(owner, title, text());
            if (!button.IsDisposed && button.Visible && button.Enabled) button.Focus();
        };
        return button;
    }
    internal static void Show(Control owner, string title, string text)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
            Size = new Size(560, 350), MinimumSize = new Size(320, 240), Font = owner.Font };
        var content = new ThemedTextBox { Text = text, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, AccessibleName = title };
        var close = new FlatButton { Text = "Chiudi", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom, Height = 36 };
        dialog.Controls.Add(content); dialog.Controls.Add(close);
        dialog.CancelButton = close;
        Ui.ScaleFromDesignDpi(dialog);
        dialog.Shown += (_, _) => { Theme.Apply(dialog); Theme.ApplyWindowChrome(dialog); content.Focus(); };
        dialog.ShowDialog(owner.FindForm());
    }
}
