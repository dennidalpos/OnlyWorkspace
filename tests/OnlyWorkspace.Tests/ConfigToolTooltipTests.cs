using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

/// <summary>Every command and setting of the ConfigTool says what it is for, also on the tray's archive window and the wizard.</summary>
[Collection(WinFormsCollection.Name)]
public sealed class ConfigToolTooltipTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void OnSta(Action<string> action)
    {
        Exception? error = null;
        string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-tips-" + Guid.NewGuid());
        var thread = new Thread(() =>
        {
            try { action(directory); }
            catch (Exception ex) { error = ex; }
            finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "UI test did not complete.");
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

    /// <summary>Controls the user acts on: buttons, boxes, lists, fields that can be typed in.</summary>
    private static bool Interactive(Control c) =>
        c is ButtonBase or ComboBox or ListBox or NumericUpDown or DateTimePicker or DataGridView ||
        c is TextBoxBase { ReadOnly: false } && c.Parent is not NumericUpDown && c.Parent is not SelectableLabel;

    private static string Name(Control c) => $"{c.GetType().Name} «{c.Text}» in {c.Parent?.GetType().Name}";

    private static void AssertEveryControlHasATip(Control root, ToolTip tips)
    {
        var missing = Descendants(root).Where(Interactive)
            // The inner edit box of an InputFrame shows its frame's tip and vice versa: either is enough.
            .Where(c => string.IsNullOrWhiteSpace(tips.GetToolTip(c)) && string.IsNullOrWhiteSpace(tips.GetToolTip(c.Parent!)))
            .Select(Name).ToList();
        Assert.True(missing.Count == 0, "Senza tooltip: " + string.Join("; ", missing));
    }

    private static ToolTip Tips(object form) => (ToolTip)form.GetType().GetField("_tips", Flags)!.GetValue(form)!;

    [Fact]
    public void ContrastTheme_UsesSystemColours_AndAnOpenWindowFollowsTheSwitch()
    {
        OnSta(directory =>
        {
            int changes = 0;
            EventHandler counter = (_, _) => changes++;
            Theme.Changed += counter;
            try
            {
                Theme.Force(true);
                Theme.ForceHighContrast(false);
                using var form = new MainConfigForm(directory);
                Assert.NotEqual(System.Drawing.SystemColors.Window, form.BackColor);
                changes = 0;
                // Only the contrast theme changes: the open window still takes the system colours.
                Theme.ForceHighContrast(true);
                Application.DoEvents();
                Assert.Equal(1, changes);
                Assert.Equal(System.Drawing.SystemColors.Window, Theme.Colors.Window);
                Assert.Equal(System.Drawing.SystemColors.WindowText, Theme.Colors.Text);
                Assert.Equal(System.Drawing.SystemColors.Window, form.BackColor);
            }
            finally
            {
                Theme.Changed -= counter;
                Theme.ForceHighContrast(null);
                Theme.Force(null);
            }
        });
    }

    [Fact]
    public void SelectingAnotherProfile_DropsThePreviousProbeStatus()
    {
        OnSta(directory =>
        {
            var config = new WorkspaceConfig { DefaultProfile = "a", Profiles = new()
            {
                new LlmProfileConfig { Id = "a", DisplayName = "A", Model = "synthetic" },
                new LlmProfileConfig { Id = "b", DisplayName = "B", Model = "synthetic" }
            } };
            var service = new FileConfigurationService(directory, directory);
            service.SaveConfiguration(config); service.SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, null, directory);
            typeof(MainConfigForm).GetMethod("SetProbeStatus", Flags)!.Invoke(form,
                new object?[] { "Prova precedente completata", Tone.Success, null });
            ((ListBox)typeof(MainConfigForm).GetField("_lstProfiles", Flags)!.GetValue(form)!).SelectedIndex = 1;
            var badge = (OperationalStatusLabel)typeof(MainConfigForm).GetField("_lblProbeStatus", Flags)!.GetValue(form)!;
            Assert.Equal(OnlyWorkspace.Core.Archive.StatusLight.Off, badge.State);
            Assert.DoesNotContain("precedente", badge.Text);
        });
    }

    [Fact]
    public void StatusBadge_KeepsSelectableTextAndStopsAnimationWhenWaiting()
    {
        OnSta(_ =>
        {
            using var badge = new OperationalStatusLabel { Text = "Esito completo con motivo", Size = new System.Drawing.Size(300, 40) };
            badge.CreateControl();
            badge.SetState(OnlyWorkspace.Core.Archive.StatusLight.Working);
            Assert.Contains("in corso", badge.AccessibleDescription);
            badge.SetState(OnlyWorkspace.Core.Archive.StatusLight.Waiting);
            var animation = (System.Windows.Forms.Timer)typeof(OperationalStatusLabel).GetField("_animation", Flags)!.GetValue(badge)!;
            Assert.False(animation.Enabled);
            Assert.Equal("Esito completo con motivo", badge.Controls.OfType<TextBox>().Single().Text);
            Assert.True(badge.Controls.OfType<TextBox>().Single().ReadOnly);
            Assert.Contains("nessuna operazione", badge.AccessibleDescription);
            using var pixels = new System.Drawing.Bitmap(badge.Width, badge.Height);
            badge.DrawToBitmap(pixels, badge.ClientRectangle);
            badge.SetState(OnlyWorkspace.Core.Archive.StatusLight.Error);
            Assert.Equal("Esito completo con motivo", badge.Text);
            Assert.Contains("Errore", badge.AccessibleDescription);
        });
    }

    [Fact]
    public void TrayGroups_StatusUpdatesReuseImages_AndIaIsUnverifiedAfterConfigurationChanges()
    {
        OnSta(directory =>
        {
            new FileConfigurationService(directory, directory).SaveConfiguration(new WorkspaceConfig
            {
                DefaultProfile = "test", Profiles = new() { new LlmProfileConfig { Id = "test", DisplayName = "Profilo sintetico", Model = "synthetic" } }
            });
            using var tray = new OnlyWorkspace.ConfigTool.Tray.TrayApplicationContext(directory, true,
                preferencesDir: directory, archiveDir: Path.Combine(directory, "archive"));
            var type = tray.GetType();
            var menu = (ContextMenuStrip)type.GetField("_contextMenu", Flags)!.GetValue(tray)!;
            Assert.Equal(new[] { "Archivio", "IA", "Configurazione", "Esci da OnlyWorkspace" }, menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text));
            var set = type.GetMethod("SetArchiveStatus", Flags)!;
            var archive = (ToolStripMenuItem)type.GetField("_itemArchiveStatus", Flags)!.GetValue(tray)!;
            set.Invoke(tray, new object[] { OnlyWorkspace.Core.Archive.StatusLight.Error, "Errore sintetico" });
            var image = archive.Image;
            set.Invoke(tray, new object[] { OnlyWorkspace.Core.Archive.StatusLight.Error, "Errore ancora presente" });
            Assert.Same(image, archive.Image);
            Assert.Equal("Errore ancora presente", archive.Text);
            tray.UpdateProfileStatus();
            var ia = (ToolStripMenuItem)type.GetField("_itemStatus", Flags)!.GetValue(tray)!;
            Assert.Contains("non verificata", ia.Text);
            Assert.Contains("Profilo sintetico", ia.Text);
        });
    }

    [Fact]
    public void MainConfigForm_EveryPageCommandAndSetting()
    {
        OnSta(directory =>
        {
            var config = new WorkspaceConfig
            {
                DefaultProfile = "a",
                Profiles = new() { new LlmProfileConfig { Id = "a", DisplayName = "a", Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "m" } }
            };
            new FileConfigurationService(directory, directory).SaveConfiguration(config);
            new FileConfigurationService(directory, directory).SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, null, directory);
            // Pages filled on first display (Informazioni) are built here too.
            var pages = (List<Control>)typeof(MainConfigForm).GetField("_pages", Flags)!.GetValue(form)!;
            for (int i = 0; i < pages.Count; i++) typeof(MainConfigForm).GetMethod("ShowPage", Flags)!.Invoke(form, new object[] { i });
            AssertEveryControlHasATip(form, Tips(form));
        });
    }

    [Fact]
    public void WizardArchiveWindowAndNotice_EveryControl()
    {
        OnSta(_ =>
        {
            var profile = new LlmProfileConfig { Id = "a", Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "m" };
            using (var wizard = new SetupWizardForm(profile, null, archiveEnabled: true, indexAttachments: true)) AssertEveryControlHasATip(wizard, Tips(wizard));
            using (var archive = new ArchiveSearchForm(refreshOnShow: false)) AssertEveryControlHasATip(archive, Tips(archive));
            using var notice = new DataEgressConsentForm(new[] { new OnlyWorkspace.Core.Privacy.DataEgressProvider { Host = "cloud.example" } }, null);
            AssertEveryControlHasATip(notice, Tips(notice));
        });
    }

    [Fact]
    public void Notice_TallerThanTheSpaceBelowTheOwner_StaysInsideTheScreen()
    {
        // 1080p at 100%: the 992 px notice centered on the ConfigTool went 200 px under the taskbar.
        var area = new System.Drawing.Rectangle(1920, 0, 1920, 1032);
        var owner = new System.Drawing.Rectangle(1970, 30, 900, 640);
        var at = DataEgressConsentForm.Placement(area, owner, new System.Drawing.Size(634, 992));
        Assert.Equal(new System.Drawing.Point(2103, 0), at);
        Assert.Equal(new System.Drawing.Point(2103, 135), DataEgressConsentForm.Placement(area, owner, new System.Drawing.Size(634, 430)));
    }

    [Fact]
    public void Notice_AcceptsOnlyCompleteDataAndConfirmedReading()
    {
        OnSta(_ =>
        {
            // The server's declarations come prefilled from an earlier acceptance; the company's are typed here.
            var declared = new OnlyWorkspace.Core.Privacy.DataEgressProvider
            {
                Host = "cloud.example", Provider = "Fornitore", ProcessingAgreement = true, Region = "UE",
                TransferBasis = OnlyWorkspace.Core.Privacy.DataEgress.TransferBases[0], NoTraining = true, Retention = "nessuna"
            };
            using var notice = new DataEgressConsentForm(new[] { declared }, null);
            var accept = (Button)typeof(DataEgressConsentForm).GetField("_accept", Flags)!.GetValue(notice)!;
            Assert.False(accept.Enabled);
            foreach (var (field, value) in new[] { ("_company", "Esempio Srl"), ("_firstName", "Anna"), ("_lastName", "Bianchi"), ("_role", "DPO"), ("_email", "anna@esempio.example"), ("_dpia", "DPIA-1") })
                ((TextBox)typeof(DataEgressConsentForm).GetField(field, Flags)!.GetValue(notice)!).Text = value;
            ((ComboBox)typeof(DataEgressConsentForm).GetField("_legalBasis", Flags)!.GetValue(notice)!).SelectedIndex = 2;
            foreach (var field in new[] { "_workers", "_literacy" })
                ((CheckBox)typeof(DataEgressConsentForm).GetField(field, Flags)!.GetValue(notice)!).Checked = true;
            Assert.False(accept.Enabled);
            ((CheckBox)typeof(DataEgressConsentForm).GetField("_read", Flags)!.GetValue(notice)!).Checked = true;
            Assert.True(accept.Enabled);
            Assert.Equal("Anna", notice.Signer.FirstName);
            Assert.Equal("Fornitore", Assert.Single(notice.Providers).Provider);
            ((TextBox)typeof(DataEgressConsentForm).GetField("_email", Flags)!.GetValue(notice)!).Text = "anna";
            Assert.False(accept.Enabled);
        });
    }
}
