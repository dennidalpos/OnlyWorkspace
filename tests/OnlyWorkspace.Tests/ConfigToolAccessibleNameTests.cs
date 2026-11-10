using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

/// <summary>INT03 (#073): every control the user acts on exposes a non-empty accessible name, the one a screen reader reads.</summary>
[Collection(WinFormsCollection.Name)]
public sealed class ConfigToolAccessibleNameTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void OnSta(Action<string> action)
    {
        Exception? error = null;
        string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-a11y-" + Guid.NewGuid());
        var thread = new Thread(() =>
        {
            try { action(directory); }
            catch (Exception ex) { error = ex; }
            finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "UI test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>Controls the user acts on; the inner edit box of a spin box or selectable label is named by its owner.</summary>
    private static bool Interactive(Control c) =>
        c is ButtonBase or ComboBox or ListBox or NumericUpDown or DateTimePicker or DataGridView ||
        c is TextBoxBase { ReadOnly: false } && c.Parent is not NumericUpDown && c.Parent is not SelectableLabel;

    private static void Offscreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000);
        form.Show();
        Application.DoEvents();
    }

    private static List<string> Unnamed(Control root) => Descendants(root).Where(Interactive)
        .Where(c => string.IsNullOrWhiteSpace(c.AccessibilityObject.Name))
        .Select(c => $"{c.GetType().Name} {c.Name} «{c.Text}» in {c.Parent?.GetType().Name} {c.Parent?.Name}").Distinct().ToList();

    [Fact]
    public void MainConfigForm_EveryPage_NamesEveryControl()
    {
        OnSta(directory =>
        {
            var service = new FileConfigurationService(directory, directory);
            service.SaveConfiguration(new WorkspaceConfig
            {
                DefaultProfile = "a",
                Profiles = new()
                { new LlmProfileConfig { Id = "a", DisplayName = "Profilo sintetico", Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "m" } }
            });
            service.SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, null, directory);
            Offscreen(form);
            var pages = (List<Control>)typeof(MainConfigForm).GetField("_pages", Flags)!.GetValue(form)!;
            var missing = new List<string>();
            for (int i = 0; i < pages.Count; i++)
            {
                typeof(MainConfigForm).GetMethod("ShowPage", Flags)!.Invoke(form, new object[] { i });
                Application.DoEvents();
                missing.AddRange(Unnamed(pages[i]).Select(m => $"pagina {i + 1}: {m}"));
            }
            Assert.True(missing.Count == 0, "Senza nome accessibile: " + string.Join("; ", missing.Distinct()));
        });
    }

    [Fact]
    public void WizardArchiveWindowAndNotice_NameEveryControl()
    {
        OnSta(_ =>
        {
            var missing = new List<string>();
            var profile = new LlmProfileConfig { Id = "a", Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "m" };
            using (var wizard = new SetupWizardForm(profile, null, archiveEnabled: true, indexAttachments: true))
            {
                Offscreen(wizard);
                var steps = (System.Collections.IList)typeof(SetupWizardForm).GetField("_steps", Flags)!.GetValue(wizard)!;
                for (int i = 0; i < steps.Count; i++)
                {
                    typeof(SetupWizardForm).GetMethod("ShowStep", Flags)!.Invoke(wizard, new object[] { i });
                    Application.DoEvents();
                    missing.AddRange(Unnamed(wizard).Select(m => $"procedura passo {i + 1}: {m}"));
                }
            }
            using (var archive = new ArchiveSearchForm(false, IsolatedArchive.NewPath()))
            {
                Offscreen(archive);
                var toggle = Descendants(archive).OfType<Button>().Single(b => b.Name == "FactFiltersToggle");
                Assert.Equal("Filtri su importi e scadenze", toggle.AccessibilityObject.Name);
                Assert.Equal("Compresso", toggle.AccessibilityObject.Description);
                toggle.PerformClick();
                Assert.Equal("Espanso", toggle.AccessibilityObject.Description);
                missing.AddRange(Unnamed(archive).Select(m => "archivio: " + m));
            }
            using (var notice = new DataEgressConsentForm())
            {
                Offscreen(notice);
                missing.AddRange(Unnamed(notice).Select(m => "avviso: " + m));
            }
            Assert.True(missing.Count == 0, "Senza nome accessibile: " + string.Join("; ", missing.Distinct()));
        });
    }
}
