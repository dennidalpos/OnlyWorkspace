using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OnlyWorkspace.Core.Agents;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// What «Appuntamenti e scadenze» tells the pane after a scan (#177): the summary lines, the cards of the items with the email
/// each one comes from, and the list of items under the summary. No Outlook, no model: the coordinator reads and asks, this
/// only words the outcome.
/// </summary>
internal static class CalendarScanReport
{
    /// <summary>Counts and notes of one scan, as the coordinator observed them.</summary>
    internal sealed class Outcome
    {
        public bool Stopped { get; set; }
        public int Read { get; set; }
        public int Total { get; set; }
        public string ScopeLabel { get; set; } = string.Empty;
        public int Candidates { get; set; }
        public bool Requested { get; set; }
        public int TooLong { get; set; }
        public int NoFacts { get; set; }
        public int UnreadDocuments { get; set; }
        public string Coverage { get; set; } = string.Empty;
        public string WebNote { get; set; } = string.Empty;
        public ExtractionDiagnostics Diagnostics { get; set; } = new();
    }

    /// <summary>The line above the items: what was read, what was asked, what was found and what the user should check.</summary>
    public static string Summary(Outcome o, IReadOnlyList<ExtractedActionItem> items, Func<ExtractedActionItem, bool> inOutlook)
    {
        var sb = new StringBuilder();
        sb.Append(o.Stopped ? "Analisi interrotta: " : string.Empty)
          .Append($"lette {o.Read} email su {o.Total} ({o.ScopeLabel}), ")
          .Append(CalendarScanSummary.Request(o.Candidates, o.Requested, o.Stopped));
        sb[0] = char.ToUpperInvariant(sb[0]);
        if (!o.Stopped)
            sb.Append(items.Count == 0 ? "Nessuna voce futura con una data certa."
                : items.Count == 1 ? "Trovata 1 voce: controllala e aggiungila a Outlook."
                : $"Trovate {items.Count} voci: controlla le date, seleziona quelle giuste e aggiungile a Outlook.");
        int existing = items.Count(inOutlook);
        if (existing > 0) sb.Append(existing == 1 ? " 1 è già in Outlook e non è selezionata." : $" {existing} sono già in Outlook e non sono selezionate.");
        int unverified = Unverified(items);
        if (unverified > 0) sb.Append(unverified == 1 ? " 1 voce ha una data non verificata nell'email citata e non è selezionata." : $" {unverified} voci hanno una data non verificata nell'email citata e non sono selezionate.");
        sb.Append(' ').Append(o.Coverage);
        if (o.Diagnostics.IsPartial) sb.Append(' ').Append(o.Diagnostics.Describe());
        if (o.TooLong > 0) sb.Append(o.TooLong == 1 ? " 1 email non letta: è troppo lunga per il contesto del profilo in uso." : $" {o.TooLong} email non lette: sono troppo lunghe per il contesto del profilo in uso.");
        if (o.NoFacts > 0) sb.Append(o.NoFacts == 1 ? " 1 email senza fatti locali utilizzabili." : $" {o.NoFacts} email senza fatti locali utilizzabili.");
        if (o.UnreadDocuments > 0) sb.Append($" {o.UnreadDocuments} allegati non letti; il loro contenuto può mancare dal risultato.");
        sb.Append(o.WebNote);
        return sb.ToString();
    }

    /// <summary>Items whose date the code could not find in the cited email.</summary>
    public static int Unverified(IReadOnlyList<ExtractedActionItem> items) => items.Count(i => i.Unverified.Count > 0);

    /// <summary>One line per item under the summary, with the email it comes from and what is not verified.</summary>
    public static void AppendItems(StringBuilder sb, IReadOnlyList<ExtractedActionItem> items, MailEvidencePack pack)
    {
        foreach (var i in items)
        {
            sb.AppendLine().Append("- **").Append(i.IsAppointment ? "Appuntamento" : "Attività").Append("** — ").Append(i.Title).Append(" (").Append(i.WhenLabel).Append(") — ").Append(Origin(i, pack));
            if (i.Unverified.Count > 0) sb.Append(" — ").Append(Check(i));
        }
    }

    /// <summary>The cards of the pane: one per item, selectable unless already in Outlook or not verified.</summary>
    public static List<object> Payload(IReadOnlyList<ExtractedActionItem> items, MailEvidencePack pack, Func<ExtractedActionItem, bool> inOutlook) =>
        items.Select(i => (object)new
        {
            kind = i.Kind,
            title = i.Title,
            when = i.WhenLabel,
            due = i.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            start = i.Start?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            end = i.End?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            allDay = i.AllDay,
            location = i.Location,
            notes = i.Notes,
            source = SourceOf(i, pack),
            dateText = i.DateText,
            selectable = true,
            exists = inOutlook(i),
            verified = i.Unverified.Count == 0,
            warning = i.Unverified.Count > 0 ? "Non verificata: " + string.Join("; ", i.Unverified) : null
        }).ToList();

    /// <summary>Rows of the copy/CSV export, one per item (the header is the caller's).</summary>
    public static IEnumerable<string[]> ExportRows(IReadOnlyList<ExtractedActionItem> items, MailEvidencePack pack) =>
        items.Select(i => new[]
        {
            i.IsAppointment ? "Appuntamento" : "Scadenza",
            i.Title,
            (i.IsAppointment ? i.Start : i.Due)?.ToString(i.AllDay || !i.IsAppointment ? "dd/MM/yyyy" : "dd/MM/yyyy HH:mm") ?? string.Empty,
            i.Location,
            i.Notes,
            Origin(i, pack),
            Check(i)
        });

    /// <summary>«[Mn] date · subject» of the cited email, or a plain statement when the item cites none of the pack.</summary>
    public static string Origin(ExtractedActionItem i, MailEvidencePack pack)
    {
        if (i.Source < 1 || i.Source > pack.Count) return "email non indicata";
        var m = pack.Items[i.Source - 1].Mail;
        return $"[M{i.Source}] {m.Received:dd/MM/yyyy} · {Truncate(m.Subject, 60)}";
    }

    /// <summary>The email each item comes from, shown on its card and openable from there (A121).</summary>
    public static object? SourceOf(ExtractedActionItem i, MailEvidencePack pack)
    {
        if (i.Source < 1 || i.Source > pack.Count) return null;
        var m = pack.Items[i.Source - 1].Mail;
        return new { label = $"[M{i.Source}] {Truncate(m.Subject, 60)}", meta = m.Received.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), entryId = m.EntryId, storeId = m.StoreId };
    }

    public static string Check(ExtractedActionItem i) => i.Unverified.Count == 0 ? string.Empty : "⚠ " + string.Join("; ", i.Unverified);

    private static string Truncate(string text, int max) => text.Length > max ? text.Substring(0, max - 1).TrimEnd() + "…" : text;
}
