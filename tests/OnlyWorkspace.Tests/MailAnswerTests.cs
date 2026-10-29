using System;
using System.Linq;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using Xunit;

namespace OnlyWorkspace.Tests;

public class MailAnswerTests
{
    private static MailEvidence Mail(string subject, DateTime received, string text, bool whole = true, DateTime? sent = null) => new()
    {
        Mail = new ChatMailItem { Subject = subject, SenderName = "Fornitore", Received = received, Sent = sent, EntryId = subject, StoreId = "s" },
        Text = text,
        Whole = whole
    };

    private static MailEvidencePack Pack() => MailEvidencePack.Chronological(new[]
    {
        Mail("Sollecito", new DateTime(2026, 10, 20, 9, 0, 0), "La fattura FT-2026/045 di 1.250,00 EUR scade il 15 novembre 2026. Ordine ORD 2026/001."),
        Mail("Offerta", new DateTime(2026, 10, 1, 9, 0, 0), "[preventivo] Offerta da 980,50 € valida fino al 12/03/2027", whole: false)
    });

    [Fact]
    public void Pack_IsChronological_AndNumbersEveryEmail()
    {
        var pack = MailEvidencePack.Chronological(new[]
        {
            Mail("C", new DateTime(2026, 10, 3), "c"),
            Mail("A", new DateTime(2026, 10, 9), "a", sent: new DateTime(2026, 10, 1)),
            Mail("B", new DateTime(2026, 10, 2), "b")
        });
        Assert.Equal(new[] { "A", "B", "C" }, pack.Items.Select(i => i.Mail.Subject));
        var rendered = pack.Render();
        Assert.True(rendered.IndexOf("[M1]", StringComparison.Ordinal) < rendered.IndexOf("«A»", StringComparison.Ordinal));
        Assert.Contains("[M3]", rendered);
        Assert.Equal(3, pack.Sources().Count);
    }

    [Fact]
    public void Parse_ReadsSectionsSourcesAndGaps_IncludingCitationsInTheText()
    {
        var answer = MailAnswer.Parse("{\"sections\":[{\"title\":\"Stato\",\"points\":[{\"text\":\"Fattura in scadenza [M1]\",\"sources\":[\"M2\",3]},{\"text\":\"\",\"sources\":[]}]},{\"title\":\"Vuota\",\"points\":[]}],\"gaps\":[\"Manca la conferma\",\"\"]}");
        var section = Assert.Single(answer.Sections);
        var point = Assert.Single(section.Points);
        Assert.Equal("Fattura in scadenza", point.Text);
        Assert.Equal(new[] { 2, 3, 1 }, point.Sources);
        Assert.Equal(new[] { "Manca la conferma" }, answer.Gaps);
        Assert.Throws<InvalidModelOutputException>(() => MailAnswer.Parse("{}"));
    }

    [Fact]
    public void WebCitations_StayWebSources_NotEmails()
    {
        // 27/10/2026: «W1» lost its letter and became [M1], a web definition credited to an email.
        var answer = MailAnswer.Parse("{\"sections\":[{\"title\":\"\",\"points\":[" +
            "{\"text\":\"La norma ISO/IEC 42001 del 2023 regola i sistemi di IA\",\"sources\":[\"W1\"]}," +
            "{\"text\":\"Fattura FT-2026/045 da 1.250,00 EUR [W2]\",\"sources\":[\"M2\"]}," +
            "{\"text\":\"Importo inventato 9.999,00 EUR\",\"sources\":[\"M2\"]}]}],\"gaps\":[]}");
        var points = answer.Points.ToList();
        Assert.Empty(points[0].Sources);
        Assert.Equal(new[] { 1 }, points[0].WebSources);
        Assert.Equal(new[] { 2 }, points[1].Sources);
        Assert.Equal(new[] { 2 }, points[1].WebSources);

        MailAnswerVerifier.Verify(answer, Pack());

        Assert.Empty(points[0].Unverified);
        Assert.Empty(points[0].InvalidSources);
        Assert.Equal(1, MailAnswerRenderer.UnverifiedCount(answer));
        var rendered = MailAnswerRenderer.Render(answer);
        Assert.Contains("regola i sistemi di IA [W1]", rendered);
        Assert.Contains("1.250,00 EUR [M2][W2]", rendered);
        Assert.DoesNotContain("[M1]", rendered);
        Assert.Contains("non verificato nelle email citate: 9.999,00", rendered);
    }

    [Fact]
    public void Verify_FindsValuesInTheCitedEmail_InAnyCommonFormat()
    {
        var pack = Pack();
        var answer = MailAnswer.Parse("{\"sections\":[{\"title\":\"\",\"points\":[" +
            "{\"text\":\"La fattura FT-2026/045 da 1250,00 euro scade il 15/11/2026\",\"sources\":[\"M2\"]}," +
            "{\"text\":\"Riferimento ORD-2026-001\",\"sources\":[\"M2\"]}," +
            "{\"text\":\"Offerta da 980,50 € fino al 12/03\",\"sources\":[\"M1\"]}]}],\"gaps\":[]}");
        MailAnswerVerifier.Verify(answer, pack);
        Assert.All(answer.Points, p => Assert.Empty(p.Unverified));
        Assert.Contains("Importi, date e riferimenti citati trovati", MailAnswerRenderer.Coverage(pack, answer));
    }

    [Fact]
    public void Verify_OnAnOriginalPastTheRegexTimeout_KeepsTheAnswer()
    {
        // A136: 4 million characters of digits separated by spaces take the amount patterns past their 2 s timeout on net48.
        string huge = "Totale 1.250,00 EUR. " + string.Concat(Enumerable.Repeat("1 ", 2_000_000));
        var pack = MailEvidencePack.Chronological(new[] { Mail("Registro", new DateTime(2026, 4, 21), huge) });
        var answer = MailAnswer.Parse("{\"sections\":[{\"title\":\"\",\"points\":[" +
            "{\"text\":\"Totale 1.250,00 EUR\",\"sources\":[\"M1\"]}]}],\"gaps\":[]}");

        MailAnswerVerifier.Verify(answer, pack);

        // No exception: a value written as in the email is still found in its text; the others would be declared not verified.
        Assert.Empty(Assert.Single(answer.Points).Unverified);
    }

    [Fact]
    public void Verify_AcceptsAWholeAmountWrittenWithoutDecimals_ButNotAnotherNumber()
    {
        // Real case (25/10/2026): a tax form lists «A 7.769 7.769 0 0», the answer writes «7.769,00 lordi».
        var pack = MailEvidencePack.Chronological(new[] { Mail("CU", new DateTime(2026, 4, 21), "C.F.: 01704930682 A 7.769 7.769 0 0 2103111438") });
        var answer = MailAnswer.Parse("{\"sections\":[{\"title\":\"\",\"points\":[" +
            "{\"text\":\"Redditi diversi per 7.769,00 euro lordi\",\"sources\":[\"M1\"]}," +
            "{\"text\":\"Prima rata di 769,00 euro\",\"sources\":[\"M1\"]}]}],\"gaps\":[]}");
        MailAnswerVerifier.Verify(answer, pack);
        Assert.Empty(answer.Points.First().Unverified);
        Assert.Equal(new[] { "769,00" }, answer.Points.Last().Unverified);
    }

    [Fact]
    public void Verify_MarksInventedAmounts_ValuesOfAnotherEmail_AndMissingSources()
    {
        var pack = Pack();
        var answer = MailAnswer.Parse("{\"sections\":[{\"title\":\"\",\"points\":[" +
            "{\"text\":\"Totale da pagare 9.999,00 EUR entro il 15/11/2026\",\"sources\":[\"M2\"]}," +
            "{\"text\":\"Offerta da 980,50 €\",\"sources\":[\"M2\"]}," +
            "{\"text\":\"Scade il 30/11/2026\",\"sources\":[\"M9\"]}]}],\"gaps\":[]}");
        MailAnswerVerifier.Verify(answer, pack);
        var points = answer.Points.ToList();
        Assert.Equal(new[] { "9.999,00" }, points[0].Unverified);
        Assert.Equal(new[] { "980,50" }, points[1].Unverified);
        Assert.Equal(new[] { 9 }, points[2].InvalidSources);
        var text = MailAnswerRenderer.Render(answer);
        Assert.Contains("[M2] — ⚠ *non verificato nelle email citate: 9.999,00*", text);
        Assert.Contains("Scade il 30/11/2026 — ⚠ *senza fonte tra le email lette* — ⚠ *non verificato nelle email citate: 30/11/2026*", text);
        Assert.Contains("3 punti non verificati", MailAnswerRenderer.Coverage(pack, answer));
    }

    [Theory]
    [InlineData("Sono arrivate 3 email nel 2026 [W1]", new string[0])]
    [InlineData("Importo € 1500 e codice PRNDNY81C03E897M", new[] { "1500", "PRNDNY81C03E897M" })]
    [InlineData("Pratica 45821 del 3 marzo 2026, ore 10:30", new[] { "45821", "3 marzo 2026" })]
    public void Values_LeaveOutCountsYearsAndCitations(string text, string[] expected) =>
        Assert.Equal(expected, MailAnswerVerifier.Values(text));

    [Fact]
    public void Table_ParsesRows_VerifiesEachRowOnItsEmail_AndNamesTheEmail()
    {
        var pack = Pack();
        var table = MailTable.Parse("{\"columns\":[\"Documento\",\" Importo \",\"documento\",\"Scadenza\"],\"rows\":[" +
            "{\"source\":\"M2\",\"cells\":[\"FT-2026/045\",\"1.250,00\",\"15/11/2026\",\"extra\"]}," +
            "{\"source\":\"M1\",\"cells\":[\"Offerta\",\"1.000,00\"]}," +
            "{\"source\":\"M1\",\"cells\":[\"\",\"\",\"\"]}]}");
        Assert.Equal(new[] { "Documento", "Importo", "Scadenza" }, table.Columns);
        Assert.Equal(2, table.Rows.Count);
        MailAnswerVerifier.Verify(table, pack);
        Assert.Empty(table.Rows[0].Unverified);
        Assert.Equal(new[] { "1.000,00" }, table.Rows[1].Unverified);
        var rows = MailAnswerRenderer.TableRows(table, pack);
        Assert.Equal("[M2] 20/10/2026 · Sollecito", rows[0].Last());
        Assert.EndsWith("⚠ non verificato: 1.000,00", rows[1].Last());
        Assert.Contains("1 riga con valori non verificati", MailAnswerRenderer.Coverage(pack, table));
    }

    [Theory]
    [InlineData("Quante mail ho ricevuto oggi?", true)]
    [InlineData("elenca le email di Biofer di questa settimana", true)]
    [InlineData("Mostrami le mail arrivate stamattina", true)]
    [InlineData("how many emails did ACME send?", true)]
    [InlineData("elenca le mail con le scadenze", false)]
    [InlineData("Riassumi le mail di oggi", false)]
    [InlineData("quante fatture ha pagato ACME?", false)]
    [InlineData("Che cosa chiede Laura nelle ultime mail?", false)]
    public void ListOrCount_EndsWithTheLocalList(string request, bool expected) =>
        Assert.Equal(expected, MailRequest.IsListOrCount(request));

    [Theory]
    [InlineData("leggi per intero le mail di ACME", true)]
    [InlineData("riassumi le mail di ACME", false)]
    public void WantsWhole_IsTheExplicitRequest(string request, bool expected) =>
        Assert.Equal(expected, MailRequest.WantsWhole(request));
}
