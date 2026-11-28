using System;
using System.Linq;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Prompts;
using Xunit;

namespace OnlyWorkspace.Tests;

public class MailScanTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0);

    [Theory]
    [InlineData("Riunione di progetto", "Ci aggiorniamo sullo stato dei lavori.")]
    [InlineData("Re: offerta", "Possiamo sentirci giovedì alle 15?")]
    [InlineData("Documenti", "Vi chiediamo di inviare il modulo entro il 20/10.")]
    [InlineData("Invito", "Partecipa: https://teams.microsoft.com/l/meetup-join/abc")]
    [InlineData("Follow up", "Let's have a call tomorrow at 10:30.")]
    public void IsCandidate_AcceptsMeetingsAndDeadlines(string subject, string body)
    {
        Assert.True(CalendarCandidateFilter.IsCandidate(subject, body));
    }

    [Theory]
    [InlineData("Newsletter di ottobre", "Scopri le novità del nostro catalogo e le offerte del mese.")]
    [InlineData("Fattura n. 124", "In allegato la fattura del 03/10/2026 per un totale di 1.250,00 euro.")]
    [InlineData("", "")]
    public void IsCandidate_RejectsEmailsWithoutMeetingOrDeadline(string subject, string body)
    {
        Assert.False(CalendarCandidateFilter.IsCandidate(subject, body));
    }

    [Fact]
    public void Merge_DropsPastItems_MergesDuplicates_AndSortsByDate()
    {
        var items = new[]
        {
            Appointment("Call con fornitore Rossi", new DateTime(2026, 10, 20, 10, 0, 0)),
            Appointment("Riunione passata", new DateTime(2026, 10, 1, 10, 0, 0)),
            Appointment("Call fornitore Rossi", new DateTime(2026, 10, 20, 10, 0, 0)),
            Todo("Inviare preventivo", new DateTime(2026, 10, 15)),
            Todo("Inviare preventivo", new DateTime(2026, 10, 5)),
            Todo("Richiamare il cliente", null)
        };

        var merged = ActionItemMerger.Merge(items, Now);

        Assert.Equal(new[] { "Inviare preventivo", "Call con fornitore Rossi", "Richiamare il cliente" }, merged.Select(i => i.Title).ToArray());
    }

    [Theory]
    [InlineData(3, false, true, "3 email candidate, nessuna richiesta inviata al modello. ")]
    [InlineData(3, true, true, "3 inviate al modello in una sola richiesta, interrotta prima della risposta: nessuna voce ricevuta. ")]
    [InlineData(1, true, false, "1 inviata al modello in una sola richiesta perché parlano di incontri o scadenze. ")]
    [InlineData(0, false, false, "nessuna parla di incontri o scadenze. ")]
    public void ScanSummary_SaysTheRequestWasSent_OnceItStarted(int candidates, bool requested, bool stopped, string expected)
    {
        Assert.Equal(expected, CalendarScanSummary.Request(candidates, requested, stopped));
    }

    [Fact]
    public void Merge_KeepsTheVerifiedCopyOfADuplicate()
    {
        var unverified = Appointment("Call con fornitore Rossi", new DateTime(2026, 10, 20, 10, 0, 0));
        unverified.Source = 1;
        unverified.Unverified.Add("«martedì» non è nell'email citata");
        var verified = Appointment("Call fornitore Rossi", new DateTime(2026, 10, 20, 10, 0, 0));
        verified.Source = 2;
        var later = Appointment("Call fornitore Rossi", new DateTime(2026, 10, 20, 10, 0, 0));
        later.Source = 3;
        later.Unverified.Add("data diversa");

        var merged = ActionItemMerger.Merge(new[] { unverified, verified, later }, Now);

        Assert.Equal(2, Assert.Single(merged).Source);
    }

    [Fact]
    public void Merge_DatedOnly_DropsItemsWithoutDate()
    {
        var items = new[]
        {
            Todo("Richiamare il cliente", null),
            Todo("Inviare preventivo", new DateTime(2026, 10, 15)),
            Appointment("Call con fornitore Rossi", new DateTime(2026, 10, 20, 10, 0, 0))
        };

        var merged = ActionItemMerger.Merge(items, Now, datedOnly: true);

        Assert.Equal(new[] { "Inviare preventivo", "Call con fornitore Rossi" }, merged.Select(i => i.Title).ToArray());
    }

    [Fact]
    public void CertainDatesRule_ReachesTheScanOnly()
    {
        var scan = PromptCatalog.CreateCalendarScanPrompt("[M1] testo").First().Content;
        var single = string.Concat(PromptCatalog.CreateActionItemsPrompt("testo", Now).Select(m => m.Content));

        Assert.Contains("data esplicita e confermata", scan);
        Assert.DoesNotContain("data esplicita e confermata", single);
    }

    [Fact]
    public void Merge_KeepsDifferentMeetingsAtTheSameTime()
    {
        var start = new DateTime(2026, 10, 20, 10, 0, 0);
        var merged = ActionItemMerger.Merge(new[] { Appointment("Call fornitore Rossi", start), Appointment("Visita medica", start) }, Now);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void ExistsInCalendar_MatchesCloseStartAndSimilarTitle()
    {
        var calendar = new[] { new AppointmentInfo { Subject = "Call fornitore Rossi", Start = new DateTime(2026, 10, 20, 10, 10, 0) } };

        Assert.True(ActionItemMerger.ExistsInCalendar(Appointment("Call con Rossi", new DateTime(2026, 10, 20, 10, 0, 0)), calendar));
        Assert.False(ActionItemMerger.ExistsInCalendar(Appointment("Call con Rossi", new DateTime(2026, 10, 21, 10, 0, 0)), calendar));
        Assert.False(ActionItemMerger.ExistsInCalendar(Appointment("Visita medica", new DateTime(2026, 10, 20, 10, 0, 0)), calendar));
    }

    [Fact]
    public void ExistsInTasks_MatchesTitleAndDueDate()
    {
        var open = new[] { Todo("Inviare preventivo", new DateTime(2026, 10, 15)) };

        Assert.True(ActionItemMerger.ExistsInTasks(Todo("Inviare il preventivo", new DateTime(2026, 10, 15)), open));
        Assert.False(ActionItemMerger.ExistsInTasks(Todo("Inviare il preventivo", new DateTime(2026, 10, 16)), open));
    }

    [Fact]
    public void CreateRefineReplyPrompt_CarriesDraftInstructionAndEmail()
    {
        var messages = PromptCatalog.CreateRefineReplyPrompt("Testo dell'email originale.", "Gentile cliente, confermo.", "più breve e formale").ToList();

        Assert.Equal(2, messages.Count);
        Assert.Contains("<bozza>", messages[0].Content);
        Assert.Contains("<bozza>\nGentile cliente, confermo.\n</bozza>", messages[1].Content);
        Assert.Contains("più breve e formale", messages[1].Content);
        Assert.Contains("<email>\nTesto dell'email originale.\n</email>", messages[1].Content);
    }

    private static ExtractedActionItem Appointment(string title, DateTime start) =>
        new() { Kind = "appointment", Title = title, Start = start, End = start.AddHours(1) };

    private static ExtractedActionItem Todo(string title, DateTime? due) => new() { Kind = "task", Title = title, Due = due };
}
