using System.Linq;
using OnlyWorkspace.Core.Security;
using Xunit;

namespace OnlyWorkspace.Tests;

public class EmailSecurityAnalyzerTests
{
    private const string PassHeaders =
        "Received: from mx.example.com\r\n" +
        "Authentication-Results: spf=pass (sender IP is 1.2.3.4)\r\n smtp.mailfrom=example.com; dkim=pass (signature was verified)\r\n header.d=example.com;dmarc=pass action=none header.from=example.com;compauth=pass reason=100\r\n" +
        "From: Mario Rossi <mario@example.com>\r\n";

    internal static EmailSecurityReport Analyze(string headers = "", string from = "mario@example.com", string fromName = "Mario Rossi",
        string html = "", string[]? attachments = null, string[]? own = null, string subject = "Offerta", string body = "Buongiorno, in allegato l'offerta.") =>
        EmailSecurityAnalyzer.Analyze(new EmailSecurityInput
        {
            Subject = subject,
            TransportHeaders = headers,
            FromAddress = from,
            FromName = fromName,
            HtmlBody = html,
            Body = body,
            Attachments = (attachments ?? new string[0]).Select(n => new AttachmentFacts(n) { ScanStatus = AttachmentScanStatus.Clean }).ToList(),
            OwnDomains = own ?? new[] { "contoso.com" }
        });

    internal static SecurityOutcome Outcome(EmailSecurityReport report) => SecurityVerdict.Decide(report).Outcome;

    private static ObservationKind KindOf(EmailSecurityReport report, string code) => report.Observations.First(o => o.Code == code).Kind;

    [Fact]
    public void AuthenticatedOrdinaryMessage_HasNoAnomaly()
    {
        var report = Analyze(PassHeaders);
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(report));
        Assert.Equal(ObservationKind.Context, KindOf(report, "dmarc.pass"));
        Assert.Equal(ObservationOrigin.Declared, report.Observations.First(o => o.Code == "dmarc.pass").Origin);
        Assert.False(SecurityVerdict.Decide(report).Partial);
    }

    [Fact]
    public void ConflictingAuthenticationResults_AreDeclaredAndToReview_WithoutClaimingTrust()
    {
        var headers = "Authentication-Results: spf=fail smtp.mailfrom=example.com; dkim=none; dmarc=fail action=oreject header.from=example.com;compauth=fail reason=000\r\n" +
                      "Authentication-Results: spf=pass; dkim=pass; dmarc=pass\r\n";
        var report = Analyze(headers);
        Assert.Equal(SecurityOutcome.ToReview, Outcome(report));
        Assert.Equal(ObservationKind.Attention, KindOf(report, "dmarc.fail"));
        Assert.Contains(report.Observations, o => o.Code == "compauth.fail" && o.Text.Contains("000"));
        Assert.Equal(ObservationKind.Context, KindOf(report, "spf.fail"));
        Assert.Contains(report.Limits, l => l.Text.Contains("contraddittori"));
        Assert.Contains(report.Limits, l => l.Text.Contains("origine e firme non verificate"));
    }

    [Fact]
    public void MissingAuthentication_IsALimitNotAnAlarm()
    {
        var report = Analyze("Received: from mx.example.com\r\nFrom: mario@example.com\r\n");
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(report));
        Assert.Contains(report.Limits, l => l.Text.Contains("SPF/DKIM/DMARC") && !l.Partial);
    }

    [Fact]
    public void ReplyToOnOtherDomain_IsToReview_ButContextForAutomaticMail()
    {
        var report = Analyze(PassHeaders + "Reply-To: \"Mario\" <mario.rossi@gmail.com>\r\n");
        Assert.Equal(ObservationKind.Attention, KindOf(report, "replyto.mismatch"));
        Assert.Equal(SecurityOutcome.ToReview, Outcome(report));

        var newsletter = Analyze(PassHeaders + "List-Id: <news.example.com>\r\nReply-To: <help@service.example.org>\r\n");
        Assert.Equal(ObservationKind.Context, KindOf(newsletter, "replyto.mismatch"));
        Assert.Contains(newsletter.Observations, o => o.Code == "context.automatic");
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(newsletter));
    }

    [Theory]
    [InlineData("c0ntoso.com")]
    [InlineData("contosso.com")]
    [InlineData("rncontoso.com")]
    public void LookalikeOfOwnDomain_IsToReview(string domain)
    {
        var report = Analyze(PassHeaders, from: "ceo@" + domain, own: new[] { domain == "rncontoso.com" ? "mcontoso.com" : "contoso.com" });
        Assert.Equal(ObservationKind.Attention, KindOf(report, "domain.lookalike"));
        Assert.Equal(SecurityOutcome.ToReview, Outcome(report));
    }

    [Fact]
    public void OwnDomain_IsNotLookalike()
    {
        var report = Analyze(PassHeaders, from: "anna@mail.contoso.com");
        Assert.DoesNotContain(report.Observations, o => o.Code == "domain.lookalike");
    }

    [Fact]
    public void PunycodeSender_IsContextOnly()
    {
        var report = Analyze(PassHeaders, from: "info@xn--pple-43d.com");
        Assert.Equal(ObservationKind.Context, KindOf(report, "domain.idn"));
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(report));
    }

    /// <summary>The regression: a PEC notice whose display name carries the provider's other address is not high risk.</summary>
    [Fact]
    public void LegalmailNotice_WithAnotherAddressOfTheSameProvider_IsNotAnAlarm()
    {
        var report = Analyze("Received: from mx.legalmail.it\r\nFrom: \"avvisi@legalmail.it\" <posta-certificata@legalmail.it>\r\n",
            from: "posta-certificata@legalmail.it", fromName: "avvisi@legalmail.it", subject: "Avviso di scadenza della casella");
        Assert.Equal(ObservationKind.Context, KindOf(report, "from.shownsame"));
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(report));
        Assert.Contains(report.Limits, l => l.Text.Contains("SPF/DKIM/DMARC"));

        // Installed qualification (28/10/2026): with a PEC box of the same provider it was "un tuo indirizzo", red.
        var ownProvider = Analyze("Received: from mx.legalmail.it\r\nFrom: \"avvisi@legalmail.it\" <posta-certificata@legalmail.it>\r\n",
            from: "posta-certificata@legalmail.it", fromName: "avvisi@legalmail.it", subject: "Avviso di scadenza della casella", own: new[] { "gmail.com", "legalmail.it" });
        Assert.DoesNotContain(ownProvider.Observations, o => o.Code == "from.shownown");
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(ownProvider));
    }

    [Fact]
    public void DisplayNameWithAnAddressOfYourProvider_IsAnotherDomain_NotYourAddress()
    {
        var report = Analyze(PassHeaders, from: "x@attacker.net", fromName: "mario.rossi@gmail.com", own: new[] { "gmail.com" });
        Assert.DoesNotContain(report.Observations, o => o.Code == "from.shownown");
        Assert.Equal(ObservationKind.Attention, KindOf(report, "from.shownother"));
    }

    [Fact]
    public void PecEnvelope_ShowingTheOriginalSender_IsContext_ButALookalikeInsideStaysAnObservation()
    {
        var envelope = Analyze("X-Trasporto: posta-certificata\r\nFrom: \"Per conto di: mario@fornitore.it\" <posta-certificata@legalmail.it>\r\n",
            from: "posta-certificata@legalmail.it", fromName: "Per conto di: mario@fornitore.it", subject: "POSTA CERTIFICATA: Ordine 12");
        Assert.Contains(envelope.Observations, o => o.Code == "context.pec");
        Assert.Equal(ObservationKind.Context, KindOf(envelope, "from.pecshown"));
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(envelope));

        // No exemption for PEC: a lookalike of the user's domain in the envelope is still observed.
        var lookalike = Analyze("X-Trasporto: posta-certificata\r\n", from: "posta-certificata@legalmail.it",
            fromName: "Per conto di: ceo@c0ntoso.com", subject: "POSTA CERTIFICATA: Pagamento");
        Assert.Contains(lookalike.Observations, o => o.Code == "domain.lookalike");
        Assert.Equal(SecurityOutcome.ToReview, Outcome(lookalike));
    }

    [Fact]
    public void PecSubjectAlone_IsNotAnEnvelope()
    {
        var report = Analyze(PassHeaders, from: "x@attacker.net", fromName: "ceo@fornitore.it", subject: "POSTA CERTIFICATA: fattura");
        Assert.DoesNotContain(report.Observations, o => o.Code == "context.pec");
        Assert.Equal(ObservationKind.Attention, KindOf(report, "from.shownother"));
    }

    [Fact]
    public void DisplayNameWithAddressOfAnotherDomain_IsToReview_AndWithYourAddressIsSuspicious()
    {
        var other = Analyze(PassHeaders, from: "x@attacker.net", fromName: "ceo@fornitore.it");
        Assert.Equal(ObservationKind.Attention, KindOf(other, "from.shownother"));
        Assert.Equal(SecurityOutcome.ToReview, Outcome(other));

        var own = Analyze(PassHeaders, from: "x@attacker.net", fromName: "ceo@contoso.com");
        Assert.Equal(ObservationKind.Deception, KindOf(own, "from.shownown"));
        Assert.Contains("del tuo dominio", own.Observations.First(o => o.Code == "from.shownown").Text);
        Assert.Equal(SecurityOutcome.Suspicious, Outcome(own));
    }

    [Fact]
    public void OnBehalfAndForward_AreExplained()
    {
        var report = Analyze(PassHeaders + "Sender: <assistente@example.com>\r\n", subject: "I: preventivo");
        Assert.Contains(report.Observations, o => o.Code == "context.onbehalf");
        Assert.Contains(report.Observations, o => o.Code == "context.forward");
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(report));
    }

    [Theory]
    [InlineData("anna.bianchi", "x9f3@attacker.net", ObservationKind.Deception)]   // own address as display name
    [InlineData("Anna Bianchi", "x9f3@attacker.net", ObservationKind.Attention)]  // own name, foreign sender
    [InlineData("ANNA BIANCHI", "billing@gmail.com", ObservationKind.Attention)]
    public void SenderNamedLikeTheRecipient_IsObserved(string fromName, string from, ObservationKind kind)
    {
        var report = AnalyzeAs(fromName, from);
        Assert.Contains(report.Observations, o => o.Code is "from.selfaddress" or "from.selfname" && o.Kind == kind);
    }

    [Theory]
    [InlineData("Anna Bianchi", "anna.bianchi@contoso.com")]  // really me
    [InlineData("Anna Bianchi", "anna.bianchi@gmail.com")]    // name mirrors the sender's own address
    [InlineData("Mario Rossi", "mario@example.com")]
    [InlineData("Ann", "x@attacker.net")]
    public void SenderWithOwnOrUnrelatedName_IsNotFlagged(string fromName, string from)
    {
        Assert.DoesNotContain(AnalyzeAs(fromName, from).Observations, o => o.Code is "from.selfname" or "from.selfaddress");
    }

    private static EmailSecurityReport AnalyzeAs(string fromName, string from) =>
        EmailSecurityAnalyzer.Analyze(new EmailSecurityInput
        {
            TransportHeaders = PassHeaders,
            FromAddress = from,
            FromName = fromName,
            Body = "Testo",
            OwnDomains = new[] { "contoso.com" },
            OwnAddresses = new[] { "anna.bianchi@contoso.com" },
            OwnNames = new[] { "Anna Bianchi" }
        });

    [Fact]
    public void LinkTextShowingOtherDomain_IsSuspicious_ButFileNamesAndSafeLinksAreNot()
    {
        var html = "<a href=\"https://evil.example.net/login\">www.contoso.com</a>" +
                   "<a href=\"https://contoso.sharepoint.com/doc\">fattura.pdf</a>" +
                   "<a href=\"https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fwww.fabrikam.com%2Fa&data=x\">www.fabrikam.com</a>";
        var report = Analyze(PassHeaders, html: html);
        var mismatches = report.Observations.Where(o => o.Code == "link.mismatch").ToList();
        Assert.Single(mismatches);
        Assert.Contains("evil.example.net", mismatches[0].Text);
        Assert.Equal("Email", mismatches[0].Source);
        Assert.Equal(SecurityOutcome.Suspicious, Outcome(report));
    }

    [Fact]
    public void ScriptLinksAreSuspicious_IpLinksToReview()
    {
        var report = Analyze(PassHeaders, html: "<a href='javascript:alert(1)'>clic</a><a href=http://192.168.10.5/x>qui</a>");
        Assert.Equal(ObservationKind.Deception, KindOf(report, "link.script"));
        Assert.Equal(ObservationKind.Attention, KindOf(report, "link.ip"));
    }

    [Fact]
    public void Attachments_AreDescribedByWhatTheyAre_NotByPresence()
    {
        var report = Analyze(PassHeaders, attachments: new[] { "fattura.pdf.exe", "report.xlsm", "foto.zip", "documento.pdf", "setup.msi" });
        Assert.Equal(ObservationKind.Deception, report.Observations.First(o => o.Code == "att.double" && o.Text.Contains("fattura.pdf.exe")).Kind);
        Assert.Equal(ObservationKind.Attention, report.Observations.First(o => o.Code == "att.exec" && o.Text.Contains("setup.msi")).Kind);
        // Not inspected: a macro format cannot be checked; inspected without macros it is only context.
        Assert.Equal(ObservationKind.Attention, KindOf(report, "att.macroformat"));
        Assert.Equal(ObservationKind.Context, KindOf(report, "att.archive"));
        Assert.DoesNotContain(report.Observations, o => o.Text.Contains("documento.pdf") && o.Kind != ObservationKind.Context);
    }

    [Fact]
    public void ProgramsInsideAnArchive_AreJudgedByTheirNames()
    {
        // Installed qualification (28/10/2026): a ZIP with fattura.pdf.exe was green.
        var inspection = new AttachmentInspection { Format = "zip", Inspected = 1 };
        inspection.Entries.Add(new InspectionEntry { File = "fattura.pdf.exe", DetectedType = "exe" });
        inspection.Entries.Add(new InspectionEntry { File = "strumenti.zip › setup.msi" });
        inspection.Entries.Add(new InspectionEntry { File = "foto.jpg", DetectedType = "exe" });
        inspection.Entries.Add(new InspectionEntry { File = "nota.txt" });
        var report = EmailSecurityAnalyzer.Analyze(new EmailSecurityInput
        {
            Body = "x",
            Attachments = new[] { new AttachmentFacts("fattura.zip") { ScanStatus = AttachmentScanStatus.Clean, Inspection = inspection } }
        });
        var masked = report.Observations.Single(o => o.Code == "att.double");
        Assert.Equal(ObservationKind.Deception, masked.Kind);
        Assert.Equal("allegato «fattura.zip» › fattura.pdf.exe", masked.Source);
        Assert.Contains(report.Observations, o => o.Code == "att.exec" && o.Text.Contains("setup.msi") && o.Kind == ObservationKind.Attention);
        Assert.Contains(report.Observations, o => o.Code == "att.disguised" && o.Text.Contains("foto.jpg"));
        Assert.DoesNotContain(report.Observations, o => o.Text.Contains("nota.txt"));
        Assert.Equal(SecurityOutcome.Suspicious, Outcome(report));
    }

    [Fact]
    public void AttachmentsBlockedByOutlook_AreToReview_AndALimit()
    {
        // Installed qualification (28/10/2026): Outlook hid aggiorna.ps1 from the Object Model and the badge was green.
        var report = EmailSecurityAnalyzer.Analyze(new EmailSecurityInput { TransportHeaders = PassHeaders, FromAddress = "mario@example.com", Body = "x", HasAttachmentFlag = true });
        Assert.Equal(ObservationKind.Attention, KindOf(report, "att.blocked"));
        Assert.Contains(report.Limits, l => l.Partial && l.Text.Contains("bloccati da Outlook"));
        Assert.Equal(SecurityOutcome.ToReview, Outcome(report));
    }

    [Theory]
    [InlineData("Allegato 1: nome non disponibile, controlli non eseguiti.", false)]
    [InlineData("Allegato 1: metadati non leggibili, controlli non eseguiti.", false)]
    [InlineData("Elenco degli allegati non letto integralmente: controlli limitati agli elementi disponibili.", false)]
    [InlineData("HTML non disponibile: controllati solo i link nel corpo di testo leggibile.", true)]
    public void BlockedAttachmentInference_RequiresACompleteInventory(string limitation, bool complete)
    {
        var input = new EmailSecurityInput
        {
            TransportHeaders = PassHeaders, FromAddress = "mario@example.com", Body = "x",
            HasAttachmentFlag = true, AttachmentListComplete = complete
        };
        input.ReadLimitations.Add(limitation);
        var report = EmailSecurityAnalyzer.Analyze(input);
        Assert.Equal(complete, report.Observations.Any(o => o.Code == "att.blocked"));
        Assert.Contains(report.Limits, l => l.Partial && l.Text == limitation);
        if (!complete) Assert.DoesNotContain(report.Limits, l => l.Text.Contains("bloccati da Outlook"));
    }

    [Fact]
    public void VisibleAttachments_DoNotProveWhetherOtherFilesWereBlocked()
    {
        var report = EmailSecurityAnalyzer.Analyze(new EmailSecurityInput
        {
            TransportHeaders = PassHeaders, FromAddress = "mario@example.com", Body = "x", HasAttachmentFlag = true,
            Attachments = new[] { new AttachmentFacts("nota.txt") }
        });
        Assert.DoesNotContain(report.Observations, o => o.Code == "att.blocked");
    }

    [Fact]
    public void MacroFormatWithoutMacros_AfterInspection_IsContext()
    {
        var report = EmailSecurityAnalyzer.Analyze(new EmailSecurityInput
        {
            Body = "x",
            Attachments = new[] { new AttachmentFacts("report.xlsm") { ScanStatus = AttachmentScanStatus.Clean, Inspection = new AttachmentInspection { Format = "xlsx", Inspected = 1 } } }
        });
        Assert.Equal(ObservationKind.Context, KindOf(report, "att.macroformat"));
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(report));
    }

    [Fact]
    public void DefenderResults_StaySeparate_AndNeverDecideTheBadge()
    {
        var disguised = new AttachmentFacts("fattura.pdf", 2048) { DetectedType = FileSignature.Detect(new byte[] { 0x4D, 0x5A, 0x90, 0 }, 4), ScanStatus = AttachmentScanStatus.Clean };
        var renamed = new AttachmentFacts("offerta.docx", 4096) { DetectedType = "ole", ScanStatus = AttachmentScanStatus.Clean };
        var report = EmailSecurityAnalyzer.Analyze(new EmailSecurityInput { TransportHeaders = PassHeaders, FromAddress = "mario@example.com", Body = "x", Attachments = new[] { disguised, renamed } });
        Assert.Equal(ObservationKind.Deception, KindOf(report, "att.disguised"));
        Assert.Equal(ObservationKind.Attention, KindOf(report, "att.mismatch"));

        var infected = new AttachmentFacts("listino.xlsx", 1024) { DetectedType = "zip", ScanStatus = AttachmentScanStatus.Threat, ScanDetail = "Trojan:Test" };
        var threat = EmailSecurityAnalyzer.Analyze(new EmailSecurityInput { TransportHeaders = PassHeaders, FromAddress = "mario@example.com", Body = "x", Attachments = new[] { infected } });
        Assert.DoesNotContain(threat.Observations, o => o.Text.Contains("Trojan"));
        var entry = Assert.Single(threat.Defender);
        Assert.Equal("Defender ha segnalato una minaccia in «listino.xlsx»: Trojan:Test.", entry.Text);
        Assert.Contains(threat.Limits, l => l.Text.Contains("listino.xlsx") && l.Partial);
        var view = SecurityReportView.From(threat, SecurityCheckState.Done, string.Empty);
        Assert.Equal("none", view.Outcome);
        Assert.True(view.Partial);
        Assert.Equal("threat", Assert.Single(view.Defender).Status);
    }

    [Fact]
    public void RemovedCopyAndFailedScan_AreLimits_NotInfections()
    {
        var removed = new AttachmentFacts("a.pdf") { ScanStatus = AttachmentScanStatus.Removed };
        var failed = new AttachmentFacts("b.pdf") { ScanStatus = AttachmentScanStatus.Failed, ScanDetail = "timeout" };
        var clean = new AttachmentFacts("c.pdf") { ScanStatus = AttachmentScanStatus.Clean };
        var report = EmailSecurityAnalyzer.Analyze(new EmailSecurityInput { Body = "x", Attachments = new[] { removed, failed, clean } });
        Assert.DoesNotContain(report.Observations, o => o.Kind != ObservationKind.Context);
        Assert.Contains(report.Limits, l => l.Text.Contains("a.pdf") && l.Text.Contains("causa non confermata"));
        Assert.Contains(report.Limits, l => l.Text.Contains("b.pdf") && l.Text.Contains("timeout"));
        Assert.Contains(report.Defender, d => d.File == "c.pdf" && d.Text.Contains("non certifica"));
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(report));
        Assert.True(SecurityVerdict.Decide(report).Partial);
    }

    [Fact]
    public void MissingHeaders_IsALimitOnly()
    {
        var report = Analyze(from: "/O=EXCHANGELABS/OU=EXCHANGE/CN=RECIPIENTS/CN=ANNA");
        Assert.Equal(SecurityOutcome.NoAnomaly, Outcome(report));
        Assert.Contains(report.Limits, l => l.Text.Contains("Intestazioni Internet assenti") && !l.Partial);
    }

    [Fact]
    public void MicrosoftPhishVerdict_IsADeclarationToReview()
    {
        var report = Analyze(PassHeaders + "X-Forefront-Antispam-Report: CIP:1.2.3.4;CTRY:;LANG:it;SCL:5;SRV:;IPV:NLI;SFV:SPM;H:x;PTR:;CAT:PHSH;SFTY:9.25;\r\n");
        var cat = report.Observations.First(o => o.Code == "ms.cat");
        Assert.Equal(ObservationOrigin.Declared, cat.Origin);
        Assert.Equal(ObservationKind.Attention, cat.Kind);
        Assert.Contains(report.Observations, o => o.Code == "ms.firstcontact");
        Assert.Equal(SecurityOutcome.ToReview, Outcome(report));
    }

    [Fact]
    public void NothingReadable_IsUnverifiable_NeverGreen()
    {
        var report = Analyze(PassHeaders, body: "");
        Assert.Equal(SecurityOutcome.Unverifiable, Outcome(report));
    }

    [Fact]
    public void TwoDescriptionsOfOneFact_AreOneReason()
    {
        var report = Analyze(PassHeaders, html: "<a href=\"https://evil.example.net/a\">www.contoso.com</a><a href=\"https://evil.example.net/b\">www.contoso.com</a>");
        var verdict = SecurityVerdict.Decide(report);
        Assert.Single(verdict.Reasons);
        Assert.Single(SecurityReportView.From(report, SecurityCheckState.Done, string.Empty).Observations, o => o.Text.Contains("evil.example.net"));
    }

    [Fact]
    public void KnownDomainDoesNotLowerConcreteEvidence()
    {
        // A trusted-looking provider and a passing DMARC do not clear a masked attachment.
        var facts = new AttachmentFacts("fattura.pdf") { DetectedType = "exe", ScanStatus = AttachmentScanStatus.Clean };
        var report = EmailSecurityAnalyzer.Analyze(new EmailSecurityInput { TransportHeaders = PassHeaders, FromAddress = "fatture@legalmail.it", Subject = "POSTA CERTIFICATA: fattura", Body = "x", Attachments = new[] { facts } });
        Assert.Equal(SecurityOutcome.Suspicious, Outcome(report));
        Assert.StartsWith("Non aprire l’allegato", SecurityVerdict.Decide(report).Action);
    }

    [Fact]
    public void InspectionFindings_BecomeObservationsWithTheirPlace()
    {
        var inspection = new AttachmentInspection { Format = "zip", Inspected = 2 };
        inspection.Findings.Add(new InspectionFinding { Rule = "script.download-exec", File = "avvio.ps1", Line = 4, Snippet = "Invoke-Expression $r.Content" });
        inspection.Limits.Add(new InspectionLimit { File = "avvio.ps1", Reason = "obfuscated" });
        inspection.Links.Add(new InspectionLink { File = "pagina.html", Text = "www.contoso.com", Destination = "https://evil.example.net/" });
        var facts = new AttachmentFacts("pacchetto.zip") { DetectedType = "zip", ScanStatus = AttachmentScanStatus.Clean, Inspection = inspection };
        var report = EmailSecurityAnalyzer.Analyze(new EmailSecurityInput { Body = "x", Attachments = new[] { facts } });

        var script = report.Observations.First(o => o.Code == "script.download-exec");
        Assert.Equal(ObservationKind.Deception, script.Kind);
        Assert.Equal("allegato «pacchetto.zip» › avvio.ps1, riga 4", script.Source);
        Assert.Equal("Invoke-Expression $r.Content", script.Quote);
        Assert.Contains(report.Observations, o => o.Code == "link.mismatch" && o.Source == "allegato «pacchetto.zip» › pagina.html");
        Assert.Contains(report.Limits, l => l.Text.Contains("offuscato") && l.Partial);
        Assert.DoesNotContain(report.Observations, o => o.Code == "att.archive");
        Assert.Equal("Lo script scarica codice da Internet e lo esegue.", SecurityVerdict.Decide(report).Reasons[0]);
    }
}
