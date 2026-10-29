using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Models;

/// <summary>
/// The email being written (compose window or inline reply): what the writing actions work on. Read on the
/// Outlook UI thread; recipients are display names only.
/// </summary>
public sealed class ComposeDraft
{
    public string Subject { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Cc { get; set; } = string.Empty;
    /// <summary>Whole body as Outlook gives it: a reply or forward also carries the quoted thread.</summary>
    public string Body { get; set; } = string.Empty;
    /// <summary>Text selected in the editor, empty when nothing is selected.</summary>
    public string Selection { get; set; } = string.Empty;

    /// <summary>"new", "reply" or "forward", from the subject prefix Outlook writes.</summary>
    public string Kind => KindOf(Subject);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex ReplyPrefix = new(@"^\s*(re|r|ris|aw|sv|antw)\s*:", Options);
    private static readonly Regex ForwardPrefix = new(@"^\s*(fw|fwd|i|inoltro|wg|tr|rv)\s*:", Options);

    public static string KindOf(string? subject) =>
        ReplyPrefix.IsMatch(subject ?? string.Empty) ? "reply"
        : ForwardPrefix.IsMatch(subject ?? string.Empty) ? "forward"
        : "new";

    // Outlook's separator before a quoted message: header block (Da:/From:…), "Messaggio originale" line or a rule.
    private static readonly Regex QuoteStart = new(
        @"^\s*(-{2,}\s*(messaggio originale|original message|messaggio inoltrato|forwarded message)\b.*|_{8,}\s*|(da|from|von|de)\s*:\s*\S.*|il\s.+\sha scritto:\s*|on\s.+\swrote:\s*)$",
        Options | RegexOptions.Multiline);

    /// <summary>What the user wrote: the body before the quoted thread (signature included).</summary>
    public string OwnText => Split().Own;

    /// <summary>The quoted thread of a reply or forward, empty for a new email.</summary>
    public string Quoted => Split().Quoted;

    /// <summary>
    /// Written text without the closing lines the draft already ends with (its signature): QwenCloud repeated the
    /// signature on 21/10/2026 even when told not to, and inserting it doubled the block. Two lines at least must match,
    /// so a one-line closing like «Un saluto,» stays.
    /// </summary>
    public string WithoutDraftSignature(string text)
    {
        // The last lines written in the draft: Outlook's plain text of an HTML signature may space them with blank lines.
        var own = OwnText.Split('\n').Select(Comparable).Where(l => l.Length > 0).ToList();
        var signature = new HashSet<string>(own.Skip(Math.Max(0, own.Count - 8)), StringComparer.OrdinalIgnoreCase);
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").TrimEnd().Split('\n');
        int end = lines.Length, matched = 0;
        for (string line; end > 0 && ((line = Comparable(lines[end - 1])).Length == 0 || signature.Contains(line)); end--)
            if (line.Length > 0) matched++;
        return matched >= 2 ? string.Join("\n", lines.Take(end)).TrimEnd() : text ?? string.Empty;
    }

    /// <summary>
    /// «Oggetto: …» on the first line and the body. compose.write asks for it only when the subject is missing, but
    /// QwenCloud writes it in replies too even when told not to (21/10/2026): a reply keeps Outlook's own subject.
    /// </summary>
    public static (string Subject, string Body) SplitSubject(string text)
    {
        var trimmed = (text ?? string.Empty).TrimStart();
        var label = trimmed.TrimStart('*', '_');   // «**Oggetto:** …» in Markdown
        if (!label.StartsWith("Oggetto:", StringComparison.OrdinalIgnoreCase) && !label.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            return (string.Empty, trimmed.Trim());
        int end = trimmed.IndexOf('\n');
        var line = end < 0 ? trimmed : trimmed.Substring(0, end);
        var subject = line.Substring(line.IndexOf(':') + 1).Trim().Trim('*').Trim();
        return (subject, end < 0 ? string.Empty : trimmed.Substring(end + 1).Trim());
    }

    // Outlook's plain text of a signature link adds « <mailto:…>»; the model writes Markdown emphasis.
    private static readonly Regex LinkTarget = new(@"\s*<(mailto:|https?://)[^>]*>", Options);

    private static string Comparable(string line) =>
        Regex.Replace(LinkTarget.Replace(line, string.Empty).Replace(' ', ' '), @"\s+", " ").Trim().Trim('*', '_').Trim();

    private (string Own, string Quoted) Split()
    {
        var body = (Body ?? string.Empty).Replace("\r\n", "\n");
        var m = QuoteStart.Match(body);
        return m.Success ? (body.Substring(0, m.Index).Trim(), body.Substring(m.Index).Trim()) : (body.Trim(), string.Empty);
    }
}
