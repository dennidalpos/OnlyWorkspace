using OnlyWorkspace.Core.Agents;
using Xunit;

namespace OnlyWorkspace.Tests;

public class StructuredDataParserTests
{
    private const string Json = "{\"type\":\"Fattura\",\"fields\":[{\"name\":\"Numero documento\",\"value\":\"FT 12/2026\"},{\"name\":\"Totale\",\"value\":\"1.234,50 €\"},{\"name\":\"Vuoto\",\"value\":\"\"}]," +
                                "\"lines\":[{\"description\":\"Cavo \\\"HDMI\\\"; 2m\",\"quantity\":3,\"unit_price\":\"4,50\",\"amount\":\"13,50\"},{}]}";

    [Fact]
    public void ParsesFieldsAndLines_KeepingValuesVerbatim()
    {
        var data = StructuredDataParser.Parse(Json);

        Assert.Equal("fattura", data.DocumentType);
        Assert.Equal(2, data.Fields.Count);
        Assert.Equal("1.234,50 €", data.Fields[1].Value);
        var line = Assert.Single(data.Lines);
        Assert.Equal("3", line[1]);
    }

    [Fact]
    public void Csv_QuotesSeparatorsAndQuotes_TsvIsPlain()
    {
        var data = StructuredDataParser.Parse(Json);

        var csv = data.ToCsv(';');
        Assert.Contains("Numero documento;FT 12/2026", csv);
        Assert.Contains("\"Cavo \"\"HDMI\"\"; 2m\";3;4,50;13,50", csv);
        Assert.Contains("Cavo \"HDMI\"; 2m\t3\t4,50\t13,50", data.ToTsv());
    }

    [Fact]
    public void InvalidOutput_IsReportedNotEmpty()
    {
        Assert.Throws<InvalidModelOutputException>(() => StructuredDataParser.Parse("niente"));
        Assert.Throws<InvalidModelOutputException>(() => StructuredDataParser.Parse("{}"));
        Assert.True(StructuredDataParser.Parse("{\"type\":\"altro\",\"fields\":[],\"lines\":[]}").IsEmpty);
    }

    [Fact]
    public void FormulaLikeValues_AreNeutralized_AmountsAndPhonesAreNot()
    {
        var data = StructuredDataParser.Parse(
            "{\"fields\":[{\"name\":\"Note\",\"value\":\"=HYPERLINK(\\\"http://x\\\")\"},{\"name\":\"Rif\",\"value\":\"@SUM(A1)\"}," +
            "{\"name\":\"Cmd\",\"value\":\"-2+cmd|' /C calc'!A0\"},{\"name\":\"Saldo\",\"value\":\"-1.234,50 €\"},{\"name\":\"Telefono\",\"value\":\"+39 02 1234567\"}]}");

        var tsv = data.ToTsv();
        Assert.Contains("Note\t'=HYPERLINK", tsv);
        Assert.Contains("Rif\t'@SUM(A1)", tsv);
        Assert.Contains("Cmd\t'-2+cmd", tsv);
        Assert.Contains("Saldo\t-1.234,50 €", tsv);
        Assert.Contains("Telefono\t+39 02 1234567", tsv);
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", data.ToCsv(';'));
    }
}
