using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>Keeps label layout while exposing its complete text through a native read-only edit control.</summary>
internal class SelectableLabel : Label
{
    private readonly TextBox _text;
    private (string, Font, Rectangle, ContentAlignment)? _placed;

    public SelectableLabel()
    {
        _text = new TextBox
        {
            BorderStyle = BorderStyle.None, Multiline = true, ReadOnly = true,
            // Selectable with the mouse, not a Tab stop: Tab would select the whole label.
            WordWrap = true, ShortcutsEnabled = true, TabStop = false,
            Margin = Padding.Empty
        };
        // The edit control's own side margins would clip AutoSize labels measured as plain text.
        _text.HandleCreated += (_, _) => SendMessage(_text.Handle, EmSetMargins, (IntPtr)3, IntPtr.Zero);
        _text.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A) { _text.SelectAll(); e.SuppressKeyPress = true; }
        };
        _text.ContextMenuStrip = SharedMenu.Value;
        Controls.Add(_text);
        SyncText();
    }

    // One menu for every label, acting on the box it opened on: a menu each made pages with many rows slow to show.
    private static readonly Lazy<ContextMenuStrip> SharedMenu = new(() =>
    {
        var menu = new ContextMenuStrip();
        TextBox? Source() => menu.SourceControl as TextBox;
        menu.Items.Add("Copia", null, (_, _) => Source()?.Copy());
        menu.Items.Add("Copia tutto", null, (_, _) => { Source()?.SelectAll(); Source()?.Copy(); });
        menu.Items.Add("Seleziona tutto", null, (_, _) => Source()?.SelectAll());
        menu.Opening += (_, _) => Theme.Style(menu);
        return menu;
    });

    private void SyncText()
    {
        if (_text == null) return;
        if (_text.Text != Text) _text.Text = Text;
        _text.Font = Font;
        _text.ForeColor = ForeColor;
        _text.BackColor = BackColor;
        _text.AccessibleName = AccessibleName ?? Text;
        // A Label's DisplayRectangle ignores Padding: the text area is the client area inside it.
        var area = new Rectangle(Padding.Left, Padding.Top,
            Math.Max(0, ClientSize.Width - Padding.Horizontal), Math.Max(0, ClientSize.Height - Padding.Vertical));
        // Every layout pass of the page lands here: measure again only when something the measure uses changed.
        var placed = (Text, Font, area, TextAlign);
        if (placed.Equals(_placed)) return;
        _placed = placed;
        int textHeight = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font,
            new Size(Math.Max(1, area.Width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        int top = TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight
            ? Math.Max(0, (area.Height - textHeight) / 2) : 0;
        _text.Bounds = new Rectangle(area.X, area.Y + top, area.Width, Math.Max(0, area.Height - top));
        _text.TextAlign = TextAlign is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter
            ? HorizontalAlignment.Center : TextAlign is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight
            ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var size = base.GetPreferredSize(proposedSize);
        return AutoSize ? new Size(size.Width + 2, size.Height) : size;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); SyncText(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); SyncText(); }
    protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); SyncText(); }
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); SyncText(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); SyncText(); }

    private const int EmSetMargins = 0xD3;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
