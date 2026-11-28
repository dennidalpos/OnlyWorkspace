using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OnlyWorkspace.Core.Archive;

/// <summary>One email found for a chat question: archive row or Outlook header.</summary>
public sealed class ChatMailItem
{
    /// <summary>Archive row; 0 for an email seen only in Outlook.</summary>
    public long ArchiveId { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public List<ArchiveEmailRef> Copies { get; set; } = new();
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderAddress { get; set; } = string.Empty;
    /// <summary>Local time.</summary>
    public DateTime Received { get; set; }
    public DateTime? Sent { get; set; }
    public bool OutsideFilters { get; set; }
    public List<string> LinkReasons { get; set; } = new();
    public string Preview { get; set; } = string.Empty;
    public bool HasAttachments { get; set; }
    /// <summary>Facts extracted locally, read only for questions with fact filters.</summary>
    public List<ArchiveFact> Facts { get; set; } = new();

    public string Sender => SenderName.Length > 0 ? SenderName
        : SenderAddress.StartsWith("/", StringComparison.Ordinal) ? string.Empty : SenderAddress;
}

/// <summary>
/// Chat over the mail without a model call per email (20/10/2026: «resoconto delle mail della mattina» took minutes,
/// one note request per email): the list shown at once from headers, and the notes for one final answer.
/// </summary>
public static class ChatMailDigest
{
    public static List<ChatMailItem> OnePerMessage(IEnumerable<ChatMailItem> items)
    {
        var result = new List<ChatMailItem>();
        int unknown = 0;
        foreach (var group in items.GroupBy(m => m.MessageId.Length > 0 ? "m:" + m.MessageId : m.StoreId.Length > 0 && m.EntryId.Length > 0 ? "e:" + m.StoreId + "\n" + m.EntryId : "unknown:" + unknown++, StringComparer.Ordinal))
        {
            var item = group.First();
            item.Copies = group.SelectMany(m => m.Copies.Concat(new[] { new ArchiveEmailRef { Id = m.ArchiveId, EntryId = m.EntryId, StoreId = m.StoreId } }))
                .GroupBy(c => c.StoreId + "\n" + c.EntryId, StringComparer.Ordinal).Select(g => g.First()).ToList();
            item.OutsideFilters = group.All(m => m.OutsideFilters);
            item.LinkReasons = group.SelectMany(m => m.LinkReasons).Distinct(StringComparer.Ordinal).ToList();
            result.Add(item);
        }
        return result;
    }

    private const int SummaryChars = 160;
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>The text without its markdown table rows: what a prior turn keeps of an email listing.</summary>
    public static string WithoutTables(string text) =>
        string.Join("\n", (text ?? string.Empty).Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("|", StringComparison.Ordinal)));

    /// <summary>Markdown table, oldest first; <paramref name="more"/> emails left out are named in a closing line.</summary>
    public static string Listing(IReadOnlyList<ChatMailItem> items, int more)
    {
        if (items.Count == 0) return string.Empty;
        bool oneDay = items.Select(i => (i.Sent ?? i.Received).Date).Distinct().Count() == 1;
        var sb = new StringBuilder();
        sb.Append(items.Count == 1 ? "**1 email**" : $"**{items.Count} email**")
          .Append(oneDay ? " del " + (items[0].Sent ?? items[0].Received).ToString("dd/MM/yyyy", Italian) : string.Empty).Append("\n\n");
        sb.Append("| Ora | Da | Oggetto | Sintesi |\n|---|---|---|---|\n");
        foreach (var item in items)
        {
            sb.Append("| ").Append((item.Sent ?? item.Received).ToString(oneDay ? "HH:mm" : "dd/MM HH:mm", Italian))
              .Append(" | ").Append(Cell(item.Sender))
              .Append(" | ").Append(Cell(item.Subject.Length > 0 ? item.Subject : "(senza oggetto)") + (item.HasAttachments ? " (con allegati)" : string.Empty) + (item.OutsideFilters ? " (correlata fuori filtro)" : string.Empty))
              .Append(" | ").Append(Cell(Summary(item)))
              .Append(" |\n");
        }
        if (more > 0) sb.Append('\n').Append(more == 1 ? "Un'altra email non è in elenco." : $"Altre {more} email non sono in elenco.").Append('\n');
        return sb.ToString();
    }

    /// <summary>The acquired archive preview.</summary>
    public static string Summary(ChatMailItem item)
    {
        var text = item.Preview;
        text = text.Trim();
        return text.Length > SummaryChars ? text.Substring(0, SummaryChars).TrimEnd() + "…" : text;
    }

    /// <summary>Note of one email for the final prompt: the header, then its original or literal local facts.</summary>
    public static string Note(int number, ChatMailItem item, string body, bool includeDetails = true) =>
        $"[M{number}] [Email del {item.Received.ToString("dd/MM/yyyy HH:mm", Italian)}" +
        (includeDetails ? $" da {Display(item)} — «{item.Subject}»" : string.Empty) + "]\n" +
        (includeDetails && item.OutsideFilters ? "Messaggio correlato fuori dai filtri iniziali: " + string.Join("; ", item.LinkReasons) + "\n" : "") + body.Trim();

    private static string Display(ChatMailItem item) =>
        item.SenderAddress.Length > 0 && !item.SenderAddress.StartsWith("/", StringComparison.Ordinal) && item.SenderAddress != item.SenderName
            ? (item.SenderName.Length > 0 ? $"{item.SenderName} <{item.SenderAddress}>" : item.SenderAddress)
            : item.SenderName;

    private static string Cell(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Trim();
}
