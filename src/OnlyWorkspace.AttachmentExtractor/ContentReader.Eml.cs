using System;
using MimeKit;

namespace OnlyWorkspace.AttachmentExtractor;

/// <summary>
/// Attached emails saved as files (.eml, MIME): same output as an Outlook .msg, with the text of their own
/// attachments. Signatures are not verified.
/// </summary>
internal static partial class ContentReader
{
    /// <returns>true when one of the attachments has VBA macros.</returns>
    private static bool ReadEml(MimeMessage message, string workDir, LimitedText text, int depth)
    {
        if (text.Enabled)
        {
            AppendField(text, "Da", message.From.ToString());
            AppendField(text, "A", message.To.ToString());
            if (message.Date != DateTimeOffset.MinValue) AppendField(text, "Data", message.Date.LocalDateTime.ToString("dd/MM/yyyy HH:mm"));
            AppendField(text, "Oggetto", message.Subject);

            var body = message.TextBody;
            if (string.IsNullOrWhiteSpace(body)) body = HtmlToText(message.HtmlBody);
            text.AppendBlock(body);
        }
        _inspection?.Body(message.HtmlBody);

        bool macros = false;
        // Leaf parts, whatever their disposition: a PEC marks postacert.eml as inline.
        foreach (var entity in message.BodyParts)
        {
            var name = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
            var attached = entity as MessagePart;
            var part = entity as MimePart;

            if (attached == null)
            {
                // Unnamed parts are the body; images shown in it and detached signatures are not documents.
                if (part?.Content == null || string.IsNullOrWhiteSpace(name)) continue;
                if (!part.IsAttachment && part.ContentType.IsMimeType("image", "*")) continue;
                if (part.ContentType.IsMimeType("application", "pkcs7-signature") || part.ContentType.IsMimeType("application", "x-pkcs7-signature")) continue;
            }
            else if (string.IsNullOrWhiteSpace(name))
            {
                name = attached.Message?.Subject ?? "email";
            }

            if (attached != null)
            {
                if (depth >= MaxMsgDepth || attached.Message == null)
                {
                    text.AppendLine($"[Email allegata: {name} — non letta]");
                    text.Incomplete("depth-limit");
                    continue;
                }
                text.AppendLine($"[Email allegata: {name}]");
                _inspection?.Enter(name!);
                try { macros |= ReadEml(attached.Message, workDir, text, depth + 1); }
                finally { _inspection?.Leave(); }
                continue;
            }

            // Encoded length: never below the decoded one.
            IMimeContent content = part!.Content!;
            macros |= ReadNestedFile(name!, content.Stream?.Length ?? 0,stream => content.DecodeTo(stream), workDir, text, depth, encodedLength: true);
        }
        return macros;
    }
}
