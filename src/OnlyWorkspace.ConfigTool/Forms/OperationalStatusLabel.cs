using System;
using System.Drawing;
using System.Windows.Forms;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>Selectable status with a symbol and a border. Only real work animates, respecting Windows preferences.</summary>
internal sealed class OperationalStatusLabel : SelectableLabel
{
    private readonly Timer _animation = new() { Interval = 100 };
    private int _frame;
    internal StatusLight State { get; private set; }

    public OperationalStatusLabel()
    {
        Padding = new Padding(24, 3, 5, 3);
        _animation.Tick += (_, _) => { RefreshAnimation(); _frame = (_frame + 1) % 12; Invalidate(new Rectangle(0, 0, Padding.Left, Height)); };
    }

    internal void SetState(StatusLight state)
    {
        State = state;
        AccessibleDescription = state switch
        {
            StatusLight.Working => "Operazione in corso",
            StatusLight.Ok => "Operazione riuscita",
            StatusLight.Error => "Errore: consultare il motivo",
            StatusLight.Waiting => "In attesa: nessuna operazione in corso",
            _ => "Informazione"
        };
        RefreshAnimation();
        Invalidate();
    }

    private void RefreshAnimation() => _animation.Enabled = Visible && State == StatusLight.Working &&
        StatusDot.AnimationsEnabled;

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); RefreshAnimation(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var color = SystemInformation.HighContrast ? SystemColors.WindowText : State switch
        {
            StatusLight.Ok => Theme.Colors.Success,
            StatusLight.Error => Theme.Colors.Danger,
            StatusLight.Working => Theme.Colors.Warning,
            _ => Theme.Colors.Muted
        };
        using var pen = new Pen(color);
        e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        int glyphHeight = Ui.Px(this, 14);
        var area = new Rectangle(Ui.Px(this, 4), Math.Max(Ui.Px(this, 3), (Height - glyphHeight) / 2), Math.Max(Ui.Px(this, 10), Padding.Left - Ui.Px(this, 8)), glyphHeight);
        if (State == StatusLight.Working)
        {
            pen.Width = 2;
            e.Graphics.DrawArc(pen, area, _frame * 30, 270);
        }
        else TextRenderer.DrawText(e.Graphics, State == StatusLight.Ok ? "✓" : State == StatusLight.Error ? "!" : State == StatusLight.Waiting ? "…" : "–",
            Font, area, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animation.Dispose();
        base.Dispose(disposing);
    }
}
