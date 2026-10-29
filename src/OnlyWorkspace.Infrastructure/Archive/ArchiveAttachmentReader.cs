using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Infrastructure.Archive;

public static class ArchiveAttachmentReader
{
    /// <param name="ocr">The administrator's OCR setting; a text already recognized and cached is used either way.</param>
    public static async Task<ArchiveAttachment> ReadAsync(AttachmentFacts facts, string sourceId, string? path, CancellationToken ct,
        Func<string, CancellationToken, Task<string?>>? cachedText = null, bool ocr = true)
    {
        ct.ThrowIfCancellationRequested();
        var result = new ArchiveAttachment { SourceId = sourceId, Name = facts.Name, Size = Math.Max(0, facts.Size), Type = facts.Extension };
        if (facts.IsInline && !facts.IsPecReceiptData) { result.State = "excluded"; result.Problem = "inline-or-receipt"; return result; }
        if (facts.ScanStatus is AttachmentScanStatus.Threat or AttachmentScanStatus.Removed) { result.State = "excluded"; result.Problem = "antivirus"; return result; }
        if (AttachmentTextFormats.ArchiveReferenceReason(facts.Extension, facts.DetectedType) is { } reference)
        { result.State = "reference"; result.Problem = reference; return result; }
        if (path == null || !File.Exists(path))
        {
            result.State = facts.Size > 30L * 1024 * 1024 || facts.ScanDetail?.StartsWith("oltre il limite", StringComparison.Ordinal) == true ? "limited" : "retryable";
            result.Problem = facts.ScanDetail ?? "unavailable"; return result;
        }
        result.Version = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var hash = SHA256.Create();
            using var file = File.OpenRead(path);
            var header = new byte[FileSignature.HeaderLength];
            int count = file.Read(header, 0, header.Length);
            if (AttachmentTextFormats.ArchiveReferenceReason(facts.Extension, FileSignature.Detect(header, count)) is { } reason)
            { result.State = "reference"; result.Problem = reason; return null; }
            file.Position = 0;
            return Convert.ToBase64String(hash.ComputeHash(file));
        }, ct).ConfigureAwait(false);
        if (result.Version == null) return result;
        if (cachedText != null && await cachedText(result.Version, ct).ConfigureAwait(false) is { } cached)
        {
            ct.ThrowIfCancellationRequested();
            result.Text = cached;
            result.State = facts.IsPecReceiptData ? "facts-only" : "indexed";
            return result;
        }
        var extraction = await AttachmentTextExtractor.ExtractAsync(path, AttachmentTextExtractor.WholeDocumentChars, TimeSpan.FromSeconds(60), ct, ocr, archive: true).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        result.Problem = extraction.Error ?? extraction.Problem;
        result.State = extraction.Truncated ? "limited" : extraction.Error != null ? "retryable" : extraction.Problem ?? "indexed";
        result.TextLimited = extraction.Truncated;
        if (!extraction.Truncated && extraction.Error == null && extraction.Problem is "archive-container" or "executable" or "unsupported") result.State = "reference";
        if (result.State is "indexed" or "limited") result.Text = extraction.Text;
        if (facts.IsPecReceiptData && result.State == "indexed") result.State = "facts-only";
        return result;
    }

    public static void Add(ArchiveBody body, ArchiveAttachment attachment)
    {
        if (!IsReceipt(attachment.Name) && (long)body.Text.Length + body.Attachments.Where(a => !IsReceipt(a.Name)).Sum(a => (long)a.Text.Length) + attachment.Text.Length > AttachmentTextExtractor.WholeDocumentChars)
        { attachment.Text = string.Empty; attachment.State = "limited"; attachment.Problem = "combined-text-limit"; }
        body.Attachments.Add(attachment);
        if (!IsReceipt(attachment.Name) && attachment.State is not ("indexed" or "reference" or "excluded" or "facts-only")) body.AttachmentsLimited = true;
    }

    public static string WholeText(ArchiveBody body, bool includeAttachments = true)
    {
        if (body.Unavailable) throw new InvalidOperationException("Il testo originale non è disponibile.");
        if (body.Text.Length > AttachmentTextExtractor.WholeDocumentChars)
            throw new InvalidOperationException("Il corpo supera il limite di lettura integrale; nessun testo è stato tagliato.");
        if (!includeAttachments) return body.Text;
        if (body.AttachmentsLimited && body.Attachments.Count == 0)
            throw new InvalidOperationException("Il contenuto degli allegati non è stato letto; il testo completo non può essere fornito.");
        if (body.Attachments.Any(a => !IsReceipt(a.Name) && (a.TextLimited || a.State is not ("indexed" or "reference" or "excluded" or "facts-only") ||
            (a.State == "indexed" && a.ReaderRevision != ArchiveAttachment.CurrentReaderRevision))))
            throw new InvalidOperationException("Almeno un allegato non è leggibile per intero; il testo completo non può essere fornito.");
        var text = new StringBuilder(body.Text);
        foreach (var attachment in body.Attachments.Where(a => a.State is not ("excluded" or "facts-only") && !IsReceipt(a.Name)))
        {
            text.Append("\n\n--- Allegato: ").Append(attachment.Name).Append(" ---\n");
            text.Append(attachment.State == "indexed" ? attachment.Text : attachment.ReferenceDescription());
        }
        if (text.Length > AttachmentTextExtractor.WholeDocumentChars) throw new InvalidOperationException("Il testo completo supera il limite di lettura integrale; nessun testo è stato tagliato.");
        return text.ToString();
    }

    /// <summary>
    /// The original for a person to read (tray «Recupera testo originale»): body and attachments read whole, then the
    /// attachments not read whole, named with the reason. Never a model input: cards and analyses use <see cref="WholeText"/>.
    /// </summary>
    public static string ReadableText(ArchiveBody body)
    {
        if (body.Unavailable) throw new InvalidOperationException("Il testo originale non è disponibile.");
        var text = new StringBuilder(body.Text);
        var unread = body.Attachments.Where(a => a.State is not ("indexed" or "reference" or "excluded" or "facts-only")).ToList();
        foreach (var attachment in body.Attachments.Where(a => a.State == "indexed"))
            text.Append("\n\n--- Allegato: ").Append(attachment.Name).Append(" ---\n").Append(attachment.Text);
        foreach (var attachment in body.Attachments.Where(a => a.State == "reference"))
            text.Append("\n\n--- Allegato: ").Append(attachment.Name).Append(" ---\n").Append(attachment.ReferenceDescription());
        if (unread.Count > 0)
        {
            text.Append("\n\n--- Allegati non letti per intero (").Append(unread.Count).Append(") ---");
            foreach (var attachment in unread) text.Append("\n• ").Append(attachment.Name).Append(": ").Append(UnreadReason(attachment));
        }
        else if (body.AttachmentsLimited)
        {
            text.Append("\n\n--- Il contenuto degli allegati non è stato letto. ---");
        }
        if (text.Length > AttachmentTextExtractor.WholeDocumentChars) throw new InvalidOperationException("Il testo supera il limite di lettura integrale; nessun testo è stato tagliato.");
        return text.ToString();
    }

    private static string UnreadReason(ArchiveAttachment attachment) => attachment.State switch
    {
        "limited" when attachment.Problem == "combined-text-limit" => "testo complessivo dell'email oltre il limite di lettura",
        "limited" when attachment.Problem?.StartsWith("oltre il limite", StringComparison.Ordinal) == true => attachment.Problem!,
        "limited" => "testo oltre il limite di lettura",
        "retryable" => "non leggibile ora, verrà riprovato",
        "pending" => "non ancora letto",
        _ => ArchiveAttachment.StateLabel(attachment.State)
    };

    internal static bool IsReceipt(string name) => name.Equals("daticert.xml", StringComparison.OrdinalIgnoreCase);
}
