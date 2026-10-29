using System;
using System.Globalization;

namespace OnlyWorkspace.Core.Agents;

/// <summary>Sent email with no reply from others in its conversation.</summary>
public sealed class FollowUpCandidate
{
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Recipients { get; set; } = string.Empty;
    public DateTime SentOn { get; set; }

    public int DaysWaiting(DateTime now) => Math.Max(0, (int)(now.Date - SentOn.Date).TotalDays);

    public string WhenLabel(DateTime now)
    {
        var it = CultureInfo.GetCultureInfo("it-IT");
        int days = DaysWaiting(now);
        return $"Inviata {SentOn.ToString("ddd d MMM", it)} a {Recipients} · {days} {(days == 1 ? "giorno" : "giorni")} senza risposta";
    }
}
