using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;
using Xunit;

namespace OnlyWorkspace.Tests;

[Collection(WinFormsCollection.Name)]
public sealed class AdminPasswordServiceTests
{
    [Fact]
    public void Verifier_AllowsChangeAndRemovalWithoutStoringPlaintext()
    {
        // Short names: net48 stops at MAX_PATH and the managed temp root plus the atomic-write temp names leave little room.
        string directory = Path.Combine(Path.GetTempPath(), "owap-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(directory);
        try
        {
            var guard = new AdminPasswordService(directory);
            Assert.False(guard.Enabled);
            guard.ApplyRecord(AdminPasswordService.CreateRecord("initial-secret"));
            Assert.True(guard.Enabled);
            Assert.True(guard.Verify("initial-secret"));
            Assert.False(guard.Verify("wrong-secret"));
            Assert.DoesNotContain("initial-secret", File.ReadAllText(Path.Combine(directory, "admin-password.json")));
            guard.ApplyRecord(AdminPasswordService.CreateRecord("replacement-secret"));
            Assert.False(guard.Verify("initial-secret"));
            Assert.True(guard.Verify("replacement-secret"));
            guard.ApplyRecord(null);
            Assert.False(guard.Enabled);
        }
        finally { TestArtifacts.DeleteDirectory(directory, true); }
    }

    [Fact]
    public void CliImport_RequiresPasswordOnEveryWrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "owac-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string target = Path.Combine(directory, "target");
        Directory.CreateDirectory(target);
        try
        {
            var guard = new AdminPasswordService(target);
            guard.ApplyRecord(AdminPasswordService.CreateRecord("correct-secret"));
            string source = Path.Combine(directory, "source.json");
            File.WriteAllText(source, JsonSerializer.Serialize(new WorkspaceConfig
            {
                Profiles = new() { new LlmProfileConfig { Id = "ollama-lan", DisplayName = "Local" } }
            }));

            int Run(string password)
            {
                var start = ConfigToolCli("import", source, target, "--skip-test");
                using var process = Process.Start(start)!;
                process.StandardInput.WriteLine(password);
                process.StandardInput.Close();
                Assert.True(process.WaitForExit(30000));
                return process.ExitCode;
            }

            Assert.NotEqual(0, Run("wrong-secret"));
            Assert.False(File.Exists(Path.Combine(target, "config.json")));
            Assert.Equal(0, Run("correct-secret"));
            Assert.True(File.Exists(Path.Combine(target, "config.json")));
        }
        finally { TestArtifacts.DeleteDirectory(directory, true); }
    }

    [Fact]
    public void GuiStartsLockedAndCannotSaveWhenPasswordIsEnabled()
    {
        string directory = Path.Combine(Path.GetTempPath(), "owag-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(directory);
        try
        {
            new AdminPasswordService(directory).ApplyRecord(AdminPasswordService.CreateRecord("correct-secret"));
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new MainConfigForm(directory, null, directory);
                    var type = typeof(MainConfigForm);
                    Assert.True((bool)type.GetField("_isLocked", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!);
                    Assert.False(((Button)type.GetField("_btnSave", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!).Enabled);
                    Assert.False(((Task<bool>)type.GetMethod("SaveActiveConfiguration", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null)!).GetAwaiter().GetResult());
                    Assert.False(File.Exists(Path.Combine(directory, "config.json")));
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(30000));
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { TestArtifacts.DeleteDirectory(directory, true); }
    }

    [Fact]
    public void CliRestore_RequiresPasswordBeforeReplacingConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "owar-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string source = Path.Combine(directory, "source");
        string target = Path.Combine(directory, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(source, "config.json"), JsonSerializer.Serialize(new WorkspaceConfig
            {
                Profiles = new() { new LlmProfileConfig { Id = "ollama-lan", DisplayName = "Backup" } }
            }));
            string backup = Path.Combine(directory, "backup.owbak");
            ConfigurationBackupService.CreateBackup(source, backup);
            new AdminPasswordService(target).ApplyRecord(AdminPasswordService.CreateRecord("correct-secret"));

            int Run(string password)
            {
                var start = ConfigToolCli("restore", backup, target);
                using var process = Process.Start(start)!;
                process.StandardInput.WriteLine(password);
                process.StandardInput.Close();
                Assert.True(process.WaitForExit(30000));
                return process.ExitCode;
            }

            Assert.NotEqual(0, Run("wrong-secret"));
            Assert.False(File.Exists(Path.Combine(target, "config.json")));
            Assert.Equal(0, Run("correct-secret"));
            Assert.True(File.Exists(Path.Combine(target, "config.json")));
        }
        finally { TestArtifacts.DeleteDirectory(directory, true); }
    }

    /// <summary>The ConfigTool command line: through the dotnet host on net10, the net48 exe itself otherwise.</summary>
    private static ProcessStartInfo ConfigToolCli(params string[] args)
    {
        string tool = typeof(AdminPasswordService).Assembly.Location;
#if NET
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add(tool);
        foreach (var arg in args) start.ArgumentList.Add(arg);
#else
        var start = new ProcessStartInfo(tool, string.Join(" ", args.Select(arg => "\"" + arg + "\"")));
#endif
        start.UseShellExecute = false;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        return start;
    }
}
