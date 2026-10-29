using System;
using OnlyWorkspace.Core.Agents;
using Xunit;

namespace OnlyWorkspace.Tests;

public class ActionItemParserTests
{
    [Fact]
    public void ParsesTasksAndAppointments_WithDefaults()
    {
        var json = "```json\n{\"items\":[" +
                   "{\"type\":\"task\",\"title\":\"Inviare preventivo\",\"due\":\"2026-10-16\"}," +
                   "{\"type\":\"appointment\",\"title\":\"Call fornitore\",\"start\":\"2026-10-15T10:00\",\"location\":\"Teams\"}," +
                   "{\"type\":\"appointment\",\"title\":\"Fiera\",\"start\":\"2026-11-02\"}]}\n```";
        var items = ActionItemParser.Parse(json);

        Assert.Equal(3, items.Count);
        Assert.Equal(new DateTime(2026, 10, 16), items[0].Due);
        Assert.True(items[1].IsAppointment);
        Assert.Equal(new DateTime(2026, 10, 15, 11, 0, 0), items[1].End);
        Assert.True(items[2].AllDay);
        Assert.Equal(new DateTime(2026, 11, 3), items[2].End);
    }

    [Fact]
    public void AppointmentWithoutStart_BecomesTask_AndInvalidEntriesAreDropped()
    {
        var json = "{\"items\":[{\"type\":\"appointment\",\"title\":\"Riunione da fissare\",\"start\":\"giovedì\"},{\"type\":\"task\",\"title\":\"\"},42]}";
        var items = ActionItemParser.Parse(json);

        var item = Assert.Single(items);
        Assert.False(item.IsAppointment);
        Assert.Null(item.Due);
    }

    [Theory]
    [InlineData("2026-10-13T10:00:00Z")]
    [InlineData("2026-10-13T10:00+02:00")]
    [InlineData("2026-10-13T10:00:00.000Z@2026-10-13T10:00")] // gemini-3.8-flash, 2026-10-10
    public void StartKeepsTheStatedTime_WhateverTheModelAppends(string start)
    {
        var item = Assert.Single(ActionItemParser.Parse("{\"items\":[{\"type\":\"appointment\",\"title\":\"Riunione\",\"start\":\"" + start + "\"}]}"));

        Assert.Equal(new DateTime(2026, 10, 13, 10, 0, 0), item.Start);
        Assert.False(item.AllDay);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nessun json")]
    [InlineData("{\"items\": \"x\"}")]
    [InlineData("{rotto")]
    public void InvalidResponses_AreReportedNotEmpty(string json)
    {
        Assert.Throws<InvalidModelOutputException>(() => ActionItemParser.Parse(json));
    }

    [Fact]
    public void EmptyItemList_IsAValidAnswer()
    {
        Assert.Empty(ActionItemParser.Parse("{\"items\": []}"));
    }

    [Fact]
    public void TruncatedUsage_IsReported()
    {
        ModelOutput.EnsureComplete(null);
        ModelOutput.EnsureComplete(new OnlyWorkspace.Core.Models.LlmCompletion());
        var ex = Assert.Throws<InvalidModelOutputException>(() => ModelOutput.EnsureComplete(new OnlyWorkspace.Core.Models.LlmCompletion { IsTruncated = true }));
        Assert.Equal(ModelOutput.TruncatedMessage, ex.Message);
    }
}
