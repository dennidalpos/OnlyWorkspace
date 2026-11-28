using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Infrastructure.Security;

public sealed class DefenderScanResult
{
    public DefenderScanResult(AttachmentScanStatus status, string? detail = null)
    {
        Status = status;
        Detail = detail;
    }

    public AttachmentScanStatus Status { get; }
    public string? Detail { get; }
}

/// <summary>
/// On-demand Microsoft Defender custom scan of a single file (MpCmdRun -Scan -ScanType 3 -File -DisableRemediation).
/// Exit code 2 means "threat or scan error", so the verdict also relies on the command output.
/// </summary>
public static class DefenderScanner
{
    private static readonly Regex FoundThreats = new(@"found\s+(\d+)\s+threats?", RegexOptions.IgnoreCase);
    private static readonly Regex ThreatName = new(@"^\s*Threat\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Latest platform folder first (as recommended by Microsoft), then the inbox copy.</summary>
    public static string? FindMpCmdRun()
    {
        try
        {
            var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
            if (Directory.Exists(platform))
            {
                var latest = Directory.GetDirectories(platform)
                    .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
                    .Select(d => Path.Combine(d, "MpCmdRun.exe"))
                    .FirstOrDefault(File.Exists);
                if (latest != null) return latest;
            }
        }
        catch
        {
            // Fall back to the inbox location
        }

        var inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(inbox) ? inbox : null;
    }

    public static Task<DefenderScanResult> ScanFileAsync(string filePath, TimeSpan timeout, CancellationToken cancellationToken) =>
        ScanFileAsync(filePath, timeout, cancellationToken, FindMpCmdRun, (exe, args, limit, ct) => ProcessRunner.RunAsync(exe, args, limit, ct));

    /// <summary>Test seam: scanner lookup and process launch replaced, so no real Defender scan starts.</summary>
    internal static async Task<DefenderScanResult> ScanFileAsync(string filePath, TimeSpan timeout, CancellationToken cancellationToken,
        Func<string?> findScanner, Func<string, string, TimeSpan, CancellationToken, Task<ProcessResult?>> run)
    {
        if (!File.Exists(filePath)) return new DefenderScanResult(AttachmentScanStatus.Removed);

        var exe = findScanner();
        if (exe == null) return new DefenderScanResult(AttachmentScanStatus.Failed, "Microsoft Defender non installato");

        ProcessResult? scan;
        try
        {
            scan = await run(exe, $"-Scan -ScanType 3 -File \"{filePath}\" -DisableRemediation", timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DefenderScanResult(AttachmentScanStatus.Failed, ex.Message);
        }
        if (scan == null) return new DefenderScanResult(AttachmentScanStatus.Failed, "timeout");

        // Real-time protection may delete the copy while the scan runs.
        if (!File.Exists(filePath)) return new DefenderScanResult(AttachmentScanStatus.Removed);
        return Interpret(scan.ExitCode, scan.Output);
    }

    public static DefenderScanResult Interpret(int exitCode, string output)
    {
        var found = FoundThreats.Match(output);
        var name = ThreatName.Match(output);
        if (name.Success || found.Success && int.TryParse(found.Groups[1].Value, out var count) && count > 0)
        {
            return new DefenderScanResult(AttachmentScanStatus.Threat, name.Success ? name.Groups[1].Value.Trim() : null);
        }

        if (exitCode == 0) return new DefenderScanResult(AttachmentScanStatus.Clean);

        var hr = Regex.Match(output, @"hr\s*=\s*(0x[0-9A-Fa-f]{8})");
        return new DefenderScanResult(AttachmentScanStatus.Failed, hr.Success ? "errore " + hr.Groups[1].Value : $"codice {exitCode}");
    }

    private static Version ParseVersion(string folderName)
    {
        // Platform folders look like "4.18.26080.4-0".
        var core = folderName.Split('-')[0];
        return Version.TryParse(core, out var v) ? v : new Version(0, 0);
    }
}
