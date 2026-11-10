using System;
using System.IO;
using System.Text;
using OpenMcdf;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.AttachmentExtractor;

/// <summary>
/// Attached emails as Outlook saves them (.msg, MS-OXMSG compound file): headers, body and the text of their
/// own attachments. A PEC carries the original message this way (postacert.eml) with the documents inside it.
/// </summary>
internal static partial class ContentReader
{
    private const string MsgProperties = "__properties_version1.0";
    private const string MsgAttachmentPrefix = "__attach_version1.0_";
    private const string MsgEmbeddedMessage = "__substg1.0_3701000D";
    private const string MsgAttachmentData = "__substg1.0_37010102";

    /// <summary>Envelope → attached email → email attached to it; deeper ones are only named.</summary>
    private const int MaxMsgDepth = 2;
    private const long MaxNestedBytes = 30L * 1024 * 1024;
    [ThreadStatic] private static long _expandedBytes;
    /// <summary>HTML body read for its links in security mode, within the process input bound.</summary>
    private const int MaxInspectedHtmlBytes = 30 * 1024 * 1024;

    /// <returns>true when one of the attachments has VBA macros.</returns>
    private static bool ReadMessage(Storage msg, bool topLevel, string workDir, LimitedText text, int depth)
    {
        if (text.Enabled)
        {
            var address = MsgString(msg, "5D01") ?? MsgString(msg, "0C1F");
            var sender = MsgString(msg, "0C1A");
            if (address != null && address.Contains("@") && !string.Equals(address, sender, StringComparison.OrdinalIgnoreCase))
            {
                sender = string.IsNullOrWhiteSpace(sender) ? address : $"{sender} <{address}>";
            }
            AppendField(text, "Da", sender);
            AppendField(text, "A", MsgString(msg, "0E04"));
            AppendField(text, "Data", MsgDate(msg, topLevel)?.ToString("dd/MM/yyyy HH:mm"));
            AppendField(text, "Oggetto", MsgString(msg, "0037"));

            var body = MsgString(msg, "1000", text.Remaining * 2, text);
            if (string.IsNullOrWhiteSpace(body)) body = HtmlToText(MsgHtml(msg, text.Remaining * 6, text));
            text.AppendBlock(body);
        }
        _inspection?.Body(MsgHtml(msg, MaxInspectedHtmlBytes, new LimitedText(1)));

        bool macros = false;
        foreach (var entry in msg.EnumerateEntries())
        {
            if (entry.Type != EntryType.Storage || !entry.Name.StartsWith(MsgAttachmentPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var attachment = msg.OpenStorage(entry.Name);

            // Images referenced by the HTML body (logos, signatures) have a Content-ID.
            if (attachment.ContainsEntry("__substg1.0_3712001F") || attachment.ContainsEntry("__substg1.0_3712001E")) continue;

            var name = MsgString(attachment, "3707") ?? MsgString(attachment, "3704") ?? MsgString(attachment, "3001") ?? "allegato";
            if (attachment.TryOpenStorage(MsgEmbeddedMessage, out var embedded))
            {
                if (depth >= MaxMsgDepth)
                {
                    text.AppendLine($"[Email allegata: {name} — non letta]");
                    text.Incomplete("depth-limit");
                    continue;
                }
                text.AppendLine($"[Email allegata: {name}]");
                _inspection?.Enter(name);
                try { macros |= ReadMessage(embedded, topLevel: false, workDir, text, depth + 1); }
                finally { _inspection?.Leave(); }
                continue;
            }

            macros |= ReadMessageAttachment(attachment, name, workDir, text, depth);
        }
        return macros;
    }

    private static bool ReadMessageAttachment(Storage attachment, string name, string workDir, LimitedText text, int depth)
    {
        if (!attachment.TryOpenStream(MsgAttachmentData, out var data)) { text.Incomplete("unsupported-msg-attachment"); return false; }
        using (data) return ReadNestedFile(name, data.Length, data.CopyTo, workDir, text, depth);
    }

    /// <summary>Copies an email's attachment next to the source file and reads it like any other document.</summary>
    private static bool ReadNestedFile(string name, long length, Action<Stream> copyTo, string workDir, LimitedText text, int depth, bool encodedLength = false)
    {
        if (ArchiveMode && AttachmentTextFormats.ArchiveReferenceReason(Path.GetExtension(name)) != null)
        {
            AppendReference(text, name, length, encodedLength);
            return false;
        }
        if (depth >= MaxMsgDepth || length > MaxNestedBytes || length > MaxNestedBytes - _expandedBytes)
        {
            text.AppendLine($"[Allegato: {name} — non letto: troppo grande]");
            text.Incomplete(depth >= MaxMsgDepth ? "depth-limit" : "expanded-size-limit");
            return false;
        }

        var path = Path.Combine(workDir, Guid.NewGuid().ToString("N") + ExtensionChain(SafeName(name)));
        try
        {
            using (var file = File.Create(path))
            using (var bounded = new ExpansionStream(file)) copyTo(bounded);

            _inspection?.Enter(name);
            AttachmentExtraction nested;
            try { nested = Read(path, text.Remaining, depth + 1); }
            finally { _inspection?.Leave(); }
            if (ArchiveMode && nested.Error == null && !nested.Truncated && nested.Problem is "archive-container" or "executable" or "unsupported")
            {
                AppendReference(text, name, new FileInfo(path).Length);
                return false;
            }
            if (nested.Text.Length > 0)
            {
                text.AppendLine($"[Allegato: {name}]");
                text.AppendBlock(nested.Text);
            }
            else
            {
                text.AppendLine($"[Allegato: {name} — non letto: {NestedProblem(nested)}]");
            }
            // Security mode: a script or page read statically, or an image left to OCR, is not a gap of the inspection.
            bool inspected = _inspection != null && nested.Error == null && !nested.Truncated &&
                             ((nested.Problem == "unsupported" && _inspection.Read(path)) || nested.Problem is "ocr-off" or "scanned");
            if (!inspected && (nested.Truncated || nested.Problem != null || nested.Error != null))
                text.Incomplete(nested.Problem ?? nested.Error ?? "partial");
            return nested.HasMacros;
        }
        catch (Exception)
        {
            text.AppendLine($"[Allegato: {name} — non letto: errore di lettura]");
            text.Incomplete("nested-read-error");
            return false;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } // temp copy: a leftover is cleaned with %TEMP%
        }
    }

    private static void AppendReference(LimitedText text, string name, long length, bool encodedLength = false) =>
        text.AppendLine($"[Allegato: {name} — solo riferimento; tipo: {Path.GetExtension(name).TrimStart('.')}; dimensione{(encodedLength ? " codificata" : "")}: {length} byte; contenuto non estratto]");

    private static string NestedProblem(AttachmentExtraction nested) => nested.Problem switch
    {
        "scanned" => "scansione senza testo riconoscibile",
        "ocr-unavailable" => "scansione, OCR non disponibile",
        "ocr-off" => "scansione, OCR disattivato",
        "protected" => "protetto da password",
        "unsupported" => "formato non supportato",
        _ => "nessun testo"
    };

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static void AppendField(LimitedText text, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) text.AppendLine(label + ": " + value!.Trim());
    }

    /// <summary>String property: stream __substg1.0_{id}001F (UTF-16) or {id}001E (ANSI).</summary>
    private static string? MsgString(Storage storage, string id, int maxChars = 2000, LimitedText? completeness = null)
    {
        int maxBytes = Math.Max(0, Math.Min(maxChars, 4_000_000)) * 2;
        string? value = null;
        if (storage.TryOpenStream("__substg1.0_" + id + "001F", out var unicode))
        {
            using (unicode)
            {
                if (unicode.Length > maxBytes) completeness?.Incomplete("text-limit");
                value = Encoding.Unicode.GetString(ReadBytes(unicode, maxBytes));
            }
        }
        else if (storage.TryOpenStream("__substg1.0_" + id + "001E", out var ansi))
        {
            using (ansi)
            {
                if (ansi.Length > maxBytes) completeness?.Incomplete("text-limit");
                value = Encoding.GetEncoding(1252).GetString(ReadBytes(ansi, maxBytes));
            }
        }
        value = value?.TrimEnd('\0');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>HTML body (binary, in the message code page: UTF-8 tried first).</summary>
    private static string? MsgHtml(Storage msg, int maxBytes, LimitedText completeness)
    {
        if (maxBytes <= 0) return null;
        if (!msg.TryOpenStream("__substg1.0_10130102", out var html)) return MsgString(msg, "1013", maxBytes, completeness);
        using (html)
        {
            if (html.Length > maxBytes) completeness.Incomplete("text-limit");
            var bytes = ReadBytes(html, maxBytes);
            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
        }
    }

    /// <summary>
    /// Sent (or delivery) time from the fixed-length property stream: 16-byte entries (tag, flags, value)
    /// after a header of 32 bytes in a .msg file and 24 in an embedded message.
    /// </summary>
    private static DateTime? MsgDate(Storage msg, bool topLevel)
    {
        if (!msg.TryOpenStream(MsgProperties, out var stream)) return null;
        using (stream)
        {
            var bytes = ReadBytes(stream, 64 * 1024);
            for (int i = topLevel ? 32 : 24; i + 16 <= bytes.Length; i += 16)
            {
                uint tag = BitConverter.ToUInt32(bytes, i);
                if (tag != 0x00390040 && tag != 0x0E060040) continue; // PR_CLIENT_SUBMIT_TIME, PR_MESSAGE_DELIVERY_TIME
                try
                {
                    return DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, i + 8)).ToLocalTime();
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }
        return null;
    }

    private static byte[] ReadBytes(Stream stream, int maxBytes)
    {
        var buffer = new byte[(int)Math.Min(stream.Length, Math.Max(0, maxBytes))];
        int total = 0, read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
        if (total < buffer.Length) Array.Resize(ref buffer, total);
        return buffer;
    }
}
