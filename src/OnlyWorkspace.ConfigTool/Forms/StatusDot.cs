using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>Traffic-light frames for menus: callers cache them and animate only real work.</summary>
internal static class StatusDot
{
    internal static bool AnimationsEnabled => !SystemInformation.HighContrast && SystemParametersInfo(0x1042, 0, out bool enabled, 0) && enabled;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

    /// <summary>Shape as well as colour: circle ok, ring working, square error, grey bars paused, grey dot waiting, hollow grey off.</summary>
    public static Bitmap Image(StatusLight light, int size = 16, int frame = 0)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = Theme.Colors;
        var rect = new Rectangle(2, 2, size - 4, size - 4);
        switch (light)
        {
            case StatusLight.Ok:
                using (var b = new SolidBrush(c.Success)) g.FillEllipse(b, rect);
                using (var p = new Pen(Color.White, 1.6f)) g.DrawLines(p, new[] { new Point(size / 4, size / 2), new Point(size * 7 / 16, size * 11 / 16), new Point(size * 3 / 4, size / 3) });
                break;
            case StatusLight.Working:
                using (var p = new Pen(c.Warning, System.Math.Max(2, size / 8))) g.DrawArc(p, rect, frame * 30, 270);
                break;
            case StatusLight.Error:
                using (var b = new SolidBrush(c.Danger)) g.FillRectangle(b, rect);
                using (var p = new Pen(Color.White, 1.6f))
                {
                    g.DrawLine(p, size / 2, size / 4, size / 2, size * 9 / 16);
                    g.DrawLine(p, size / 2, size * 11 / 16, size / 2, size * 12 / 16);
                }
                break;
            case StatusLight.Paused:
                using (var b = new SolidBrush(c.Muted))
                {
                    int bar = System.Math.Max(2, size / 8);
                    g.FillRectangle(b, rect.Left, rect.Top, bar, rect.Height);
                    g.FillRectangle(b, rect.Right - bar, rect.Top, bar, rect.Height);
                }
                break;
            case StatusLight.Waiting:
                using (var p = new Pen(c.Muted, 1)) g.DrawEllipse(p, rect);
                using (var b = new SolidBrush(c.Muted)) g.FillEllipse(b, size / 2 - size / 8, size / 2 - size / 8, size / 4, size / 4);
                break;
            default:
                using (var p = new Pen(c.Muted, 1)) g.DrawEllipse(p, rect);
                break;
        }
        return bitmap;
    }
}
