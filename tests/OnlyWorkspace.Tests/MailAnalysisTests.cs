using System;
using System.Linq;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Prompts;
using OnlyWorkspace.Core.Security;
using Xunit;

namespace OnlyWorkspace.Tests;

public class MailAnalysisTests
{
    [Theory]
    [InlineData("unsupported", DocumentStatus.Incomplete, "elementi non supportati")]
    [InlineData("depth-limit", DocumentStatus.Incomplete, "profondità massima")]
    [InlineData("protected", DocumentStatus.Incomplete, "protetti da password")]
    [InlineData("scanned", DocumentStatus.Incomplete, "pagine scansionate")]
    [InlineData("ocr-off", DocumentStatus.Incomplete, "OCR è disattivato nelle impostazioni")]
    [InlineData("nested-read-error", DocumentStatus.Incomplete, "errore di lettura")]
    [InlineData("malformed-calendar", DocumentStatus.Incomplete, "calendario non valido")]
    [InlineData("unrecognized-problem", DocumentStatus.Incomplete, "non letto per intero")]
    [InlineData("text-limit", DocumentStatus.TooLarge, "troppo grande")]
    [InlineData("size-limit", DocumentStatus.TooLarge, "troppo grande")]
    [InlineData("expanded-size-limit", DocumentStatus.TooLarge, "troppo grande")]
    [InlineData(null, DocumentStatus.TooLarge, "troppo grande")]
    public void PartialExtractionReportsItsCauseWithoutClaimingItWasRead(string? problem, DocumentStatus expected, string reason)
    {
        var outcome = new DocumentOutcome { Name = "documento.zip" };
        Assert.False(outcome.WasRead);
        outcome.ApplyExtraction(new AttachmentExtraction { Truncated = true, Problem = problem, Text = "partial text" });
        Assert.False(outcome.WasRead);
        Assert.Equal(expected, outcome.Status);
        var stats = new AnalysisStats();
        stats.Documents.Add(outcome);
        Assert.Contains(reason, AnalysisReport.BuildFooter(stats));
    }

    [Theory]
    [InlineData("ocr-off", "OCR disattivato nelle impostazioni")]
    [InlineData("ocr-unavailable", "OCR non disponibile su questo PC")]
    [InlineData("scanned", "senza testo riconoscibile")]
    public void UnreadScanOrImageSaysWhy(string problem, string reason)
    {
        var outcome = new DocumentOutcome { Name = "foto.png" };
        outcome.ApplyExtraction(new AttachmentExtraction { Format = "image", Problem = problem });
        Assert.Equal(DocumentStatus.Scanned, outcome.Status);
        Assert.Contains(reason, outcome.Reason);
        Assert.False(outcome.WasRead);
    }

    [Theory]
    [InlineData("sample.doc")]
    [InlineData("sample.dot")]
    [InlineData("sample.xls")]
    [InlineData("sample.xlt")]
    [InlineData("sample.zip")]
    public void SupportedFormatsReportUnsupportedContentRatherThanRejectingTheWholeFormat(string name)
    {
        var outcome = new DocumentOutcome { Name = name };
        outcome.ApplyExtraction(new AttachmentExtraction { Problem = "unsupported" });
        Assert.Equal("struttura o contenuto non supportati", outcome.Reason);
        Assert.False(outcome.WasRead);
        outcome.ApplyExtraction(new AttachmentExtraction { Text = "whole document" });
        Assert.True(outcome.WasRead);
        Assert.Equal("letto", outcome.Reason);
    }

    [Theory]
    [InlineData("text-limit")]
    [InlineData("size-limit")]
    [InlineData("expanded-size-limit")]
    public void ExplicitSizeProblemsRemainTooLargeWithoutATruncatedFlag(string problem)
    {
        var outcome = new DocumentOutcome();
        outcome.ApplyExtraction(new AttachmentExtraction { Problem = problem });
        Assert.Equal(DocumentStatus.TooLarge, outcome.Status);
        Assert.False(outcome.WasRead);
    }

    [Fact]
    public void TextAccompaniedByAnErrorOrProblemCannotBeReportedAsRead()
    {
        var outcome = new DocumentOutcome();
        outcome.ApplyExtraction(new AttachmentExtraction { Text = "partial text", Error = "synthetic failure" });
        Assert.Equal(DocumentStatus.Error, outcome.Status);
        Assert.False(outcome.WasRead);
        outcome.ApplyExtraction(new AttachmentExtraction { Text = "partial text", Problem = "unsupported" });
        Assert.Equal(DocumentStatus.Unsupported, outcome.Status);
        Assert.False(outcome.WasRead);
    }

    [Fact]
    public void Goal_FreeTextAlone_IsTheGoal_WithPresetItRefinesIt()
    {
        Assert.Equal("scadenze delle licenze", new AnalysisGoal { Instruction = " scadenze delle licenze " }.Describe());

        var table = new AnalysisGoal { Preset = AnalysisGoal.Table, Instruction = "fornitore, scadenza" }.Describe();
        Assert.Contains("Raccogli i dati principali", table);
        Assert.EndsWith("Indicazioni dell'utente: fornitore, scadenza", table);
    }

    [Fact]
    public void Footer_ListsUnreadDocumentsWithReason()
    {
        var stats = new AnalysisStats { Found = 12, Analyzed = 10, Relevant = 4, Oldest = new DateTime(2026, 9, 1), Newest = new DateTime(2026, 10, 10) };
        var date = new DateTime(2026, 10, 3);
        stats.Documents.Add(new DocumentOutcome { Name = "contratto.docx", Status = DocumentStatus.Read, EmailSubject = "Rinnovo", EmailDate = date });
        stats.Documents.Add(new DocumentOutcome { Name = "scansione.pdf", Status = DocumentStatus.Scanned, EmailSubject = "Rinnovo", EmailDate = date });
        stats.Documents.Add(new DocumentOutcome { Name = "vecchio.doc", Status = DocumentStatus.Unsupported, EmailSubject = "Licenze", EmailDate = date });
        stats.Documents.Add(new DocumentOutcome { Name = "foto.heic", Status = DocumentStatus.Unsupported, EmailSubject = "Licenze", EmailDate = date });

        var footer = AnalysisReport.BuildFooter(stats);

        Assert.Contains("10 email lette su 12 (dal 01/09/2026 al 10/10/2026), 4 citate nella risposta", footer);
        Assert.DoesNotContain("limite di", footer);
        Assert.Contains("1 allegato letto.", footer);
        Assert.Contains("Documenti non letti (3)", footer);
        Assert.Contains("`scansione.pdf` — scansione o immagine senza testo riconoscibile", footer);
        Assert.Contains("`vecchio.doc` — struttura o contenuto non supportati", footer);
        Assert.Contains("`foto.heic` — immagine in un formato senza OCR", footer);
        Assert.DoesNotContain("`contratto.docx`", footer);
    }

    [Fact]
    public void Footer_CountsAttachmentsNotSentToTheProvider_WithoutListingThem()
    {
        var stats = new AnalysisStats { Found = 2, Analyzed = 2, Relevant = 2 };
        var date = new DateTime(2026, 10, 3);
        stats.Documents.Add(new DocumentOutcome { Name = "offerta.pdf", Status = DocumentStatus.NotSent, EmailSubject = "Offerta", EmailDate = date });
        stats.Documents.Add(new DocumentOutcome { Name = "listino.xlsx", Status = DocumentStatus.NotSent, EmailSubject = "Offerta", EmailDate = date });
        stats.Documents.Add(new DocumentOutcome { Name = "foto.jpg", Status = DocumentStatus.Unsupported, EmailSubject = "Offerta", EmailDate = date });

        var footer = AnalysisReport.BuildFooter(stats);

        Assert.Contains("2 allegati non inviati al provider esterno (opzione del profilo disattivata).", footer);
        Assert.Contains("Documenti non letti (1)", footer);
        Assert.Contains("`foto.jpg`", footer);
        Assert.DoesNotContain("offerta.pdf", footer);
    }

    [Theory]
    [InlineData("scansione.pdf", "pdf", "ocr-off", false, "scansione o immagine: OCR disattivato nelle impostazioni")]
    [InlineData("fattura.png", "image", "scanned", false, "scansione o immagine senza testo riconoscibile")]
    [InlineData("riservato.pdf", "pdf", "protected", false, "protetto da password")]
    [InlineData("listino.xlsx", "xlsx", "text-limit", true, "troppo grande per essere letto intero")]
    public void EmailAction_DeclaresEachUnreadAttachmentWithItsReason_InThePaneAndToTheModel(string name, string format, string problem, bool truncated, string reason)
    {
        var read = new DocumentOutcome { Name = "offerta.docx" };
        read.ApplyExtraction(new AttachmentExtraction { Format = "docx", Text = "Importo 1.250,00 EUR" });
        var unread = new DocumentOutcome { Name = name };
        unread.ApplyExtraction(new AttachmentExtraction { Format = format, Problem = problem, Truncated = truncated, Text = truncated ? "parte" : string.Empty });
        var documents = new[] { read, unread };

        var pane = AnalysisReport.EmailDocuments(documents);
        Assert.Contains("**Documenti non letti (1)**", pane);
        Assert.Contains("- `" + name + "` — " + reason, pane);
        Assert.DoesNotContain("offerta.docx", pane);
        Assert.DoesNotContain("· email", pane);

        Assert.Equal("\n\n[Allegati non letti, contenuto non incluso: " + name + " (" + reason + ")]", AnalysisReport.NotReadForModel(documents));
    }

    [Fact]
    public void EmailAction_AllAttachmentsRead_AddsNothing()
    {
        var read = new DocumentOutcome { Name = "offerta.docx" };
        read.ApplyExtraction(new AttachmentExtraction { Format = "docx", Text = "testo" });
        Assert.Equal(string.Empty, AnalysisReport.EmailDocuments(new[] { read }));
        Assert.Equal(string.Empty, AnalysisReport.NotReadForModel(new[] { read }));
    }

    [Fact]
    public void EmailAction_ExternalProfileWithoutConsent_CountsNotSentInThePane_AndNamesThemToTheModel()
    {
        var offer = new DocumentOutcome { Name = "offerta.pdf", Status = DocumentOutcome.NotCopied(new AttachmentFacts("offerta.pdf", 1000), hasReader: true, contentAllowed: false) };
        var photo = new DocumentOutcome { Name = "foto.heic", Status = DocumentOutcome.NotCopied(new AttachmentFacts("foto.heic", 1000), hasReader: false, contentAllowed: false) };
        var documents = new[] { offer, photo };

        var pane = AnalysisReport.EmailDocuments(documents);
        Assert.StartsWith("\n\n*1 allegato non inviato al provider esterno (opzione del profilo disattivata).*", pane);
        Assert.Contains("- `foto.heic` — immagine in un formato senza OCR", pane);
        Assert.Contains("offerta.pdf (contenuto non inviato al provider esterno); foto.heic (immagine in un formato senza OCR)", AnalysisReport.NotReadForModel(documents));
    }

    [Fact]
    public void NotCopied_SaysWhyOutlookGaveNoFile()
    {
        Assert.Equal(DocumentStatus.TooLarge, DocumentOutcome.NotCopied(new AttachmentFacts("grande.pdf", AttachmentFacts.MaxCopyBytes + 1), true, true));
        var link = new DocumentOutcome { Name = "contratto.pdf", Status = DocumentOutcome.NotCopied(new AttachmentFacts("contratto.pdf", 1000), true, true) };
        Assert.Equal(DocumentStatus.NotCopiable, link.Status);
        Assert.Contains("non salva come file", link.Reason);
    }

    [Fact]
    public void FlowFooter_ReportsMissingOriginalsAndDoesNotPresentAnInterruptedRunAsComplete()
    {
        var stats = new AnalysisStats { Found = 3, Analyzed = 1, Relevant = 1, Stopped = true };
        stats.Documents.Add(new DocumentOutcome
        {
            Name = "contratto.pdf", Status = DocumentStatus.NotSent,
            EmailSubject = "Offerta", EmailDate = new DateTime(2026, 10, 1)
        });
        var footer = AnalysisReport.BuildFooter(stats) + FlowAnalysisReport.MissingOriginals(2, new[] { "«Risposta» del 02/10/2026 10:00" });

        Assert.Contains("**Analisi interrotta**: 1 email letta su 3", footer);
        Assert.Contains("1 allegato non inviato al provider esterno", footer);
        Assert.DoesNotContain("contratto.pdf", footer);
        Assert.Contains("2 originali non disponibili", footer);
        Assert.Contains("La sintesi non copre quelle email", footer);
        Assert.Contains("«Risposta» del 02/10/2026 10:00; …", footer);
    }

    [Fact]
    public void Table_Csv_QuotesSeparatorsAndKeepsOneLinePerRow()
    {
        var csv = AnalysisTable.ToDelimited(new[] { "Nome", "Note" }, new[] { new[] { "ACME; Srl", "riga 1\nriga 2" } }, ';', quote: true);
        var lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("\"ACME; Srl\";riga 1 riga 2", lines[1]);
    }

    [Fact]
    public void Table_FormulaLikeCells_AreNeutralized_AmountsAreNot()
    {
        var rows = new[] { new[] { "=HYPERLINK(\"http://x\")", "-12,50" } };
        Assert.Equal("'=HYPERLINK(\"http://x\")\t-12,50", AnalysisTable.ToDelimited(new[] { "A", "B" }, rows, '\t', quote: false).Split('\n')[1].TrimEnd('\r'));
        Assert.StartsWith("\"'=HYPERLINK", AnalysisTable.ToDelimited(new[] { "A", "B" }, rows, ';', quote: true).Split('\n')[1]);
    }

    [Fact]
    public void SenderFilter_MatchesTheFromAddress()
    {
        // "senderemail" alone never matches in Outlook table filters: the address is in "fromemail".
        var filter = OnlyWorkspace.Core.Utils.DaslQueryBuilder.BuildEmailSearchFilter(new EmailSearchQuery { Sender = "laura@example.test" });
        Assert.Contains("\"urn:schemas:httpmail:fromemail\" LIKE '%laura@example.test%'", filter);
        Assert.Contains("\"urn:schemas:httpmail:sendername\" LIKE", filter);
    }

    [Fact]
    public void PromptBudget_IsTheProfileBudgetOnOllama_AndNoLimitOnCloud()
    {
        var local = new LlmProfileConfig { Type = "ollama", NumCtx = 16384, MaxOutputTokens = 2048, ContextBudgetTokens = 12698 };
        Assert.Equal(12698, ProviderCatalog.PromptBudget(local));

        // Values left in config.json by an older version are ignored: the provider decides.
        foreach (var type in new[] { "openai", "anthropic", "gemini" })
        {
            Assert.Equal(0, ProviderCatalog.PromptBudget(new LlmProfileConfig { Type = type, ContextBudgetTokens = 111104, MaxOutputTokens = 4096 }));
        }
        Assert.Equal(new[] { 65536, 131072, 262144 }, ProviderCatalog.Presets.Where(p => p.ContextWindow > 32768).Select(p => p.ContextWindow));
        Assert.All(ProviderCatalog.Presets.Where(p => p.ContextWindow > 32768), p => Assert.Equal(4096, p.MaxOutputTokens));
    }

    [Fact]
    public void Footer_ListsEmailsTooLongForTheProfile()
    {
        var stats = new AnalysisStats { Found = 3, Analyzed = 2, Relevant = 2 };
        stats.TooLong.Add("«Contratto quadro» del 02/10/2026");

        var footer = AnalysisReport.BuildFooter(stats);

        Assert.Contains("Email troppo lunghe per il contesto del profilo in uso (1)", footer);
        Assert.Contains("«Contratto quadro» del 02/10/2026", footer);
    }
}
