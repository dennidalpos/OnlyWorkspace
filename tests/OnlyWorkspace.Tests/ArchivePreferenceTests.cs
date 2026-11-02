using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

public sealed class ArchivePreferenceTests
{
    [Fact]
    public void ConfigTool_UnreadableChoiceAndFailedSave_AreIndeterminateUntilReread()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ow-preference-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunOnUiThread(() =>
            {
                var service = new FileConfigurationService(directory, directory);
                service.SetUserFlag(ArchiveScope.UserOffFlag, true);
                var type = typeof(OnlyWorkspace.ConfigTool.Forms.MainConfigForm).Assembly.GetType("OnlyWorkspace.ConfigTool.Forms.MailboxAuthorizationPanel")!;
                using var tips = new System.Windows.Forms.ToolTip();
                System.Windows.Forms.Control panel;
                using (new FileStream(Path.Combine(directory, "preferences.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    panel = (System.Windows.Forms.Control)Activator.CreateInstance(type, service, tips, CancellationToken.None)!;
                    var mine = (System.Windows.Forms.CheckBox)type.GetField("_mine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;
                    Assert.Equal(System.Windows.Forms.CheckState.Indeterminate, mine.CheckState);
                    mine.CheckState = System.Windows.Forms.CheckState.Checked;
                    Assert.Equal(System.Windows.Forms.CheckState.Indeterminate, mine.CheckState);
                    var status = (System.Windows.Forms.Control)type.GetField("_status", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;
                    Assert.Contains("Scelta non salvata", status.Text);
                }
                using (panel)
                {
                    type.GetMethod("RefreshUserPreference", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(panel, null);
                    var mine = (System.Windows.Forms.CheckBox)type.GetField("_mine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;
                    Assert.Equal(System.Windows.Forms.CheckState.Unchecked, mine.CheckState);
                    Assert.True(service.GetUserFlag(ArchiveScope.UserOffFlag));
                }
                return Task.CompletedTask;
            });
        }
        finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, true); }
    }

    [Fact]
    public void ConfigTool_IndeterminateChoice_IsNotDrawnAsTicked()
    {
        RunOnUiThread(() =>
        {
            var type = typeof(OnlyWorkspace.ConfigTool.Forms.MainConfigForm).Assembly.GetType("OnlyWorkspace.ConfigTool.Forms.ThemedCheckBox")!;
            using var box = (System.Windows.Forms.CheckBox)Activator.CreateInstance(type, nonPublic: true)!;
            box.Text = "Archivio attivo per me";
            box.Size = box.GetPreferredSize(System.Drawing.Size.Empty);
            string Draw(System.Windows.Forms.CheckState state)
            {
                box.CheckState = state;
                using var bitmap = new System.Drawing.Bitmap(box.Width, box.Height);
                box.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, box.Size));
                var pixels = new System.Text.StringBuilder();
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < box.Height; x++) pixels.Append(bitmap.GetPixel(x, y).ToArgb()).Append(',');
                return pixels.ToString();
            }
            string indeterminate = Draw(System.Windows.Forms.CheckState.Indeterminate);
            Assert.NotEqual(Draw(System.Windows.Forms.CheckState.Checked), indeterminate);
            Assert.NotEqual(Draw(System.Windows.Forms.CheckState.Unchecked), indeterminate);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void UnreadableArchiveChoice_DoesNotStartCollection_AndRecovers()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ow-preference-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunOnUiThread(async () =>
            {
                var service = new FileConfigurationService(directory, directory);
                service.SaveConfiguration(new WorkspaceConfig { DefaultProfile = "p", Profiles = new() { new() { Id = "p", Model = "synthetic" } } });
                service.SetUserFlag(ArchiveScope.UserOffFlag, true);
                var outlook = new FakeOutlook(new[] { Header("e", FakeOutlook.Newest) }, new());
                using var indexer = new ArchiveIndexer(outlook, service, directory, idleMs: () => int.MaxValue,
                    launchEngine: () => false, timerTicks: false);
                using (new FileStream(Path.Combine(directory, "preferences.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    await indexer.TickAsync();
                    Assert.Null(await indexer.OpenForSearchAsync());
                    Assert.Empty(outlook.Readers);
                }
                Assert.True(service.GetUserFlag(ArchiveScope.UserOffFlag));
                Assert.Null(await indexer.OpenForSearchAsync());
                Assert.Equal(0, outlook.OffUiThread);
            });
        }
        finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, true); }
    }
}
