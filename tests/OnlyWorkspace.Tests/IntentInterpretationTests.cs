using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Models;
using Xunit;

namespace OnlyWorkspace.Tests;

/// <summary>#201: the line next to the answer says which Outlook data the question was read as, with period, sender and name.</summary>
public sealed class IntentInterpretationTests
{
    private static readonly DateTime Today = new(2026, 11, 11, 12, 0, 0);

    private static string For(string question) => IntentInterpretation.Describe(IntentClassifier.ClassifyLocal(question, Today), question);

    [Fact]
    public void Mail_NamesSenderPeriodAndWords()
    {
        string text = For("Trova le email da Mario Rossi di ieri sul contratto ALFA-42");
        Assert.StartsWith("Interpretata come ricerca nella posta", text);
        Assert.Contains("da «Mario Rossi»", text);
        Assert.Contains("il 10/11/2026", text);
        Assert.Contains("parole: «contratto ALFA-42»", text);
    }

    [Fact]
    public void Mail_WithoutFilters_ShowsTheQuestionWordsAsTheSearchText()
    {
        string text = For("Cerca le email sul preventivo Acme");
        Assert.Contains("parole:", text);
        Assert.DoesNotContain("da «", text);
    }

    [Fact]
    public void Calendar_SaysThePeriodItUsed_AndWhenItIsTheDefaultWeek()
    {
        Assert.Contains("dal 15/11/2026 al 20/11/2026", For("Che appuntamenti ho dal 15/11/2026 al 20/11/2026?"));
        string stated = For("Ho riunioni domani mattina?");
        Assert.Contains("calendario", stated);
        Assert.Contains("il 12/11/2026 dalle 00:00 alle 12:59", stated);
        Assert.DoesNotContain("periodo non indicato", stated);
        string defaulted = For("Cosa ho in agenda?");
        Assert.Contains("periodo non indicato: dal 11/11/2026 al 18/11/2026", defaulted);
    }

    [Fact]
    public void Contacts_NamesThePerson()
    {
        Assert.Equal("Interpretata come ricerca nei contatti: «Mario Rossi».", For("Trova il numero di telefono di Mario Rossi"));
    }

    [Fact]
    public void WriteAndGeneral_SayNothingInOutlookIsRead()
    {
        Assert.Contains("nessuna ricerca", For("Scrivi a Mario Rossi per spostare la riunione di venerdì"));
        Assert.Contains("domanda generale", For("Spiega cos'è un attacco di phishing"));
    }

    [Fact]
    public void OtherReadings_HaveTheirOwnLine()
    {
        Assert.Contains("email selezionata", IntentInterpretation.ForSelectedEmail());
        Assert.Contains("bozza", IntentInterpretation.ForDraft());
        Assert.Contains("già lette", IntentInterpretation.ForRetry());
    }

    [Fact]
    public void LongText_IsCutAndKeptOnOneLine()
    {
        var intent = new UserIntentResult { Target = "email", Keywords = new string('a', 200) + "\n b" };
        string text = IntentInterpretation.Describe(intent, "q");
        Assert.DoesNotContain('\n', text);
        Assert.Contains("…»", text);
        Assert.True(text.Length < 200);
    }

    [Fact]
    public void ChatSendsTheInterpretation_ForEveryReadingOfTheQuestion()
    {
        string chat = File.ReadAllText(Path.Combine(TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.Outlook", "UI", "TaskPaneCoordinator.Chat.cs"));
        Assert.Contains("stream_interpretation", chat);
        foreach (string call in new[] { "IntentInterpretation.Describe", "ForSelectedEmail", "ForDraft", "ForRetry" })
            Assert.Contains(call, chat);
    }
}
