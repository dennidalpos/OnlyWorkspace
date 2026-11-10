using System;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Context;
using Xunit;

namespace OnlyWorkspace.Tests;

public class LocalChatIntentTests
{
    [Theory]
    [InlineData("Riassumi le email sull'ordine 4471", "")]
    [InlineData("Cosa chiede l'email sull'ordine 4471 e chi la manda?", "")]
    [InlineData("Cosa chiede l’email sulla fattura INV-4471 e chi la invia?", "")]
    [InlineData("Fammi un elenco dei messaggi sul protocollo PR/4471", "")]
    [InlineData("Puoi riepilogarmi le email sull'ordine 4471 per favore?", "")]
    [InlineData("Riassumi le email sull'ordine 4471 con ritardo consegna", "ritardo consegna")]
    [InlineData("Trova le email sull'ordine 4471 di Mario Rossi con ticket TK-9988", "Mario Rossi ticket TK-9988")]
    [InlineData("Cerca le email sull'ordine 4471 con codice TK-riassumi-9988", "codice TK-riassumi-9988")]
    [InlineData("Riassumi le email sull'ordine 4471 con titolo \"Elenco\"", "titolo \"Elenco\"")]
    public void FactChat_RemovesRequestsButKeepsContent(string question, string keywords)
    {
        var filters = new System.Collections.Generic.List<ArchiveFactFilter>();
        Assert.Equal(keywords, ArchiveSearchText.ParseChat(question, filters));
        Assert.Single(filters);
        Assert.True(LocalFactAnswer.HasReference(filters));
    }

    [Theory]
    [InlineData("Riassumi le email di Mario Rossi")]
    [InlineData("Riassumi le email con importo >=100 EUR")]
    [InlineData("Riassumi le email con scadenza entro 2026-11-30")]
    [InlineData("ordine senza numero, riassumi ritardo consegna")]
    public void FactChat_WithoutReferenceKeepsLiteralParsing(string question)
    {
        var literal = new System.Collections.Generic.List<ArchiveFactFilter>();
        var chat = new System.Collections.Generic.List<ArchiveFactFilter>();
        Assert.Equal(ArchiveSearchText.Parse(question, literal), ArchiveSearchText.ParseChat(question, chat));
        Assert.Equal(literal.Count, chat.Count);
    }

    // Tuesday 20/10/2026 10:30.
    private readonly DateTime _now = new(2026, 10, 20, 10, 30, 0);

    [Fact]
    public void MorningSummary_IsTodayUntilOnePm_WithoutTheModel()
    {
        var intent = LocalChatIntent.TryParse("fammi un resoconto delle ultime mail della mattina", _now);

        Assert.NotNull(intent);
        Assert.Equal(new DateTime(2026, 10, 20, 0, 0, 0), intent!.From);
        Assert.Equal(new DateTime(2026, 10, 20, 12, 59, 59), intent.To);
        Assert.Null(intent.Sender);
        Assert.False(intent.Latest);
    }

    [Theory]
    [InlineData("Riassumi le email di oggi", "2026-10-20 00:00:00", "2026-10-20 23:59:59")]
    [InlineData("quali mail ho ricevuto ieri?", "2026-10-19 00:00:00", "2026-10-19 23:59:59")]
    [InlineData("resoconto delle mail di ieri pomeriggio", "2026-10-19 13:00:00", "2026-10-19 18:59:59")]
    [InlineData("riepilogo della posta di stamattina", "2026-10-20 00:00:00", "2026-10-20 12:59:59")]
    [InlineData("Riassumi le email degli ultimi 3 giorni", "2026-10-17 10:30:00", null)]
    [InlineData("elenca le mail delle ultime due ore", "2026-10-20 08:30:00", null)]
    [InlineData("riassumi le mail di questa settimana", "2026-10-19 00:00:00", null)]
    [InlineData("riepilogo delle email della settimana scorsa", "2026-10-12 00:00:00", "2026-10-18 23:59:59")]
    [InlineData("riassumi la posta del mese scorso", "2026-09-01 00:00:00", "2026-09-30 23:59:59")]
    [InlineData("mostrami le mail dell'ultima ora", "2026-10-20 09:30:00", null)]
    public void Periods_AreReadWithHours(string question, string from, string? to)
    {
        var intent = LocalChatIntent.TryParse(question, _now);

        Assert.NotNull(intent);
        Assert.Equal(DateTime.Parse(from, System.Globalization.CultureInfo.InvariantCulture), intent!.From);
        Assert.Equal(to == null ? null : DateTime.Parse(to, System.Globalization.CultureInfo.InvariantCulture), intent.To);
    }

    [Theory]
    [InlineData("Quali mail ho ricevuto oggi da LinkedIn?", "LinkedIn")]
    [InlineData("riassumi le mail di Mario Rossi", "Mario Rossi")]
    [InlineData("resoconto delle email inviate da rossi", "rossi")]
    [InlineData("riassumi le mail di acmespa.com", "acmespa.com")]
    [InlineData("elenca le mail da info@example.com di ieri", "info@example.com")]
    public void Sender_IsANameADomainOrAnAddress(string question, string sender)
    {
        var intent = LocalChatIntent.TryParse(question, _now);

        Assert.NotNull(intent);
        Assert.Equal(sender, intent!.Sender);
    }

    [Fact]
    public void LatestEmails_WithoutAPeriod_AreCounted()
    {
        var latest = LocalChatIntent.TryParse("riassumi le ultime mail", _now);
        var five = LocalChatIntent.TryParse("riassumi le ultime 5 mail", _now);
        var sender = LocalChatIntent.TryParse("riassumi le mail di Rossi", _now);

        Assert.True(latest!.Latest);
        Assert.Equal(LocalChatIntent.LatestCount, latest.Count);
        Assert.Equal(5, five!.Count);
        Assert.True(sender!.Latest);
    }

    /// <summary>A topic, the user's own sent mail or a question without mail words are left to the full local classifier.</summary>
    [Theory]
    [InlineData("riassumi le mail di oggi sulla fattura")]
    [InlineData("quali offerte LinkedIn ho ricevuto negli ultimi 10 giorni?")]
    [InlineData("riassumi le mail di lavoro")]
    [InlineData("quali mail ho inviato ieri?")]
    [InlineData("cosa ho in agenda oggi?")]
    [InlineData("riassumi le email importanti di oggi")]
    [InlineData("riassumi le mail della PEC di ieri")]
    [InlineData("riassumi le PEC di oggi")]
    [InlineData("elenca le ultime 5 pec")]
    [InlineData("riassumi le mail della casella Gmail")]
    [InlineData("chi è Mario Rossi?")]
    [InlineData("scrivi una mail a Rossi per domani")]
    [InlineData("")]
    public void OtherQuestions_GoToTheModel(string question)
    {
        Assert.Null(LocalChatIntent.TryParse(question, _now));
    }

    [Fact]
    public void ToEmailSearch_KeepsHoursAndSender()
    {
        var query = LocalChatIntent.TryParse("riassumi le mail di stamattina da Rossi", _now)!.ToEmailSearch();

        Assert.Equal(new DateTime(2026, 10, 20, 0, 0, 0), query.StartDate);
        Assert.Equal(new DateTime(2026, 10, 20, 12, 59, 59), query.EndDate);
        Assert.Equal("Rossi", query.Sender);
        Assert.Null(query.Keywords);
    }

    [Theory]
    [InlineData("cerca sul web le ultime notizie su Acme", true)]
    [InlineData("verifica online questa azienda", true)]
    [InlineData("riassumi le mail della mattina", false)]
    public void ChatWebRequest_IsExplicitOnlyWhenAsked(string question, bool expected)
    {
        Assert.Equal(expected, ChatWebRequest.IsExplicit(question));
        Assert.True(ChatWebRequest.Wanted("general", question));
        Assert.Equal(expected, ChatWebRequest.Wanted("email", question));
    }
}

public class ChatMailDigestTests
{
    private static ChatMailItem Item(int hour, string subject, bool attachments = false) => new()
    {
        ArchiveId = hour,
        EntryId = "E" + hour,
        StoreId = "S",
        Subject = subject,
        SenderName = "Mario | Rossi",
        SenderAddress = "mario@example.test",
        Received = new DateTime(2026, 10, 20, hour, 5, 0),
        Preview = "Anteprima del testo\ncon un a capo",
        HasAttachments = attachments
    };

    [Fact]
    public void Listing_UsesTheOriginalPreview()
    {
        var listing = ChatMailDigest.Listing(new[] { Item(8, "Offerta", attachments: true), Item(9, "Saluti") }, more: 2);

        Assert.Contains("**2 email** del 20/10/2026", listing);
        Assert.Contains("| Ora | Da | Oggetto | Sintesi |", listing);
        Assert.Contains("| 08:05 | Mario / Rossi | Offerta (con allegati) | Anteprima del testo con un a capo |", listing);
        Assert.DoesNotContain("Offerta per 10 pezzi", listing);
        Assert.Contains("| 09:05 | Mario / Rossi | Saluti | Anteprima del testo con un a capo |", listing);
        Assert.Contains("Altre 2 email non sono in elenco.", listing);
        Assert.Equal(string.Empty, ChatMailDigest.Listing(Array.Empty<ChatMailItem>(), 0));
    }

    [Fact]
    public void Summary_IsClipped()
    {
        var item = Item(8, "Lunga");
        item.Preview = new string('b', 400);

        Assert.True(ChatMailDigest.Summary(item).Length <= 170);
        Assert.EndsWith("…", ChatMailDigest.Summary(item));
    }

    [Fact]
    public void Note_CarriesMarkerTimeAndSender()
    {
        var note = ChatMailDigest.Note(3, Item(8, "Offerta"), "Testo intero\n");

        Assert.Equal("[M3] [Email del 20/10/2026 08:05 da Mario | Rossi <mario@example.test> — «Offerta»]\nTesto intero", note);
    }
}
