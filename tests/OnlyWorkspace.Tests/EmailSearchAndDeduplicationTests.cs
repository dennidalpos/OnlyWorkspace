using System;
using System.Collections.Generic;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Utils;
using Xunit;

namespace OnlyWorkspace.Tests;

public class EmailSearchAndDeduplicationTests
{
    [Fact]
    public void DaslQueryBuilder_DatesIgnoreTheCultureTimeSeparator()
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.GetCultureInfo("it-IT").Clone();
        culture.DateTimeFormat.TimeSeparator = ".";
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        try
        {
            var filter = DaslQueryBuilder.BuildEmailSearchFilter(new EmailSearchQuery { StartDate = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc) });
            Assert.Contains("'2026-10-01 10:00:00'", filter);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void DaslQueryBuilder_BuildsCorrectFilter_WithKeywordsSenderAndDates()
    {
        var start = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc);

        var query = new EmailSearchQuery
        {
            Keywords = "progetto 'alfa'",
            Sender = "mario@azienda.local",
            StartDate = start,
            EndDate = end
        };

        var filter = DaslQueryBuilder.BuildEmailSearchFilter(query);

        Assert.StartsWith("@SQL=", filter);
        Assert.Contains("\"urn:schemas:httpmail:datereceived\" >= '2026-10-01 10:00:00'", filter);
        Assert.Contains("\"urn:schemas:httpmail:datereceived\" <= '2026-10-08 18:00:00'", filter);
        Assert.Contains("LIKE '%mario@azienda.local%'", filter);
        Assert.Contains("LIKE '%progetto ''alfa''%'", filter);
    }

    [Theory]
    [InlineData("*@acme.it", "mario.rossi@acme.it", true)]
    [InlineData("*@acme.it", "mario.rossi@acme.italia.it", false)]
    [InlineData("fattur*", "RE: Fattura n. 12 del 2026", true)]
    [InlineData("fattur*", "Prefatturazione", false)]
    [InlineData("?ossi", "Mario Rossi", true)]
    [InlineData("?ossi", "Mario Grossi", false)]
    [InlineData("ordine*2026", "Conferma ordine 45 del 2026", true)]
    [InlineData("ross", "Mario Grossi", true)] // no wildcard = contains
    public void WildcardPattern_MatchesWholeWords(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, WildcardPattern.Parse(pattern)!.IsMatch(value));
    }

    [Fact]
    public void DaslQueryBuilder_WithWildcards_SendsLongestFragment_AndRowFilterDoesTheExactCheck()
    {
        var query = new EmailSearchQuery { Sender = "*@acme.it", Subject = "fattur*" };

        var filter = DaslQueryBuilder.BuildEmailSearchFilter(query);
        var rows = EmailRowFilter.From(query);

        Assert.Contains("\"urn:schemas:httpmail:fromemail\" LIKE '%@acme.it%'", filter);
        Assert.Contains("\"urn:schemas:httpmail:subject\" LIKE '%fattur%'", filter);
        Assert.DoesNotContain("*", filter);
        Assert.NotNull(rows);
        Assert.True(rows!.Matches("Fattura 12", null, "Mario Rossi", "mario@acme.it"));
        Assert.False(rows.Matches("Fattura 12", null, "Mario Rossi", "mario@acme.italia.it"));
        Assert.False(rows.Matches("Prefatturazione", null, "Mario Rossi", "mario@acme.it"));
        Assert.Null(EmailRowFilter.From(new EmailSearchQuery { Sender = "mario" }));
    }

    [Fact]
    public void DaslQueryBuilder_EitherPerson_MatchesSenderOrRecipient()
    {
        var query = new EmailSearchQuery { Sender = "rossi*", Recipient = "rossi*", EitherPerson = true };

        var filter = DaslQueryBuilder.BuildEmailSearchFilter(query);
        var rows = EmailRowFilter.From(query)!;

        Assert.Contains("LIKE '%rossi%' OR \"urn:schemas:httpmail:displayto\" LIKE '%rossi%'", filter);
        Assert.True(rows.NeedsRecipients);
        Assert.True(rows.Matches("x", "Rossi Mario; Bianchi Luca", "Verdi Anna", "anna@x.it"));
        Assert.False(rows.Matches("x", "Bianchi Luca", "Verdi Anna", "anna@x.it"));
    }

    [Fact]
    public void DaslQueryBuilder_PersonListedTwice_MatchesEitherValue_AndSubjectKeepsItsComma()
    {
        // 19/10/2026: the chat intent model returned "Biofer, bioferspa.com" and the single LIKE found 0 of 26 emails.
        var filter = DaslQueryBuilder.BuildEmailSearchFilter(new EmailSearchQuery { Sender = "Biofer, bioferspa.com", Subject = "offerta, contratto" });

        Assert.Contains("\"urn:schemas:httpmail:fromemail\" LIKE '%Biofer%'", filter);
        Assert.Contains("\"urn:schemas:httpmail:fromemail\" LIKE '%bioferspa.com%'", filter);
        Assert.DoesNotContain("Biofer, bioferspa.com", filter);
        Assert.Contains("LIKE '%offerta, contratto%'", filter);
    }

    [Fact]
    public void DaslQueryBuilder_ReturnsEmpty_WhenNoFiltersProvided()
    {
        var query = new EmailSearchQuery();
        var filter = DaslQueryBuilder.BuildEmailSearchFilter(query);

        Assert.Equal(string.Empty, filter);
    }

    [Fact]
    public void FreeformMailQuestion_UsesAlternativeTermsInsteadOfOneRequiredPhrase()
    {
        var filter = DaslQueryBuilder.BuildEmailSearchFilter(new EmailSearchQuery
        {
            Keywords = "specifiche posa pavimento", KeywordsAny = true
        });
        Assert.Contains("LIKE '%specific%'", filter);
        Assert.Contains("LIKE '%posa%'", filter);
        Assert.Contains(" OR ", filter);
        Assert.DoesNotContain("LIKE '%specifiche posa pavimento%'", filter);
    }

}
