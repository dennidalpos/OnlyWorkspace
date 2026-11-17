using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// The unread-email part of «Briefing di oggi» (#177): the summary and the list by priority, the category buttons, and what a
/// restricted profile may reuse of it. No Outlook, no model: the coordinator reads and asks, this only words and shapes it.
/// </summary>
internal static class BriefingReport
{
    /// <summary>Emails of the model's triage, most urgent first, then in the order they were listed.</summary>
    public static List<TriageEntry> Triaged(BriefingResult briefing) =>
        briefing.Emails.OrderBy(t => t.PriorityRank).ThenBy(t => t.Index).ToList();

    /// <summary>
    /// Appends the summary and the unread list to the agenda text already in <paramref name="md"/>: triaged emails with their
    /// priority and reason, then those the model left out, plainly. <paramref name="triageFailure"/> says why there is no triage.
    /// </summary>
    public static void AppendUnread(StringBuilder md, BriefingResult briefing, IReadOnlyList<TriageEntry> triaged,
        IReadOnlyList<EmailSearchResult> unread, string? triageFailure)
    {
        if (briefing.Summary.Length > 0) md.AppendLine().AppendLine("**In sintesi**").AppendLine().AppendLine(briefing.Summary);
        md.AppendLine().Append("**Email non lette (").Append(unread.Count).AppendLine(")**").AppendLine();
        if (triageFailure != null) md.AppendLine(triageFailure).AppendLine();

        foreach (var t in triaged)
        {
            var e = unread[t.Index];
            md.Append("- **").Append(char.ToUpperInvariant(t.Priority[0]) + t.Priority.Substring(1)).Append("** — ").Append(e.Subject).Append(" (").Append(e.SenderName).Append(')');
            if (t.Reason.Length > 0) md.Append(": ").Append(t.Reason);
            md.AppendLine();
        }
        foreach (var i in Enumerable.Range(0, unread.Count).Where(i => triaged.All(t => t.Index != i)))
        {
            md.Append("- ").Append(unread[i].Subject).Append(" (").Append(unread[i].SenderName).AppendLine(")");
        }
    }

    /// <summary>One card for each triaged email whose category exists: a button that applies it in Outlook, once.</summary>
    public static List<object> CategoryCards(IReadOnlyList<TriageEntry> triaged, IReadOnlyList<EmailSearchResult> unread) =>
        triaged.Where(t => t.Category.Length > 0).Select(t => (object)new
        {
            kind = "triage",
            title = unread[t.Index].Subject,
            when = $"Priorità {t.Priority} · {unread[t.Index].SenderName}",
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
