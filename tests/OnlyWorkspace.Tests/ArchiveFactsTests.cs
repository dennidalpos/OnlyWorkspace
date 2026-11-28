using System.Globalization;
using System.IO.Compression;
using System.Text;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveFactsTests
{
    [Theory]
    [InlineData("Scadenza pagamento:", ArchiveFactKind.Deadline)]
    [InlineData("Scadenza fattura:", ArchiveFactKind.Deadline)]
    [InlineData("Data di scadenza:", ArchiveFactKind.Deadline)]
    [InlineData("Data scadenza", ArchiveFactKind.Deadline)]
    [InlineData("Scadenza:", ArchiveFactKind.Deadline)]
    [InlineData("Data fattura:", ArchiveFactKind.Date)]
    [InlineData("Pagamento ricevuto:", ArchiveFactKind.Date)]
    [InlineData("Scadenza pagamento concordata nella riunione del", ArchiveFactKind.Date)]
    public void DeadlineLabels_KeepTheLiteralDateAndDoNotPromoteUnlabelledDates(string label, ArchiveFactKind kind)
    {
        const string date = "15/11/2026";
        var text = "😀 " + label + " " + date;
        var fact = Assert.Single(ArchiveFactExtractor.Extract(text), f => f.Kind is ArchiveFactKind.Date or ArchiveFactKind.Deadline);
        Assert.Equal(kind, fact.Kind);
        Assert.Equal("2026-11-15", fact.Date);
        Assert.Equal(date, text.Substring(fact.Start, fact.Length));
        Assert.False(Assert.Single(ArchiveFactExtractor.Extract(label + " 03/04/2026"), f => f.Kind is ArchiveFactKind.Date or ArchiveFactKind.Deadline).Resolved);
    }

    [Theory]
    [InlineData("Importo 1000,01 EUR", "1000.01", "EUR")]
    [InlineData("Amount 1000.01 USD", "1000.01", "USD")]
    [InlineData("Totale: GBP -25.50", "-25.5", "GBP")]
    [InlineData("total +42 CHF", "42", "CHF")]
    [InlineData("importo: € 1.000,00", "1000", "EUR")]
    [InlineData("Totale 1000,00 €", "1000", "EUR")]
    [InlineData("Amount\t-25.50\t£", "-25.5", "GBP")]
    [InlineData("Total 1000.00 $", "1000", null)]
    [InlineData("Totale\u00a01000,01\u00a0EUR", "1000.01", "EUR")]
    [InlineData("EUR 1000,01", "1000.01", "EUR")]
    [InlineData("1000.01 USD", "1000.01", "USD")]
    [InlineData("Importo 1000,01", "1000.01", null)]
    [InlineData("Amount 1,000 USD", null, "USD")]
    [InlineData("Importo EUR 1.000", null, "EUR")]
    public void LabelledMoney_PreservesCurrencyAndOneVerbatimOccurrence(string text, string? value, string? currency)
    {
        var fact = Assert.Single(ArchiveFactExtractor.Extract("😀 " + text + "; fine"), f => f.Kind == ArchiveFactKind.Amount);
        Assert.Equal(value, fact.Value); Assert.Equal(currency, fact.Currency);
        Assert.Equal(value != null, fact.Resolved);
        Assert.Equal(text, fact.Raw); Assert.Equal(3, fact.Start); Assert.Equal(text.Length, fact.Length);
    }

    [Fact]
    public void LabelledMoney_DoesNotDuplicateAdjacentOccurrencesOrInferUnlabelledNumbers()
    {
        var facts = ArchiveFactExtractor.Extract("Importo 1000,01 EUR; amount USD 25.50; totale 42; telefono 123456").Where(f => f.Kind == ArchiveFactKind.Amount).ToList();
        Assert.Equal(3, facts.Count);
        Assert.Equal(new[] { "1000.01", "25.5", "42" }, facts.Select(f => f.Value));
        Assert.Equal(new string?[] { "EUR", "USD", null }, facts.Select(f => f.Currency));
    }

    [Theory]
    [InlineData("Importo EUR 1000,01.", "Importo EUR 1000,01")]
    [InlineData("Amount USD 1000.01, please pay.", "Amount USD 1000.01")]
    [InlineData("Total 1000.01 EUR.", "Total 1000.01 EUR")]
    public void LabelledMoney_KeepsSentencePunctuationOutsideTheOccurrence(string text, string raw)
    {
        var amount = Assert.Single(ArchiveFactExtractor.Extract(text), f => f.Kind == ArchiveFactKind.Amount);
        Assert.Equal(raw, amount.Raw); Assert.Equal("1000.01", amount.Value);
    }

    [Theory]
    [InlineData("1.000,00", "1000")]
    [InlineData("1,000.00", "1000")]
    [InlineData("1000,00", "1000")]
    [InlineData("-0.25", "-0.25")]
    [InlineData("0", "0")]
    [InlineData("99999999999999999999999.99", "99999999999999999999999.99")]
    [InlineData("1.000", null)]
    [InlineData("1,000", null)]
    [InlineData("1.23.00", null)]
    [InlineData("12,34.50", null)]
    [InlineData("0.00000000000000000000000000001", null)]
    [InlineData("1.12345678901234567890123456789", null)]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    public void Amounts_AreExactAndAmbiguityIsNotGuessed(string text, string? expected) => Assert.Equal(expected, ArchiveFactExtractor.NormalizeAmount(text));

    [Theory]
    [InlineData("2026-10-23", "2026-10-23")]
    [InlineData("23/10/2026", "2026-10-23")]
    [InlineData("10/23/2026", "2026-10-23")]
    [InlineData("23 ottobre 2026", "2026-10-23")]
    [InlineData("October 23, 2026", "2026-10-23")]
    [InlineData("23 October 2026", "2026-10-23")]
    [InlineData("03/04/2026", null)]
    [InlineData("31/02/2026", null)]
    [InlineData("23/10", null)]
    public void Dates_KeepCalendarDaysAndDoNotInferAmbiguousValues(string raw, string? expected) => Assert.Equal(expected, ArchiveFactExtractor.NormalizeDate(raw));

    [Fact]
    public void EveryCategory_HasVerbatimUnicodeSourceAndPosition()
    {
        string text = "😀 Società Èsempio\r\nScadenza: 2026-11-30\nData 2026-10-23\nTotale EUR 1.000,00\nOrdine ORD-2026/118\nInvoice INV-75/2026\nProtocollo 45/2026\n" +
            "IBAN IT60X0542811101000000123456\nP.IVA 01114601006\nCodice fiscale RSSMRA85T10A562S\nMittente: Mario Rossi <mario@example.test>\nRicevuta di avvenuta consegna\n" +
            "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:demo@example.test\nDTSTART:20261023T090000Z\nSUMMARY:Riunione\nEND:VEVENT\nEND:VCALENDAR\n";
        var facts = ArchiveFactExtractor.Extract(text, "att-1", "lettera.txt");
        foreach (ArchiveFactKind kind in Enum.GetValues(typeof(ArchiveFactKind))) Assert.Contains(facts, f => f.Kind == kind && f.Resolved);
        foreach (var f in facts) { Assert.Equal(text.Substring(f.Start, f.Length), f.Raw); Assert.Equal("att-1", f.SourceId); Assert.Equal("lettera.txt", f.SourceName); Assert.Contains(f.Raw, f.Context); }
        Assert.Contains(facts, f => f.Kind == ArchiveFactKind.Amount && f.Value == "1000" && f.Currency == "EUR");
        Assert.Contains(facts, f => f.Kind == ArchiveFactKind.Order && f.Value == "ORD-2026/118");
    }

    [Fact]
    public void Iban_NormalizesSpacesChecksChecksumAndDoesNotAcceptATruncatedPrefix()
    {
        var facts = ArchiveFactExtractor.Extract("IT60 X054 2811 1010 0000 0123 456\nIT60X0542811101000000123456X\nIT60X0542811101000000123457\nNO9386011117947");
        Assert.Contains(facts, f => f.Kind == ArchiveFactKind.Iban && f.Value == "IT60X0542811101000000123456");
        Assert.Contains(facts, f => f.Kind == ArchiveFactKind.Iban && f.Value == "NO9386011117947");
        Assert.Equal(2, facts.Count(f => f.Kind == ArchiveFactKind.Iban && !f.Resolved));
    }

    [Fact]
    public void NumbersWithoutMoneyContext_AreNotAmounts_AndBadFiscalCodesStayUnresolved()
    {
        var facts = ArchiveFactExtractor.Extract("Telefono +39 0422 123456\nOrdine 123456\nP.IVA 01114601007\nC.F. RSSMRA85T10A562A\nTotale $ 1000.00\nScadenza 03/04/2026");
        Assert.Null(Assert.Single(facts, f => f.Kind == ArchiveFactKind.Amount).Currency);
        Assert.All(facts.Where(f => f.Kind is ArchiveFactKind.Vat or ArchiveFactKind.TaxCode or ArchiveFactKind.Deadline), f => Assert.False(f.Resolved));
    }

    [Fact]
    public void Calendar_PreservesFoldedSourceTimeZonesFloatingDaysAndRecurrence()
    {
        string text = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:test\r\nSUMMARY:Una riunione \r\n lunga\r\nDTSTART;TZID=Europe/Rome:20261025T023000\r\nDTEND:20261025T040000\r\nRRULE:FREQ=WEEKLY;COUNT=3\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nDUE;VALUE=DATE:20261130\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        Assert.True(ArchiveFactExtractor.IsCalendar(text));
        var facts = ArchiveFactExtractor.Extract(text);
        Assert.Contains(facts, f => f.Label == "SUMMARY" && f.Value == "Una riunione lunga" && f.Raw.Contains("\r\n lunga"));
        Assert.Equal("2026-10-25T02:30:00;TZID=Europe/Rome", Assert.Single(facts, f => f.Label == "DTSTART").Value);
        Assert.Contains(facts, f => f.Label == "DTEND" && f.Value!.EndsWith(";floating", StringComparison.Ordinal));
        Assert.Contains(facts, f => f.Kind == ArchiveFactKind.Deadline && f.Value == "2026-11-30");
        Assert.Single(facts, f => f.Label == "RRULE");
        Assert.All(facts, f => Assert.Equal(text.Substring(f.Start, f.Length), f.Raw));
        Assert.False(ArchiveFactExtractor.IsCalendar(text.Replace("END:VEVENT", "END:WRONG")));
    }

    [Fact]
    public void Calendar_InconsistentTypesAndZonesStayUnresolved()
    {
        string text = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nDTSTART;TZID=Europe/Rome:20261023T090000Z\nDTEND;VALUE=DATE:20261023T090000\nDUE;TZID=Europe/Rome;VALUE=DATE:20261023\nEND:VEVENT\nEND:VCALENDAR\n";
        var facts = ArchiveFactExtractor.Extract(text);
        Assert.Equal(4, facts.Count);
        Assert.All(facts, f => { Assert.False(f.Resolved); Assert.Null(f.Date); });
    }

    [Fact]
    public void PecXml_ExtractsDeclaredTypeAndParties_WithoutResolvingExternalEntities()
    {
        const string xml = "<postacert tipo=\"avvenuta-consegna\"><intestazione><mittente>sender@example.test</mittente><destinatari>dest@example.test</destinatari></intestazione><dati><identificativo>receipt-123</identificativo><giorno>23/10/2026</giorno></dati></postacert>";
        var facts = ArchiveFactExtractor.Extract(xml, "xml-1", "daticert.xml");
        Assert.Contains(facts, f => f.Label == "postacert.tipo" && f.Value == "avvenuta-consegna");
        Assert.Contains(facts, f => f.Kind == ArchiveFactKind.Party && f.Value == "sender@example.test");
        Assert.Contains(facts, f => f.Label == "identificativo" && f.Value == "receipt-123");
        Assert.All(facts, f => Assert.Equal(xml.Substring(f.Start, f.Length), f.Raw));
        var hostile = ArchiveFactExtractor.Extract("<!DOCTYPE postacert [<!ENTITY x SYSTEM 'file:///does-not-exist'>]><postacert tipo=\"x\"><mittente>&x;</mittente></postacert>");
        Assert.DoesNotContain(hostile, f => f.Label == "postacert.tipo");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IcsHelper_ReadsDirectAndNestedCalendars(bool nested)
    {
        const string calendar = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:test@example.test\r\nDTSTART:20261023T090000Z\r\nSUMMARY:Invoice INV-1\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        byte[] bytes = Encoding.UTF8.GetBytes(calendar);
        if (nested)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) using (var output = zip.CreateEntry("invito.ics").Open()) output.Write(bytes, 0, bytes.Length);
            bytes = stream.ToArray();
        }
        var result = await AttachmentExtractionTests.ExtractAsync(nested ? "calendar.zip" : "calendar.ics", bytes);
        Assert.Null(result.Problem); Assert.False(result.Truncated); Assert.Contains("DTSTART:20261023T090000Z", result.Text);
        Assert.Contains(ArchiveFactExtractor.Extract(result.Text), f => f.Kind == ArchiveFactKind.Calendar && f.Label == "UID");
    }

    [Fact]
    public async Task IcsHelper_MalformedAndLimitedNeverClaimWholeText()
    {
        var malformed = await AttachmentExtractionTests.ExtractAsync("broken.ics", Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:x\n"));
        Assert.Equal("malformed-calendar", malformed.Problem);
        Assert.Empty(malformed.Text);
        var limited = await AttachmentExtractionTests.ExtractAsync("long.ics", Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:x\nSUMMARY:" + new string('a', 5000) + "\nEND:VEVENT\nEND:VCALENDAR\n"), maxChars: 200);
        Assert.True(limited.Truncated);
    }

    [Fact]
    public void Cancellation_DoesNotReturnACompletePartialPass()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ArchiveFactExtractor.Extract("Importo EUR 100", ct: stop.Token));
    }
}
