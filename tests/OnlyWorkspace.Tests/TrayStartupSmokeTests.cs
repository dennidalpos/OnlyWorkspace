using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.ConfigTool.Tray;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

[Collection(WinFormsCollection.Name)]
public sealed class TrayStartupSmokeTests
{
    [Fact]
    public void EarlyShowSignal_OpensResponsiveConfigWindow_OnFirstRequest()
    {
        string root = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-tray-smoke-" + Guid.NewGuid().ToString("N"));
        string configDir = Path.Combine(root, "config");
        string preferencesDir = Path.Combine(root, "preferences");
        string archiveDir = Path.Combine(root, "archive");
        string scope = "Smoke_" + Guid.NewGuid().ToString("N");
        Exception? error = null;
        bool responded = false;
        string stage = "not started";
        bool exited = false;

        try
        {
            var config = new WorkspaceConfig
            {
                Profiles = new List<LlmProfileConfig> { new() { Id = "ollama-lan", DisplayName = "Ollama locale" } }
            };
            var settings = new FileConfigurationService(configDir, preferencesDir);
            settings.SaveConfiguration(config);
            settings.SetUserFlag("guidedSetupDone", true);

            var thread = new Thread(() =>
            {
                System.Windows.Forms.Timer? timer = null;
                MainConfigForm? form = null;
                try
                {
                    stage = "starting primary";
                    Assert.True(SingleInstanceManager.TryStartPrimary(out var primary, scope));
                    using (primary)
                    {
                        Assert.True(SingleInstanceManager.SignalPrimaryInstance(scope));
                        Thread.Sleep(100);
                        stage = "constructing tray";
                        using var tray = new TrayApplicationContext(configDir, startMinimized: true,
                            singleInstance: primary, preferencesDir: preferencesDir, archiveDir: archiveDir);
                        stage = "running message loop";
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        timer = new System.Windows.Forms.Timer { Interval = 50 };
                        timer.Tick += (_, _) =>
                        {
                            stage = "polling window";
                            form = Application.OpenForms.OfType<MainConfigForm>().FirstOrDefault();
                            if (form is { Visible: true, IsHandleCreated: true })
                            {
                                timer.Stop();
                                form.BeginInvoke(new Action(() =>
                                {
                                    stage = "processing window callback";
                                    responded = form.Visible && form.Enabled && !form.IsDisposed;
                                    Application.ExitThread();
                                }));
                            }
                            else if (DateTime.UtcNow >= deadline)
                            {
                                timer.Stop();
                                error = new TimeoutException("First show request did not open the ConfigTool window.");
                                Application.ExitThread();
                            }
                        };
                        timer.Start();
                        Application.Run(tray);
                        stage = "message loop exited";
                    }
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    timer?.Dispose();
                    form?.Dispose();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            exited = thread.Join(TimeSpan.FromSeconds(20));
            Assert.True(exited, "Tray smoke did not exit at stage: " + stage);
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            Assert.True(responded, "ConfigTool window did not process the first show request.");
        }
        finally
        {
            if (exited && Directory.Exists(root)) TestArtifacts.DeleteDirectory(root, recursive: true);
        }
    }

    [Fact]
    public void ShowSignalAfterStartup_CreatesConfigWindowOnUiThread()
    {
        // Autostart at logon: the tray runs idle, then Outlook signals it to open the configuration.
        string root = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-tray-late-" + Guid.NewGuid().ToString("N"));
        string configDir = Path.Combine(root, "config");
        string preferencesDir = Path.Combine(root, "preferences");
        string scope = "Late_" + Guid.NewGuid().ToString("N");
        Exception? error = null;
        bool? invokeRequired = null;
        bool exited = false;

        try
        {
            var settings = new FileConfigurationService(configDir, preferencesDir);
            settings.SaveConfiguration(new WorkspaceConfig
            {
                Profiles = new List<LlmProfileConfig> { new() { Id = "ollama-lan", DisplayName = "Ollama locale" } }
            });
            settings.SetUserFlag("guidedSetupDone", true);

            var thread = new Thread(() =>
            {
                System.Windows.Forms.Timer? timer = null;
                MainConfigForm? form = null;
                try
                {
                    Assert.True(SingleInstanceManager.TryStartPrimary(out var primary, scope));
                    using (primary)
                    {
                        using var tray = new TrayApplicationContext(configDir, startMinimized: true,
                            singleInstance: primary, preferencesDir: preferencesDir, archiveDir: Path.Combine(root, "archive"));
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        bool signaled = false;
                        timer = new System.Windows.Forms.Timer { Interval = 50 };
                        timer.Tick += (_, _) =>
                        {
                            if (!signaled) signaled = SingleInstanceManager.SignalPrimaryInstance(scope);
                            form = Application.OpenForms.OfType<MainConfigForm>().FirstOrDefault();
                            if (form is { IsHandleCreated: true } || DateTime.UtcNow >= deadline)
                            {
                                timer.Stop();
                                invokeRequired = form?.InvokeRequired;
                                Application.ExitThread();
                            }
                        };
                        timer.Start();
                        Application.Run(tray);
                    }
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    timer?.Dispose();
                    if (form != null && invokeRequired == false) form.Dispose();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            exited = thread.Join(TimeSpan.FromSeconds(20));
            Assert.True(exited, "Tray did not exit after the show request.");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            Assert.NotNull(invokeRequired);
            Assert.False(invokeRequired, "ConfigTool window was created off the tray UI thread.");
        }
        finally
        {
            if (exited && Directory.Exists(root)) TestArtifacts.DeleteDirectory(root, recursive: true);
        }
    }
}
