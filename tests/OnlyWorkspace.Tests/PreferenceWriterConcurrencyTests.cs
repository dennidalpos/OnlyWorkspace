using System.Text;
using System.Text.Json;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

/// <summary>
/// #232: the writers' 2 s mutex wait expired twice under the load of the full suite on 12/11/2026. Alone it never did,
/// so the cross-process writers run in a sequential collection, after the parallel tests, instead of raising the limit.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PreferenceWriterCollection
{
    public const string Name = "Preference writers";
}

[Collection(PreferenceWriterCollection.Name)]
public sealed class PreferenceWriterConcurrencyTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "OnlyWorkspaceTests_" + Guid.NewGuid().ToString("N"));

    public PreferenceWriterConcurrencyTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) TestArtifacts.DeleteDirectory(_tempDir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ConcurrentProcesses_PreserveEveryFlag()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), FileConfigurationServiceTests.TwoProfiles);
        var service = new FileConfigurationService(_tempDir, _tempDir);
        service.SetUserFlag("archiveOff", true);
        // Public writers in independent processes, synchronized at the start. No private-method seam.
        string script = Path.Combine(_tempDir, "writer.ps1");
        File.WriteAllText(script, @"
param($assembly, $directory, $worker, $start)
$ErrorActionPreference = 'Stop'
$cold = [Diagnostics.Stopwatch]::StartNew()
Add-Type -Path $assembly
$service = [OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService]::new($directory, $directory, $null)
$gate = [Threading.EventWaitHandle]::OpenExisting($start)
$ready = [Threading.EventWaitHandle]::OpenExisting($start + '-ready-' + $worker)
$samples = [Collections.Generic.List[object]]::new()
$cold.Stop()
try {
    [void]$ready.Set()
    if (-not $gate.WaitOne(30000)) { throw 'start timeout' }
    for ($i = 0; $i -lt 30; $i++) {
        foreach ($kind in @('unique', 'shared')) {
            $began = [Diagnostics.Stopwatch]::GetTimestamp()
            $errorKind = ''
            try {
                if ($kind -eq 'unique') { $service.SetUserFlag(('worker' + $worker + '_' + $i), $true) }
                else { $service.SetUserFlag('shared', ($i % 2 -eq 0)) }
            } catch { $errorKind = $_.Exception.GetBaseException().GetType().FullName; throw }
            finally {
                $ended = [Diagnostics.Stopwatch]::GetTimestamp()
                $samples.Add(@{iteration=$i;kind=$kind;began=$began;ended=$ended;error=$errorKind})
            }
        }
    }
} finally {
    @{pid=$PID;coldMs=$cold.Elapsed.TotalMilliseconds;frequency=[Diagnostics.Stopwatch]::Frequency;samples=@($samples.ToArray())} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory ('worker-' + $worker + '-timings.json'))
    $ready.Dispose()
    $gate.Dispose()
}
");
        string eventName = "Local\\ow-preferences-test-" + Guid.NewGuid().ToString("N");
        using var start = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, eventName);
#if NET48
        string shell = "powershell.exe";
#else
        string shell = "pwsh.exe";
#endif
        var workers = new List<System.Diagnostics.Process>();
        var ready = new List<System.Threading.EventWaitHandle>();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                ready.Add(new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, eventName + "-ready-" + i));
                workers.Add(System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shell,
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" \"" + typeof(FileConfigurationService).Assembly.Location +
                    "\" \"" + _tempDir + "\" " + i + " \"" + eventName + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!);
            }
            foreach (var signal in ready) Assert.True(signal.WaitOne(30000), "Preference writer was not ready");
            start.Set();
            foreach (var worker in workers)
            {
                Assert.True(worker.WaitForExit(30000), "Preference writer timeout");
            }
            // Let both processes finish their timing records before reporting either failure; the summary names the slow write.
            foreach (var worker in workers)
            {
                Assert.True(worker.ExitCode == 0, worker.StandardError.ReadToEnd() + WriterTimings(2));
            }
            Assert.True(service.GetUserFlag("archiveOff"));
            for (int w = 0; w < 2; w++)
                for (int i = 0; i < 30; i++) Assert.True(service.GetUserFlag("worker" + w + "_" + i));
            Assert.False(service.GetUserFlag("shared"));
            // A234: the writers' own timings are asserted. The production writer waits 2 s for the mutex before it
            // fails, so no completed operation may come near that; 5 s leaves room for a loaded machine.
            for (int w = 0; w < 2; w++)
            {
                using var timings = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "worker-" + w + "-timings.json")));
                double frequency = timings.RootElement.GetProperty("frequency").GetDouble();
                var samples = timings.RootElement.GetProperty("samples");
                Assert.Equal(60, samples.GetArrayLength());
                double slowest = 0;
                foreach (var sample in samples.EnumerateArray())
                {
                    Assert.Equal(string.Empty, sample.GetProperty("error").GetString());
                    slowest = Math.Max(slowest, (sample.GetProperty("ended").GetInt64() - sample.GetProperty("began").GetInt64()) * 1000.0 / frequency);
                }
                Assert.True(slowest < 5000, "Worker " + w + " slowest preference write: " + slowest.ToString("F0") + " ms." + WriterTimings(2));
            }
        }
        finally
        {
            foreach (var worker in workers)
            {
                if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(); }
                worker.Dispose();
            }
            foreach (var signal in ready) signal.Dispose();
        }
    }

    /// <summary>Per writer: operations, slowest write with its start relative to the first write of both, and failures.</summary>
    private string WriterTimings(int count)
    {
        var text = new StringBuilder();
        long origin = long.MaxValue;
        var documents = new List<JsonDocument>();
        try
        {
            for (int w = 0; w < count; w++)
            {
                string file = Path.Combine(_tempDir, "worker-" + w + "-timings.json");
                if (!File.Exists(file)) { text.Append(" | worker " + w + ": no timings"); continue; }
                var document = JsonDocument.Parse(File.ReadAllText(file));
                documents.Add(document);
                foreach (var sample in document.RootElement.GetProperty("samples").EnumerateArray())
                    origin = Math.Min(origin, sample.GetProperty("began").GetInt64());
            }
            foreach (var document in documents)
            {
                var root = document.RootElement;
                double frequency = root.GetProperty("frequency").GetDouble();
                double slowest = 0, slowestAt = 0;
                int failed = 0, over500 = 0, total = 0;
                foreach (var sample in root.GetProperty("samples").EnumerateArray())
                {
                    total++;
                    long began = sample.GetProperty("began").GetInt64();
                    double ms = (sample.GetProperty("ended").GetInt64() - began) * 1000.0 / frequency;
                    if (ms > 500) over500++;
                    if (!string.IsNullOrEmpty(sample.GetProperty("error").GetString())) failed++;
                    if (ms > slowest) { slowest = ms; slowestAt = (began - origin) * 1000.0 / frequency; }
                }
                text.Append(" | pid " + root.GetProperty("pid").GetInt32() + ": " + total + " ops, " + failed + " failed, " + over500 +
                    " over 500 ms, slowest " + slowest.ToString("F0") + " ms at +" + slowestAt.ToString("F0") + " ms");
            }
        }
        finally { foreach (var document in documents) document.Dispose(); }
        return text.ToString();
    }
}
