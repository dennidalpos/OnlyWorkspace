using System;
using System.Globalization;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Utils;
using Xunit;

namespace OnlyWorkspace.Tests;

public class CalendarQueryTests
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    [Fact]
    public void CalendarQueryBuilder_BuildsJetFilter_InRegionalFormat()
    {
        var start = new DateTime(2026, 10, 10, 9, 0, 0);
        var end = new DateTime(2026, 10, 15, 18, 0, 0);

        var filter = CalendarQueryBuilder.BuildDateRangeFilter(start, end, Italian);

        Assert.Equal("[Start] < '15/10/2026 18:00' AND [End] > '10/10/2026 09:00'", filter);
    }

    [Fact]
    public void CalendarQueryBuilder_InvertsDates_IfEndPrecedesStart()
    {
        var start = new DateTime(2026, 10, 15, 18, 0, 0);
        var end = new DateTime(2026, 10, 10, 9, 0, 0);

        var filter = CalendarQueryBuilder.BuildDateRangeFilter(start, end, Italian);

        Assert.Equal("[Start] < '15/10/2026 18:00' AND [End] > '10/10/2026 09:00'", filter);
    }
}
