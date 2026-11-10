using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.AttachmentExtractor;

/// <summary>
/// Digitally signed files (.p7m, CAdES): a CMS SignedData envelope around the original document, which is
/// read like any other file once unwrapped. The signature is not verified.
/// </summary>
internal static partial class ContentReader
{
    private static AttachmentExtraction ReadSigned(string path, int maxChars, int depth)
    {
        var content = UnwrapSigned(File.ReadAllBytes(path));
        if (content == null) return new AttachmentExtraction { Format = "p7m", Problem = "unsupported" };
        if (depth >= MaxMsgDepth || content.Length > MaxNestedBytes - _expandedBytes)
            return new AttachmentExtraction { Format = "p7m", Problem = "container-limit", Truncated = true };
        _expandedBytes += content.Length;

        // atto.pdf.p7m → .pdf; a countersigned atto.pdf.p7m.p7m → .pdf.p7m, unwrapped again by Read.
        var extension = ExtensionChain(Path.GetFileNameWithoutExtension(path));
        if (extension.Length == 0)
        {
            extension = FileSignature.Detect(content, Math.Min(content.Length, FileSignature.HeaderLength)) switch
            {
                "pdf" => ".pdf",
                "rtf" => ".rtf",
                _ => string.Empty
            };
        }

        var inner = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + extension);
        try
        {
            File.WriteAllBytes(inner, content);
            return Read(inner, maxChars, depth + 1);
        }
        finally
        {
            try { File.Delete(inner); } catch (IOException) { } catch (UnauthorizedAccessException) { } // temp copy: a leftover is cleaned with %TEMP%
        }
    }

    /// <returns>The signed document; null when the file is not a CMS envelope or the signature is detached.</returns>
    private static byte[]? UnwrapSigned(byte[] data)
    {
        try
        {
            // DER starts with 0x30 (SEQUENCE); otherwise the envelope is Base64, with or without PEM header lines.
            if (data.Length > 0 && data[0] != 0x30)
            {
                var base64 = new StringBuilder();
                foreach (var line in Encoding.ASCII.GetString(data).Split('\n'))
                {
                    if (!line.StartsWith("-----", StringComparison.Ordinal)) base64.Append(line.Trim());
                }
                data = Convert.FromBase64String(base64.ToString());
            }

            var cms = new SignedCms();
            cms.Decode(data);
            var content = cms.ContentInfo.Content;
            return content != null && content.Length > 0 ? content : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Extension with the ones under a .p7m: ".pdf" for atto.pdf, ".pdf.p7m" for atto.pdf.p7m.</summary>
    private static string ExtensionChain(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".p7m", StringComparison.OrdinalIgnoreCase)
            ? ExtensionChain(Path.GetFileNameWithoutExtension(name)) + extension
            : extension;
    }
}
