using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Microsoft.Win32;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

/// <summary>
/// The Outlook profile is known before Outlook has run with the add-in: the ConfigTool reads it from the registry, so
/// «Le mie cassette» and the setup wizard name it, or say that Outlook is not set up yet, instead of an empty page.
/// </summary>
[Collection(WinFormsCollection.Name)]
public sealed class OutlookProfilesTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string TestRoot = @"Software\OnlyWorkspaceTest";

    [Fact]
    public void Read_DefaultProfileFirst_OnlyProfileAsDefault_NoneWhenMissing()
    {
        WithKey(key =>
        {
            Assert.Empty(OutlookProfiles.Read(key).Names);
            Assert.Null(OutlookProfiles.Read(key).Default);
            Assert.Equal(OutlookProfiles.NotConfigured, OutlookProfiles.Read(key).Describe());

            using (var outlook = Registry.CurrentUser.CreateSubKey(key)) outlook.CreateSubKey(@"Profiles\Outlook").Dispose();
            var only = OutlookProfiles.Read(key);
            Assert.Equal("Outlook", only.Default);
            Assert.Equal("Profilo Outlook: Outlook.", only.Describe());

            using (var outlook = Registry.CurrentUser.CreateSubKey(key))
            {
                outlook.CreateSubKey(@"Profiles\Casa").Dispose();
                outlook.CreateSubKey(@"Profiles\Lavoro").Dispose();
            }
            // Several profiles and no DefaultProfile: Outlook asks which one to open.
            var asked = OutlookProfiles.Read(key);
            Assert.Null(asked.Default);
            Assert.Equal(new[] { "Casa", "Lavoro", "Outlook" }, asked.Names);
            Assert.Contains("chiede quale usare", asked.Describe());

            using (var outlook = Registry.CurrentUser.CreateSubKey(key)) outlook.SetValue("DefaultProfile", "lavoro");
            var chosen = OutlookProfiles.Read(key);
            Assert.Equal("Lavoro", chosen.Default);
            Assert.Equal(new[] { "Lavoro", "Casa", "Outlook" }, chosen.Names);
            Assert.Equal("Profilo Outlook: Lavoro. Altri profili: Casa, Outlook.", chosen.Describe());
        });
    }

    [Fact]
    public void Wizard_StartPage_NamesTheOutlookProfileOrWarns()
    {
        OnSta(() =>
        {
            var profile = new LlmProfileConfig { Id = "a", Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "m" };
            using var wizard = new SetupWizardForm(profile, null, archiveEnabled: true, indexAttachments: true);
            var outlook = new OutlookProfiles(Array.Empty<string>(), null);
            wizard.ReadOutlookProfiles = () => outlook;
            var next = typeof(SetupWizardForm).GetMethod("Next", Flags)!;
            var line = (Label)typeof(SetupWizardForm).GetField("_lblOutlook", Flags)!.GetValue(wizard)!;
            for (int i = 0; i < 3; i++) next.Invoke(wizard, null);
            Assert.Equal(OutlookProfiles.NotConfigured, line.Text);

            // Set up meanwhile: read again when the page is shown.
            outlook = new OutlookProfiles(new[] { "Lavoro" }, "Lavoro");
            next.Invoke(wizard, null);
            Assert.Equal("Profilo Outlook: Lavoro.", line.Text);
            Assert.Contains("Profilo Outlook: Lavoro.", ((Label)typeof(SetupWizardForm).GetField("_lblSummary", Flags)!.GetValue(wizard)!).Text);
        });
    }

    [Fact]
    public void MailboxPanel_WithoutArchive_ShowsTheOutlookProfileAndPolls()
    {
        WithPanel(archiveEnabled: true, (form, panel, setOutlook, setArchives) =>
        {
            setOutlook(new OutlookProfiles(new[] { "Lavoro", "Casa" }, "Lavoro"));
            Load(panel);
            Assert.Equal(new[] { "Lavoro" }, Combo(panel).Items.Cast<string>());
            Assert.Equal(MailboxAuthorizationPanel.ArchiveNotCreated, Status(panel));
            Assert.True(Poll(panel).Enabled);

            setOutlook(new OutlookProfiles(Array.Empty<string>(), null));
            Load(panel);
            Assert.Empty(Combo(panel).Items);
            Assert.Equal(OutlookProfiles.NotConfigured, Status(panel));

            setOutlook(new OutlookProfiles(new[] { "Lavoro" }, "Lavoro"));
            setArchives(() => throw new TimeoutException("The archive engine did not complete this request in time."));
            Load(panel);
            Assert.Equal(new[] { "Lavoro" }, Combo(panel).Items.Cast<string>());
            Assert.StartsWith("Motore dell'archivio non raggiungibile", Status(panel));
            Assert.True(Poll(panel).Enabled);

            // Not on screen: the next tick stops the timer without reading.
            bool asked = false;
            setArchives(() => { asked = true; return Array.Empty<string>(); });
            form.Hide();
            typeof(System.Windows.Forms.Timer).GetMethod("OnTick", Flags)!.Invoke(Poll(panel), new object[] { EventArgs.Empty });
            Application.DoEvents();
            Assert.False(Poll(panel).Enabled);
            Assert.False(asked);
        });
    }

    [Fact]
    public void MailboxPanel_ArchiveNotEnabled_SaysSoInsteadOfUnreachable()
    {
        WithPanel(archiveEnabled: false, (_, panel, setOutlook, setArchives) =>
        {
            bool asked = false;
            setOutlook(new OutlookProfiles(new[] { "Lavoro" }, "Lavoro"));
            setArchives(() => { asked = true; return Array.Empty<string>(); });
            Load(panel);
            Assert.False(asked);
            Assert.Equal(MailboxAuthorizationPanel.ArchiveDisabled, Status(panel));
            Assert.Equal(new[] { "Lavoro" }, Combo(panel).Items.Cast<string>());
        });
    }

    private static ComboBox Combo(Control panel) => (ComboBox)typeof(MailboxAuthorizationPanel).GetField("_profiles", Flags)!.GetValue(panel)!;
    private static string Status(Control panel) => ((Label)typeof(MailboxAuthorizationPanel).GetField("_status", Flags)!.GetValue(panel)!).Text;
    private static System.Windows.Forms.Timer Poll(Control panel) => (System.Windows.Forms.Timer)typeof(MailboxAuthorizationPanel).GetField("_poll", Flags)!.GetValue(panel)!;

    private static void Load(MailboxAuthorizationPanel panel)
    {
        var task = panel.LoadAsync();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until) Application.DoEvents();
        Assert.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private delegate void PanelCheck(Form form, MailboxAuthorizationPanel panel, Action<OutlookProfiles> setOutlook, Action<Func<IReadOnlyList<string>>> setArchives);

    /// <summary>A shown panel with isolated configuration and preferences, fake Outlook profiles and no archive engine.</summary>
    private static void WithPanel(bool archiveEnabled, PanelCheck check)
    {
        OnSta(() =>
        {
            // As under Application.Run: the panel's awaits resume on this thread, where its controls and timer live.
            // Without a message loop DoEvents would swap it for a thread-pool context on its way out.
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-profiles-" + Guid.NewGuid().ToString("N"));
            try
            {
                var settings = new FileConfigurationService(directory, directory);
                settings.SaveConfiguration(new WorkspaceConfig
                {
                    Profiles = new() { new() { Id = "ollama-lan", DisplayName = "Synthetic" } },
                    Archive = new WorkspaceArchiveConfig { Enabled = archiveEnabled }
                });
                var outlook = new OutlookProfiles(Array.Empty<string>(), null);
                Func<IReadOnlyList<string>> archives = () => Array.Empty<string>();
                using var tips = new ToolTip();
                using var form = new Form();
                using var panel = new MailboxAuthorizationPanel(settings, tips, CancellationToken.None, _ => Task.FromResult(archives()), () => outlook);
                form.Controls.Add(panel);
                form.Show();
                Application.DoEvents();
                check(form, panel, o => outlook = o, a => archives = a);
            }
            finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, recursive: true); }
        });
    }

    /// <summary>A key of its own under HKCU\Software\OnlyWorkspaceTest, removed afterwards with the root when empty.</summary>
    private static void WithKey(Action<string> test)
    {
        string key = TestRoot + @"\" + Guid.NewGuid().ToString("N") + @"\Outlook";
        try { test(key); }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key.Substring(0, key.LastIndexOf('\\')), throwOnMissingSubKey: false);
            bool empty;
            using (var root = Registry.CurrentUser.OpenSubKey(TestRoot)) empty = root is { SubKeyCount: 0, ValueCount: 0 };
            if (empty) Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
        }
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "UI test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
