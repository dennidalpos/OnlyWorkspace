using OnlyWorkspace.Core.Security;
using Xunit;

namespace OnlyWorkspace.Tests;

/// <summary>The security check sends only domains to the web search, and nothing for the user's own email.</summary>
public class SecurityWebQueryTests
{
    private static string? Query(string from, string html = "", string body = "", string[]? own = null)
    {
        var input = new EmailSecurityInput { FromAddress = from, HtmlBody = html, Body = body, OwnAddresses = own ?? new[] { "mario.rossi@gmail.com" } };
        return SecurityWebQuery.For(input, EmailSecurityAnalyzer.Analyze(input));
    }

    [Fact]
    public void OwnEmail_SendsNothing() =>
        Assert.Null(Query("Mario.Rossi@gmail.com", "<a href=\"https://www.acme.it/fattura\">fattura</a>"));

    [Fact]
    public void CompanySender_SendsItsDomainAndTheLinkDomains_NeverTheAddress()
    {
        var query = Query("amministrazione@fatture.acme.it",
            "<a href=\"https://login.acme-pagamenti.com/x?u=mario.rossi@gmail.com\">Paga</a> <a href=\"https://www.acme.it\">sito</a>",
            "Dettagli su www.example.org/a");
        Assert.NotNull(query);
        Assert.Contains("acme.it", query);
        Assert.Contains("acme-pagamenti.com", query);
        Assert.Contains("example.org", query);
        Assert.DoesNotContain("@", query);
        Assert.DoesNotContain("amministrazione", query);
        Assert.Equal(query!.IndexOf("acme.it, ", System.StringComparison.Ordinal), query.LastIndexOf("acme.it, ", System.StringComparison.Ordinal));
    }

    [Fact]
    public void MailboxProviderSenderWithoutLinks_SendsNothing()
    {
        Assert.Null(Query("sconosciuto@gmail.com"));
        Assert.Null(Query("ufficio@pec.it"));
    }

    [Fact]
    public void ScanOfSeveralEmails_SendsOnlyTheSenderDomains()
    {
        var domains = SecurityWebQuery.SenderDomains(new[]
        {
            "Ufficio Acquisti <acquisti@bioferspa.com>", "Mario Rossi <mario.rossi@gmail.com>", "amici@gmail.com",
            "noreply@fatture.acme.it", "Altro <info@BIOFERSPA.com>", "Nome senza indirizzo"
        }, new[] { "mario.rossi@gmail.com" });
        Assert.Equal("bioferspa.com, acme.it", domains);
        Assert.Null(SecurityWebQuery.SenderDomains(new[] { "Mario Rossi <mario.rossi@gmail.com>", "x@pec.it" }, new[] { "mario.rossi@gmail.com" }));
    }

    [Fact]
    public void MailboxProviderSender_SendsOnlyTheLinkDomains()
    {
        var query = Query("sconosciuto@yahoo.it", "<a href=\"https://bit.ly/abc\">clic</a>");
        Assert.Equal("Segnalazioni tecniche di phishing, frode o compromissione dei domini (escludere recensioni e reputazione commerciale): bit.ly", query);
    }
}
