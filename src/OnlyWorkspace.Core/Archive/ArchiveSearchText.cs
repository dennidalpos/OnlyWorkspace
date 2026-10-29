using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Archive;

/// <summary>Literal code search and explicit local fact filters; never invokes a model.</summary>
public static class ArchiveSearchText
{
    private static readonly Regex Code = new(@"[\p{L}\p{Nd}]+(?:[-/_.][\p{L}\p{Nd}]+)*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Reference = new(@"\b(ordine|order|fattura|invoice|protocollo|protocol)\s+(?:n(?:umero)?\.?\s*)?(?<value>[\p{L}\p{Nd}]+(?:[-/_.][\p{L}\p{Nd}]+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Amount = new(@"\b(?:importo|amount)\s*(?<op>>=|<=|=|>|<)?\s*(?<value>\d+(?:[.,]\d{1,2})?)\s*(?<currency>EUR|USD|GBP|CHF)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Due = new(@"\b(?:scadenza|due)\s*(?<op>entro|dal|al|by|from|on)?\s*(?<value>\d{4}-\d{2}-\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // These are instructions to the chat, not properties of the email. Keep this separate from literal search.
    private static readonly Regex ChatRequest = new(
        @"(?<![\p{L}\p{Nd}_./@-])(?:cosa\s+(?:chiede|dice)|chi\s+(?:(?:la|lo|le|li)\s+)?(?:manda|invia|scrive)|" +
        @"(?:fammi|fai|farmi)\s+(?:un\s+)?(?:riassunto|riepilogo|elenco|resoconto)|" +
        @"riassumi(?:mi)?|riassumermi|riepiloga(?:mi)?|riepilogarmi|sintetizza|elenca(?:mi)?|elencarmi|mostra(?:mi)?|cerca(?:mi)?|trova(?:mi)?|" +
        @"riassunto|riepilogo|sintesi|elenco|resoconto|e-?mail|mail|messaggi|messaggio)(?![\p{L}\p{Nd}_./@-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ChatQuoted = new(@"""[^""]*""|«[^»]*»", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> ChatConnectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "fammi", "mi", "me", "puoi", "potresti", "vorrei", "voglio", "dammi", "dimmi", "un", "uno", "una",
        "il", "lo", "la", "i", "gli", "le", "l", "di", "da", "del", "dello", "della", "dei", "degli", "delle", "dell",
        "a", "al", "allo", "alla", "ai", "agli", "alle", "all", "in", "nel", "nella", "nei", "negli", "nelle", "nell",
        "su", "sul", "sullo", "sulla", "sui", "sugli", "sulle", "sull", "per", "con", "e", "ed", "che", "favore", "grazie"
    };
    public static List<string> Codes(string? text) => Code.Matches(text ?? "").Cast<Match>().Select(m => m.Value)
        .Where(v => v.Any(char.IsDigit)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public static string CodeTerms(string? text) => string.Join(" ", Codes(text));
    public static string Literal(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

    /// <summary>Only labelled, unambiguous forms are consumed. Other words remain searchable.</summary>
    public static string Parse(string? text, IList<ArchiveFactFilter> filters)
    {
        string remaining = text ?? "";
        remaining = Reference.Replace(remaining, m =>
        {
            string value = m.Groups["value"].Value;
            if (!value.Any(char.IsDigit)) return m.Value;
            var label = m.Groups[1].Value.ToLowerInvariant();
            filters.Add(new ArchiveFactFilter { Kind = label is "ordine" or "order" ? ArchiveFactKind.Order : label is "fattura" or "invoice" ? ArchiveFactKind.Invoice : ArchiveFactKind.Protocol, Value = value.ToUpperInvariant() });
            return " ";
        });
        remaining = Amount.Replace(remaining, m =>
        {
            if (!decimal.TryParse(m.Groups["value"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return m.Value;
            string op = m.Groups["op"].Value;
            if (op is ">" or "<") return m.Value; // No silently inclusive interpretation.
            filters.Add(new ArchiveFactFilter { Kind = ArchiveFactKind.Amount, Currency = m.Groups["currency"].Value.ToUpperInvariant(), AmountMin = op == "<=" ? null : amount, AmountMax = op == ">=" ? null : amount });
            return " ";
        });
        remaining = Due.Replace(remaining, m =>
        {
            string day = m.Groups["value"].Value;
            if (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return m.Value;
            string op = m.Groups["op"].Value.ToLowerInvariant();
            filters.Add(new ArchiveFactFilter { Kind = ArchiveFactKind.Deadline, DateFrom = op is "entro" or "by" ? null : day, DateTo = op is "dal" or "from" ? null : day });
            return " ";
        });
        return remaining.Trim(' ', '\t', '\r', '\n', ';', ',');
    }

    /// <summary>
    /// Chat instructions around an explicit order, invoice or protocol are not text filters.
    /// Unrecognised content words and codes stay searchable; Parse retains its literal-search contract.
    /// </summary>
    public static string ParseChat(string? text, IList<ArchiveFactFilter> filters)
    {
        string remaining = Parse(text, filters);
        if (!LocalFactAnswer.HasReference(filters)) return remaining;
        var words = new List<string>();
        int start = 0;
        foreach (Match quoted in ChatQuoted.Matches(remaining))
        {
            words.AddRange(ChatWords(remaining.Substring(start, quoted.Index - start)));
            words.Add(quoted.Value);
            start = quoted.Index + quoted.Length;
        }
        words.AddRange(ChatWords(remaining.Substring(start)));
        return string.Join(" ", words);
    }

    private static IEnumerable<string> ChatWords(string text) =>
        Code.Matches(ChatRequest.Replace(text, " ")).Cast<Match>().Select(m => m.Value)
            .Where(word => !ChatConnectors.Contains(word));

    public static List<string> MessageIds(string? text) => Regex.Matches(text ?? "", @"<[^<>\s]+>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
        .Cast<Match>().Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
}
