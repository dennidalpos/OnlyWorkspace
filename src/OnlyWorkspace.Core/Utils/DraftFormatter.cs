using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Utils;

/// <summary>
/// Turns the model's markdown draft into email body content (same subset rendered by the task pane).
/// </summary>
public static class DraftFormatter
{
    private static readonly Regex HeadingRegex = new(@"^#{1,6}\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^\s*[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberedRegex = new(@"^\s*(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RuleOrFenceRegex = new(@"^\s*(---|\*\*\*|___|```.*)\s*$", RegexOptions.Compiled);
    private static readonly Regex CodeRegex = new(@"`([^`\n]+)`", RegexOptions.Compiled);
    private static readonly Regex BoldRegex = new(@"\*\*([^*\n]+)\*\*|(?<![\w])__([^_\n]+)__(?![\w])", RegexOptions.Compiled);
    private static readonly Regex ItalicRegex = new(@"(?<![*\w])\*([^*\n]+)\*(?![\w])|(?<![\w])_([^_\n]+)_(?![\w])", RegexOptions.Compiled);

    public static string ToPlainText(string markdown)
    {
        var sb = new StringBuilder();
        foreach (var raw in SplitLines(markdown))
        {
            if (RuleOrFenceRegex.IsMatch(raw)) continue;
            Match m;
            string line;
            if ((m = HeadingRegex.Match(raw)).Success) line = m.Groups[1].Value;
            else if ((m = BulletRegex.Match(raw)).Success) line = "- " + m.Groups[1].Value;
            else if ((m = NumberedRegex.Match(raw)).Success) line = m.Groups[1].Value + ". " + m.Groups[2].Value;
            else line = raw;
            sb.Append(StripInline(line)).Append("\r\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Word-style paragraphs (class MsoNormal) so the draft inherits the reply's font.</summary>
    public static string ToHtml(string markdown)
    {
        var sb = new StringBuilder();
        string? listTag = null;

        void CloseList()
        {
            if (listTag == null) return;
            sb.Append("</").Append(listTag).Append('>');
            listTag = null;
        }

        void OpenList(string tag)
        {
            if (listTag == tag) return;
            CloseList();
            sb.Append('<').Append(tag).Append(" style=\"margin-top:0;margin-bottom:0\">");
            listTag = tag;
        }

        foreach (var raw in SplitLines(markdown))
        {
            if (RuleOrFenceRegex.IsMatch(raw)) continue;
            Match m;
            if ((m = BulletRegex.Match(raw)).Success)
            {
                OpenList("ul");
                sb.Append("<li class=MsoNormal>").Append(InlineHtml(m.Groups[1].Value)).Append("</li>");
                continue;
            }
            if ((m = NumberedRegex.Match(raw)).Success)
            {
                OpenList("ol");
                sb.Append("<li class=MsoNormal>").Append(InlineHtml(m.Groups[2].Value)).Append("</li>");
                continue;
            }

            CloseList();
            if (raw.Trim().Length == 0)
                sb.Append("<p class=MsoNormal>&nbsp;</p>");
            else if ((m = HeadingRegex.Match(raw)).Success)
                sb.Append("<p class=MsoNormal><b>").Append(InlineHtml(m.Groups[1].Value)).Append("</b></p>");
            else
                sb.Append("<p class=MsoNormal>").Append(InlineHtml(raw)).Append("</p>");
        }
        CloseList();
        // Blank line between the draft and the signature/quoted original.
        sb.Append("<p class=MsoNormal>&nbsp;</p>");
        return sb.ToString();
    }

    private static string[] SplitLines(string markdown) =>
        (markdown ?? string.Empty).Trim().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string StripInline(string text)
    {
        text = CodeRegex.Replace(text, "$1");
        text = BoldRegex.Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return ItalicRegex.Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    private static string InlineHtml(string text)
    {
        text = WebUtility.HtmlEncode(text);
        text = CodeRegex.Replace(text, "$1");
        text = BoldRegex.Replace(text, m => "<b>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</b>");
        return ItalicRegex.Replace(text, m => "<i>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</i>");
    }
}
