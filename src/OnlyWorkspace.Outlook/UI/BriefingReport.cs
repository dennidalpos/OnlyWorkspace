using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// The unread-email part of «Briefing di oggi»: the summary, the list by date, the category buttons, and what a
/// restricted profile may reuse of it. No Outlook, no model: the coordinator reads and asks, this only words and shapes it.
/// </summary>
internal static class BriefingReport
{
    public static IOrderedEnumerable<EmailSearchResult> ByReceivedDate(IEnumerable<EmailSearchResult> unread) =>
        unread.OrderByDescending(BriefingContext.ReceivedDate);

    /// <summary>Orders triage entries by their position in the date-sorted unread list.</summary>
    public static List<TriageEntry> Triaged(BriefingResult briefing) =>
        briefing.Emails.OrderBy(t => t.Index).ToList();

    public static HashSet<int> AcceptBatch(BriefingResult briefing, BriefingResult batch, IEnumerable<int> requested)
    {
        var allowed = new HashSet<int>(requested);
        var accepted = batch.Emails.Where(t => allowed.Contains(t.Index)).ToList();
        briefing.Emails.AddRange(accepted);
        return new HashSet<int>(accepted.Select(t => t.Index));
    }

    /// <summary>
    /// Appends the summary and all unread emails in received-date order, with triage when available.
    /// </summary>
    public static void AppendUnread(StringBuilder md, BriefingResult briefing, IReadOnlyList<TriageEntry> triaged,
        IReadOnlyList<EmailSearchResult> unread, string? triageFailure)
    {
        if (briefing.Summary.Length > 0) md.AppendLine().AppendLine("**In sintesi**").AppendLine().AppendLine(briefing.Summary);
        md.AppendLine().Append("**Email non lette (").Append(unread.Count).AppendLine(")**").AppendLine();
        if (triageFailure != null) md.AppendLine(triageFailure).AppendLine();

        var byIndex = triaged.ToDictionary(t => t.Index);
        for (int i = 0; i < unread.Count; i++)
        {
            var e = unread[i];
            md.Append("- ");
            if (BriefingContext.ReceivedLabel(e) is { } received) md.Append(received).Append(" · ");
            if (byIndex.TryGetValue(i, out var t))
                md.Append("**").Append(char.ToUpperInvariant(t.Priority[0]) + t.Priority.Substring(1)).Append("** — ");
            md.Append(SecurityContentAnalysis.MarkdownText(e.Subject)).Append(" (").Append(SecurityContentAnalysis.MarkdownText(e.SenderName)).Append(')');
            if (t?.Reason.Length > 0) md.Append(": ").Append(t.Reason);
            md.AppendLine();
        }
    }

    /// <summary>One card for each triaged email whose category exists: a button that applies it in Outlook, once.</summary>
    public static List<object> CategoryCards(IReadOnlyList<TriageEntry> triaged, IReadOnlyList<EmailSearchResult> unread) =>
        triaged.Where(t => t.Category.Length > 0).Select(t => (object)new
        {
            kind = "triage",
            title = unread[t.Index].Subject,
            when = (BriefingContext.ReceivedLabel(unread[t.Index]) is { } received ? received + " · " : "") + $"Priorità {t.Priority} · {unread[t.Index].SenderName}",
            buttonLabel = $"Applica categoria «{t.Category}»",
            buttonTip = "Aggiunge la categoria all'email in Outlook",
            once = true,
            message = new { type = "apply_category", entryId = unread[t.Index].EntryId, storeId = unread[t.Index].StoreId, category = t.Category }
        }).ToList();

    /// <summary>
    /// The validated model result alone, for a restricted profile: the displayed text also lists local-only mail metadata, so only
    /// this goes back to the model in later turns.
    /// </summary>
    public static string ModelContent(BriefingResult briefing, IReadOnlyList<TriageEntry> triaged) =>
        JsonSerializer.Serialize(new
        {
            summary = briefing.Summary,
            emails = triaged.Select(t => new { id = t.Index + 1, priority = t.Priority, category = t.Category, reason = t.Reason })
        });
}
