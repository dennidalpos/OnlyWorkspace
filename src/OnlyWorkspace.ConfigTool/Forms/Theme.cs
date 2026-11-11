using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>Meaning of a label's colour, so it survives a theme switch.</summary>
internal enum Tone { Normal, Muted, Success, Warning, Danger }

internal interface IThemed
{
    void ApplyTheme();
}

/// <summary>
/// The one set of sizes and spaces of the ConfigTool, at 96 DPI (forms scale them with <see cref="Ui.ScaleFromDesignDpi"/>).
/// Commands are 32 px high; a compact button sits beside a field and matches its height.
/// </summary>
internal static class Metrics
{
    public const int Gap = 8;
    public const int ButtonHeight = 32;
    public const int CompactButtonHeight = 26;
    public const int ButtonMinWidth = 96;
    public const int ButtonPadding = 12;
    /// <summary>Inside a dialog and around its button bar.</summary>
    public const int DialogPadding = 16;
    /// <summary>A checkbox that depends on the one above it.</summary>
    public const int Indent = 24;
    /// <summary>Space above a group heading inside a card.</summary>
    public const int GroupSpacing = 14;

    // Widths where a long label wraps (MaximumSize), at 96 DPI. Forms must not repeat these numbers.
    public const int TextWidthSide = 300;
    public const int TextWidthCaption = 360;
    public const int TextWidthNarrow = 440;
    public const int TextWidthMedium = 520;
    public const int TextWidthDialog = 560;
    public const int TextWidthPage = 600;
    public const int StorageLabelNarrow = 190;
    public const int StorageLabelWide = 330;
    public const int WebSupportMinWidth = 160;
    public const int ColumnLabelWide = 160;
    public const int ColumnLabelNarrow = 140;
    public const int RowMinHeight = 30;
    public const int SearchLayoutMinHeight = 480;

    // Smallest sizes of each window or popup, at 96 DPI.
    public static readonly Size MainWindowMin = new(820, 500);
    public static readonly Size WizardWindowMin = new(560, 420);
    public static readonly Size ConsentWindowMin = new(380, 360);
    public static readonly Size SearchWindowMin = new(680, 480);
    public static readonly Size MessageBoxMin = new(420, 200);
    public static readonly Size HelpPopupMin = new(320, 240);
    public static readonly Size HelpButtonMin = new(28, 28);

    private static T Sized<T>(T button, int minWidth, int height, int padding, Padding margin) where T : Button
    {
        button.AutoSize = true;
        // GrowAndShrink: a GrowOnly button keeps the size DPI scaling gave it on top of its own.
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(minWidth, height);
        button.Padding = new Padding(padding, 0, padding, 0);
        button.Margin = margin;
        return button;
    }

    /// <summary>A button of a dialog bar or of the window footer; bars flow right to left, so the gap sits on the left.</summary>
    public static T DialogButton<T>(T button) where T : Button => Sized(button, ButtonMinWidth, ButtonHeight, ButtonPadding, new Padding(Gap, 0, 0, 0));

    /// <summary>A command inside a page, in a row read left to right.</summary>
    public static T CommandButton<T>(T button) where T : Button => Sized(button, 0, ButtonHeight, ButtonPadding, new Padding(0, 0, Gap, Gap));

    /// <summary>A button beside a field (choose, show, browse).</summary>
    public static T CompactButton<T>(T button) where T : Button => Sized(button, 0, CompactButtonHeight, Gap, new Padding(Gap / 2, 0, 0, 0));
}

internal sealed class Palette
{
    public Color Window, Sidebar, Card, Border;
    public Color Text, Muted, Disabled;
    public Color Input, InputBorder, InputDisabled, ButtonBorder;
    public Color Accent, AccentHover, OnAccent;
    public Color Button, ButtonHover, DangerFill, DangerHover;
    public Color Success, Warning, Danger;
}

/// <summary>Light and dark palettes following the Windows app theme.</summary>
internal static class Theme
{
    private static readonly Palette Light = new()
    {
        Window = Color.FromArgb(243, 243, 243),
        Sidebar = Color.FromArgb(234, 236, 240),
        Card = Color.White,
        Border = Color.FromArgb(222, 222, 222),
        Text = Color.FromArgb(27, 27, 27),
        Muted = Color.FromArgb(96, 96, 96),
        Disabled = Color.FromArgb(160, 160, 160),
        Input = Color.White,
        // Field borders at 3:1 (WCAG 1.4.11); buttons keep the lighter border, their text identifies them.
        InputBorder = Color.FromArgb(138, 138, 138),
        InputDisabled = Color.FromArgb(243, 243, 243),
        ButtonBorder = Color.FromArgb(196, 196, 196),
        Accent = Color.FromArgb(24, 90, 157),
        AccentHover = Color.FromArgb(34, 108, 184),
        OnAccent = Color.White,
        Button = Color.FromArgb(251, 251, 251),
        ButtonHover = Color.FromArgb(240, 240, 240),
        DangerFill = Color.FromArgb(196, 62, 40),
        DangerHover = Color.FromArgb(176, 46, 26),
        Success = Color.FromArgb(16, 124, 16),
        Warning = Color.FromArgb(157, 93, 0),
        Danger = Color.FromArgb(197, 15, 31)
    };

    private static readonly Palette Dark = new()
    {
        Window = Color.FromArgb(32, 32, 32),
        Sidebar = Color.FromArgb(26, 26, 26),
        Card = Color.FromArgb(43, 43, 43),
        Border = Color.FromArgb(60, 60, 60),
        Text = Color.FromArgb(240, 240, 240),
        Muted = Color.FromArgb(170, 170, 170),
        Disabled = Color.FromArgb(112, 112, 112),
        Input = Color.FromArgb(55, 55, 55),
        InputBorder = Color.FromArgb(128, 128, 128),
        InputDisabled = Color.FromArgb(46, 46, 46),
        ButtonBorder = Color.FromArgb(92, 92, 92),
        Accent = Color.FromArgb(96, 165, 235),
        AccentHover = Color.FromArgb(120, 180, 240),
        OnAccent = Color.FromArgb(12, 12, 12),
        Button = Color.FromArgb(58, 58, 58),
        ButtonHover = Color.FromArgb(70, 70, 70),
        DangerFill = Color.FromArgb(196, 62, 40),
        DangerHover = Color.FromArgb(176, 46, 26),
        Success = Color.FromArgb(108, 203, 95),
        Warning = Color.FromArgb(252, 185, 65),
        Danger = Color.FromArgb(255, 125, 125)
    };

    private static readonly ConditionalWeakTable<Control, StrongBox<Tone>> Tones = new();
    private static bool? _forced;
    private static bool? _forcedHighContrast;
    private static bool _watching;

    public static bool IsDark { get; private set; } = ReadSystemDark();

    /// <summary>A Windows contrast theme is on: the colours are the system ones, not the app palette.</summary>
    public static bool IsHighContrast { get; private set; } = SystemInformation.HighContrast;

    public static Palette Colors => IsHighContrast ? SystemPalette() : IsDark ? Dark : Light;

    /// <summary>Raised when the Windows app theme or contrast theme changes (possibly off the UI thread).</summary>
    public static event EventHandler? Changed;

    public static void Watch()
    {
        if (_watching) return;
        _watching = true;
        SystemEvents.UserPreferenceChanged += (s, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color) Refresh();
        };
        // The tray reads the theme at startup and opens its windows later: catch a change made in between.
        Refresh();
    }

    /// <summary>Test hook: true/false pins the theme, null follows Windows again.</summary>
    public static void Force(bool? dark)
    {
        _forced = dark;
        Refresh();
    }

    /// <summary>Test hook: true/false pins the contrast theme, null follows Windows again.</summary>
    public static void ForceHighContrast(bool? on)
    {
        _forcedHighContrast = on;
        Refresh();
    }

    private static void Refresh()
    {
        bool dark = _forced ?? ReadSystemDark();
        bool highContrast = _forcedHighContrast ?? SystemInformation.HighContrast;
        if (dark == IsDark && highContrast == IsHighContrast) return;
        IsDark = dark;
        IsHighContrast = highContrast;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // Contrast themes: text, borders and accents from the user's system colours; the state labels keep their symbols.
    private static Palette SystemPalette() => new()
    {
        Window = SystemColors.Window,
        Sidebar = SystemColors.Window,
        Card = SystemColors.Window,
        Border = SystemColors.WindowText,
        Text = SystemColors.WindowText,
        Muted = SystemColors.WindowText,
        Disabled = SystemColors.GrayText,
        Input = SystemColors.Window,
        InputBorder = SystemColors.WindowText,
        InputDisabled = SystemColors.Window,
        ButtonBorder = SystemColors.WindowText,
        Accent = SystemColors.Highlight,
        AccentHover = SystemColors.Highlight,
        OnAccent = SystemColors.HighlightText,
        Button = SystemColors.Window,
        ButtonHover = SystemColors.Window,
        DangerFill = SystemColors.Highlight,
        DangerHover = SystemColors.Highlight,
        Success = SystemColors.WindowText,
        Warning = SystemColors.WindowText,
        Danger = SystemColors.WindowText
    };

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Color ColorOf(Tone tone) => tone switch
    {
        Tone.Muted => Colors.Muted,
        Tone.Success => Colors.Success,
        Tone.Warning => Colors.Warning,
        Tone.Danger => Colors.Danger,
        _ => Colors.Text
    };

    public static void SetTone(Control control, Tone tone)
    {
        Tones.GetOrCreateValue(control).Value = tone;
        control.ForeColor = ColorOf(tone);
    }

    /// <summary>Re-colours the themed controls and the toned labels under <paramref name="root"/>.</summary>
    public static void Apply(Control root)
    {
        if (root is IThemed themed) themed.ApplyTheme();
        if (Tones.TryGetValue(root, out var tone)) root.ForeColor = ColorOf(tone.Value);
        foreach (Control child in root.Controls) Apply(child);
    }

    /// <summary>Dark or light title bar (Windows 10 2004+; attribute 19 on older builds).</summary>
    public static void ApplyWindowChrome(Form form)
    {
        if (!form.IsHandleCreated) return;
        int on = IsDark && !IsHighContrast ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Scroll bars of a native list or edit control in the current theme.</summary>
    public static void ApplyScrollBars(Control control)
    {
        if (control.IsHandleCreated) SetWindowTheme(control.Handle, IsDark && !IsHighContrast ? "DarkMode_Explorer" : null, null);
    }

    public static void Style(ContextMenuStrip menu)
    {
        menu.ShowImageMargin = false;
        menu.BackColor = Colors.Card;
        menu.ForeColor = Colors.Text;
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Colors.Card;
        public override Color MenuBorder => Colors.Border;
        public override Color MenuItemBorder => Colors.ButtonHover;
        public override Color MenuItemSelected => Colors.ButtonHover;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}
