using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

/// <summary>
/// On a move between monitors WinForms rescales the form font, and net48 rescales explicit child fonts by a
/// different factor; fonts set with Ui.SetFont must keep their proportion to the form font either way.
/// </summary>
[Collection(WinFormsCollection.Name)]
public sealed class DpiFontTests
{
    [Fact]
    public void CapabilityCommands_RemainInsideTheViewportAcrossFontScales()
    {
        OnSta(() => WithConfigurator(form =>
        {
            foreach (float scale in new[] { 1f, 1.25f, 1.5f })
            {
                form.Font = new Font("Segoe UI", 9f * scale);
                form.ClientSize = new Size((int)(820 * scale), (int)(600 * scale));
                Descendants(form).OfType<NavButton>().Single(b => b.Text == "Capacità e verifiche").PerformClick();
                Application.DoEvents();
                var buttons = Descendants(form).OfType<Button>().Where(b => b.Text is "Aggiorna capacità" or "Prova funzioni" or "Annulla").ToList();
                Assert.Equal(3, buttons.Count);
                foreach (var button in buttons)
                {
                    Assert.True(button.Left >= 0 && button.Right <= button.Parent!.ClientSize.Width,
                        $"{button.Text} extends outside its row at font scale {scale}");
                    Assert.True(button.Parent!.Right <= button.Parent.Parent!.ClientSize.Width,
                        $"Command row extends outside the capability viewport at font scale {scale}");
                }
                var web = Descendants(form).OfType<CheckBox>().Single(c => c.Text == "Prova anche la Ricerca web");
                Assert.True(web.Right <= web.Parent!.ClientSize.Width);
                Assert.True(web.Width >= web.GetPreferredSize(Size.Empty).Width);
            }
        }));
    }

    [Fact]
    public void RowsCreatedAfterScaling_UseTheCurrentContainerScaleOnce()
    {
        OnSta(() =>
        {
            foreach (float points in new[] { 9f, 11.25f, 13.5f, 9f })
            {
                using var parent = new Panel { Font = new Font("Segoe UI", points) };
                using var row = new FlowLayoutPanel();
                var field = new TextBox { Width = 360 };
                var button = new Button { MinimumSize = new Size(0, 26), Padding = new Padding(8, 0, 8, 0) };
                row.Controls.Add(field); row.Controls.Add(button);
                float scale = parent.Font.Height / 16f;
                Ui.ScaleDynamic(row, parent);
                // The native text box border keeps its physical pixels when Control.Scale scales the client area.
                Assert.InRange(field.Width, (int)Math.Round(360 * scale) - 3, (int)Math.Round(360 * scale) + 3);
                Assert.InRange(button.MinimumSize.Height, (int)Math.Round(26 * scale) - 1, (int)Math.Round(26 * scale) + 1);
            }
        });
    }

    [Fact]
    public void RoleFonts_FollowTheFormFont_AcrossDpiChanges()
    {
        OnSta(() => WithConfigurator(form =>
        {
            var expected = Roles(form);
            Assert.Contains(expected, role => role.Text == "OnlyWorkspace" && Math.Abs(role.Ratio - 11f / 9f) < 0.01f);

            // 96 -> 144 -> 96 DPI as net48 sees it: system-DPI fonts shrink to 6 pt on a 96 DPI monitor and grow back.
            foreach (float size in new[] { 6f, 9f, 6f })
            {
                // net48 also scales explicit child fonts on its own, by the window's DPI ratio.
                foreach (var label in Descendants(form).OfType<SelectableLabel>().Where(label => label.Text == "OnlyWorkspace"))
                    label.Font = new Font(label.Font.FontFamily, label.Font.Size * 1.5f, label.Font.Style);
                form.Font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point);
                Application.DoEvents();
                foreach (var role in expected)
                {
                    float ratio = role.Control.Font.Size / form.Font.Size;
                    Assert.True(Math.Abs(role.Ratio - ratio) < 0.01f,
                        $"«{role.Text}» at {size} pt: {ratio:0.###} instead of {role.Ratio:0.###}.");
                    Assert.Equal(role.Style, role.Control.Font.Style);
                    Assert.Equal(role.Family, role.Control.Font.Name);
                }
            }
        }));
    }

    private sealed record Role(Control Control, string Text, float Ratio, FontStyle Style, string Family);

    /// <summary>Labels whose font differs from the form font, as a ratio of the form font size.</summary>
    private static List<Role> Roles(Form form) => Descendants(form).OfType<SelectableLabel>()
        .Where(label => label.Font.Size != form.Font.Size || label.Font.Style != form.Font.Style || label.Font.Name != form.Font.Name)
        .Select(label => new Role(label, label.Text, label.Font.Size / form.Font.Size, label.Font.Style, label.Font.Name))
        .ToList();

    private static void WithConfigurator(Action<MainConfigForm> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-dpi-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new FileConfigurationService(directory, directory);
            settings.SaveConfiguration(new WorkspaceConfig { Profiles = new() { new() { Id = "ollama-lan", DisplayName = "Synthetic" } } });
            settings.SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, trayContext: null, preferencesDir: directory);
            form.Show();
            Application.DoEvents();
            check(form);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "DPI font test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
