using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Infrastructure.Security;

/// <summary>Runs OnlyWorkspace.AttachmentExtractor.exe (installed in "extractor" next to the add-in) on one file.</summary>
public static class AttachmentTextExtractor
{
    public const string HelperFileName = "OnlyWorkspace.AttachmentExtractor.exe";

    /// <summary>
    /// Safety limit for "the whole document": far beyond any model's context (about a million tokens), it only
    /// keeps a hostile file from flooding the add-in. A document that reaches it counts as not read.
    /// </summary>
    public const int WholeDocumentChars = 4_000_000;

    /// <summary>Committed memory allowed to the helper: a hostile file must not exhaust the PC.</summary>
    private const long MemoryLimitBytes = 1024L * 1024 * 1024;

    /// <summary>Extensions the helper can open (text and/or macros).</summary>
    public static bool IsSupported(string extension) => AttachmentTextFormats.HasTextReader(extension) || extension is "ppt" or "pps";

    public static string? FindHelper()
    {
        foreach (var dir in new[] { Path.GetDirectoryName(typeof(AttachmentTextExtractor).Assembly.Location), AppDomain.CurrentDomain.BaseDirectory })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            // Installed in its own subfolder: its dependency versions must not replace the add-in's.
            foreach (var path in new[] { Path.Combine(dir, "extractor", HelperFileName), Path.Combine(dir, HelperFileName) })
            {
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    /// <summary>
    /// The language OCR reads on this PC for this Windows user ("" when Windows has no recognizer for the profile's
    /// languages), or null when the helper cannot say: the archive rereads ocr-unavailable scans once a recognizer appears (A123).
    /// </summary>
    public static async Task<string?> OcrLanguageAsync(CancellationToken cancellationToken)
    {
        var helper = FindHelper();
        if (helper == null) return null;
        try
        {
            var run = await ProcessRunner.RunAsync(helper, "--ocr-languages", TimeSpan.FromSeconds(30), cancellationToken, lowPriority: true, memoryLimitBytes: MemoryLimitBytes).ConfigureAwait(false);
            if (run == null || run.ExitCode != 0) return null;
            using var doc = JsonDocument.Parse(run.Output.Split('\n')[0]);
            return doc.RootElement.TryGetProperty("used", out var used) && used.ValueKind == JsonValueKind.String ? used.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <param name="maxChars">0 = macro check only.</param>
    /// <param name="ocr">false: scans and images stay unread ("ocr-off"), as the administrator set (E8).</param>
    public static async Task<AttachmentExtraction> ExtractAsync(string filePath, int maxChars, TimeSpan timeout, CancellationToken cancellationToken, bool ocr = true, bool archive = false)
    {
        var helper = FindHelper();
        if (helper == null) return new AttachmentExtraction { Error = "estrattore non installato" };

        ProcessResult? run;
        try
        {
            run = await ProcessRunner.RunAsync(helper, $"\"{filePath}\" {maxChars}" + (ocr ? "" : " --no-ocr") + (archive ? " --archive" : ""), timeout, cancellationToken, lowPriority: true, memoryLimitBytes: MemoryLimitBytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AttachmentExtraction { Error = ex.Message };
        }
        if (run == null) return new AttachmentExtraction { Error = "timeout" };

        // JSON is the first stdout line; stderr (if any) follows.
        var json = run.Output.Split('\n')[0];
        try
        {
            return JsonSerializer.Deserialize<AttachmentExtraction>(json) ?? new AttachmentExtraction { Error = "risposta vuota" };
        }
        catch (JsonException)
        {
            return new AttachmentExtraction { Error = $"risposta non valida (codice {run.ExitCode})" };
        }
    }

    /// <summary>
    /// Static reading for the security check (--security): scripts, pages, links and active content of one copy,
    /// in the same isolated helper and limits. Nothing is executed or fetched; archive text and caches are not involved.
    /// </summary>
    public static async Task<AttachmentInspection> InspectAsync(string filePath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var helper = FindHelper();
        if (helper == null) return new AttachmentInspection { Error = "estrattore non installato" };

        ProcessResult? run;
        try
        {
            run = await ProcessRunner.RunAsync(helper, $"--security \"{filePath}\"", timeout, cancellationToken, lowPriority: true, memoryLimitBytes: MemoryLimitBytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AttachmentInspection { Error = ex.Message };
        }
        if (run == null) return new AttachmentInspection { Error = "timeout" };

        var json = run.Output.Split('\n')[0];
        try
        {
            return JsonSerializer.Deserialize<AttachmentInspection>(json) ?? new AttachmentInspection { Error = "risposta vuota" };
        }
        catch (JsonException)
        {
            return new AttachmentInspection { Error = $"risposta non valida (codice {run.ExitCode})" };
        }
    }
}
