using System;
using System.IO;
using System.Text;

namespace OnlyWorkspace.Infrastructure.Configuration;

/// <summary>Writes to a temp file next to the target, then swaps: a crash mid-write never leaves a truncated file.</summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] content)
    {
        // One temp file per write: Outlook and ConfigTool may write the same target at once.
        string tmp = TempName(path);
        try
        {
            // Flushed to disk before the swap, so a power loss cannot leave the renamed file empty.
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } // temp file of a failed write: must not mask the original error
        }
    }

    /// <summary>48 random bits for temp names: net48 stops at MAX_PATH, so two full GUIDs in a name cap the folder length
    /// at about 168 characters. A clash makes CreateNew fail; it never overwrites.</summary>
    internal static string TempName(string path) => path + "." + ShortId() + ".tmp";

    internal static string ShortId() => Guid.NewGuid().ToString("N").Substring(0, 12);

    /// <summary>UTF-8 without BOM, like File.WriteAllText.</summary>
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
}
