using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit.Text;

namespace OnlyWorkspace.Core.Security;

/// <summary>
/// Active content of an HTML attachment (or an attached email's HTML body): script blocks and handlers read
/// with the script rules, password forms and where they send, automatic redirects and links. Nothing is rendered.
/// </summary>
public static class HtmlActiveContent
{
    public const int MaxLinks = 400;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Absolute = new(@"^\s*https?://", Options, Timeout);
    private static readonly Regex RefreshUrl = new(@"url\s*=\s*['""]?\s*(https?://[^'""\s;]+)", Options, Timeout);
    private static readonly Regex ScriptRedirect = new(@"(?<![\w$])(window\.|document\.|top\.|self\.)?location(\.href)?\s*=\s*['""`]https?://|location\.(replace|assign)\s*\(\s*['""`]https?://", Options, Timeout);
    private static readonly Regex ScriptSend = new(@"\b(fetch|sendBeacon|XMLHttpRequest|\.ajax|\$\.post|axios\.post)\b[^\n]*https?://|https?://[^\n]*\b(fetch|sendBeacon|XMLHttpRequest)\b", Options, Timeout);

    /// <param name="bodyOnly">An email's HTML body: links only, its scripts never run in Outlook.</param>
    public static void Inspect(string html, string file, AttachmentInspection result, bool bodyOnly = false)
    {
        if (html.Length > ScriptStaticRules.MaxChars)
        {
            ScriptStaticRules.Limit(result, file, "text-limit");
            html = html.Substring(0, ScriptStaticRules.MaxChars);
        }
        if (!bodyOnly) result.Inspected++;
        try
        {
            if (!bodyOnly) ActiveContent(html, file, result);
            foreach (var link in SecurityLinks.Read(html, string.Empty)) AddLink(result, file, link.Text, link.Destination);
        }
        catch (RegexMatchTimeoutException)
        {
            ScriptStaticRules.Limit(result, file, "timeout");
        }
    }

    private static void ActiveContent(string html, string file, AttachmentInspection result)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        StringBuilder? script = null;
        var language = ScriptStaticRules.Language.JScript;
        int scriptLine = 0, passwordLine = 0;
        string? formAction = null;
        bool password = false, external = false;
        var allScripts = new StringBuilder();

        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlScriptDataToken data)
            {
                script?.Append(data.Data);
                continue;
            }
            if (token is not HtmlTagToken tag) continue;
            string name = tag.Name.ToLowerInvariant();
            string? Attr(string attribute) => tag.Attributes.FirstOrDefault(a => a.Name.Equals(attribute, StringComparison.OrdinalIgnoreCase))?.Value;

            if (name == "script")
            {
                if (!tag.IsEndTag)
                {
                    script = new StringBuilder();
                    scriptLine = tokenizer.LineNumber;
                    var kind = (Attr("type") ?? Attr("language") ?? string.Empty).ToLowerInvariant();
                    language = kind.Contains("vbs") ? ScriptStaticRules.Language.VBScript : ScriptStaticRules.Language.JScript;
                    AddAt(result, "html.script", file, html, scriptLine);
                    var src = Attr("src");
                    if (!string.IsNullOrWhiteSpace(src)) AddLink(result, file, string.Empty, src!);
                }
                else if (script != null)
                {
                    var code = script.ToString();
                    allScripts.AppendLine(code);
                    ScriptStaticRules.Inspect(code, language, file, result, Math.Max(0, scriptLine - 1));
                    result.Inspected--;
                    script = null;
                }
                continue;
            }
            if (tag.IsEndTag)
            {
                if (name == "form") formAction = null;
                continue;
            }

            foreach (var attribute in tag.Attributes)
            {
                // Inline handlers are scripts too: «onload="…"».
                if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attribute.Value))
                {
                    allScripts.AppendLine(attribute.Value!);
                    ScriptStaticRules.Inspect(attribute.Value!, ScriptStaticRules.Language.JScript, file, result, Math.Max(0, tokenizer.LineNumber - 1));
                    result.Inspected--;
                }
            }

            switch (name)
            {
                case "form":
                    formAction = Attr("action");
                    if (!string.IsNullOrWhiteSpace(formAction)) AddLink(result, file, string.Empty, formAction!);
                    break;
                case "input" when string.Equals(Attr("type"), "password", StringComparison.OrdinalIgnoreCase):
                    password = true;
                    passwordLine = tokenizer.LineNumber;
                    var target = Attr("formaction") ?? formAction;
                    if (target != null && Absolute.IsMatch(target)) external = true;
                    break;
                case "meta" when string.Equals(Attr("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase):
                    var refresh = RefreshUrl.Match(Attr("content") ?? string.Empty);
                    if (refresh.Success)
                    {
                        AddAt(result, "html.redirect", file, html, tokenizer.LineNumber);
                        AddLink(result, file, string.Empty, refresh.Groups[1].Value);
                    }
                    break;
                case "iframe" or "frame" or "embed":
                    var source = Attr("src");
                    if (!string.IsNullOrWhiteSpace(source)) AddLink(result, file, string.Empty, source!);
                    break;
            }
        }

        var scripts = allScripts.ToString();
        if (ScriptRedirect.IsMatch(scripts)) AddAt(result, "html.redirect", file, html, LineOfMatch(html, ScriptRedirect));
        if (password && !external && ScriptSend.IsMatch(scripts)) external = true;
        if (password) AddAt(result, external ? "html.password-external" : "html.password", file, html, passwordLine);
    }

    private static int LineOfMatch(string html, Regex pattern)
    {
        var match = pattern.Match(html);
        if (!match.Success) return 1;
        int line = 1;
        for (int i = 0; i < match.Index; i++) if (html[i] == '\n') line++;
        return line;
    }

    private static void AddAt(AttachmentInspection result, string rule, string file, string html, int line)
    {
        int index = 0;
        for (int current = 1; current < line && index >= 0; current++)
        {
            index = html.IndexOf('\n', index);
            if (index >= 0) index++;
        }
        ScriptStaticRules.Add(result, rule, file, html, Math.Max(0, index), 0);
    }

    public static void AddLink(AttachmentInspection result, string file, string text, string destination)
    {
        destination = destination.Trim();
        if (destination.Length == 0 || result.Links.Any(l => l.File == file && l.Destination == destination && l.Text == text)) return;
        if (result.Links.Count >= MaxLinks)
        {
            ScriptStaticRules.Limit(result, file, "links-limit");
            return;
        }
        result.Links.Add(new InspectionLink { File = file, Text = text, Destination = destination });
    }

    /// <summary>Web addresses written in a document's text («www.…», «https://…»).</summary>
    public static void TextLinks(string text, string file, AttachmentInspection result)
    {
        foreach (var link in SecurityLinks.Read(string.Empty, text)) AddLink(result, file, string.Empty, link.Destination);
    }
}
