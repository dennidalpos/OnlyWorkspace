using System.Text;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the wording and cards of «Appuntamenti e scadenze» outside TaskPaneCoordinator, with no Outlook and no model.</summary>
public sealed class CalendarScanReportTests
{
    private static MailEvidence Mail(string subject, DateTime received) => new()
    {
        Mail = new ChatMailItem { Subject = subject, SenderName = "Studio Alfa", Received = received, EntryId = "e-" + subject, StoreId = "s" },
        Text = "testo",
        Whole = true
    };

    private static MailEvidencePack Pack() => MailEvidencePack.Chronological(new[]
    {
        Mail("Sopralluogo", new DateTime(2026, 11, 2, 9, 0, 0)),
        Mail("Documenti", new DateTime(2026, 11, 3, 9, 0, 0))
    });

    private static ExtractedActionItem Appointment(int source = 1, string title = "Sopralluogo") => new()
    {
        Kind = "appointment", Title = title, Start = new DateTime(2026, 11, 20, 10, 0, 0), End = new DateTime(2026, 11, 20, 11, 0, 0),
        Location = "Cantiere", Notes = "n", Source = source, DateText = "venerdì alle 10"
    };

    private static ExtractedActionItem Task(int source = 2) => new()
    {
        Kind = "task", Title = "Inviare il modulo", Due = new DateTime(2026, 11, 30), Source = source, DateText = "entro il 30/11/2026"
    };

    private static CalendarScanReport.Outcome Outcome(Action<CalendarScanReport.Outcome>? edit = null)
    {
        var o = new CalendarScanReport.Outcome { Read = 5, Total = 8, ScopeLabel = "ultimi 30 giorni", Candidates = 2, Requested = true, Coverage = "Copertura." };
        edit?.Invoke(o);
        return o;
    }

    [Fact]
    public void Summary_CountsTheReadEmailsAndCapitalizesTheFirstLetter()
    {
        string text = CalendarScanReport.Summary(Outcome(), new[] { Appointment() }, _ => false);

        Assert.StartsWith("Lette 5 email su 8 (ultimi 30 giorni), ", text, StringComparison.Ordinal);
        Assert.Contains("Trovata 1 voce: controllala e aggiungila a Outlook.", text, StringComparison.Ordinal);
        Assert.Contains("Copertura.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "Nessuna voce futura con una data certa.")]
    [InlineData(2, "Trovate 2 voci: controlla le date, seleziona quelle giuste e aggiungile a Outlook.")]
    public void Summary_WordsTheNumberOfItems(int count, string expected)
    {
        var items = Enumerable.Range(0, count).Select(i => Appointment(1, "A" + i)).ToList();

        Assert.Contains(expected, CalendarScanReport.Summary(Outcome(), items, _ => false), StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_WhenStopped_SaysSoAndDoesNotCountItems()
    {
        string text = CalendarScanReport.Summary(Outcome(o => { o.Stopped = true; }), new[] { Appointment() }, _ => false);

        Assert.StartsWith("Analisi interrotta: lette 5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Trovata", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_MentionsItemsAlreadyInOutlookAndUnverifiedOnes()
    {
        var existing = Appointment(1, "Esiste");
        var unverified = Task();
        unverified.Unverified.Add("data non trovata");
        var items = new[] { existing, unverified };

        string text = CalendarScanReport.Summary(Outcome(), items, i => ReferenceEquals(i, existing));

        Assert.Contains(" 1 è già in Outlook e non è selezionata.", text, StringComparison.Ordinal);
        Assert.Contains(" 1 voce ha una data non verificata nell'email citata e non è selezionata.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_AppendsPluralAndSingularLossesAndTheWebNoteLast()
    {
        string text = CalendarScanReport.Summary(Outcome(o => { o.TooLong = 2; o.NoFacts = 1; o.UnreadDocuments = 3; o.WebNote = " [web]"; }),
            Array.Empty<ExtractedActionItem>(), _ => false);

        Assert.Contains(" 2 email non lette: sono troppo lunghe per il contesto del profilo in uso.", text, StringComparison.Ordinal);
        Assert.Contains(" 1 email senza fatti locali utilizzabili.", text, StringComparison.Ordinal);
        Assert.Contains(" 3 allegati non letti; il loro contenuto può mancare dal risultato.", text, StringComparison.Ordinal);
        Assert.EndsWith(" [web]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_WhenTheExtractionLostItems_DescribesThePartialResult()
    {
        var diagnostics = new ExtractionDiagnostics();
        diagnostics.Discard("data impossibile");

        string text = CalendarScanReport.Summary(Outcome(o => { o.Diagnostics = diagnostics; }), Array.Empty<ExtractedActionItem>(), _ => false);

        Assert.Contains("Risultato parziale: 1 voci non valide scartate (data impossibile: 1).", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Origin_NamesTheCitedEmailOrSaysNoneWasCited()
    {
        var pack = Pack();

        Assert.Equal("[M1] 02/11/2026 · Sopralluogo", CalendarScanReport.Origin(Appointment(1), pack));
        Assert.Equal("[M2] 03/11/2026 · Documenti", CalendarScanReport.Origin(Task(2), pack));
        Assert.Equal("email non indicata", CalendarScanReport.Origin(Task(0), pack));
        Assert.Equal("email non indicata", CalendarScanReport.Origin(Task(9), pack));
    }

    [Fact]
    public void Origin_TruncatesALongSubject()
    {
        var pack = MailEvidencePack.Chronological(new[] { Mail(new string('x', 100), new DateTime(2026, 11, 2)) });

        string origin = CalendarScanReport.Origin(Appointment(1), pack);

        Assert.EndsWith("…", origin, StringComparison.Ordinal);
        Assert.True(origin.Length < 100);
    }

    [Fact]
    public void SourceOf_GivesTheEmailThePaneCanOpen()
    {
        var source = CalendarScanReport.SourceOf(Appointment(1), Pack());

        Assert.NotNull(source);
        var type = source!.GetType();
        Assert.Equal("[M1] Sopralluogo", type.GetProperty("label")!.GetValue(source));
        Assert.Equal("02/11/2026", type.GetProperty("meta")!.GetValue(source));
        Assert.Equal("e-Sopralluogo", type.GetProperty("entryId")!.GetValue(source));
        Assert.Null(CalendarScanReport.SourceOf(Task(0), Pack()));
    }

    [Fact]
    public void Check_IsEmptyWhenVerifiedAndWarnsOtherwise()
    {
        var item = Task();
        Assert.Equal(string.Empty, CalendarScanReport.Check(item));

        item.Unverified.Add("data non trovata");
        item.Unverified.Add("orario non trovato");

        Assert.Equal("⚠ data non trovata; orario non trovato", CalendarScanReport.Check(item));
    }

    [Fact]
    public void Payload_MarksAlreadyInOutlookAndUnverifiedItems()
    {
        var existing = Appointment(1);
        var unverified = Task();
        unverified.Unverified.Add("data non trovata");

        var cards = CalendarScanReport.Payload(new[] { existing, unverified }, Pack(), i => ReferenceEquals(i, existing));

        Assert.Equal(2, cards.Count);
        Assert.Equal(true, cards[0].GetType().GetProperty("exists")!.GetValue(cards[0]));
        Assert.Equal("2026-11-20T10:00", cards[0].GetType().GetProperty("start")!.GetValue(cards[0]));
        Assert.Equal(false, cards[1].GetType().GetProperty("verified")!.GetValue(cards[1]));
        Assert.Equal("Non verificata: data non trovata", cards[1].GetType().GetProperty("warning")!.GetValue(cards[1]));
        Assert.Equal("2026-11-30", cards[1].GetType().GetProperty("due")!.GetValue(cards[1]));
    }

    [Fact]
    public void AppendItems_WritesOneLinePerItemWithTheOriginAndTheCheck()
    {
        var unverified = Task();
        unverified.Unverified.Add("data non trovata");
        var sb = new StringBuilder("Riassunto");

        CalendarScanReport.AppendItems(sb, new[] { Appointment(1), unverified }, Pack());

        string text = sb.ToString();
        Assert.StartsWith("Riassunto", text, StringComparison.Ordinal);
        Assert.Contains("- **Appuntamento** — Sopralluogo (", text, StringComparison.Ordinal);
        Assert.Contains(") — [M1] 02/11/2026 · Sopralluogo", text, StringComparison.Ordinal);
        Assert.Contains("- **Attività** — Inviare il modulo (", text, StringComparison.Ordinal);
        Assert.Contains(" — ⚠ data non trovata", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRows_UseTheDateFormatOfEachKind()
    {
        var rows = CalendarScanReport.ExportRows(new[] { Appointment(1), Task(2) }, Pack()).ToList();

        Assert.Equal(new[] { "Appuntamento", "Sopralluogo", "20/11/2026 10:00", "Cantiere", "n", "[M1] 02/11/2026 · Sopralluogo", "" }, rows[0]);
        Assert.Equal("Scadenza", rows[1][0]);
        Assert.Equal("30/11/2026", rows[1][2]);
    }
}
