using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Tests;

public class SecurityPrecisionTests
{
    private static bool Has(EmailSecurityReport report, string code) => report.Observations.Any(o => o.Code == code);

    [Fact]
    public void ContextObservationsDoNotAddUp()
    {
        var report = EmailSecurityAnalyzer.Analyze(new()
        {
            Body = "x",
            FromAddress = "info@xn--bcher-kva.de",
            TransportHeaders = "Authentication-Results: receiver; spf=temperror; dmarc=none; compauth=softpass\r\nX-Forefront-Antispam-Report: SFTY:9.25;\r\n",
            Attachments = new[] { new AttachmentFacts("photo.zip") { ScanStatus = AttachmentScanStatus.Clean } }
        });
        Assert.Equal(SecurityOutcome.NoAnomaly, SecurityVerdict.Decide(report).Outcome);
        Assert.Contains(report.Observations, o => o.Code == "domain.idn" && o.Kind == ObservationKind.Context);
    }

    [Fact]
    public void OneMacroIsToReview_NotSuspicious()
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = "x", Attachments = new[] { new AttachmentFacts("report.docx") { HasMacros = true, ScanStatus = AttachmentScanStatus.Clean } } });
        Assert.Equal(SecurityOutcome.ToReview, SecurityVerdict.Decide(report).Outcome);
    }

    [Theory]
    [InlineData("https://a.github.io/path", "https://b.github.io/path", true)]
    [InlineData("https://mail.example.co.uk", "https://billing.example.co.uk", false)]
    [InlineData("https://foo.city.kawasaki.jp", "https://bar.city.kawasaki.jp", false)]
    [InlineData("https://bücher.de", "https://xn--bcher-kva.de", false)]
    public void LinkDomainComparisonUsesPublicPrivateAndIdnRules(string shown, string destination, bool deceptive)
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { HtmlBody = $"<a href='{destination}'>{shown}</a>" });
        Assert.Equal(deceptive, Has(report, "link.mismatch"));
    }

    [Theory]
    [InlineData("paypal.com.", true)]
    [InlineData("(www.paypal.com)", true)]
    [InlineData("Accedi a www.paypal.com per confermare", true)]
    [InlineData("Accedi a https://paypal.com/login.", true)]
    [InlineData("Vedi fattura.pdf allegata", false)]
    [InlineData("Accedi a www.evil.tk", false)]
    public void LinkTextWithPunctuationOrWords_StillRevealsTheShownDomain(string text, bool deceptive)
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { HtmlBody = $"<a href='https://evil.tk/x'>{text}</a>" });
        Assert.Equal(deceptive, Has(report, "link.mismatch"));
    }

    [Fact]
    public void LinkWithUserInfo_IsDeceptiveEvenWhenTextMatchesHref()
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { HtmlBody = "<a href='https://www.paypal.com@evil.tk/'>https://www.paypal.com@evil.tk/</a>" });
        Assert.Contains(report.Observations, o => o.Code == "link.userinfo" && o.Kind == ObservationKind.Deception);
        Assert.False(Has(EmailSecurityAnalyzer.Analyze(new() { HtmlBody = "<a href='https://www.paypal.com/'>paypal</a>" }), "link.userinfo"));
    }

    [Fact]
    public void PunycodeLookalikeIsDecodedForComparison()
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = "x", FromAddress = "x@xn--pple-43d.com", OwnDomains = new[] { "apple.com" } });
        Assert.True(Has(report, "domain.lookalike"));
        Assert.Equal(SecurityOutcome.ToReview, SecurityVerdict.Decide(report).Outcome);
    }

    [Fact]
    public void FakeSafeLinksSuffixIsNotUnwrapped()
    {
        var report = EmailSecurityAnalyzer.Analyze(new()
        {
            HtmlBody = "<a href='https://evilsafelinks.protection.outlook.com/?url=https%3A%2F%2Fcontoso.com'>https://contoso.com</a>"
        });
        Assert.True(Has(report, "link.mismatch"));
        Assert.Contains("evilsafelinks", Assert.Single(report.Links).Destination);
    }

    [Fact]
    public void HtmlTokenizerHandlesQuotedGreaterThanAndNestedMarkup()
    {
        var report = EmailSecurityAnalyzer.Analyze(new()
        {
            HtmlBody = "<a title='a > b' href='https://evil.net/?x=1&amp;y=2'><span>https://contoso.com</span></a>"
        });
        Assert.True(Has(report, "link.mismatch"));
        Assert.Equal("https://evil.net/?x=1&y=2", Assert.Single(report.Links).Destination);
    }

    [Fact]
    public void PlainTextLinksAreCheckedAndRepeatedEvidenceIsDeduplicated()
    {
        var report = EmailSecurityAnalyzer.Analyze(new()
        {
            Body = "Apri http://192.168.1.5/login e http://192.168.1.5/login",
            HtmlBody = "<a href='http://192.168.1.5/login'>http://192.168.1.5/login</a>"
        });
        Assert.Single(report.Links);
        Assert.Single(SecurityReportView.From(report, SecurityCheckState.Done, "").Observations, o => o.Text.Contains("192.168.1.5"));
    }

    [Fact]
    public void LinkLimitIsVisibleWithoutCuttingEmailBody()
    {
        var body = string.Join(" ", Enumerable.Range(0, 201).Select(i => $"https://example.com/{i}")) + " TAIL";
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = body });
        Assert.Equal(200, report.Links.Count);
        Assert.Contains(report.Limits, l => l.Text.Contains("200") && l.Partial);
        Assert.EndsWith("TAIL", SecurityContentAnalysis.CreateSources("Test", "Mario", "m@example.com", body, report)[0].Text);
    }

    [Fact]
    public void ForwardedMessageWithDmarcPassAndSpfFailStaysContext()
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = "x", TransportHeaders = "Authentication-Results: mx; spf=fail; dkim=pass; dmarc=pass\r\n" });
        Assert.Equal(SecurityOutcome.NoAnomaly, SecurityVerdict.Decide(report).Outcome);
        Assert.Contains(report.Observations, o => o.Code == "spf.fail" && o.Kind == ObservationKind.Context);
    }

    [Fact]
    public void ForgedPassCannotSuppressAFailureInAnotherDeclaration()
    {
        var report = EmailSecurityAnalyzer.Analyze(new()
        {
            Body = "x",
            TransportHeaders = "Authentication-Results: forged; dmarc=pass\r\nAuthentication-Results: receiver; dmarc=fail\r\n"
        });
        Assert.Equal(SecurityOutcome.ToReview, SecurityVerdict.Decide(report).Outcome);
        Assert.Contains(report.Observations, o => o.Code == "dmarc.fail" && o.Origin == ObservationOrigin.Declared);
        Assert.Contains(report.Limits, l => l.Text.Contains("contraddittori"));
    }

    [Fact]
    public void UnscannedAttachmentsShowTheLimitReason()
    {
        var report = EmailSecurityAnalyzer.Analyze(new()
        {
            Body = "x",
            Attachments = new[] { new AttachmentFacts("large.pdf") { ScanDetail = "oltre il limite di 30 MB" } }
        });
        Assert.Contains(report.Limits, l => l.Text.Contains("large.pdf") && l.Text.Contains("30 MB") && l.Partial);
    }

    [Fact]
    public void ObservedMacrosReplaceTheFormatHeuristic()
    {
        var report = EmailSecurityAnalyzer.Analyze(new()
        {
            Body = "x",
            Attachments = new[] { new AttachmentFacts("report.docm") { HasMacros = true, ScanStatus = AttachmentScanStatus.Clean } }
        });
        Assert.Single(report.Observations, o => o.Kind == ObservationKind.Attention);
        Assert.True(Has(report, "att.macro"));
        Assert.False(Has(report, "att.macroformat"));
    }

    [Fact]
    public void PageAttachmentNotRead_IsToReview_ReadWithoutFindingsIsNot()
    {
        var unread = EmailSecurityAnalyzer.Analyze(new() { Body = "x", Attachments = new[] { new AttachmentFacts("modulo.html") { ScanStatus = AttachmentScanStatus.Clean } } });
        Assert.True(Has(unread, "att.page"));
        var read = EmailSecurityAnalyzer.Analyze(new()
        {
            Body = "x",
            Attachments = new[] { new AttachmentFacts("modulo.html") { ScanStatus = AttachmentScanStatus.Clean, Inspection = new AttachmentInspection { Format = "html", Inspected = 1 } } }
        });
        Assert.False(Has(read, "att.page"));
        Assert.Equal(SecurityOutcome.NoAnomaly, SecurityVerdict.Decide(read).Outcome);
    }
}
