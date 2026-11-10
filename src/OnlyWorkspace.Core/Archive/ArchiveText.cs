using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Archive;

/// <summary>Text rules of the archive: subjects compared across replies, and free text turned into a full-text query.</summary>
public static class ArchiveText
{
    private static readonly Regex ReplyPrefix = new(
        @"^\s*((re|r|rif|fw|fwd|i|inoltra|aw|wg|tr|res|enc)\s*(\[\d+\])?\s*:|\[[^\]]{1,12}\])\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Word = new(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "che", "chi", "con", "per", "del", "dei", "della", "delle", "dello", "degli", "dal", "dalla", "dai", "nel", "nella", "nei", "nelle",
        "sul", "sulla", "sui", "alla", "alle", "allo", "agli", "una", "uno", "gli", "non", "sono", "come", "tra", "fra", "anche", "più", "piu",
        "tutte", "tutti", "tutto", "tutta", "quali", "quale", "cosa", "dove", "quando", "stato", "stata", "stati", "state", "essere", "avere",
        "the", "and", "for", "with", "from", "that", "this", "are", "was", "have", "has",
        "email", "mail", "messaggi", "messaggio", "riepilogo", "riassunto", "sintesi", "elenco", "trova", "cerca", "mostra", "dimmi"
    };

    /// <summary>Subject without reply/forward prefixes and tags, lower case: the same for a message and its replies.</summary>
    public static string NormalizeSubject(string? subject)
    {
        var s = subject ?? string.Empty;
        for (int i = 0; i < 10; i++)
        {
            var m = ReplyPrefix.Match(s);
            if (!m.Success) break;
            s = s.Substring(m.Length);
        }
        return Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
    }

    /// <summary>
    /// FTS5 query from free text: significant words in OR (the best matches rank first), long words as a
    /// prefix without the ending so that "specifiche" also finds "specifica". Empty when nothing is left.
    /// </summary>
    public static string BuildFtsQuery(string? text)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Word.Matches((text ?? string.Empty).ToLowerInvariant()))
        {
            var w = m.Value;
            if (w.Length < 3 || StopWords.Contains(w)) continue;
            var term = Stem(w);
            if (seen.Add(term)) terms.Add(term.EndsWith("*", StringComparison.Ordinal) ? "\"" + term.TrimEnd('*') + "\"*" : "\"" + term + "\"");
        }
        return string.Join(" OR ", terms);
    }

    private static string Stem(string word)
    {
        if (word.Length < 6 || char.IsDigit(word[word.Length - 1])) return word;
        var sb = new StringBuilder(word);
        while (sb.Length > 4 && "aeiouàèéìòù".IndexOf(sb[sb.Length - 1]) >= 0) sb.Length--;
        if (sb.Length > 4 && sb[sb.Length - 1] == 'h') sb.Length--;
        return sb.Length == word.Length ? word : sb + "*";
    }
}
