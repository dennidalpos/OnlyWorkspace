using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using OnlyWorkspace.Core.Security;
using UglyToad.PdfPig;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace OnlyWorkspace.AttachmentExtractor;

/// <summary>
/// Security mode (--security): the same walk as the text reading, with its depth, size and character limits,
/// while each file met on the way is also read statically: scripts, pages, PDF and Office links, emails' HTML
/// bodies. Nothing is executed or fetched; OCR stays off.
/// </summary>
internal static partial class ContentReader
{
    /// <summary>Text read for the addresses written in documents: the same bound as the whole-document reading.</summary>
    private const int InspectionChars = 4_000_000;

    [ThreadStatic] private static SecurityVisit? _inspection;

    public static AttachmentInspection Inspect(string path)
    {
        var result = new AttachmentInspection();
        bool ocr = OcrEnabled;
        _inspection = new SecurityVisit(result);
        OcrEnabled = false;
        try
        {
            var read = new AttachmentExtraction();
            try { read = Read(path, InspectionChars, result: read); }
            catch (Exception ex)
            {
                // What was read before the failure stays: findings are evidence, the error is a declared limit.
                result.Error = ex.GetType().Name + ": " + ex.Message;
            }
            result.Format = read.Problem == "unsupported" && result.Inspected > 0 ? "script" : read.Format;
            result.HasMacros = read.HasMacros;
            result.Truncated = read.Truncated;
            result.Problem = read.Problem is "ocr-off" or "scanned" ? null : read.Problem;
            if (result.Error == null && read.Problem != "unsupported") result.Inspected++;
            // Addresses written as text (PDF, Word, plain text…), unless a structured link already gave them.
            var written = new AttachmentInspection();
            HtmlActiveContent.TextLinks(read.Text, string.Empty, written);
            foreach (var link in written.Links)
                // An address that is only the visible text of a structured link is not another link.
                if (!result.Links.Any(l => l.Destination == link.Destination ||
                    string.Equals(WithoutScheme(l.Text), WithoutScheme(link.Destination), StringComparison.OrdinalIgnoreCase)))
                    HtmlActiveContent.AddLink(result, string.Empty, string.Empty, link.Destination);
        }
        finally
        {
            _inspection = null;
            OcrEnabled = ocr;
        }
        return result;
    }

    private static string WithoutScheme(string address)
    {
        address = address.Trim();
        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return address.Substring(8);
        if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return address.Substring(7);
        return address;
    }

    private sealed class SecurityVisit
    {
        private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase) { "html", "htm", "xhtml", "shtml", "svg", "hta", "wsf" };
        private const int MaxEntries = 200;

        private readonly AttachmentInspection _result;
        private readonly Stack<string> _names = new();
        private readonly HashSet<string> _read = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A file the text reading cannot open but the static reading did (script, page): not a gap.</summary>
        public bool Read(string path) => _read.Contains(path);

        public SecurityVisit(AttachmentInspection result) => _result = result;

        private string File => string.Join(" › ", _names.Reverse());

        public void Enter(string name) => _names.Push(name);
        public void Leave() { if (_names.Count > 0) _names.Pop(); }

        /// <summary>An email's HTML body inside the attachment: its links, never its scripts (Outlook does not run them).</summary>
        public void Body(string? html)
        {
            if (!string.IsNullOrWhiteSpace(html)) HtmlActiveContent.Inspect(html!, File, _result, bodyOnly: true);
        }

        public void Visit(string path, string? type, string ext)
        {
            if (File.Length > 0)
            {
                if (_result.Entries.Count < MaxEntries) _result.Entries.Add(new InspectionEntry { File = File, DetectedType = type });
                else if (_result.Entries.Count == MaxEntries && !_result.Limits.Any(l => l.Reason == "entries-limit")) ScriptStaticRules.Limit(_result, string.Empty, "entries-limit");
            }
            var language = ScriptStaticRules.LanguageOf(ext);
            if (language != null && type is null)
            {
                var script = ReadTextFile(path);
                if (script == null) ScriptStaticRules.Limit(_result, File, "unreadable-script");
                else ScriptStaticRules.Inspect(script, language.Value, File, _result);
                _read.Add(path);
                return;
            }
            if (PageExtensions.Contains(ext) && type is null)
            {
                var page = ReadTextFile(path);
                if (page == null) ScriptStaticRules.Limit(_result, File, "unreadable-script");
                else HtmlActiveContent.Inspect(page, File, _result);
                _read.Add(path);
                return;
            }
            switch (type)
            {
                case "exe":
                    ScriptStaticRules.Limit(_result, File, "binary-program");
                    break;
                case "pdf":
                    Pdf(path);
                    break;
                case "zip" when ext.StartsWith("doc") || ext.StartsWith("dot") || ext.StartsWith("xls") || ext.StartsWith("xlt") || ext == "xlam" ||
                                ext.StartsWith("ppt") || ext.StartsWith("pot") || ext.StartsWith("pps") || ext == "ppam":
                    Office(path, ext);
                    break;
            }
        }

        private void Pdf(string path)
        {
            // Inspect uncompressed name tokens, including #xx escapes. Compressed object streams stay hidden:
            // this raw scan is evidence of candidate actions, never proof that active content is absent.
            var bytes = System.IO.File.ReadAllBytes(path);
            var raw = Encoding.ASCII.GetString(bytes);
            if (HasName(raw, "/JavaScript") || HasName(raw, "/JS")) Finding("pdf.javascript");
            if (HasName(raw, "/Launch")) Finding("pdf.launch");
            try
            {
                using var document = PdfDocument.Open(path);
                foreach (var page in document.GetPages())
                    foreach (var link in page.GetHyperlinks())
                        if (!string.IsNullOrWhiteSpace(link.Uri)) HtmlActiveContent.AddLink(_result, File, (link.Text ?? string.Empty).Trim(), link.Uri);
            }
            catch (Exception)
            {
                // Protected or damaged: the text reading reports it.
            }
        }

        /// <summary>Compare whole names after one #xx decoding pass (ISO 32000-1, 7.2.2 and 7.3.5).</summary>
        private static bool HasName(string raw, string name)
        {
            for (int at = 0; at < raw.Length; at++)
            {
                if (raw[at] != '/') continue;
                int length = 1; // The leading slash in the comparison name.
                bool equal = true;
                for (at++; at < raw.Length && !EndsPdfName(raw[at]); at++)
                {
                    char value = raw[at];
                    if (value == '#' && at + 2 < raw.Length)
                    {
                        int high = Hex(raw[at + 1]), low = Hex(raw[at + 2]);
                        if (high >= 0 && low >= 0)
                        {
                            value = (char)((high << 4) | low);
                            at += 2;
                        }
                    }
                    // Decoded delimiters belong to the name; they cannot end it or introduce another name.
                    if (length >= name.Length || value != name[length]) equal = false;
                    length++;
                }
                if (equal && length == name.Length) return true;
                at--; // A raw slash delimiter may introduce the next name.
            }
            return false;
        }

        private static bool EndsPdfName(char value) => value is '\0' or '\t' or '\n' or '\f' or '\r' or ' ' or
            '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

        private static int Hex(char value) => value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'A' and <= 'F' => value - 'A' + 10,
            >= 'a' and <= 'f' => value - 'a' + 10,
            _ => -1
        };

        private void Office(string path, string ext)
        {
            try
            {
                using OpenXmlPackage package = ext.StartsWith("doc") || ext.StartsWith("dot") ? WordprocessingDocument.Open(path, false)
                    : ext.StartsWith("xls") || ext.StartsWith("xlt") || ext == "xlam" ? SpreadsheetDocument.Open(path, false)
                    : PresentationDocument.Open(path, false);
                var texts = new Dictionary<string, string>(StringComparer.Ordinal);
                if (package is WordprocessingDocument word && word.MainDocumentPart?.Document?.Body is { } body)
                    foreach (var hyperlink in body.Descendants<W.Hyperlink>())
                        if (hyperlink.Id?.Value is { } id && !texts.ContainsKey(id)) texts[id] = hyperlink.InnerText;

                foreach (var part in package.GetAllParts())
                {
                    foreach (var relationship in part.HyperlinkRelationships.Where(r => r.IsExternal))
                        HtmlActiveContent.AddLink(_result, File, part == (package as WordprocessingDocument)?.MainDocumentPart && texts.TryGetValue(relationship.Id, out var text) ? text : string.Empty,
                            relationship.Uri.OriginalString);
                    // A template fetched from the web when the document opens (attachedTemplate, TargetMode External).
                    foreach (var external in part.ExternalRelationships)
                    {
                        if (!external.RelationshipType.EndsWith("/attachedTemplate", StringComparison.Ordinal)) continue;
                        var target = external.Uri.OriginalString;
                        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) || target.StartsWith("\\\\", StringComparison.Ordinal))
                        {
                            Finding("ooxml.remote-template", target);
                            HtmlActiveContent.AddLink(_result, File, string.Empty, target);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // The text reading reports damaged or unsupported packages.
            }
        }

        private void Finding(string rule, string? snippet = null)
        {
            if (_result.Findings.Any(f => f.Rule == rule && f.File == File)) return;
            if (_result.Findings.Count >= ScriptStaticRules.MaxFindings) { ScriptStaticRules.Limit(_result, File, "findings-limit"); return; }
            _result.Findings.Add(new InspectionFinding { Rule = rule, File = File, Snippet = snippet == null ? string.Empty : snippet.Length > 200 ? snippet.Substring(0, 200) + "…" : snippet });
        }
    }
}
