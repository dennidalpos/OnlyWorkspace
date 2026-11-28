using System;

namespace OnlyWorkspace.Core.Models;

public sealed class AppointmentInfo
{
    public string Subject { get; set; } = string.Empty;
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Location { get; set; } = string.Empty;
    public string Organizer { get; set; } = string.Empty;
    public bool IsAllDayEvent { get; set; }
    public bool IsRecurring { get; set; }
    public string Body { get; set; } = string.Empty;
    public string EntryId { get; set; } = string.Empty;
    public string StoreDisplayName { get; set; } = string.Empty;
}
