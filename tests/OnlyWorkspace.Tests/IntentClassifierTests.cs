using System;
using OnlyWorkspace.Core.Context;
using Xunit;

namespace OnlyWorkspace.Tests;

public class IntentClassifierTests
{
    private readonly DateTime _refDate = new(2026, 10, 8, 12, 0, 0);

    [Theory]
    [InlineData("Trova le email da Mario Rossi di ieri sul contratto ALFA-42", "Mario Rossi", "contratto ALFA-42")]
    [InlineData("Find emails from John Doe yesterday about contract ALFA-42", "John Doe", "contract ALFA-42")]
    public void LocalIntent_PreservesTheSenderPeriodAndTopic(string question, string sender, string keywords)
    {
        var intent = IntentClassifier.ClassifyLocal(question, _refDate);
        Assert.Equal("email", intent.Target);
        Assert.Equal(sender, intent.Sender);
        Assert.Equal(_refDate.Date.AddDays(-1), intent.StartDate);
        Assert.Equal(_refDate.Date.AddSeconds(-1), intent.EndDate);
        Assert.Equal(keywords, intent.Keywords);
    }

    [Theory]
    [InlineData("Ho riunioni domani mattina?", 9, 0, 12)]
    [InlineData("Do I have meetings tomorrow morning?", 9, 0, 12)]
    public void LocalCalendar_KeepsTheRequestedDayAndTime(string question, int day, int hour, int lastHour)
    {
        var intent = IntentClassifier.ClassifyLocal(question, _refDate);
        Assert.Equal("calendar", intent.Target);
        Assert.Equal(new DateTime(2026, 10, day, hour, 0, 0), intent.StartDate);
        Assert.Equal(new DateTime(2026, 10, day, lastHour, 59, 59), intent.EndDate);
    }

    [Theory]
    [InlineData("Trova il numero di telefono di Mario Rossi", "Mario Rossi")]
    [InlineData("Find John Doe's phone number", "John Doe")]
    public void LocalContacts_KeepsThePersonsName(string question, string expected) =>
        Assert.Equal(expected, IntentClassifier.ClassifyLocal(question, _refDate).PersonName);

    [Fact]
    public void LocalIntent_KeepsUnknownTermsInsteadOfInventingFilters()
    {
        var intent = IntentClassifier.ClassifyLocal("Find emails about \"Quarterly Report\" ZX-42", _refDate);
        Assert.Equal("\"Quarterly Report\" ZX-42", intent.Keywords);
        Assert.Null(intent.Sender); Assert.Null(intent.StartDate); Assert.Null(intent.EndDate);
    }

    [Fact]
    public void QuotedNamesDatesAndAddresses_AreContentRatherThanFilters()
    {
        var intent = IntentClassifier.ClassifyLocal("Find emails about \"from Mario Rossi yesterday a@example.test\"", _refDate);
        Assert.Equal("\"from Mario Rossi yesterday a@example.test\"", intent.Keywords);
        Assert.Null(intent.Sender); Assert.Null(intent.StartDate); Assert.Null(intent.EndDate);
    }

    // Routing cases cover the supported local rules independently of model prompts.
    [Theory]
    [InlineData("Ho riunioni in programma domani mattina?", "calendar")]
    [InlineData("Quali sono i miei impegni giovedi pomeriggio?", "calendar")]
    [InlineData("A che ora inizia la riunione con il fornitore la prossima settimana?", "calendar")]
    [InlineData("Ci sono eventi ricorrenti nel mio calendario per questo venerdi?", "calendar")]
    [InlineData("Chi organizza lo standup tecnico di domani?", "calendar")]
    [InlineData("Ho appuntamenti fuori sede mercoledi?", "calendar")]
    [InlineData("Qual e' la durata dell'allineamento con Mario Rossi?", "calendar")]
    [InlineData("Mostrami gli impegni fissati per lunedi prossimo tra le 9 e le 12.", "calendar")]
    [InlineData("Do I have any appointments scheduled for tomorrow morning?", "calendar")]
    [InlineData("What meetings are on my calendar for next Monday?", "calendar")]
    [InlineData("Trova il numero di telefono di Mario Rossi.", "contacts")]
    [InlineData("Qual e' l'indirizzo email di Elena Conti?", "contacts")]
    [InlineData("In quale azienda lavora John Doe?", "contacts")]
    [InlineData("Dammi il cellulare del responsabile commerciale del fornitore.", "contacts")]
    [InlineData("Cerca i contatti dell'azienda OnlyApps.", "contacts")]
    [InlineData("Qual e' la carica o ruolo di Elena Conti?", "contacts")]
    [InlineData("Find John Doe's phone number.", "contacts")]
    [InlineData("What is the contact email for Mario Rossi?", "contacts")]
    [InlineData("Which company is John Doe associated with?", "contacts")]
    [InlineData("List all contact details for Elena Conti.", "contacts")]
    [InlineData("Trova le email inviate da Mario Rossi nelle ultime due settimane.", "email")]
    [InlineData("Cerca i messaggi che parlano del contratto di fornitura Q3.", "email")]
    [InlineData("Ci sono email con allegati relative all'ordine di acquisto?", "email")]
    [InlineData("Mostrami l'ultima comunicazione inviata da security@azienda.local.", "email")]
    [InlineData("Trova le discussioni che contengono la parola chiave 'licenze software'.", "email")]
    [InlineData("Quali messaggi ho ricevuto dall'amministrazione riguardo a fatture scadute?", "email")]
    [InlineData("Cerca le email inviate da John Doe relative all'architettura Ollama.", "email")]
    [InlineData("Search for emails from Mario Rossi about contract renewals.", "email")]
    [InlineData("Find messages mentioning 'firmware upgrade' or 'maintenance'.", "email")]
    [InlineData("Did I receive any emails with attachments regarding invoice #1208?", "email")]
    [InlineData("Come posso configurare la risposta automatica in assenza?", "general")]
    [InlineData("Riassumi le principali decisioni prese nell'ultima settimana.", "general")]
    [InlineData("Quali sono le priorita operative concordate con il team?", "general")]
    [InlineData("Qual e' la procedura per richiedere nuovo hardware aziendale?", "general")]
    [InlineData("Quali regole devo seguire per la privacy della posta aziendale?", "general")]
    [InlineData("Come funziona la cifratura DPAPI dei segreti in OnlyWorkspace?", "general")]
    [InlineData("Cosa devo fare se Ollama non risponde sulla rete locale?", "general")]
    [InlineData("How does OnlyWorkspace handle cloud provider privacy?", "general")]
    [InlineData("What is the difference between LAN Ollama and Cloud profiles?", "general")]
    [InlineData("Can the user switch AI profiles if disabled by administrator?", "general")]
    [InlineData("riassumi le email di oggi sulla fattura", "email")]
    [InlineData("quali mail importanti ho ricevuto questa settimana?", "email")]
    [InlineData("Trova le email sulla riunione di domani", "email")]
    [InlineData("Search emails about tomorrow's meeting", "email")]
    [InlineData("scrivi una mail per spostare la riunione a domani", "write")]
    [InlineData("Write an email asking for John Doe's phone number", "write")]
    [InlineData("Riassumi le principali decisioni di oggi", "general")]
    [InlineData("Qual è il totale della fattura numero di ordine 42?", "general")]
    [InlineData("Trova le email con il numero di telefono di Rossi", "email")]
    [InlineData("Riassumi le email sui contatti di ieri", "email")]
    [InlineData("Search for emails containing John's phone number", "email")]
    public void Local_UsesTheRequestedDataInsteadOfTimeWords(string question, string expected)
    {
        Assert.Equal(expected, IntentClassifier.ClassifyLocal(question, _refDate).Target);
    }

    [Theory]
    [InlineData("analizza l'allegato", true)]
    [InlineData("Qual è il totale della fattura allegata?", true)]
    [InlineData("riassumi questa email", true)]
    [InlineData("cosa chiede quest'email?", true)]
    [InlineData("what does the attachment say?", true)]
    [InlineData("Come si aprono gli allegati PDF?", false)]
    [InlineData("Riassumi le email con allegati di Rossi", false)]
    [InlineData("this attachment", true)]
    [InlineData("trova le email con allegato di Rossi", false)]
    [InlineData("quali email ho ricevuto ieri con allegati?", false)]
    [InlineData("Cosa ho in agenda oggi?", false)]
    public void RefersToSelectedEmail_RecognisesQuestionsAboutTheOpenEmail(string query, bool expected)
    {
        Assert.Equal(expected, IntentClassifier.RefersToSelectedEmail(query));
    }

    /// <summary>«Ultimi 3 giorni» ends today: the mail search keeps every email received today (it kept none before).</summary>
    [Fact]
    public void ToEmailSearch_IncludesTheWholeEndDay()
    {
        var res = new OnlyWorkspace.Core.Models.UserIntentResult { Target = "email", StartDate = new DateTime(2026, 10, 16), EndDate = new DateTime(2026, 10, 19) };

        var query = res.ToEmailSearch("Riassumi le email degli ultimi 3 giorni");

        Assert.Equal(new DateTime(2026, 10, 16), query.StartDate);
        Assert.Equal(new DateTime(2026, 10, 19, 23, 59, 59), query.EndDate);
        Assert.Null(new OnlyWorkspace.Core.Models.UserIntentResult { Target = "email" }.ToEmailSearch("x").EndDate);
    }

    [Fact]
    public void LocalDateFilters_UseItalianDatesRegardlessOfCurrentCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            var res = IntentClassifier.ClassifyLocal("Trova le email dal 03/10/2026 al 10/10/2026", _refDate);
            Assert.Equal(new DateTime(2026, 10, 3), res.StartDate);
            Assert.Equal(new DateTime(2026, 10, 10, 23, 59, 59), res.EndDate);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("Trova le email da LinkedIn", "LinkedIn", null)]
    [InlineData("Trova le email di ieri", null, null)]
    [InlineData("Trova le email sulle offerte di lavoro", null, "offerte lavoro")]
    public void LocalSearch_KeepsOnlyTheActualTextAndSenderFilters(string question, string? sender, string? keywords)
    {
        var intent = IntentClassifier.ClassifyLocal(question, _refDate);
        Assert.Equal(sender, intent.Sender);
        Assert.Equal(keywords, intent.Keywords);
    }

    [Fact]
    public void FallbackClassify_DetectsCalendarQuery()
    {
        var res = IntentClassifier.FallbackClassify("Quali sono i miei impegni domani pomeriggio?", _refDate);

        Assert.Equal("calendar", res.Target);
        Assert.NotNull(res.StartDate);
        Assert.NotNull(res.EndDate);
    }

    [Fact]
    public void FallbackClassify_DetectsContactsQuery()
    {
        var res = IntentClassifier.FallbackClassify("Trova il numero di telefono di Luca", _refDate);

        Assert.Equal("contacts", res.Target);
        Assert.Contains("Luca", res.PersonName);
    }

    [Fact]
    public void FallbackClassify_DetectsEmailQuery()
    {
        var res = IntentClassifier.FallbackClassify("Cerca email inviate da Giovanni", _refDate);

        Assert.Equal("email", res.Target);
        Assert.Equal("Cerca email inviate da Giovanni", res.Keywords);
    }

}
