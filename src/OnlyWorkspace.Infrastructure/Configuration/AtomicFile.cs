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
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>UTF-8 without BOM, like File.WriteAllText.</summary>
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
}
