using System.Linq;
using OnlyWorkspace.Core.Context;
using Xunit;

namespace OnlyWorkspace.Tests;

public class ContextBuilderTests
{
    [Fact]
    public void CleanEmailBody_ManyUnclosedAngleBrackets_StaysLinear()
    {
        var raw = "<b>x</b> > " + new string('<', 200_000);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var clean = new ContextBuilder().CleanEmailBody(raw);

        Assert.True(elapsed.Elapsed < System.TimeSpan.FromSeconds(2), elapsed.Elapsed.ToString());
        Assert.DoesNotContain("<b>", clean);
    }

    [Fact]
    public void CleanEmailBody_StripsHtmlAndQuotedHeaders()
    {
        var builder = new ContextBuilder();
        var raw = @"<p>Buongiorno Marco,</p>
<p>Confermo la riunione per domani alle 15:00.</p>
<p>Saluti cordiali,<br>Mario</p>
<br>
-----Original Message-----
Da: Marco Bianchi <marco@example.com>
Inviato: giovedì 8 ottobre 2026 10:00
A: Mario Rossi <mario@example.com>
Oggetto: Riunione budget

> Possiamo vederci domani per il budget?
> Fammi sapere.
";

        var clean = builder.CleanEmailBody(raw);

        Assert.Contains("Buongiorno Marco,", clean);
        Assert.Contains("Confermo la riunione per domani alle 15:00.", clean);
        Assert.DoesNotContain("<p>", clean);
        Assert.DoesNotContain("-----Original Message-----", clean);
        Assert.DoesNotContain("Possiamo vederci domani per il budget?", clean);
    }

    [Fact]
    public void CleanEmailBody_KeepsBodyLinesThatLookLikeHeaders()
    {
        var builder = new ContextBuilder();
        var raw = "Ciao,\nDa: domani lavoro da casa.\nIl giorno 12 ci vediamo in sede.\nGrazie\n\nFrom: Marco <marco@example.com>\nSent: Thursday\nSubject: Re: test\n\nTesto citato";

        var clean = builder.CleanEmailBody(raw);

        Assert.Contains("Da: domani lavoro da casa.", clean);
        Assert.Contains("Il giorno 12 ci vediamo in sede.", clean);
        Assert.DoesNotContain("Testo citato", clean);
    }

    // An iPhone forward of an order confirmation (A39): the data is all in the forwarded message.
    private const string ForwardedOrder = "Inviato da iPhone\n\nInizio messaggio inoltrato:\n\n\tDa: Negozio <ordini@shop.example>\n\tData: 14 ottobre 2026 alle ore 11:13:57 CEST\n\tA: cliente@example.com\n\tOggetto: Ordine ORD-153 confermato\n\nRiepilogo ordine\nProfumo 15ml × 1\t7,21€\nTotale\n0,00€ EUR\nIndirizzo di spedizione\nVia Roma 1\n\nIl giorno 13 ott 2026, Negozio ha scritto:\n> Testo di un messaggio ancora precedente";

    [Fact]
    public void CleanEmailBody_ForwardMarker_KeepsTheForwardedMessage()
    {
        var clean = new ContextBuilder().CleanEmailBody(ForwardedOrder, "Fwd: Ordine ORD-153 confermato");

        Assert.Contains("Da: Negozio <ordini@shop.example>", clean);
        Assert.Contains("Totale\n0,00€ EUR", clean.Replace("\r", ""));
        Assert.Contains("Via Roma 1", clean);
        // History inside the forwarded message is still cut.
        Assert.DoesNotContain("ha scritto", clean);
        Assert.DoesNotContain("ancora precedente", clean);
    }

    [Fact]
    public void CleanEmailBody_ForwardMarkerWithoutSubject_KeepsTheForwardedMessage()
    {
        Assert.Contains("0,00€ EUR", new ContextBuilder().CleanEmailBody(ForwardedOrder));
    }

    [Fact]
    public void CleanEmailBody_OutlookForwardSubject_KeepsTheForwardedMessage()
    {
        var raw = "Vedi sotto.\n\nDa: Fornitore <f@example.com>\nInviato: lunedì 12 ottobre 2026 09:00\nA: Mario <m@example.com>\nOggetto: Fattura 42\n\nImporto 1.250,00 €\n\nDa: Mario <m@example.com>\nInviato: domenica\nA: Fornitore\nOggetto: richiesta\n\nRichiesta originale";

        var forward = new ContextBuilder().CleanEmailBody(raw, "I: Fattura 42");
        var reply = new ContextBuilder().CleanEmailBody(raw, "R: Fattura 42");

        Assert.Contains("Importo 1.250,00 €", forward);
        Assert.DoesNotContain("Richiesta originale", forward);
        Assert.DoesNotContain("Importo", reply);
    }

    [Fact]
    public void EnsureFits_NeverCuts_ThrowsWhenTheWholeTextDoesNotFit()
    {
        var builder = new ContextBuilder();
        var longText = new string('A', 5000);

        var ex = Assert.Throws<ContextTooLongException>(() => builder.EnsureFits(longText, budgetTokens: 100));
        Assert.Equal(100, ex.BudgetTokens);
        Assert.True(ex.EstimatedTokens > 1400);
        Assert.False(builder.Fits(longText, 100));

        // Budget 0 = no client-side limit (cloud: the provider decides).
        Assert.Same(longText, builder.EnsureFits(longText, 0));
        Assert.Same(longText, builder.EnsureFits(longText, 2000));
    }

    [Fact]
    public void FormatEmailsContext_TakesWholeEmailsWhileTheyFit()
    {
        var builder = new ContextBuilder();
        var emails = System.Linq.Enumerable.Range(1, 5).Select(i => new OnlyWorkspace.Core.Models.EmailSearchResult
        {
            Subject = "Email " + i,
            Body = "FINE-" + i + " " + new string('x', 900) + " CODA-" + i
        }).ToList();

        // About 1.000 characters each: 700 tokens (2.450 characters) hold two whole emails.
        var res = builder.FormatEmailsContext(emails, 700, out int included);

        Assert.Equal(2, included);
        Assert.Contains("CODA-2", res);
        Assert.DoesNotContain("FINE-3", res);

        builder.FormatEmailsContext(emails, 0, out included);
        Assert.Equal(5, included);

        // Not even the first one fits: nothing to answer on.
        Assert.Throws<ContextTooLongException>(() => builder.FormatEmailsContext(emails, 100, out _));
    }

    [Fact]
    public void FormatEmailsContext_FormatsMetadataAndCleanedBodies()
    {
        var builder = new ContextBuilder();
        var emails = new[]
        {
            new OnlyWorkspace.Core.Models.EmailSearchResult
            {
                SenderName = "Mario",
                SenderEmail = "mario@example.com",
                Subject = "Progetto A",
                FolderName = "Inbox",
                StoreDisplayName = "Exchange",
                ReceivedTime = new System.DateTime(2026, 10, 8, 10, 30, 0),
                Body = "<p>Dettagli del progetto.</p>"
            }
        };

        var res = builder.FormatEmailsContext(emails, 1000, out int included);

        Assert.Equal(1, included);
        Assert.Contains("--- EMAIL #1 ---", res);
        Assert.Contains("Da: Mario <mario@example.com>", res);
        Assert.Contains("Oggetto: Progetto A", res);
        Assert.Contains("Dettagli del progetto.", res);
        Assert.DoesNotContain("<p>", res);
    }

    [Fact]
    public void FormatCalendarContext_FormatsAppointmentsAndRecurrence()
    {
        var builder = new ContextBuilder();
        var appts = new[]
        {
            new OnlyWorkspace.Core.Models.AppointmentInfo
            {
                Subject = "Daily Standup",
                Start = new System.DateTime(2026, 10, 9, 9, 30, 0),
                End = new System.DateTime(2026, 10, 9, 10, 0, 0),
                Location = "Teams",
                Organizer = "Scrum Master",
                IsRecurring = true
            }
        };

        var res = builder.FormatCalendarContext(appts, 1000, out int included);

        Assert.Equal(1, included);
        Assert.Contains("--- IMPEGNO #1 ---", res);
        Assert.Contains("Titolo: Daily Standup", res);
        Assert.Contains("Luogo: Teams", res);
        Assert.Contains("Ricorrenza: Sì (serie ricorrente)", res);
    }

    [Fact]
    public void FormatContactsContext_FormatsContactDetails()
    {
        var builder = new ContextBuilder();
        var contacts = new[]
        {
            new OnlyWorkspace.Core.Models.ContactInfo
            {
                FullName = "Giulia Bianchi",
                Email = "giulia@partner.com",
                MobilePhone = "+39 333 1234567",
                CompanyName = "Partner Spa",
                JobTitle = "Project Manager"
            }
        };

        var res = builder.FormatContactsContext(contacts, 1000, out int included);

        Assert.Equal(1, included);
        Assert.Contains("--- CONTATTO #1 ---", res);
        Assert.Contains("Nome: Giulia Bianchi", res);
        Assert.Contains("Email: giulia@partner.com", res);
        Assert.Contains("Cellulare: +39 333 1234567", res);
        Assert.Contains("Azienda: Partner Spa", res);
    }

    [Fact]
    public void CleanEmailBody_KeepsAddressesAndLinksInAngleBrackets()
    {
        var builder = new ContextBuilder();
        var raw = "Scrivi a Mario <mario@example.com> o apri <https://example.com/ordine?id=7>.\n<a href=\"https://example.com\">sito</a>";

        var clean = builder.CleanEmailBody(raw);

        Assert.Contains("<mario@example.com>", clean);
        Assert.Contains("<https://example.com/ordine?id=7>", clean);
        Assert.DoesNotContain("href", clean);
    }
}
