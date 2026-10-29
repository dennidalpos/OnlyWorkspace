using System.Text.Json;
using OnlyWorkspace.Core.Agents;

namespace OnlyWorkspace.Tests;

public sealed class ExtractionCompletenessTests
{
    [Theory]
    [InlineData(40, 200)]
    [InlineData(41, 201)]
    public void AllValidFieldsAndRows_SurviveParsingAndExports(int fields, int lines)
    {
        string json = JsonSerializer.Serialize(new { type = "ordine", fields = Enumerable.Range(1, fields).Select(i => new { name = "field" + i, value = "value" + i }),
            lines = Enumerable.Range(1, lines).Select(i => new { description = "row" + i, amount = "=UNTRUSTED()" }) });
        var data = StructuredDataParser.Parse(json);
        Assert.Equal(fields, data.Fields.Count);
        Assert.Equal(lines, data.Lines.Count);
        Assert.False(data.Diagnostics.IsPartial);
        Assert.Contains("field" + fields, data.ToCsv(';'));
        Assert.Contains("row" + lines, data.ToCsv(';'));
        Assert.Contains("row" + lines, data.ToTsv());
        Assert.Contains("'=UNTRUSTED()", data.ToTsv());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void AllValidActionsAndFullTitles_AreKept(int count)
    {
        string title = new string('t', 240);
        var actions = ActionItemParser.Parse(JsonSerializer.Serialize(new { items = Enumerable.Range(1, count).Select(i => new { title = title + i, type = "task" }) }));
        Assert.Equal(count, actions.Count);
        Assert.Equal(title + count, actions[count - 1].Title);
        Assert.False(actions.Diagnostics.IsPartial);
    }

    [Fact]
    public void InvalidEntries_AreCountedWithReasons_AndExportsDeclarePartialCoverage()
    {
        var data = StructuredDataParser.Parse("{\"fields\":[42,{}, {\"name\":\"ok\",\"value\":\"=formula\"}],\"lines\":[null,{}, {\"description\":\"last\"}]}");
        Assert.Equal(4, data.Diagnostics.DiscardedCount);
        Assert.Single(data.Fields);
        Assert.Single(data.Lines);
        Assert.Contains("Risultato parziale", data.ToCsv(';'));
        Assert.Contains("4 voci", data.ToTsv());
        Assert.Contains("campi non oggetto: 1", data.Diagnostics.Describe());
        var actions = ActionItemParser.Parse("{\"items\":[42,{}, {\"title\":\"last\"}]}");
        Assert.Single(actions);
        Assert.Equal(2, actions.Diagnostics.DiscardedCount);
        Assert.Contains("attività senza titolo valido: 1", actions.Diagnostics.Describe());
        data.Diagnostics.Merge(actions.Diagnostics);
        Assert.Equal(6, data.Diagnostics.DiscardedCount);
    }
}
