using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

/// <summary>
/// ScanFileAsync around the scanner process, with lookup and launch replaced: no Defender scan starts.
/// Every failure must end in a non-clean status; cancellation must reach the caller.
/// </summary>
public sealed class DefenderScanFileTests : IDisposable
{
    private const string Scanner = @"C:\synthetic\MpCmdRun.exe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ow_test_defender_" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private int _launches;

    public DefenderScanFileTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "allegato.pdf");
        File.WriteAllText(_file, "contenuto sintetico");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) TestArtifacts.DeleteDirectory(_dir, recursive: true); }
        catch { }
    }

    private Func<string, string, TimeSpan, CancellationToken, Task<ProcessResult?>> Returns(ProcessResult? result) => (_, _, _, _) =>
    {
        _launches++;
        return Task.FromResult(result);
    };

    [Fact]
    public async Task MissingFile_IsRemovedWithoutLaunchingTheScanner()
    {
        File.Delete(_file);

        var result = await DefenderScanner.ScanFileAsync(_file, Timeout, CancellationToken.None, () => Scanner, Returns(new ProcessResult(0, "found no threats")));

        Assert.Equal(AttachmentScanStatus.Removed, result.Status);
        Assert.Equal(0, _launches);
    }

    [Fact]
    public async Task FileRemovedDuringTheScan_IsRemovedNotClean()
    {
        var result = await DefenderScanner.ScanFileAsync(_file, Timeout, CancellationToken.None, () => Scanner, (_, _, _, _) =>
        {
            File.Delete(_file); // real-time protection quarantined the copy meanwhile
            return Task.FromResult<ProcessResult?>(new ProcessResult(0, "found no threats"));
        });

        Assert.Equal(AttachmentScanStatus.Removed, result.Status);
    }

    [Fact]
    public async Task ScannerNotInstalled_FailsWithoutLaunching()
    {
        var result = await DefenderScanner.ScanFileAsync(_file, Timeout, CancellationToken.None, () => null, Returns(new ProcessResult(0, "")));

        Assert.Equal(AttachmentScanStatus.Failed, result.Status);
        Assert.Equal("Microsoft Defender non installato", result.Detail);
        Assert.Equal(0, _launches);
    }

    [Fact]
    public async Task ScannerPathThatCannotStart_Fails()
    {
        string missing = Path.Combine(_dir, "MpCmdRun.exe");

        // The real runner: starting a missing executable throws, which must become a failure, not a clean file.
        var result = await DefenderScanner.ScanFileAsync(_file, Timeout, CancellationToken.None, () => missing,
            (exe, args, limit, ct) => ProcessRunner.RunAsync(exe, args, limit, ct));

        Assert.Equal(AttachmentScanStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Detail));
    }

    [Fact]
    public async Task Timeout_Fails()
    {
        var result = await DefenderScanner.ScanFileAsync(_file, Timeout, CancellationToken.None, () => Scanner, Returns(null));

        Assert.Equal(AttachmentScanStatus.Failed, result.Status);
        Assert.Equal("timeout", result.Detail);
        Assert.Equal(1, _launches);
    }

    [Fact]
    public async Task Cancellation_IsPropagated()
    {
        using var cts = new CancellationTokenSource();
        string? arguments = null;
        var scan = DefenderScanner.ScanFileAsync(_file, Timeout, cts.Token, () => Scanner, async (_, args, _, ct) =>
        {
            arguments = args;
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return new ProcessResult(0, "found no threats");
        });

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Equal($"-Scan -ScanType 3 -File \"{_file}\" -DisableRemediation", arguments);
    }

    [Fact]
    public async Task AlreadyCancelled_IsPropagated()
    {
        var scan = DefenderScanner.ScanFileAsync(_file, Timeout, new CancellationToken(canceled: true), () => Scanner,
            (_, _, _, ct) => Task.FromCanceled<ProcessResult?>(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
    }
}
