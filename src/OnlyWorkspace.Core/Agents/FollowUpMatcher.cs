using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Agents;

public static class FollowUpMatcher
{
    private static readonly Regex ReferencePattern = new(@"\b(?:ORD(?:INE)?|INV(?:OICE)?|PO|RIF(?:ERIMENTO)?|FATT(?:URA)?|TICKET)(?:[\s:./#-]+|(?=\d))((?=[A-Z0-9/-]*\d)[A-Z0-9][A-Z0-9/-]{3,})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Reply and forward prefixes, Italian Outlook included (R:, I:).
    private static readonly string[] Prefixes = { "Re:", "R:", "Fw:", "Fwd:", "I:" };

    public static string NormalizeSubject(string subject)
    {
        var text = (subject ?? string.Empty).Trim();
        while (Prefixes.Any(p => text.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            text = text.Substring(text.IndexOf(':') + 1).Trim();
        return text;
    }

    public static IReadOnlyList<string> References(string text) => ReferencePattern.Matches((text ?? string.Empty).Substring(0, Math.Min((text ?? string.Empty).Length, 6000)))
        .Cast<Match>().Select(m => m.Groups[1].Value.ToUpperInvariant()).Distinct().Take(3).ToList();

    public static string? Relation(string selectedSubject, string candidateSubject, string candidatePreview,
        IReadOnlyCollection<string> references, bool sameParty)
    {
        var subject = NormalizeSubject(selectedSubject);
        bool sameSubject = subject.Length >= 4 && NormalizeSubject(candidateSubject).Equals(subject, StringComparison.OrdinalIgnoreCase);
        bool sameReference = references.Any(reference =>
            (candidateSubject ?? string.Empty).IndexOf(reference, StringComparison.OrdinalIgnoreCase) >= 0 ||
            (candidatePreview ?? string.Empty).IndexOf(reference, StringComparison.OrdinalIgnoreCase) >= 0);
        if (sameSubject && sameParty) return "Stessa controparte e oggetto";
        if (sameReference && sameParty) return "Stessa controparte e riferimento";
        if (sameReference) return "Riferimento condiviso (da verificare)";
        if (sameSubject) return "Stesso oggetto (da verificare)";
        return null;
    }
}
