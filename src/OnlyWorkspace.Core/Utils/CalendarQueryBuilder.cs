using System;
using System.Globalization;

namespace OnlyWorkspace.Core.Utils;

public static class CalendarQueryBuilder
{
    /// <param name="culture">Regional format Outlook runs with; defaults to the current culture.</param>
    public static string BuildDateRangeFilter(DateTime start, DateTime end, CultureInfo? culture = null)
    {
        if (end < start)
        {
            var temp = start;
            start = end;
            end = temp;
        }

        // Jet syntax (required with IncludeRecurrences) reads dates in the regional short date/time
        // format, without seconds: an ISO string is not parsed and the filter matches every occurrence.
        culture ??= CultureInfo.CurrentCulture;
        var startStr = start.ToString("g", culture);
        var endStr = end.ToString("g", culture);

        return $"[Start] < '{endStr}' AND [End] > '{startStr}'";
    }
}
