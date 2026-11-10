using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Utils;

/// <summary>
/// Filter text typed by the user: "*" = any text, "?" = one character, matched on whole words
/// ("fattur*" finds "RE: Fattura 12", "*@acme.it" an address). Without wildcards it means "contains".
/// </summary>
public sealed class WildcardPattern
{
    private readonly Regex _regex;

    public bool HasWildcards { get; }

    /// <summary>Longest fragment without wildcards: DASL documents only LIKE '%text%', so the store filters on this.</summary>
    public string LongestLiteral { get; }

    private WildcardPattern(string text)
    {
        HasWildcards = text.IndexOfAny(new[] { '*', '?' }) >= 0;
        LongestLiteral = text.Split('*', '?').OrderByDescending(p => p.Trim().Length).First().Trim();

        var sb = new StringBuilder();
        if (HasWildcards)
        {
            sb.Append(@"(?<!\w)");
            foreach (char c in text) sb.Append(c == '*' ? ".*" : c == '?' ? "." : Regex.Escape(c.ToString()));
            sb.Append(@"(?!\w)");
        }
        else
        {
            sb.Append(Regex.Escape(text));
        }
        _regex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    public static WildcardPattern? Parse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new WildcardPattern(text!.Trim());

    public bool IsMatch(params string?[] values) => values.Any(v => !string.IsNullOrEmpty(v) && _regex.IsMatch(v!));
}

/// <summary>
/// Exact wildcard check on the rows the coarse DASL filter returned. Filters without wildcards
/// are left to the store, which already matched them.
/// </summary>
public sealed class EmailRowFilter
{
    private readonly WildcardPattern? _sender;
    private readonly WildcardPattern? _recipient;
    private readonly WildcardPattern? _subject;
    private readonly bool _eitherPerson;

    private EmailRowFilter(WildcardPattern? sender, WildcardPattern? recipient, WildcardPattern? subject, bool eitherPerson)
    {
        _sender = sender;
        _recipient = recipient;
        _subject = subject;
        _eitherPerson = eitherPerson;
    }

    /// <summary>Null when no filter of the query uses wildcards.</summary>
    public static EmailRowFilter? From(EmailSearchQuery query)
    {
        var sender = Wild(query.Sender);
        var recipient = Wild(query.Recipient);
        var subject = Wild(query.Subject);
        return sender == null && recipient == null && subject == null ? null
            : new EmailRowFilter(sender, recipient, subject, query.EitherPerson);
    }

    public bool NeedsRecipients => _recipient != null;

    public bool Matches(string? subject, string? recipients, params string?[] senderFields)
    {
        if (_subject != null && !_subject.IsMatch(subject)) return false;
        if (_eitherPerson && _sender != null && _recipient != null)
        {
            return _sender.IsMatch(senderFields) || _recipient.IsMatch(recipients);
        }
        return (_sender == null || _sender.IsMatch(senderFields)) && (_recipient == null || _recipient.IsMatch(recipients));
    }

    private static WildcardPattern? Wild(string? text)
    {
        var pattern = WildcardPattern.Parse(text);
        return pattern != null && pattern.HasWildcards ? pattern : null;
    }
}
