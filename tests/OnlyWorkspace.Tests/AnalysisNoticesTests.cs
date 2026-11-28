using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the sentences and the table message that close a mailbox analysis, outside TaskPaneCoordinator.</summary>
public sealed class AnalysisNoticesTests
{
    [Fact]
    public void Conversation_WhenCut_SaysHowManyWereLeftOut()
    {
        Assert.Equal("La conversazione ha 25 messaggi: letti i 20 più recenti, 5 più vecchi esclusi. ", AnalysisNotices.Conversation(20, 25));
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(20, 5)]
    [InlineData(0, 0)]
    public void Conversation_WhenNothingWasCut_IsEmpty(int read, int total) => Assert.Equal(string.Empty, AnalysisNotices.Conversation(read, total));

    [Fact]
    public void Exposure_WhenNothingWasKeptOut_IsEmpty() => Assert.Equal(string.Empty, AnalysisNotices.Exposure(new EvidenceReading()));

    [Fact]
    public void Exposure_NamesEachKindOfLossWithItsCount()
    {
        var reading = new EvidenceReading();
        reading.NoFacts.AddRange(new[] { "a", "b" });
        reading.OverBudget.Add("c");

        string text = AnalysisNotices.Exposure(reading);

        Assert.Equal("2 email senza fatti locali non inviate (il livello di esposizione non invia originali). " +
                     "1 email oltre il limite di dati del livello di esposizione non inviate. ", text);
    }

    [Fact]
    public void Exposure_WithOnlyOneKind_MentionsOnlyThatKind()
    {
        var reading = new EvidenceReading();
        reading.OverBudget.AddRange(new[] { "a", "b", "c" });

        string text = AnalysisNotices.Exposure(reading);

        Assert.StartsWith("3 email oltre il limite", text, StringComparison.Ordinal);
        Assert.DoesNotContain("senza fatti", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchivePending_GivesTheCountOnItsOwnLine()
    {
        Assert.Equal("\nArchivio ancora in costruzione: 7 email sono trovate solo per oggetto e mittente, non per testo e destinatari.", AnalysisNotices.ArchivePending(7));
    }

    [Fact]
    public void FlowLimited_StartsOnANewLineAndNamesTheLimit()
    {
        Assert.StartsWith("\nOutlook ha restituito solo i 20 messaggi", AnalysisNotices.FlowLimited, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFromArchive_UsesTheSingularForOne()
    {
        Assert.StartsWith("\n1 email non è più dove", AnalysisNotices.MissingFromArchive(1), StringComparison.Ordinal);
        Assert.Contains("la cerca di nuovo", AnalysisNotices.MissingFromArchive(1), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFromArchive_UsesThePluralOtherwise()
    {
        string text = AnalysisNotices.MissingFromArchive(3);

        Assert.StartsWith("\n3 email non sono più dove", text, StringComparison.Ordinal);
        Assert.Contains("le cerca di nuovo", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCandidates_NamesTheScopeAndAppendsTheCoverage() =>
        Assert.Equal("Nessuna candidata trovata (ultimi 30 giorni). Copertura completa.", AnalysisNotices.NoCandidates("ultimi 30 giorni", "Copertura completa."));

    [Fact]
    public void StartingEmailUnavailable_DependsOnWhetherAnEmailWasChosen()
    {
        Assert.Equal("L'email scelta per questa analisi non è più disponibile (spostata o eliminata).", AnalysisNotices.StartingEmailUnavailable(true));
        Assert.Equal("Seleziona o apri un'email, poi riprova.", AnalysisNotices.StartingEmailUnavailable(false));
    }

    [Fact]
    public void FlowGoalSuffix_AsksForAChronologicalReconstructionWithTheGaps()
    {
        Assert.StartsWith(" Ricostruisci in ordine cronologico", AnalysisNotices.FlowGoalSuffix, StringComparison.Ordinal);
        Assert.Contains("lacune di copertura", AnalysisNotices.FlowGoalSuffix, StringComparison.Ordinal);
    }

    [Fact]
    public void NoneRead_IsAParagraphOfItsOwn() =>
        Assert.Equal("\n\nNessuna email è stata letta.", AnalysisNotices.NoneRead);

    [Theory]
    [InlineData(0, "Nessun dato pertinente trovato nelle email lette.")]
    [InlineData(1, "1 riga per l'obiettivo richiesto. Verifica i valori con i documenti originali prima di usarli.")]
    [InlineData(4, "4 righe per l'obiettivo richiesto. Verifica i valori con i documenti originali prima di usarli.")]
    public void TableAnswer_CountsTheRows(int rows, string expected) => Assert.Equal(expected, AnalysisNotices.TableAnswer(rows));

    [Fact]
    public void TableMessage_AddsTheEmailColumnAndBothExports()
    {
        var rows = new List<string[]> { new[] { "Alfa", "10", "[M1]" } };

        object message = AnalysisNotices.TableMessage(new[] { "Cliente", "Importo" }, rows, ";");

        var type = message.GetType();
        Assert.Equal("analisi", type.GetProperty("documentType")!.GetValue(message));
        Assert.Equal(new[] { "Cliente", "Importo", "Email" }, (string[])type.GetProperty("lineHeaders")!.GetValue(message)!);
        Assert.Same(rows, type.GetProperty("lines")!.GetValue(message));
        Assert.Equal("Cliente\tImporto\tEmail\nAlfa\t10\t[M1]".Replace("\n", Environment.NewLine), ((string)type.GetProperty("tsv")!.GetValue(message)!).TrimEnd());
        Assert.Contains("Cliente;Importo;Email", (string)type.GetProperty("csv")!.GetValue(message)!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(";", "Cliente;Importo;Email")]
    [InlineData(",", "Cliente,Importo,Email")]
    [InlineData("|", "Cliente,Importo,Email")]
    public void TableMessage_SeparatesTheCsvWithTheLocaleListSeparatorOnlyWhenItIsASemicolon(string separator, string header)
    {
        object message = AnalysisNotices.TableMessage(new[] { "Cliente", "Importo" }, new List<string[]>(), separator);

        string csv = (string)message.GetType().GetProperty("csv")!.GetValue(message)!;

        Assert.StartsWith(header, csv, StringComparison.Ordinal);
    }
}
