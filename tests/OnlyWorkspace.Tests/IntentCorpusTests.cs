using OnlyWorkspace.Core.Context;
using Xunit;

namespace OnlyWorkspace.Tests;

/// <summary>
/// #201: phrases that satisfy more than one rule of <see cref="IntentClassifier"/>, or none clearly. The rules run in a fixed
/// order (write, contacts, mail, calendar, general); each row states the intent a person means. The few rows the rules get
/// wrong (#201, fixed by #222) are listed with the intent a person means; a limit found later goes in a separate theory with
/// the outcome the rules give, and the guides name it.
/// </summary>
public sealed class IntentCorpusTests
{
    private static readonly DateTime Today = new(2026, 11, 11, 12, 0, 0);

    [Theory]
    // Writing a text wins over the data the sentence names.
    [InlineData("Scrivi una risposta alla mail di Rossi", "write")]
    [InlineData("Prepara una bozza per confermare l'appuntamento di domani", "write")]
    [InlineData("Scrivi a Mario Rossi per spostare la riunione di venerdì", "write")]
    [InlineData("Compose an email to John Doe about the meeting tomorrow", "write")]
    [InlineData("Draft a reply to the contract email", "write")]
    [InlineData("Write a message asking Elena Conti for her phone number", "write")]
    [InlineData("Traduci in inglese: grazie per la vostra disponibilità", "write")]
    [InlineData("Correggi questo testo: ho ricevuto la vostra offerta e la trovo interessanti", "write")]
    [InlineData("Job title of John Doe", "contacts")]
    // A mailbox search that names a meeting, a person or a phone number stays a mailbox search.
    [InlineData("Cerca le email sulla riunione di domani con Mario", "email")]
    [InlineData("Ci sono mail sulla riunione di domani?", "email")]
    [InlineData("Trova le mail di Mario Rossi", "email")]
    [InlineData("Mostrami le email ricevute da Elena Conti ieri", "email")]
    [InlineData("Elenca le email di oggi con allegati", "email")]
    [InlineData("Quante mail ho ricevuto dal commercialista questo mese?", "email")]
    [InlineData("Riassumi le email dell'ultima settimana sul preventivo", "email")]
    [InlineData("Show me the emails about the offsite meeting", "email")]
    [InlineData("List all emails from John Doe sent last week", "email")]
    [InlineData("Did John Doe send me any email about the contract?", "email")]
    [InlineData("Find messages that mention the phone number of Elena Conti", "email")]
    [InlineData("Cerca nella posta le fatture di ottobre", "email")]
    [InlineData("Trova le discussioni sul progetto Alfa dell'ultimo mese", "email")]
    [InlineData("Ho ricevuto messaggi dal fornitore oggi?", "email")]
    [InlineData("Mostra le comunicazioni inviate al cliente Acme", "email")]
    [InlineData("Cerca le mail inviate da Rossi con il calendario delle consegne", "email")]
    [InlineData("Search the mail for the invoice of November", "email")]
    [InlineData("Any new emails about the quarterly review?", "email")]
    [InlineData("Riepilogo delle mail arrivate stamattina", "email")]
    [InlineData("Elenca gli eventi e le mail di oggi", "email")]
    [InlineData("Mail sulla riunione di domani", "email")]
    [InlineData("Who is the sender of the invoice mail?", "email")]
    [InlineData("Chi ha scritto la mail sul preventivo?", "email")]
    // Contacts: who, number, role, company.
    [InlineData("Chi è Mario Rossi?", "contacts")]
    [InlineData("Who is Elena Conti?", "contacts")]
    [InlineData("Qual è il numero di telefono di Elena Conti?", "contacts")]
    [InlineData("Che ruolo ha Mario Rossi in azienda?", "contacts")]
    [InlineData("Cerca il contatto di Mario Rossi", "contacts")]
    [InlineData("Dammi il cellulare di Elena Conti", "contacts")]
    [InlineData("What is the phone number of John Doe?", "contacts")]
    [InlineData("Show me the contact details of Elena Conti", "contacts")]
    [InlineData("In quale azienda lavora Mario Rossi?", "contacts")]
    [InlineData("Trova nella rubrica Elena Conti", "contacts")]
    [InlineData("Find the address book entry for John Doe", "contacts")]
    [InlineData("Cerca il contatto di Mario Rossi e le sue ultime mail", "contacts")]
    [InlineData("Qual è la mail di Mario Rossi?", "contacts")]
    [InlineData("What is the email of John Doe?", "contacts")]
    // Calendar: the agenda, a meeting, a period.
    [InlineData("Ho una riunione domani?", "calendar")]
    [InlineData("Cosa ho in agenda oggi?", "calendar")]
    [InlineData("Mostra il calendario della prossima settimana", "calendar")]
    [InlineData("Che appuntamenti ho dal 15 al 20 novembre?", "calendar")]
    [InlineData("A che ora è la riunione con Acme?", "calendar")]
    [InlineData("What is on my calendar tomorrow?", "calendar")]
    [InlineData("Do I have meetings on Friday afternoon?", "calendar")]
    [InlineData("Quando è il prossimo appuntamento con il dentista?", "calendar")]
    [InlineData("Che impegni ho domani? Controlla anche la posta", "calendar")]
    [InlineData("Riassumi la riunione di ieri", "calendar")]
    [InlineData("Come è andata la riunione di ieri?", "calendar")]
    // General: how-to, concepts, texts the user pastes, nothing in Outlook to read.
    [InlineData("Come si cifra una mail con S/MIME?", "general")]
    [InlineData("Qual è la differenza tra PEC e posta elettronica ordinaria?", "general")]
    [InlineData("Spiega cos'è un attacco di phishing", "general")]
    [InlineData("Quali sono le buone pratiche per organizzare un team?", "general")]
    [InlineData("What does GDPR article 9 cover?", "general")]
    [InlineData("How long should a good subject line be?", "general")]
    [InlineData("Dammi tre idee per un titolo", "general")]
    [InlineData("Cos'è un allegato inline?", "general")]
    [InlineData("Calcola il 22% di 1.250 euro", "general")]
    [InlineData("Suggerisci un nome per il progetto", "general")]
    [InlineData("Is it better to send the report by mail or in a shared folder?", "general")]
    [InlineData("Come configuro il calendario in Outlook?", "general")]
    [InlineData("Cosa significa la parola riunione?", "general")]
    [InlineData("How do I schedule a meeting in Teams?", "general")]
    public void Phrase_IsRoutedToTheDataThePersonMeans(string question, string expected) =>
        Assert.Equal(expected, IntentClassifier.ClassifyLocal(question, Today).Target);

    [Fact]
    public void TheCorpus_HoldsAtLeastSixtyPhrases_InBothLanguages()
    {
        string source = File.ReadAllText(Path.Combine(TestBuildPaths.RepositoryRoot, "tests", "OnlyWorkspace.Tests", "IntentCorpusTests.cs"));
        var rows = System.Text.RegularExpressions.Regex.Matches(source, @"\[InlineData\(""[^""]+"", ""(?:write|email|contacts|calendar|general)""\)\]").Count;
        Assert.True(rows >= 60, $"{rows} phrases");
        Assert.Contains("What is on my calendar tomorrow?", source);
        Assert.Contains("Ho una riunione domani?", source);
    }

    [Fact]
    public void Calendar_SaysWhetherThePeriodWasStatedOrIsTheDefaultWeek()
    {
        Assert.False(IntentClassifier.ClassifyLocal("Cosa ho in agenda?", Today).PeriodStated);
        Assert.True(IntentClassifier.ClassifyLocal("Cosa ho in agenda oggi?", Today).PeriodStated);
        Assert.True(IntentClassifier.ClassifyLocal("Ho riunioni domani mattina?", Today).PeriodStated);
        Assert.True(IntentClassifier.ClassifyLocal("Che appuntamenti ho dal 15/11/2026 al 20/11/2026?", Today).PeriodStated);
    }
}
