using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;

namespace OnlyWorkspace.Tests;

/// <summary>A198: the profile list shows keyboard focus and long check box labels wrap instead of being cut.</summary>
[Collection(WinFormsCollection.Name)]
public sealed class ThemedControlsPaintTests
{
    private const string Long = "Conserva e rendi cercabile il testo degli allegati leggibili, compresi quelli annidati in altri archivi e messaggi";

    [Fact]
    public void CheckBox_WrapsOnlyWhereTheWidthIsLimited()
    {
        OnSta(() =>
        {
            using var free = new ThemedCheckBox { Text = Long, AutoSize = true };
            using var limited = new ThemedCheckBox { Text = Long, AutoSize = true, MaximumSize = new Size(300, 0) };
            using var shortText = new ThemedCheckBox { Text = "Breve", AutoSize = true, MaximumSize = new Size(300, 0) };
            using var shortFree = new ThemedCheckBox { Text = "Breve", AutoSize = true };

            var oneLine = free.GetPreferredSize(Size.Empty);
            var wrapped = limited.GetPreferredSize(Size.Empty);
            Assert.True(oneLine.Width > 300, "the test text must exceed the limit");
            Assert.Equal(300, wrapped.Width);
            Assert.True(wrapped.Height > oneLine.Height, $"{wrapped.Height} vs {oneLine.Height}");
            Assert.Equal(shortFree.GetPreferredSize(Size.Empty), shortText.GetPreferredSize(Size.Empty));
        });
    }

    [Fact]
    public void CheckBox_PaintsEveryWrappedLine()
    {
        OnSta(() =>
        {
            using var box = new ThemedCheckBox { Text = Long, AutoSize = true, MaximumSize = new Size(300, 0) };
            var size = box.GetPreferredSize(Size.Empty);
            box.Size = size;
            using var bitmap = new Bitmap(size.Width, size.Height);
            box.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
            var background = bitmap.GetPixel(size.Width - 1, size.Height - 1);
            int lowest = -1;
            for (int y = 0; y < size.Height; y++)
                for (int x = 0; x < size.Width; x++)
                    if (bitmap.GetPixel(x, y) != background) { lowest = y; break; }
            // lowest = last row with ink: wrapped text must reach below the first line.
            Assert.Equal(300, size.Width);
            Assert.True(lowest > box.Font.Height * 2, $"last ink row {lowest}, height {size.Height}");
        });
    }

    /// <summary>#075: an option the profile cannot use is drawn empty, yet keeps its saved value for Salva.</summary>
    [Fact]
    public void CheckBox_ShowUnmarked_PaintsEmptyAndKeepsTheSavedValue()
    {
        OnSta(() =>
        {
            using var marked = new ThemedCheckBox { Text = "Briefing", AutoSize = true, Checked = true, Enabled = false };
            using var hidden = new ThemedCheckBox { Text = "Briefing", AutoSize = true, Checked = true, Enabled = false, ShowUnmarked = true };
            using var empty = new ThemedCheckBox { Text = "Briefing", AutoSize = true, Checked = false, Enabled = false };

            Assert.NotEqual(Render(marked), Render(hidden));
            Assert.Equal(Render(empty), Render(hidden));
            Assert.True(hidden.Checked);
            Assert.Equal(CheckState.Checked, hidden.CheckState);

            hidden.ShowUnmarked = false;
            Assert.Equal(Render(marked), Render(hidden));
        });
    }

    private static string Render(Control control)
    {
        var size = control.GetPreferredSize(Size.Empty);
        control.Size = size;
        using var bitmap = new Bitmap(size.Width, size.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < size.Height; y++)
            for (int x = 0; x < size.Width; x++) sb.Append(bitmap.GetPixel(x, y).ToArgb().ToString("x")).Append(',');
        return sb.ToString();
    }

    [Fact]
    public void ProfileList_MarksTheFocusedRowOnlyWhenFocusCuesAreShown()
    {
        OnSta(() =>
        {
            using var list = new ProfileListBox { Width = 200, Height = 60 };
            list.Items.Add("synthetic");
            int plain = Ink(list, DrawItemState.Selected);
            int focused = Ink(list, DrawItemState.Selected | DrawItemState.Focus);
            int hidden = Ink(list, DrawItemState.Selected | DrawItemState.Focus | DrawItemState.NoFocusRect);
            Assert.True(focused > plain, $"{focused} vs {plain}");
            Assert.Equal(plain, hidden);
        });
    }

    private static int Ink(ProfileListBox list, DrawItemState state)
    {
        using var bitmap = new Bitmap(200, 30);
        using (var g = Graphics.FromImage(bitmap))
        {
            var args = new DrawItemEventArgs(g, list.Font, new Rectangle(0, 0, 200, 30), 0, state);
            typeof(ProfileListBox).GetMethod("OnDrawItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(list, new object[] { args });
        }
        var back = list.BackColor;
        int count = 0;
        // Top edge of the row: the selection fill and the outline both live there, the plain list leaves it clear.
        for (int x = 4; x < 190; x++)
            if (bitmap.GetPixel(x, 1).ToArgb() != back.ToArgb() || bitmap.GetPixel(x, 0).ToArgb() != back.ToArgb()) count++;
        // Accent pixels, to tell the outline from the grey selection fill.
        var accent = Theme.Colors.Accent;
        int accentPixels = 0;
        for (int x = 4; x < 190; x++)
            for (int y = 0; y < 3; y++)
            {
                var p = bitmap.GetPixel(x, y);
                if (Math.Abs(p.R - accent.R) + Math.Abs(p.G - accent.G) + Math.Abs(p.B - accent.B) < 60) accentPixels++;
            }
        return count * 1000 + accentPixels;
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "UI test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
