using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Context;

namespace OnlyWorkspace.Core.Archive;

/// <summary>
/// Answers from the facts the archive extracted locally, without a model: a question on the amount, deadline,
/// IBAN, VAT or tax code of a named order, invoice or protocol. Only when every asked kind is found with its
/// source text; anything else (a summary, a why, a fact not extracted) goes to the model as before.
/// </summary>
public static class LocalFactAnswer
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private static readonly (ArchiveFactKind Kind, Regex Words, string Label)[] Asked =
    {
        (ArchiveFactKind.Amount, new Regex(@"\b(importo|importi|quanto|totale|cifra|costo|prezzo|amount|how\s+much)\b", Options, Timeout), "Importo"),
        (ArchiveFactKind.Deadline, new Regex(@"\b(scadenz\w*|scade|scadono|entro\s+quando|termine|due\s+date|deadline)\b", Options, Timeout), "Scadenza"),
        (ArchiveFactKind.Iban, new Regex(@"\biban\b", Options, Timeout), "IBAN"),
        (ArchiveFactKind.Vat, new Regex(@"\b(partita\s+iva|p\.?\s?iva|vat)\b", Options, Timeout), "Partita IVA"),
        (ArchiveFactKind.TaxCode, new Regex(@"\b(codice\s+fiscale|tax\s+code)\b", Options, Timeout), "Codice fiscale")
    };

    /// <summary>Asks for reasoning or text, not a value: the model answers.</summary>
    private static readonly Regex Reasoning = new(
        @"\b(riassum\w*|riassunto|sintesi|spiega\w*|perch[ée]|come\s+mai|analizz\w*|confronta\w*|valuta\w*|scriv\w*|rispond\w*|bozza|summari[sz]e|explain|why|draft)\b",
        Options, Timeout);

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>The fact kinds the question asks for; empty when it asks for none or for reasoning.</summary>
    public static IReadOnlyList<ArchiveFactKind> AskedKinds(string question)
    {
        try
        {
            if (Reasoning.IsMatch(question)) return Array.Empty<ArchiveFactKind>();
            return Asked.Where(a => a.Words.IsMatch(question)).Select(a => a.Kind).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return Array.Empty<ArchiveFactKind>();
        }
    }

    /// <summary>A named order, invoice or protocol among the question's filters.</summary>
    public static bool HasReference(IEnumerable<ArchiveFactFilter> filters) =>
        filters.Any(f => f.Kind is ArchiveFactKind.Order or ArchiveFactKind.Invoice or ArchiveFactKind.Protocol);

    /// <summary>
    /// The answer in markdown, or null when the model must answer: no reference, no asked kind, or an asked kind
    /// found in none of the emails. Emails are numbered [Mn] oldest first, as in the model's pack.
    /// </summary>
    public static string? TryAnswer(string question, IReadOnlyList<ArchiveFactFilter> filters, IReadOnlyList<ChatMailItem> items)
    {
        // The complete turn must still incorporate an explicitly requested web search.
        if (ChatWebRequest.IsExplicit(question)) return null;
        var kinds = AskedKinds(question);
        if (kinds.Count == 0 || !HasReference(filters) || items.Count == 0) return null;
        var ordered = items.OrderBy(i => i.Sent ?? i.Received).ToList();
        if (kinds.Any(k => !ordered.Any(i => i.Facts.Any(f => f.Kind == k)))) return null;

        var sb = new StringBuilder();
        sb.Append("Dai dati estratti in locale dall'archivio:\n");
        for (int n = 0; n < ordered.Count; n++)
        {
            var item = ordered[n];
            var facts = item.Facts.Where(f => kinds.Contains(f.Kind)).ToList();
            if (facts.Count == 0) continue;
            sb.Append("\n**[M").Append(n + 1).Append("]** ").Append(item.Received.ToString("dd/MM/yyyy", Italian)).Append(" · ")
              .Append(item.Sender).Append(" · «").Append(item.Subject).Append("»\n");
            foreach (var fact in facts.GroupBy(f => (f.Kind, f.Raw)).Select(g => g.First()))
            {
                sb.Append("- ").Append(Asked.First(a => a.Kind == fact.Kind).Label).Append(": ").Append(Value(fact));
                if (fact.SourceName.Length > 0 && fact.SourceName != "Corpo email") sb.Append(" (").Append(fact.SourceName).Append(')');
                if (fact.Context.Length > 0) sb.Append(" — «").Append(fact.Context.Trim()).Append('»');
                sb.Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Value(ArchiveFact fact)
    {
        if (fact.Kind == ArchiveFactKind.Amount && decimal.TryParse(fact.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            return amount.ToString("N2", Italian) + (string.IsNullOrEmpty(fact.Currency) ? "" : " " + fact.Currency);
        if (fact.Kind == ArchiveFactKind.Deadline &&
            DateTime.TryParseExact(fact.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day.ToString("dd/MM/yyyy", Italian);
        return fact.Raw;
    }
}
