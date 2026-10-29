using System;
using System.Drawing;
using System.Threading;
using OnlyWorkspace.ConfigTool.Services;
using Xunit;

namespace OnlyWorkspace.Tests;

public class TrayIntegrationTests
{
    [Fact]
    public void AppIconHelper_EmbeddedIcon_HasEveryShellSize()
    {
        using var stream = typeof(AppIconHelper).Assembly.GetManifestResourceStream("OnlyWorkspace.ConfigTool.AppIcon.ico");
        Assert.NotNull(stream);
        using var reader = new System.IO.BinaryReader(stream!);
        reader.ReadUInt32();
        int count = reader.ReadUInt16();
        var sizes = new int[count];
        for (int i = 0; i < count; i++)
        {
            int width = reader.ReadByte();
            sizes[i] = width == 0 ? 256 : width;
            reader.ReadBytes(15);
        }
        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 256 }, sizes);
    }

    [Fact]
    public void AppIconHelper_GetTrayIcon_ShouldReturnNonNullIcon()
    {
        var icon = AppIconHelper.GetTrayIcon();
        Assert.True(icon.Width > 0);
    }

    [Fact]
    public void AppIconHelper_GetAppIcon_ShouldReturnNonNullIcon()
    {
        var icon = AppIconHelper.GetAppIcon();
        Assert.NotNull(icon);
        Assert.True(icon.Width > 0);
        Assert.True(icon.Height > 0);
    }

    [Fact]
    public void StartupManager_GetExecutablePath_ShouldReturnValidPath()
    {
        string path = StartupManager.GetExecutablePath();
        Assert.False(string.IsNullOrWhiteSpace(path));
    }

    [Fact]
    public void StartupManager_AppRegistrationName_MatchesExpected()
    {
        Assert.Equal("OnlyWorkspaceTray", StartupManager.AppRegistrationName);
    }

    [Fact]
    public void SingleInstanceManager_LifecycleAndSignaling_ShouldFunctionCorrectly()
    {
        // Unique scope: does not collide with a running ConfigTool.
        string scope = "Test_" + Guid.NewGuid().ToString("N");
        bool primaryStarted = SingleInstanceManager.TryStartPrimary(out var primaryManager, scope);
        Assert.True(primaryStarted);
        Assert.NotNull(primaryManager);

        try
        {
            bool secondStarted = SingleInstanceManager.TryStartPrimary(out var secondManager, scope);
            Assert.False(secondStarted);
            Assert.Null(secondManager);

            bool signaled = false;
            using var resetEvent = new ManualResetEventSlim(false);

            primaryManager!.ShowWindowRequested += () =>
            {
                signaled = true;
                resetEvent.Set();
            };

            bool signalSent = SingleInstanceManager.SignalPrimaryInstance(scope);
            Assert.True(signalSent);

            bool received = resetEvent.Wait(TimeSpan.FromSeconds(3));
            Assert.True(received);
            Assert.True(signaled);
        }
        finally
        {
            primaryManager?.Dispose();
        }

        bool newPrimaryStarted = SingleInstanceManager.TryStartPrimary(out var subsequentManager, scope);
        Assert.True(newPrimaryStarted);
        subsequentManager?.Dispose();
    }

    [Fact]
    public void SingleInstanceManager_SignalBeforeSubscription_IsDelivered()
    {
        string scope = "Test_" + Guid.NewGuid().ToString("N");
        Assert.True(SingleInstanceManager.TryStartPrimary(out var primary, scope));
        using (primary)
        {
            Assert.True(SingleInstanceManager.SignalPrimaryInstance(scope));
            using var received = new ManualResetEventSlim(false);
            Thread.Sleep(100);
            primary!.ShowWindowRequested += () => received.Set();
            Assert.True(received.Wait(TimeSpan.FromSeconds(3)));
        }
    }

    [Theory]
    [InlineData(true, false, "--tray")]
    [InlineData(false, false, "")]
    [InlineData(false, true, "--page mailboxes")]
    public void ConfigToolLauncher_NeverStartsHidden(bool startMinimized, bool mailboxes, string arguments)
    {
        // A hidden start would hide the first configuration window the tray opens (B1).
        var info = OnlyWorkspace.Outlook.Services.ConfigToolLauncher.StartInfo(@"C:\Program Files\OnlyWorkspace\OnlyWorkspace.ConfigTool.exe", startMinimized, mailboxes);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Normal, info.WindowStyle);
        Assert.Equal(arguments, info.Arguments);
        Assert.Equal(SingleInstanceManager.MailboxesPage, OnlyWorkspace.Outlook.Services.ConfigToolLauncher.MailboxesPage);
    }

    [Fact]
    public void SingleInstanceManager_PageRequestReachesTheRunningInstanceOnce()
    {
        string scope = "Test_" + Guid.NewGuid().ToString("N");
        Assert.True(SingleInstanceManager.TryStartPrimary(out var primary, scope));
        using (primary)
        {
            var pages = new System.Collections.Concurrent.ConcurrentQueue<string?>();
            using var received = new SemaphoreSlim(0);
            primary!.ShowWindowRequested += () => { pages.Enqueue(primary.TakeRequestedPage()); received.Release(); };

            Assert.True(SingleInstanceManager.SignalPrimaryInstance(scope, SingleInstanceManager.MailboxesPage));
            Assert.True(received.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(SingleInstanceManager.SignalPrimaryInstance(scope));
            Assert.True(received.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal(new[] { SingleInstanceManager.MailboxesPage, null }, pages.ToArray());
        }
    }
}
