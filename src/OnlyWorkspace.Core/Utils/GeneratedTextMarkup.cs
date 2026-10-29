using System;
using System.IO;
using MimeKit.Text;

namespace OnlyWorkspace.Core.Utils;

/// <summary>
/// A versioned, self-declared HTML provenance signal. It is not a signature, an AI classifier,
/// or proof that a message/container preserved its original metadata.
/// </summary>
public static class GeneratedTextMarkup
{
    public const int MaxHtmlCharacters = 4 * 1024 * 1024;
    private const string Prefix = "OnlyWorkspace:generated-text:";
    private const string Begin = Prefix + "v1:begin";
    private const string End = Prefix + "v1:end";

    /// <summary>Call only for generated HTML, before combining it with a signature or quoted original.</summary>
    public static string WrapHtml(string generatedHtml)
    {
        if (generatedHtml == null) throw new ArgumentNullException(nameof(generatedHtml));
        return string.IsNullOrWhiteSpace(generatedHtml) ? generatedHtml
            : "<!--" + Begin + "-->" + generatedHtml + "<!--" + End + "-->";
    }

    /// <summary>Reads comments without rendering HTML or contacting any service. Absence is inconclusive.</summary>
    public static GeneratedTextMarkupInspection InspectHtml(string html)
    {
        if (html == null) throw new ArgumentNullException(nameof(html));
        if (html.Length > MaxHtmlCharacters) throw new ArgumentException("HTML input exceeds the inspection limit.", nameof(html));
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader);
        bool open = false, malformed = false;
        int fragments = 0, unsupported = 0;
        int sourceLine = 1, lineStart = 0;
        while (tokenizer.ReadNextToken(out var token))
        {
            // Tokenization avoids treating an example in text, a tag attribute or script data as metadata.
            if (token is not HtmlCommentToken comment) continue;
            var value = comment.Comment.Trim();
            if (!value.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            // MimeKit's HTML recovery emits a comment even at EOF without its closing delimiter.
            // Validate the consumed source boundary, not the repaired token's serialized form.
            while (sourceLine < tokenizer.LineNumber)
            {
                lineStart = html.IndexOf('\n', lineStart) + 1;
                sourceLine++;
            }
            int end = lineStart + tokenizer.LinePosition - 1;
            if (comment.IsBogusComment || end < 3 || end > html.Length
                || string.CompareOrdinal(html, end - 3, "-->", 0, 3) != 0)
            {
                malformed = true;
                continue;
            }
            if (value == Begin)
            {
                if (open) malformed = true;
                open = true;
            }
            else if (value == End)
            {
                if (!open) malformed = true;
                else fragments++;
                open = false;
            }
            else unsupported++;
        }
        return new GeneratedTextMarkupInspection(fragments, malformed || open, unsupported);
    }
}

public sealed class GeneratedTextMarkupInspection
{
    internal GeneratedTextMarkupInspection(int declaredFragments, bool malformed, int unsupportedMarkers)
    {
        DeclaredFragments = declaredFragments;
        Malformed = malformed;
        UnsupportedMarkers = unsupportedMarkers;
    }

    public int DeclaredFragments { get; }
    public bool Malformed { get; }
    public int UnsupportedMarkers { get; }
    public string Status => Malformed ? "malformed" : UnsupportedMarkers > 0 ? "unsupported"
        : DeclaredFragments > 0 ? "declared" : "not-found";
}
