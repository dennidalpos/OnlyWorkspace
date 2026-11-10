using System;
using System.Collections.Generic;
using System.Linq;

namespace OnlyWorkspace.Core.Models;

public sealed class HistoryChatMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    /// <summary>Permitted model context when the displayed result also contains local-only data. Empty means omit it; null uses Content.</summary>
    public string? ModelContent { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public List<WebSource> WebSources { get; set; } = new();
    public List<HistoryContextReference> MailSources { get; set; } = new();
    /// <summary>Local-only reading evidence. Never appended to reusable model context.</summary>
    public List<Agents.DocumentOutcome> Documents { get; set; } = new();
    /// <summary>Email the user's message is about: the action's email, «questa email», or the draft being written.</summary>
    public HistoryContextReference? Email { get; set; }
    /// <summary>Action that produced the turn («Sintesi», «Risposta · Accetta»…); null for a chat message.</summary>
    public string? Action { get; set; }
    /// <summary>Structured result of a security check; absent in older entries, which keep their original text.</summary>
    public Security.SecurityReportData? Security { get; set; }
}

public sealed class WebSource
{
    /// <summary>Stable [Wn] number; zero in older history entries means the original list position.</summary>
    public int CitationNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class HistoryContextReference
{
    public string OutlookProfile { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    /// <summary>Evidence acquisition time; absent in old history means before any recorded deletion.</summary>
    public DateTime CapturedUtc { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    /// <summary>"email", "calendar", "contact", or "draft" for an email being written (no EntryId until Outlook saves it).</summary>
    public string ItemType { get; set; } = "email";
    public string? Sender { get; set; }
    public DateTime? Date { get; set; }
}

public sealed class ConversationSummary
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int MessageCount { get; set; }
    /// <summary>First email the conversation is about, shown in the history list.</summary>
    public HistoryContextReference? Email { get; set; }
}

public sealed class ConversationHistory
{
    /// <summary>Absent in old history: display it, but never infer permission to send its contents.</summary>
    public string? PolicyKey { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Nuova conversazione";
    public string ProfileId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<HistoryChatMessage> Messages { get; set; } = new();
    public List<HistoryContextReference> ContextReferences { get; set; } = new();

    public ConversationSummary ToSummary()
    {
        return new ConversationSummary
        {
            Id = Id,
            Title = Title,
            ProfileId = ProfileId,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            MessageCount = Messages.Count,
            Email = Messages.Select(m => m.Email).FirstOrDefault(e => e != null)
        };
    }
}
