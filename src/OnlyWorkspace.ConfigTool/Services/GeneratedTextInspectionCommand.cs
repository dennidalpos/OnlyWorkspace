using System;
using System.IO;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Utils;

namespace OnlyWorkspace.ConfigTool.Services;

internal static class GeneratedTextInspectionCommand
{
    private const int MaxInputBytes = 4 * 1024 * 1024;

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length != 2 || !(string.Equals(Path.GetExtension(args[1]), ".html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(args[1]), ".htm", StringComparison.OrdinalIgnoreCase)))
            {
                error.WriteLine("Usage: inspect-ai-html <file.html>");
                return 1;
            }
            string path = Path.GetFullPath(args[1]);
            // No remote detector or resource loading; direct UNC/device paths are refused.
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("Local file required.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxInputBytes) throw new ArgumentException("Input too large.");
            var result = GeneratedTextMarkup.InspectHtml(ReadHtml(stream));
            output.WriteLine(JsonSerializer.Serialize(new
            {
                status = result.Status,
                declaredFragments = result.DeclaredFragments,
                malformed = result.Malformed,
                unsupportedMarkers = result.UnsupportedMarkers,
                authenticated = false,
                notice = "Dichiarazione HTML non autenticata. Assenza o rimozione del marcatore non esclude origine IA."
            }));
            return 0;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
            || ex is ArgumentException || ex is System.Security.SecurityException)
        {
            error.WriteLine("Impossibile ispezionare il file HTML locale: verificare formato, accesso e limite di 4 MiB.");
            return 2;
        }
    }

    internal static string ReadHtml(Stream input)
    {
        // Bound the actual read too, rather than relying only on a file's initial reported length.
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxInputBytes + 1L - bytes.Length))) > 0)
        {
            if (bytes.Length + read > MaxInputBytes) throw new ArgumentException("Input too large.");
            bytes.Write(buffer, 0, read);
        }
        var content = bytes.GetBuffer();
        int count = (int)bytes.Length, offset = 0;
        Encoding encoding = new UTF8Encoding(false, true);
        // Detect BOM explicitly: StreamReader replaces its strict initial encoding on BOM detection
        // with a permissive UTF-16/32 instance that would silently repair malformed source bytes.
        if (count >= 4 && content[0] == 0xFF && content[1] == 0xFE && content[2] == 0 && content[3] == 0)
        { encoding = new UTF32Encoding(false, false, true); offset = 4; }
        else if (count >= 4 && content[0] == 0 && content[1] == 0 && content[2] == 0xFE && content[3] == 0xFF)
        { encoding = new UTF32Encoding(true, false, true); offset = 4; }
        else if (count >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (count >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        else if (count >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            offset = 3;
        return encoding.GetString(content, offset, count - offset);
    }
}
