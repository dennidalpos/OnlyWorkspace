using OnlyWorkspace.Core.Agents;
using Xunit;

namespace OnlyWorkspace.Tests;

public class BriefingParserTests
{
    private static readonly string[] Categories = { "Clienti", "Fornitori" };

    [Theory]
    [InlineData("\"  Motivo\\t \\r\\n\"", "Motivo")]
    [InlineData("\"   \"", "")]
    [InlineData("null", "")]
    [InlineData("42", "")]
    [InlineData("true", "")]
    [InlineData("[]", "")]
    [InlineData("{}", "")]
    public void ModelTextFields_TrimStringsWithoutConvertingOtherJsonKinds(string value, string expected)
    {
        var result = BriefingParser.Parse("{\"summary\":" + value + ",\"emails\":[{\"id\":1,\"reason\":" + value + "}]}", 1, Categories);

        Assert.Equal(expected, result.Summary);
        Assert.Equal(expected, Assert.Single(result.Emails).Reason);
    }

    [Fact]
    public void KeepsValidEntries_AndOnlyExistingCategories()
    {
        var json = "{\"summary\":\"Giornata piena.\",\"emails\":[" +
                   "{\"id\":1,\"priority\":\"Alta\",\"category\":\"clienti\",\"reason\":\"Ordine urgente\"}," +
                   "{\"id\":\"2\",\"priority\":\"boh\",\"category\":\"Inventata\"}," +
                   "{\"id\":2,\"priority\":\"bassa\"}," +
                   "{\"id\":9,\"priority\":\"alta\"}]}";
        var r = BriefingParser.Parse(json, emailCount: 3, Categories);

        Assert.Equal("Giornata piena.", r.Summary);
        Assert.Equal(2, r.Emails.Count);
        Assert.Equal("alta", r.Emails[0].Priority);
        Assert.Equal("Clienti", r.Emails[0].Category);
        Assert.Equal("media", r.Emails[1].Priority);
        Assert.Equal(string.Empty, r.Emails[1].Category);
    }

    [Theory]
    [InlineData("non è json")]
    [InlineData("{\"summary\":\"ok\"}")]
    [InlineData("{\"summary\":\"ok\",\"emails\":[")]
    public void InvalidOutput_IsReportedNotEmpty(string json)
    {
        Assert.Throws<InvalidModelOutputException>(() => BriefingParser.Parse(json, 3, Categories));
    }
}
