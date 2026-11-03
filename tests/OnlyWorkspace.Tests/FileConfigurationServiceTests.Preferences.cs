using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

public partial class FileConfigurationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserPreferences_IndependentMutexHolder_TimeoutOrAbandonmentPreservesFlags(bool abandon)
    {
        var service = new FileConfigurationService(_tempDir, _tempDir);
        service.SetUserFlag("existing", true);
        string path = Path.Combine(_tempDir, "preferences.json");
        byte[] original = File.ReadAllBytes(path);
        using var sha = SHA256.Create();
        string mutex = @"Global\OnlyWorkspace_Preferences_" + BitConverter.ToString(
            sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", "");
        string signal = "Local\\ow-preferences-holder-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, signal + "-ready");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, signal + "-release");
        string script = Path.Combine(_tempDir, "holder.ps1");
        File.WriteAllText(script, @"
param($mutex, $signal)
$ErrorActionPreference = 'Stop'
$gate = [Threading.Mutex]::new($false, $mutex)
$ready = [Threading.EventWaitHandle]::OpenExisting($signal + '-ready')
$release = [Threading.EventWaitHandle]::OpenExisting($signal + '-release')
try {
    if (!$gate.WaitOne(10000)) { throw 'holder mutex timeout' }
    [void]$ready.Set()
    if (!$release.WaitOne(30000)) { throw 'holder release timeout' }
    $gate.ReleaseMutex()
} finally { $gate.Dispose(); $ready.Dispose(); $release.Dispose() }
");
#if NET48
        const string shell = "powershell.exe";
#else
        const string shell = "pwsh.exe";
#endif
        // Keep a handle open across process exit so the abandoned kernel mutex still exists.
        using var retained = new Mutex(false, mutex);
        using var child = Process.Start(new ProcessStartInfo(shell,
            "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" \"" + mutex + "\" \"" + signal + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        try
        {
            Assert.True(ready.WaitOne(30000), "Independent mutex holder was not ready");
            if (abandon)
            {
                child.Kill();
                Assert.True(child.WaitForExit(10000));
            }
            else
            {
                var elapsed = Stopwatch.StartNew();
                Assert.Throws<TimeoutException>(() => service.SetUserFlag("new", true));
                File.WriteAllText(Path.Combine(_tempDir, "mutex-timeout-ms.txt"), elapsed.Elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(original, File.ReadAllBytes(path));
                release.Set();
                Assert.True(child.WaitForExit(10000));
                Assert.True(child.ExitCode == 0, child.StandardError.ReadToEnd());
            }
            service.SetUserFlag("new", true);
            Assert.True(service.GetUserFlag("existing"));
            Assert.True(service.GetUserFlag("new"));
        }
        finally
        {
            release.Set();
            if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
        }
    }
}
