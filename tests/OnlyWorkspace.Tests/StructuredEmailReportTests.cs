using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the results of «Attività e appuntamenti» and «Estrazione dati» outside TaskPaneCoordinator, with no Outlook and no model.</summary>
public sealed class StructuredEmailReportTests
{
    private static EmailSource Email() => new()
    {
        Subject = "Sopralluogo",
        Sender = "Lucia <lucia@example.test>",
        Received = new DateTime(2026, 11, 2),
        EntryId = "e1",
        StoreId = "s1"
    };

    private static object? Get(object source, string name) => source.GetType().GetProperty(name)!.GetValue(source);

    private const string DataJson = "{\"type\":\"Fattura\",\"fields\":[{\"name\":\"Numero\",\"value\":\"FT 1\"}]," +
                                    "\"lines\":[{\"description\":\"Cavo\",\"quantity\":3,\"unit_price\":\"4,50\",\"amount\":\"13,50\"}]}";

    [Fact]
    public void ActionItems_WithNoItems_SaysSoAndOffersNoCards()
    {
        var (text, extra) = StructuredEmailReport.ActionItems("{\"items\":[]}", Email());

        Assert.Equal("Nessuna attività o appuntamento individuato nell'email.", text);
        var cards = (System.Collections.IEnumerable)Get(extra!, "items")!;
        Assert.Empty(cards.Cast<object>());
        Assert.Null(Get(extra!, "export"));
    }

    [Fact]
    public void ActionItems_ListsEachItemAndCitesTheEmailInItsNotes()
    {
        string json = "{\"items\":[{\"type\":\"task\",\"title\":\"Inviare il modulo\",\"due\":\"2026-11-30\",\"notes\":\"firmato\"}," +
                      "{\"type\":\"appointment\",\"title\":\"Sopralluogo\",\"start\":\"2026-11-20T10:00\",\"location\":\"Cantiere\"}]}";

        var (text, extra) = StructuredEmailReport.ActionItems(json, Email());

        Assert.StartsWith("Trovate 2 voci: apri la bozza", text, StringComparison.Ordinal);
        Assert.Contains("- **Attività** — Inviare il modulo (", text, StringComparison.Ordinal);
        Assert.Contains("- **Appuntamento** — Sopralluogo (", text, StringComparison.Ordinal);
        var cards = ((System.Collections.IEnumerable)Get(extra!, "items")!).Cast<object>().ToList();
        Assert.Equal(2, cards.Count);
        string source = "Da email «Sopralluogo» di Lucia <lucia@example.test> del 02/11/2026.";
        Assert.Equal("firmato\n\n" + source, Get(cards[0], "notes"));
        Assert.Equal(source, Get(cards[1], "notes"));
        Assert.Equal("2026-11-30", Get(cards[0], "due"));
        Assert.Equal("2026-11-20T10:00", Get(cards[1], "start"));
        var origin = Get(cards[0], "source")!;
        Assert.Equal("e1", Get(origin, "entryId"));
        Assert.Equal("s1", Get(origin, "storeId"));
        Assert.Equal("Sopralluogo", Get(origin, "label"));
    }

    [Fact]
    public void ActionItems_WithOneItem_UsesTheSingular()
    {
        var (text, _) = StructuredEmailReport.ActionItems("{\"items\":[{\"type\":\"task\",\"title\":\"A\",\"due\":\"2026-11-30\"}]}", Email());

        Assert.StartsWith("Trovata 1 voce: apri la bozza", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionItems_ExportsKindTitleWhenLocationAndNotes()
    {
        var (_, extra) = StructuredEmailReport.ActionItems("{\"items\":[{\"type\":\"task\",\"title\":\"A\",\"due\":\"2026-11-30\",\"notes\":\"n\"}]}", Email());

        var export = Get(extra!, "export")!;
        Assert.Contains("Tipo\tTitolo\tQuando\tLuogo\tNote", (string)Get(export, "tsv")!, StringComparison.Ordinal);
        Assert.Contains("task\tA\t", (string)Get(export, "tsv")!, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredData_Valid_CountsFieldsAndLinesAndBuildsTheTable()
    {
        var (text, extra) = StructuredEmailReport.StructuredData(DataJson, ";");

        Assert.Equal("Dati estratti (fattura): 1 campi, 1 righe. Verifica i valori con l'email originale prima di usarli.", text);
        var table = Get(extra!, "table")!;
        Assert.Equal("fattura", Get(table, "documentType"));
        Assert.Contains("Numero\tFT 1", (string)Get(table, "tsv")!, StringComparison.Ordinal);
        Assert.Contains("Cavo;3;4,50;13,50", (string)Get(table, "csv")!, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredData_CsvUsesACommaUnlessTheLocaleSeparatorIsASemicolon()
    {
        var (_, comma) = StructuredEmailReport.StructuredData(DataJson, ",");
        var (_, other) = StructuredEmailReport.StructuredData(DataJson, "|");

        Assert.Contains("Numero,FT 1", (string)Get(Get(comma!, "table")!, "csv")!, StringComparison.Ordinal);
        Assert.Contains("Numero,FT 1", (string)Get(Get(other!, "table")!, "csv")!, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredData_WithoutUsableData_SaysSoAndHasNoTable()
    {
        var (text, extra) = StructuredEmailReport.StructuredData("{\"type\":\"altro\",\"fields\":[],\"lines\":[]}");

        Assert.Equal("Nessun dato commerciale strutturato valido individuato nell'email.", text);
        Assert.Null(extra);
    }

    [Fact]
    public void ListExport_WithNoRows_IsNull() =>
        Assert.Null(StructuredEmailReport.ListExport(new[] { "A" }, Array.Empty<string[]>()));

    [Fact]
    public void ListExport_WithRows_GivesTsvAndCsv()
    {
        var export = StructuredEmailReport.ListExport(new[] { "A", "B" }, new[] { new[] { "x", "y;z" } }, null, ";")!;

        Assert.Equal("A\tB", ((string)Get(export, "tsv")!).Split('\n')[0].TrimEnd('\r'));
        Assert.Contains("x;\"y;z\"", (string)Get(export, "csv")!, StringComparison.Ordinal);
    }

    [Fact]
    public void ListExport_WithPartialDiagnosticsAndNoRows_StillWarns()
    {
        var diagnostics = new ExtractionDiagnostics();
        diagnostics.Discard("data impossibile");

        var export = StructuredEmailReport.ListExport(new[] { "A" }, Array.Empty<string[]>(), diagnostics, ",")!;

        Assert.StartsWith("Copertura\tRisultato parziale", (string)Get(export, "tsv")!, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryText_WithoutAnExport_IsTheTextItself() =>
        Assert.Equal("testo", StructuredEmailReport.HistoryText("testo", null));

    [Fact]
    public void HistoryText_AppendsTheTsvInAFenceLongerThanAnyBacktickRunInIt()
    {
        var extra = new { table = new { tsv = "a\tb``\n```x" } };

        string text = StructuredEmailReport.HistoryText("testo", extra);

        Assert.Equal("testo\n\n````text\na\tb``\n```x\n````", text);
    }

    [Fact]
    public void HistoryText_UsesTheExportOfAListWhenThereIsNoTable()
    {
        var extra = new { items = 1, export = new { tsv = "riga" } };

        Assert.Equal("testo\n\n```text\nriga\n```", StructuredEmailReport.HistoryText("testo", extra));
    }

    [Fact]
    public void HistoryText_WithAnEmptyTsv_IsTheTextItself() =>
        Assert.Equal("testo", StructuredEmailReport.HistoryText("testo", new { table = new { tsv = "" } }));
}
