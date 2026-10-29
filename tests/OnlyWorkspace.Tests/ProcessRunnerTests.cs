using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Infrastructure.Security;
using Xunit;

namespace OnlyWorkspace.Tests;

public class ProcessRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentExit_DoesNotEndDeadlineOrCancellation_AndDescendantIsKilled(bool cancel)
    {
        // cmd creates a descendant inheriting both pipes, then exits. The parent must really exit first.
        using var cts = new CancellationTokenSource();
        if (cancel) cts.CancelAfter(500);
        var clock = Stopwatch.StartNew();
        var run = ProcessRunner.RunAsync("cmd.exe", "/d /s /c \"start /b ping.exe -n 30 127.0.0.1 >nul & exit /b 0\"",
            cancel ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(500), cts.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        else Assert.Null(await run);
        // Waiting for the descendant would take about 29 s. RunAsync itself returns 500-610 ms after the start even with
        // every processor busy (A145, 26/10/2026), but a test host busy with other tests took 2.54 s, so the bound only
        // has to stay well below the descendant's lifetime.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task CancellationAfterParentExited_KillsTheIdentifiedDescendant()
    {
        string marker = Path.Combine(Path.GetTempPath(), "ow-process-" + Guid.NewGuid().ToString("N") + ".txt");
        using var cts = new CancellationTokenSource();
        Process? descendant = null;
        Task<ProcessResult?>? run = null;
        try
        {
            // Written aside and renamed: the marker appears complete, never while PowerShell still holds it open (CI 40197087901).
            string command = "$p = Start-Process ping.exe -ArgumentList '-n 30 127.0.0.1' -NoNewWindow -PassThru; " +
                "[IO.File]::WriteAllText('" + (marker + ".tmp").Replace("'", "''") + "', ($PID.ToString() + ',' + $p.Id.ToString())); " +
                "[IO.File]::Move('" + (marker + ".tmp").Replace("'", "''") + "', '" + marker.Replace("'", "''") + "')";
            run = ProcessRunner.RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command \"" + command + "\"", Timeout, cts.Token);
            var wait = Stopwatch.StartNew();
            while (!File.Exists(marker) && wait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            Assert.True(File.Exists(marker));
            string[] ids = File.ReadAllText(marker).Split(',');
            descendant = Process.GetProcessById(int.Parse(ids[1]));
            try { using var parent = Process.GetProcessById(int.Parse(ids[0])); Assert.True(parent.WaitForExit(5000)); }
            catch (ArgumentException) { /* Parent already exited before the marker was read. */ }
            Assert.False(run.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(descendant.WaitForExit(2000));
        }
        finally
        {
            cts.Cancel();
            if (run != null) { try { await run; } catch (OperationCanceledException) { } }
            descendant?.Dispose();
            File.Delete(marker);
            File.Delete(marker + ".tmp");
        }
    }

    [Fact]
    public async Task SuccessfulOutput_PreservesBothStreamsAndExitCode()
    {
        var result = await ProcessRunner.RunAsync("cmd.exe", "/d /c \"echo standard& echo error 1>&2& exit /b 7\"", Timeout, default);
        Assert.Equal(7, result!.ExitCode);
        Assert.Contains("standard", result.Output);
        Assert.Contains("error", result.Output);
    }

    // Prints the array length only if 600 MB could be committed (a failure echoes the command line, not the number).
    private const string Allocated = "629145600";
    private const string Allocate = "-NoProfile -NonInteractive -Command \"$b = New-Object byte[] 600MB; $b.Length\"";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task RunAsync_WithoutMemoryLimit_ChildAllocates()
    {
        var result = await ProcessRunner.RunAsync("powershell.exe", Allocate, Timeout, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(Allocated, result!.Output);
    }

    [Fact]
    public async Task RunAsync_WithMemoryLimit_ChildCannotExceedIt()
    {
        var result = await ProcessRunner.RunAsync("powershell.exe", Allocate, Timeout, CancellationToken.None, memoryLimitBytes: 300L * 1024 * 1024);

        Assert.NotNull(result);
        Assert.DoesNotContain(Allocated, result!.Output);
        Assert.Contains("OutOfMemory", result.Output);
    }

    [Fact]
    public async Task RunAsync_ReturnsNullAndKillsChildOnTimeout()
    {
        var result = await ProcessRunner.RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command \"Start-Sleep 60\"", TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RunAsync_CancellationIsPropagatedInsteadOfReportedAsTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(
            "powershell.exe", "-NoProfile -NonInteractive -Command \"Start-Sleep 60\"",
            Timeout, cancellation.Token));
    }
}
