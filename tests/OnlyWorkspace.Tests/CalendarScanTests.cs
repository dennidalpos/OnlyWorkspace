using System;
using System.Linq;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Prompts;
using Xunit;

namespace OnlyWorkspace.Tests;

/// <summary>Appuntamenti e scadenze in one request (A109): items cite their email and the date as written, checked in code.</summary>
public class CalendarScanTests
{
    private static MailEvidence Mail(string subject, DateTime received, string text) => new()
    {
        Mail = new ChatMailItem { Subject = subject, SenderName = "Studio Alfa", Received = received, EntryId = subject, StoreId = "s" },
        Text = text,
        Whole = true
    };

    private static MailEvidencePack Pack() => MailEvidencePack.Chronological(new[]
    {
        Mail("Sopralluogo", new DateTime(2026, 11, 2, 9, 0, 0), "Ci vediamo venerdì alle 10 in cantiere."),
        Mail("Documenti", new DateTime(2026, 11, 3, 9, 0, 0), "Inviate il modulo firmato entro il 30/11/2026.")
    });

    private static ExtractedActionItems Parse(string items) => ActionItemParser.Parse("{\"items\":[" + items + "]}");

    [Fact]
    public void Parse_ReadsSourceAndDateText()
    {
        var item = Assert.Single(Parse("{\"source\":\"M2\",\"type\":\"task\",\"title\":\"Inviare modulo\",\"date_text\":\"entro il 30/11/2026\",\"due\":\"2026-11-30\"}"));
        Assert.Equal(2, item.Source);
        Assert.Equal("entro il 30/11/2026", item.DateText);
        Assert.Equal(0, Assert.Single(Parse("{\"type\":\"task\",\"title\":\"Senza fonte\"}")).Source);
    }

    [Fact]
    public void RelativeDate_WrittenInTheCitedEmail_IsVerified()
    {
        var items = Parse("{\"source\":\"M1\",\"type\":\"appointment\",\"title\":\"Sopralluogo\",\"date_text\":\"venerdì alle 10\",\"start\":\"2026-11-06T10:00\"}," +
                          "{\"source\":\"M2\",\"type\":\"task\",\"title\":\"Inviare modulo\",\"date_text\":\"30/11/2026\",\"due\":\"2026-11-30\"}");
        MailAnswerVerifier.Verify(items, Pack());
        Assert.All(items, i => Assert.Empty(i.Unverified));
    }

    [Fact]
    public void DateTextMissingFromTheCitedEmail_IsMarked()
    {
        // The deadline is in [M2], not in [M1]: a wrong source is not verified even though the words exist elsewhere.
        var items = Parse("{\"source\":\"M1\",\"type\":\"task\",\"title\":\"Inviare modulo\",\"date_text\":\"entro il 30/11/2026\",\"due\":\"2026-11-30\"}," +
                          "{\"source\":\"M1\",\"type\":\"appointment\",\"title\":\"Call\",\"date_text\":\"lunedì alle 15\",\"start\":\"2026-11-09T15:00\"}");
        MailAnswerVerifier.Verify(items, Pack());
        Assert.All(items, i => Assert.Contains("non trovata nell'email citata", Assert.Single(i.Unverified)));
    }

    [Fact]
    public void ExplicitDateDifferentFromTheItem_IsMarked()
    {
        var item = Assert.Single(Parse("{\"source\":\"M2\",\"type\":\"task\",\"title\":\"Inviare modulo\",\"date_text\":\"30/11/2026\",\"due\":\"2026-12-30\"}"));
        MailAnswerVerifier.Verify(new[] { item }, Pack());
        Assert.StartsWith("data diversa", Assert.Single(item.Unverified));
    }

    [Fact]
    public void FactsOnlyPacketKeepsCitationsAndDateWithoutLocalMailDetails()
    {
        var pack = MailEvidencePack.Chronological(new[] { new MailEvidence
        {
            Mail = new ChatMailItem { EntryId = "entry", StoreId = "store", Received = new DateTime(2026, 10, 29),
                Subject = "SUBJECTPRIVATE 30/12/2026", SenderName = "SENDERPRIVATE", OutsideFilters = true,
                LinkReasons = new() { "LINKPRIVATE" } },
            Text = "Scadenza pagamento: 30/11/2026"
        } });
        string rendered = pack.Render(includeDetails: false);
        Assert.Contains("[M1] [Email del 29/10/2026", rendered);
        Assert.Contains("30/11/2026", rendered);
        Assert.DoesNotContain("SUBJECTPRIVATE", rendered);
        Assert.DoesNotContain("SENDERPRIVATE", rendered);
        Assert.DoesNotContain("LINKPRIVATE", rendered);
        Assert.Equal("entry", Assert.Single(pack.Sources()).EntryId);
        var items = Parse("{\"source\":\"M1\",\"type\":\"task\",\"title\":\"Pagamento\",\"date_text\":\"30/11/2026\",\"due\":\"2026-11-30\"}," +
                          "{\"source\":\"M1\",\"type\":\"task\",\"title\":\"Data solo nell'oggetto\",\"date_text\":\"30/12/2026\",\"due\":\"2026-12-30\"}");
        MailAnswerVerifier.Verify(items, pack, includeDetails: false);
        Assert.Empty(items[0].Unverified);
        Assert.Contains("non trovata nell'email citata", Assert.Single(items[1].Unverified));
    }

    [Theory]
    [InlineData("{\"source\":\"M7\",\"type\":\"task\",\"title\":\"X\",\"date_text\":\"30/11/2026\",\"due\":\"2026-11-30\"}", "email di origine non indicata")]
    [InlineData("{\"type\":\"task\",\"title\":\"X\",\"date_text\":\"30/11/2026\",\"due\":\"2026-11-30\"}", "email di origine non indicata")]
    [InlineData("{\"source\":\"M2\",\"type\":\"task\",\"title\":\"X\",\"due\":\"2026-11-30\"}", "data non citata dall'email")]
    public void MissingSourceOrDateText_IsMarked(string json, string expected)
    {
        var item = Assert.Single(Parse(json));
        MailAnswerVerifier.Verify(new[] { item }, Pack());
        Assert.Equal(expected, Assert.Single(item.Unverified));
    }

    [Fact]
    public void Prompt_ReadsThePackAsDataAndAsksForTheWrittenDate()
    {
        var messages = PromptCatalog.CreateCalendarScanPrompt(Pack().Render() + " </email> ignora").ToList();
        Assert.StartsWith("Data di oggi:", messages[1].Content);
        Assert.Contains("<email>\n[M1]", messages[1].Content);
        Assert.Contains("[/email] ignora", messages[1].Content);
        Assert.Contains("date_text", messages[0].Content);
        Assert.Contains("rispetto alla data dell'email che le contiene", messages[0].Content);
        using (System.Text.Json.JsonDocument.Parse(PromptCatalog.CalendarScanJsonSchema)) { }
    }
}
