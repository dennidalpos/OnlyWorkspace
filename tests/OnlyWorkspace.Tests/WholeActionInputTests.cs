using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Prompts;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Tests;

public sealed class WholeActionInputTests
{
    [Theory]
    [InlineData(true, ExposureLevel.Minimal, false)]
    [InlineData(true, ExposureLevel.LocalOnly, false)]
    [InlineData(true, ExposureLevel.Standard, false)]
    [InlineData(false, ExposureLevel.Minimal, true)]
    public void Briefing_AgendaReachesTheModelOnlyWhenWholeOutlookItemsArePermitted(bool external, ExposureLevel exposure, bool permitted)
    {
        var profile = new LlmProfileConfig { External = external, Exposure = exposure };
        const string agenda = "09:30 CALENDARSENTINELPRIVATE (LOCATIONSENTINELPRIVATE)";
        var prompt = PromptCatalog.CreateBriefingPrompt(BriefingContext.AgendaForModel(profile, agenda), "Fatti locali", Array.Empty<string>(), DateTime.Today);
        string rendered = string.Join("\n", prompt.Select(m => m.Content));
        Assert.Equal(permitted, rendered.Contains("CALENDARSENTINELPRIVATE"));
        Assert.Equal(permitted, rendered.Contains("LOCATIONSENTINELPRIVATE"));
    }

    [Fact]
    public void ActionFactsContainLiteralLocalDatesAndNeverIncludeWholeBodyOrPaidSummary()
    {
        var facts = OnlyWorkspace.Core.Archive.ArchiveFactExtractor.Extract("Scadenza pagamento: 15/11/2026");
        string evidence = OnlyWorkspace.Core.Archive.ActionMailEvidence.Facts(facts)!;
        Assert.Contains("15/11/2026", evidence);
        Assert.Null(OnlyWorkspace.Core.Archive.ActionMailEvidence.Facts(Array.Empty<OnlyWorkspace.Core.Archive.ArchiveFact>()));
    }

    [Fact]
    public void Briefing_CarriesEndOfOriginalAndAttachment_InsteadOfTheSearchPreview()
    {
        var mail = new EmailSearchResult { Subject = "Ordine", Body = "ANTEPRIMA", ReceivedTime = new DateTime(2026, 10, 21) };
        string body = new string('b', 12000) + " FINALE-ORIGINALE";
        string attachment = "\n[Allegato: ordine.txt]\n" + new string('a', 16000) + " FINALE-ALLEGATO";
        var data = BriefingContext.Email(1, mail, body, attachment);
        var prompt = PromptCatalog.CreateBriefingPrompt("Agenda", data, Array.Empty<string>(), mail.ReceivedTime).ToList();
        var rendered = string.Join("\n", prompt.Select(m => m.Content));
        Assert.Contains(body, rendered);
        Assert.Contains(attachment, rendered);
        Assert.DoesNotContain("ANTEPRIMA", rendered);
        Assert.Throws<ContextTooLongException>(() => new ContextBuilder().EnsureFits(rendered, 100));
    }

    [Fact]
    public void RefineReply_KeepsTheWholeDraft_AndChecksPromptOverheadAndEvidence()
    {
        string draft = new string('a', 9000) + " FINALE-BOZZA";
        var prompt = PromptCatalog.CreateRefineReplyPrompt("Originale", draft, "Conserva tutti i dati").ToList();
        string rendered = string.Join("\n", prompt.Select(m => m.Content));
        Assert.Contains(draft, rendered);
        var builder = new ContextBuilder();
        builder.EnsureFits(rendered, 10000);
        prompt.Add(PromptCatalog.CreateWebEvidenceMessage(new string('w', 60000), "[W1] fonte"));
        Assert.Throws<ContextTooLongException>(() => builder.EnsureFits(string.Join("\n", prompt.Select(m => m.Content)), 10000));
    }
}
