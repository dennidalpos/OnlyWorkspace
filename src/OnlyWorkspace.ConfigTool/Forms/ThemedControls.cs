using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OnlyWorkspace.ConfigTool.Forms;

internal static class Ui
{
    /// <summary>
    /// Scale of custom painting, from the height of the 9-point UI font (16 px at 96 DPI): it follows
    /// the monitor like the rest of the layout, also where DeviceDpi stays at the system value.
    /// </summary>
    public static float Scale(Control control) => control.Font.Height / 16f;

    public static int Px(Control control, float value) => (int)Math.Round(value * Scale(control));

    /// <summary>A detached subtree created after the initial scaling; later DPI changes belong to WinForms.</summary>
    public static void ScaleDynamic(Control subtree, Control parent)
    {
        float scale = Scale(parent);
        if (Math.Abs(scale - 1f) > 0.01f) subtree.Scale(new SizeF(scale, scale));
    }

    public static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));

    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Rectangle r, int radius, Color fill, Color? border = null)
    {
        using var path = Rounded(r, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (border == null) return;
        using var pen = new Pen(border.Value);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// Sizes written at 96 DPI follow the monitor: without AutoScaleDimensions, AutoScaleMode.Dpi does nothing.
    /// Also keeps fonts set with <see cref="SetFont"/> in proportion to the form font, which WinForms rescales on a DPI change.
    /// </summary>
    public static void ScaleFromDesignDpi(Form form)
    {
        form.SuspendLayout();
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.ResumeLayout(true);
        form.Load += (s, e) => { MatchMonitorDpi(form); ApplyFonts(form); };
        form.VisibleChanged += (s, e) => { if (form.Visible) MatchMonitorDpi(form); };
        form.FontChanged += (s, e) => ApplyFonts(form);
        form.DpiChanged += (s, e) => ApplyFonts(form);
    }

    private sealed class FontRole
    {
        public float Points;
        public FontStyle Style;
        public string? Family;
    }

    private static readonly ConditionalWeakTable<Control, FontRole> FontRoles = new();

    /// <summary>
    /// Font of <paramref name="points"/> at the 9-point design size, scaled with the form font. A fixed Font would
    /// drift: on a monitor change net48 rescales explicit child fonts by a different factor than the form font.
    /// </summary>
    public static T SetFont<T>(T control, float points, FontStyle style = FontStyle.Regular, string? family = null) where T : Control
    {
        bool known = FontRoles.TryGetValue(control, out _);
        if (known) FontRoles.Remove(control);
        FontRoles.Add(control, new FontRole { Points = points, Style = style, Family = family });
        if (!known) control.ParentChanged += (s, e) => ApplyFont(control);
        ApplyFont(control);
        return control;
    }

    public static void ApplyFonts(Control root)
    {
        ApplyFont(root);
        foreach (Control child in root.Controls) ApplyFonts(child);
    }

    private static void ApplyFont(Control control)
    {
        if (!FontRoles.TryGetValue(control, out var role)) return;
        var baseFont = control.FindForm()?.Font;
        float size = (baseFont?.SizeInPoints ?? 9F) * role.Points / 9F;
        string family = role.Family ?? baseFont?.Name ?? "Segoe UI";
        var current = control.Font;
        if (current.Name == family && current.Style == role.Style && Math.Abs(current.SizeInPoints - size) < 0.01F) return;
        control.Font = new Font(family, size, role.Style, GraphicsUnit.Point);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// net48 builds a form at the system DPI even when its window opens on a monitor with another scale, and
    /// Windows sends WM_DPICHANGED only on a later move: deliver it now, as a move from the primary monitor would.
    /// </summary>
    private static void MatchMonitorDpi(Form form)
    {
        if (!form.IsHandleCreated || form.WindowState != FormWindowState.Normal) return;
        int actual;
        try { actual = (int)GetDpiForWindow(form.Handle); }
        catch (EntryPointNotFoundException) { return; }
        if (actual <= 0 || actual == form.DeviceDpi) return;
        float factor = actual / (float)form.DeviceDpi;
        var bounds = form.Bounds;
        int width = (int)Math.Round(bounds.Width * factor), height = (int)Math.Round(bounds.Height * factor);
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var suggested = new Rect { Left = center.X - width / 2, Top = center.Y - height / 2 };
        suggested.Right = suggested.Left + width;
        suggested.Bottom = suggested.Top + height;
        IntPtr rect = Marshal.AllocHGlobal(Marshal.SizeOf<Rect>());
        try
        {
            Marshal.StructureToPtr(suggested, rect, false);
            const int WM_DPICHANGED = 0x02E0;
            SendMessage(form.Handle, WM_DPICHANGED, (IntPtr)(actual | (actual << 16)), rect);
        }
        finally { Marshal.FreeHGlobal(rect); }
    }

    public const ControlStyles OwnerPaint =
        ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw;
}

/// <summary>Rounded panel with an optional title; replaces GroupBox.</summary>
internal sealed class Card : Panel, IThemed
{
    private readonly SelectableLabel? _title;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool StrongBorder { get; set; }

    public Card(string title = "")
    {
        Padding = title.Length > 0 ? new Padding(14, 36, 14, 10) : new Padding(14, 12, 14, 12);
        Margin = new Padding(0, 0, 0, 10);
        SetStyle(Ui.OwnerPaint, true);
        if (title.Length > 0)
        {
            _title = new SelectableLabel { Text = title, AutoSize = false };
            Controls.Add(_title);
        }
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Colors.Card;
        if (_title != null) _title.ForeColor = Theme.Colors.Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ui.FillRounded(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.Px(this, 6), BackColor,
            StrongBorder ? Theme.Colors.InputBorder : Theme.Colors.Border);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_title == null) return;
        _title.Bounds = new Rectangle(Padding.Left, Ui.Px(this, 10),
            Math.Max(0, ClientSize.Width - Padding.Horizontal), Font.Height + Ui.Px(this, 2));
        if (_title.Font.Size != Font.Size || _title.Font.Name != "Segoe UI Semibold")
            _title.Font = TitleFont(Font.Size);
    }

    // One font per size for all card titles: each card created its own and the ConfigTool has dozens.
    private static readonly Dictionary<float, Font> TitleFonts = new();

    private static Font TitleFont(float size)
    {
        if (!TitleFonts.TryGetValue(size, out var font)) TitleFonts[size] = font = new Font("Segoe UI Semibold", size);
        return font;
    }
}

internal enum ButtonKind { Secondary, Primary, Danger }

internal sealed class FlatButton : Button, IThemed
{
    private bool _hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; }

    /// <summary>Segoe MDL2 Assets character drawn instead of the text when the text does not fit; Text stays the accessible name.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; } = "";

    public FlatButton()
    {
        SetStyle(Ui.OwnerPaint, true);
    }

    /// <summary>True when the button is too narrow for its text and shows its glyph.</summary>
    public bool ShowsGlyph => Glyph.Length > 0 && TextRenderer.MeasureText(Text, Font).Width > Width - Ui.Px(this, 12);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Invalidate();
    }

    public void ApplyTheme() => Invalidate();

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        base.OnMouseEnter(e);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        base.OnMouseLeave(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var c = Theme.Colors;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? c.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill, border, text;
        switch (Kind)
        {
            case ButtonKind.Primary:
                fill = border = _hover ? c.AccentHover : c.Accent;
                text = c.OnAccent;
                break;
            case ButtonKind.Danger:
                fill = border = _hover ? c.DangerHover : c.DangerFill;
                text = Color.White;
                break;
            default:
                fill = _hover ? c.ButtonHover : c.Button;
                border = c.ButtonBorder;
                text = c.Text;
                break;
        }
        if (!Enabled)
        {
            fill = c.InputDisabled;
            border = c.Border;
            text = c.Disabled;
        }

        Ui.FillRounded(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.Px(this, 4), fill, border);
        if (ShowsGlyph)
        {
            using var icons = new Font("Segoe MDL2 Assets", Font.Size * 1.2f);
            TextRenderer.DrawText(g, Glyph, icons, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        else
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -3, -3));
    }
}

/// <summary>Sidebar entry (glyph + text) or, with TabStyle, a section tab with an underline.</summary>
internal sealed class NavButton : Button, IThemed
{
    private bool _hover;
    private bool _selected;

    /// <summary>Segoe MDL2 Assets character.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool TabStyle { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            // One Tab stop per group, on the selected tab: arrows move between tabs, Tab leaves the group.
            TabStop = value;
            Invalidate();
            if (IsHandleCreated)
            {
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                if (value) AccessibilityNotifyClients(AccessibleEvents.Selection, -1);
            }
        }
    }

    public NavButton()
    {
        SetStyle(Ui.OwnerPaint, true);
        TabStop = false;
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new NavButtonAccessibleObject(this);

    private sealed class NavButtonAccessibleObject : ControlAccessibleObject
    {
        private readonly NavButton _button;

        public NavButtonAccessibleObject(NavButton button) : base(button) => _button = button;

        public override AccessibleRole Role => AccessibleRole.PageTab;
        public override AccessibleStates State => base.State | AccessibleStates.Selectable |
            (_button.Selected ? AccessibleStates.Selected : AccessibleStates.None);
        public override string? Name
        {
            get => _button.AccessibleName ?? _button.Text;
            set => _button.AccessibleName = value;
        }
        public override string DefaultAction => "Seleziona";

        public override void DoDefaultAction()
        {
            if (_button.CanSelect) _button.PerformClick();
        }

        public override void Select(AccessibleSelection flags)
        {
            if (!_button.CanSelect) return;
            if ((flags & AccessibleSelection.TakeSelection) != 0) _button.PerformClick();
            if ((flags & AccessibleSelection.TakeFocus) != 0) _button.Focus();
        }
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        bool previous = keyData == (TabStyle ? Keys.Left : Keys.Up);
        bool next = keyData == (TabStyle ? Keys.Right : Keys.Down);
        if (Parent == null || (!previous && !next)) return base.ProcessDialogKey(keyData);

        var tabs = Parent.Controls.OfType<NavButton>().Where(tab => tab.TabStyle == TabStyle)
            .OrderBy(tab => tab.TabIndex).ToList();
        int current = tabs.IndexOf(this);
        for (int offset = 1; offset <= tabs.Count; offset++)
        {
            var target = tabs[(current + (next ? offset : -offset) + tabs.Count) % tabs.Count];
            if (!target.CanSelect) continue;
            target.PerformClick();
            // A native TabControl can move focus into its page while the click is handled.
            target.Focus();
            break;
        }
        return true;
    }

    public void ApplyTheme() => Invalidate();

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        base.OnMouseEnter(e);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        base.OnMouseLeave(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var c = Theme.Colors;
        var g = e.Graphics;
        Color back = Parent?.BackColor ?? c.Window;
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        if (_selected && !TabStyle) Ui.FillRounded(g, bounds, Ui.Px(this, 4), Ui.Blend(back, c.Text, 0.10f));
        else if (_hover && !_selected) Ui.FillRounded(g, bounds, Ui.Px(this, 4), Ui.Blend(back, c.Text, 0.06f));

        using var accent = new SolidBrush(c.Accent);
        if (TabStyle)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, _selected ? c.Text : c.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (_selected)
            {
                int inset = Ui.Px(this, 8), bar = Ui.Px(this, 3);
                g.FillRectangle(accent, inset, Height - bar, Width - inset * 2, bar);
            }
        }
        else
        {
            if (_selected)
            {
                int bar = Ui.Px(this, 3), barHeight = Ui.Px(this, 16);
                g.FillRectangle(accent, 0, (Height - barHeight) / 2, bar, barHeight);
            }
            using var icons = new Font("Segoe MDL2 Assets", Font.Size * 1.2f);
            TextRenderer.DrawText(g, Glyph, icons, new Rectangle(Ui.Px(this, 10), 0, Ui.Px(this, 26), Height), c.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.Px(this, 42), 0, Width - Ui.Px(this, 46), Height), c.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -3, -3));
    }
}

/// <summary>Row of section tabs; one is always selected.</summary>
internal sealed class SegmentedBar : FlowLayoutPanel
{
    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex { get; private set; } = -1;

    public SegmentedBar(params string[] items)
    {
        AccessibleName = "Sezioni";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
        Margin = new Padding(0, 0, 0, 10);
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            var tab = new NavButton
            {
                Text = items[i],
                TabStyle = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(0, 30),
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(0, 0, 4, 0)
            };
            tab.Click += (s, e) => Select(index);
            Controls.Add(tab);
        }
        Select(0);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SegmentedBarAccessibleObject(this);

    private sealed class SegmentedBarAccessibleObject : ControlAccessibleObject
    {
        private readonly SegmentedBar _bar;

        public SegmentedBarAccessibleObject(SegmentedBar bar) : base(bar) => _bar = bar;

        public override AccessibleRole Role => AccessibleRole.PageTabList;
        public override int GetChildCount() => _bar.Controls.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < _bar.Controls.Count
            ? _bar.Controls[index].AccessibilityObject : null;
        public override AccessibleObject? GetSelected() => _bar.SelectedIndex >= 0 && _bar.SelectedIndex < _bar.Controls.Count
            ? _bar.Controls[_bar.SelectedIndex].AccessibilityObject : null;
        public override AccessibleObject? GetFocused() => _bar.Controls.Cast<Control>()
            .FirstOrDefault(control => control.Focused)?.AccessibilityObject;
    }

    public void Select(int index)
    {
        if (index == SelectedIndex || index < 0 || index >= Controls.Count) return;
        SelectedIndex = index;
        for (int i = 0; i < Controls.Count; i++) ((NavButton)Controls[i]).Selected = i == index;
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class ThemedCheckBox : CheckBox, IThemed
{
    public ThemedCheckBox()
    {
        SetStyle(Ui.OwnerPaint, true);
    }

    public void ApplyTheme() => Invalidate();

    private int Box => Ui.Px(this, 16);

    private int TextLeft => Box + Ui.Px(this, 7);

    private const TextFormatFlags WrapFlags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        var natural = new Size(text.Width + Box + Ui.Px(this, 10), Math.Max(text.Height, Box) + Ui.Px(this, 6));
        // A long label wraps only where the page limits the width (MaximumSize); without a limit it stays on one line.
        int limit = MaximumSize.Width;
        if (limit <= 0 || natural.Width <= limit) return natural;
        var wrapped = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(1, limit - TextLeft), int.MaxValue), WrapFlags);
        return new Size(limit, Math.Max(wrapped.Height, Box) + Ui.Px(this, 6));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var c = Theme.Colors;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? c.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int left = TextLeft;
        int lineHeight = Math.Max(TextRenderer.MeasureText(Text, Font).Height, Box);
        var textArea = new Size(Math.Max(1, Width - left), int.MaxValue);
        bool wrapped = TextRenderer.MeasureText(Text, Font, textArea, WrapFlags).Height > Font.Height + 2;
        // Wrapped label: the box stays beside the first line instead of the middle of the block.
        var box = new Rectangle(1, wrapped ? Ui.Px(this, 3) + (lineHeight - Box) / 2 : (Height - Box) / 2, Box - 1, Box - 1);
        // Checked is also true for Indeterminate: an unknown state gets a dash, never the tick of a real choice.
        if (Checked)
        {
            Ui.FillRounded(g, box, Ui.Px(this, 3), Enabled ? c.Accent : c.Disabled);
            using var pen = new Pen(Enabled ? c.OnAccent : c.Card, 1.7f * Ui.Scale(this)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float u = box.Width / 16f;
            if (CheckState == CheckState.Indeterminate)
                g.DrawLine(pen, box.X + 4f * u, box.Y + 8f * u, box.X + 12f * u, box.Y + 8f * u);
            else
                g.DrawLines(pen, new[]
                {
                    new PointF(box.X + 4f * u, box.Y + 8.3f * u),
                    new PointF(box.X + 6.9f * u, box.Y + 11.2f * u),
                    new PointF(box.X + 12.2f * u, box.Y + 5.2f * u)
                });
        }
        else
        {
            Ui.FillRounded(g, box, Ui.Px(this, 3), Enabled ? c.Input : c.InputDisabled, Enabled ? c.Muted : c.Disabled);
        }

        if (wrapped)
            TextRenderer.DrawText(g, Text, Font, new Rectangle(left, Ui.Px(this, 3), Width - left, Height - Ui.Px(this, 3)), Enabled ? c.Text : c.Disabled, WrapFlags);
        else
            TextRenderer.DrawText(g, Text, Font, new Rectangle(left, 0, Width - left, Height), Enabled ? c.Text : c.Disabled,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(left - 2, 1, Width - left + 1, Height - 2));
    }
}

internal sealed class ThemedTextBox : TextBox, IThemed
{
    public ThemedTextBox()
    {
        // The native border ignores the theme: single-line boxes sit in an InputFrame instead.
        BorderStyle = BorderStyle.None;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        var c = Theme.Colors;
        BackColor = Enabled && !ReadOnly ? c.Input : c.InputDisabled;
        ForeColor = c.Text;
        if (Multiline) Theme.ApplyScrollBars(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Multiline) Theme.ApplyScrollBars(this);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ApplyTheme();
    }

    protected override void OnReadOnlyChanged(EventArgs e)
    {
        base.OnReadOnlyChanged(e);
        ApplyTheme();
    }
}

/// <summary>Themed 1-pixel frame around a borderless text box; takes over its width and anchor.</summary>
internal sealed class InputFrame : Panel, IThemed
{
    private readonly TextBox _inner;

    public InputFrame(TextBox inner)
    {
        _inner = inner;
        Size = new Size(inner.Width, 23);
        Anchor = inner.Anchor;
        Margin = new Padding(3);
        Padding = new Padding(5, 3, 4, 3);
        SetStyle(Ui.OwnerPaint, true);

        inner.Dock = DockStyle.Fill;
        inner.Margin = Padding.Empty;
        inner.Enter += (s, e) => Invalidate();
        inner.Leave += (s, e) => Invalidate();
        inner.BackColorChanged += (s, e) => ApplyTheme();
        Controls.Add(inner);
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = _inner.BackColor;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        using var pen = new Pen(_inner.Focused ? Theme.Colors.Accent : Theme.Colors.InputBorder);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class ThemedComboBox : ComboBox, IThemed
{
    private const int WM_PAINT = 0x000F;

    public ThemedComboBox()
    {
        // Flat is what makes a DropDownList honour BackColor.
        FlatStyle = FlatStyle.Flat;
        DropDownStyle = ComboBoxStyle.DropDownList;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        var c = Theme.Colors;
        BackColor = Enabled ? c.Input : c.InputDisabled;
        ForeColor = c.Text;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ApplyTheme();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT && IsHandleCreated) PaintChrome();
    }

    /// <summary>Border and arrow over the flat combo's own (system-coloured) ones.</summary>
    private void PaintChrome()
    {
        var c = Theme.Colors;
        using var g = Graphics.FromHwnd(Handle);
        int buttonWidth = Ui.Px(this, 20);
        var button = new Rectangle(Width - buttonWidth - 1, 1, buttonWidth, Height - 2);
        using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, button);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = 3.5f * Ui.Scale(this);
        float cx = button.X + button.Width / 2f, cy = Height / 2f;
        using (var pen = new Pen(Enabled ? c.Muted : c.Disabled, 1.4f * Ui.Scale(this)))
        {
            g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
        }

        g.SmoothingMode = SmoothingMode.None;
        if (!Enabled)
        {
            // Disabled, the flat combo draws a 2px system-coloured frame inside its border: light in dark mode.
            using var frame = new Pen(BackColor);
            g.DrawRectangle(frame, 1, 1, Width - 3, Height - 3);
            g.DrawRectangle(frame, 2, 2, Width - 5, Height - 5);
        }
        using var border = new Pen(Focused ? c.Accent : c.InputBorder);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class ThemedNumeric : NumericUpDown, IThemed
{
    public ThemedNumeric()
    {
        BorderStyle = BorderStyle.FixedSingle;
        // Controls[0] is the up/down button pair: its Paint event fires after it drew itself.
        Controls[0].Paint += PaintButtons;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        var c = Theme.Colors;
        BackColor = Enabled ? c.Input : c.InputDisabled;
        ForeColor = c.Text;
        Invalidate(true);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ApplyTheme();
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        Invalidate();
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var back = new Pen(BackColor);
        e.Graphics.DrawRectangle(back, 1, 1, Width - 3, Height - 3);
        using var border = new Pen(ContainsFocus ? Theme.Colors.Accent : Theme.Colors.InputBorder);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private void PaintButtons(object? sender, PaintEventArgs e)
    {
        var buttons = (Control)sender!;
        var c = Theme.Colors;
        var g = e.Graphics;
        using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, buttons.ClientRectangle);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = 3f * Ui.Scale(this);
        float cx = buttons.Width / 2f, half = buttons.Height / 2f;
        using var pen = new Pen(Enabled ? c.Muted : c.Disabled, 1.3f * Ui.Scale(this));
        float up = half / 2f + 1, down = half + half / 2f - 1;
        g.DrawLines(pen, new[] { new PointF(cx - s, up + s / 2), new PointF(cx, up - s / 2), new PointF(cx + s, up + s / 2) });
        g.DrawLines(pen, new[] { new PointF(cx - s, down - s / 2), new PointF(cx, down + s / 2), new PointF(cx + s, down - s / 2) });
    }
}

/// <summary>Scrolling page whose scroll bars follow the theme (a plain Panel keeps light ones in dark mode).</summary>
internal sealed class ScrollPanel : Panel, IThemed
{
    public ScrollPanel() => AutoScroll = true;

    public void ApplyTheme() => Theme.ApplyScrollBars(this);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyScrollBars(this);
    }
}

/// <summary>Owner-drawn profile list: accent bar on the selection, star on the marked entry.</summary>
internal sealed class ProfileListBox : ListBox, IThemed
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<object, string>? Caption { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<object, bool>? IsMarked { get; set; }

    public ProfileListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Colors.Card;
        ForeColor = Theme.Colors.Text;
        Theme.ApplyScrollBars(this);
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ItemHeight = Ui.Px(this, 30);
        Theme.ApplyScrollBars(this);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = Ui.Px(this, 30);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var c = Theme.Colors;
        var g = e.Graphics;
        object item = Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        // The selection bar alone does not say whether the list has the keyboard: an accent outline marks the focused row.
        bool focused = (e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0;

        using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, e.Bounds);
        if (selected || focused)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var row = new Rectangle(e.Bounds.X, e.Bounds.Y + 1, e.Bounds.Width - 1, e.Bounds.Height - 3);
            Ui.FillRounded(g, row, Ui.Px(this, 4), selected ? Ui.Blend(BackColor, c.Text, 0.09f) : BackColor, focused ? c.Accent : null);
        }
        if (selected)
        {
            int bar = Ui.Px(this, 3), barHeight = Ui.Px(this, 14);
            using var accent = new SolidBrush(c.Accent);
            g.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + (e.Bounds.Height - barHeight) / 2, bar, barHeight);
        }

        int star = IsMarked?.Invoke(item) == true ? Ui.Px(this, 20) : 0;
        var text = new Rectangle(e.Bounds.X + Ui.Px(this, 10), e.Bounds.Y, e.Bounds.Width - Ui.Px(this, 12) - star, e.Bounds.Height);
        TextRenderer.DrawText(g, Caption?.Invoke(item) ?? GetItemText(item), Font, text, c.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (star > 0)
        {
            TextRenderer.DrawText(g, "★", Font, new Rectangle(e.Bounds.Right - star - 2, e.Bounds.Y, star, e.Bounds.Height), c.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
