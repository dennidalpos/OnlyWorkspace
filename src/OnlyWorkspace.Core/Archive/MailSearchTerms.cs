using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Archive;

/// <summary>Broad local check for a source whose searchable text was indexed only in part.</summary>
public static class MailSearchTerms
{
    private static readonly Regex Term = new("\"([^\"]+)\"", RegexOptions.Compiled);

    public static IReadOnlyList<string> Terms(string question)
    {
        var terms = new List<string>();
        foreach (Match term in Term.Matches(ArchiveText.BuildFtsQuery(question))) terms.Add(term.Groups[1].Value);
        return terms;
    }

    public static bool Matches(string question, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var codes = ArchiveSearchText.Codes(question);
        if (codes.Count > 0) return codes.Exists(code => ArchiveSearchText.Codes(text).Exists(value => value.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0));
        foreach (var term in Terms(question))
            if (text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
