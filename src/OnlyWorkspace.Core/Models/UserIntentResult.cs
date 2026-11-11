using System;

namespace OnlyWorkspace.Core.Models;

public sealed class UserIntentResult
{
    public string Target { get; set; } = "general"; // "email", "calendar", "contacts", "write", "general"
    public string? Keywords { get; set; }
    public string? Sender { get; set; }
    public string? PersonName { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    /// <summary>The question named the period (a day, a range, «domani mattina»); false when StartDate/EndDate are the default week.</summary>
    public bool PeriodStated { get; set; }

    /// <summary>
    /// Mail search of a chat question. The model gives days (yyyy-MM-dd, midnight): the end day is included whole, or
    /// «today» and «last 3 days» left out every email received today. The question words are a text filter only when
    /// the model gave no filter at all: «riassumi le email degli ultimi 3 giorni» kept 2 of 7 emails, those with «giorni».
    /// </summary>
    public EmailSearchQuery ToEmailSearch(string question) => new()
    {
        Keywords = !string.IsNullOrWhiteSpace(Keywords) ? Keywords
            : string.IsNullOrWhiteSpace(Sender) && StartDate == null && EndDate == null ? question : null,
        KeywordsAny = true,
        Sender = Sender,
        StartDate = StartDate,
        EndDate = EndDate is { } end ? end.TimeOfDay == TimeSpan.Zero ? end.Date.AddDays(1).AddSeconds(-1) : end : null
    };
}
