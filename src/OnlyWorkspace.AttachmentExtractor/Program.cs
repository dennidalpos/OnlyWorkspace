using System;
using System.IO;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.AttachmentExtractor;

/// <summary>
/// Parses one untrusted attachment in its own process (crash/hang isolation from Outlook).
/// Usage: OnlyWorkspace.AttachmentExtractor.exe &lt;file&gt; [maxChars] [--no-ocr] [--archive] → AttachmentExtraction JSON on stdout.
/// maxChars 0 = macro check only; --no-ocr leaves scans and images unread (ocr-off).
/// --ocr-languages → {"installed":[...],"used":...}: what OCR can read on this PC.
/// --security &lt;file&gt; → AttachmentInspection JSON: static reading of scripts, pages and links, nothing executed.
/// </summary>
internal static class Program
{
    private const long MaxFileBytes = 30L * 1024 * 1024;

    private static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        AttachmentExtraction result;
        int exitCode = 0;
        try
        {
            if (args.Length < 1) throw new ArgumentException("Uso: OnlyWorkspace.AttachmentExtractor <file> [maxChars]");
            if (args[0] == "--ocr-languages")
            {
                var (installed, used) = ContentReader.OcrLanguages();
                stdout.Write(JsonSerializer.Serialize(new { installed, used }));
                stdout.Flush();
                return 0;
            }
            if (args[0] == "--security")
            {
                if (args.Length < 2) throw new ArgumentException("Uso: OnlyWorkspace.AttachmentExtractor --security <file>");
                var target = new FileInfo(args[1]);
                if (!target.Exists) throw new FileNotFoundException("File non trovato.");
                if (target.Length > MaxFileBytes) throw new InvalidDataException("File troppo grande.");
                stdout.Write(JsonSerializer.Serialize(ContentReader.Inspect(target.FullName)));
                stdout.Flush();
                return 0;
            }
            int maxChars = args.Length > 1 && int.TryParse(args[1], out var m) && m >= 0 ? m : 20000;
            ContentReader.OcrEnabled = Array.IndexOf(args, "--no-ocr", 1) < 0;

            var info = new FileInfo(args[0]);
            if (!info.Exists) throw new FileNotFoundException("File non trovato.");
            if (info.Length > MaxFileBytes) throw new InvalidDataException("File troppo grande.");

            ContentReader.ArchiveMode = Array.IndexOf(args, "--archive") >= 0;
            result = ContentReader.Read(info.FullName, maxChars);
        }
        catch (Exception ex)
        {
            result = new AttachmentExtraction { Error = ex.GetType().Name + ": " + ex.Message };
            exitCode = 1;
        }

        stdout.Write(JsonSerializer.Serialize(result));
        stdout.Flush();
        return exitCode;
    }
}
