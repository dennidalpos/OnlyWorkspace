using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>A182: synthetic coverage of ConfigToolLauncher (fake executables beside the test assembly) and UiThreadWatch.</summary>
public sealed class OutlookLauncherAndWatchTests
{
    [Fact]
    public void ConfigToolLauncher_ReportsMissingAndBrokenExecutables_AndStartsAValidOne()
    {
        string dir = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_Launcher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string missing = Path.Combine(dir, "missing.exe");
            string broken = Path.Combine(dir, "broken.exe");
            string valid = Path.Combine(dir, "valid.exe");
            File.WriteAllText(broken, "not an executable");
            File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), valid);

            Assert.False(ConfigToolLauncher.TryLaunch(new[] { missing }, dir, false, false));
            Assert.False(ConfigToolLauncher.TryLaunch(new[] { missing, broken }, dir, true, false));
            Assert.True(ConfigToolLauncher.TryLaunch(new[] { missing, valid }, dir, false, true));
        }
        finally
        {
            // The started process may still hold the copy for a moment.
            for (int i = 0; i < 50 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, true); }
                catch (IOException) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            }
        }
    }

    [Fact]
    public void UiThreadWatch_Activity_NestsRestoresAndToleratesDoubleDispose()
    {
        Assert.Null(UiThreadWatch.CurrentActivity);
        using (UiThreadWatch.Activity("outer"))
        {
            Assert.Equal("outer", UiThreadWatch.CurrentActivity);
            var inner = UiThreadWatch.Activity("inner");
            Assert.Equal("inner", UiThreadWatch.CurrentActivity);
            inner.Dispose();
            Assert.Equal("outer", UiThreadWatch.CurrentActivity);
            inner.Dispose();
            Assert.Equal("outer", UiThreadWatch.CurrentActivity);
        }
        Assert.Null(UiThreadWatch.CurrentActivity);
    }

    [Fact]
    public void UiThreadWatch_ActivityNames_AreKeptPerThread()
    {
        using var outer = UiThreadWatch.Activity("main thread work");
        string? seenElsewhere = "unset";
        var other = new Thread(() => seenElsewhere = UiThreadWatch.CurrentActivity);
        other.Start();
        other.Join();
        Assert.Null(seenElsewhere);
        Assert.Equal("main thread work", UiThreadWatch.CurrentActivity);
    }

    [Fact]
    public void UiThreadWatch_StartBeatsAndSurvivesAStall_ThenStops()
    {
        RunOnUiThread(async () =>
        {
            UiThreadWatch.Start();
            UiThreadWatch.Start(); // a second call is ignored
            try
            {
                await Task.Delay(350);
                Assert.True(UiThreadWatch.BeatAgeMs >= 0);

                // Hold the UI thread past the stall limit with a named activity, then with none: the watcher logs, never throws.
                using (UiThreadWatch.Activity("test stall")) Thread.Sleep(450);
                await Task.Delay(250);
                Thread.Sleep(450);
                await Task.Delay(250);
                Assert.True(UiThreadWatch.BeatAgeMs >= 0);
            }
            finally { UiThreadWatch.Stop(); }
        });
    }
}
