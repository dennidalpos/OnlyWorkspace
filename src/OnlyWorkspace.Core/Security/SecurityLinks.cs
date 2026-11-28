using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit.Text;

namespace OnlyWorkspace.Core.Security;

public sealed class SecurityLink
{
    public SecurityLink(string text, string destination)
    {
        Text = text;
        Destination = destination;
    }
    public string Text { get; }
    public string Destination { get; }
}

internal static class SecurityLinks
{
    // URL discovery only; Uri performs validation and host parsing.
    private static readonly Regex PlainUrl = new(@"\b(?:https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase);

    internal static IEnumerable<SecurityLink> Read(string html, string plainText)
    {
        using var reader = new StringReader(html ?? string.Empty);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        string? href = null;
        var text = new StringBuilder();
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag && tag.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                if (href != null) yield return new SecurityLink(text.ToString().Trim(), href);
                href = tag.IsEndTag ? null : tag.Attributes.FirstOrDefault(a => a.Name.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value;
                text.Clear();
            }
            else if (href != null && token is HtmlDataToken data) text.Append(data.Data);
        }
        if (href != null) yield return new SecurityLink(text.ToString().Trim(), href);
        foreach (Match match in PlainUrl.Matches(plainText ?? string.Empty))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ')', ']', '}');
            yield return new SecurityLink(url, url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url);
        }
    }
}
