using System;

namespace OnlyWorkspace.Core.Models;

public sealed class CalendarQuery
{
    public DateTime StartDate { get; set; } = DateTime.Today;
    public DateTime EndDate { get; set; } = DateTime.Today.AddDays(7);
    public int MaxResults { get; set; } = 50;
    public string? StoreId { get; set; }
}
