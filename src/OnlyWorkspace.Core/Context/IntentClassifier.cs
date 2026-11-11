using System;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Context;

public static class IntentClassifier
{
    /// <summary>Classification never sends the question to a model. Unrecognized content remains a search clue.</summary>
    public static UserIntentResult ClassifyLocal(string query, DateTime referenceDate)
    {
        var result = FallbackClassify(query, referenceDate);
        var filters = LocalChatIntent.ParseFilters(query, referenceDate);
        if (filters != null && result.Target is "email" or "calendar")
        {
            result.Keywords = filters.Keywords;
            result.Sender = filters.Sender;
            if (filters.From != null) { result.StartDate = filters.From; result.EndDate = filters.To; result.PeriodStated = true; }
        }
        if (result.Target == "contacts")
        {
            var name = System.Text.RegularExpressions.Regex.Match(query,
                @"(?:\b(?:di|for|of|who\s+is|chi\s+[èe])\s+)(?<name>(?-i:\p{Lu}[\p{L}'’-]*(?:\s+\p{Lu}[\p{L}'’-]*)*))|\b(?:find|trova)\s+(?<name>[\p{L}][\p{L}'’-]*(?-i:\s+\p{Lu}[\p{L}'’-]*)*)(?:'s|’s)", RuleOptions);
            if (name.Success) result.PersonName = name.Groups["name"].Value;
        }
        return result;
    }

    private const System.Text.RegularExpressions.RegexOptions RuleOptions =
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;

    private static readonly System.Text.RegularExpressions.Regex ThisEmail = new(
        @"\b(quest[ao'’]\s*(e-?mail|mail|messaggio|pec|conversazione)|(e-?mail|mail|messaggio|pec)\s+(selezionat\w+|apert\w+|corrente)|this\s+(e-?mail|message))\b", RuleOptions);

    private static readonly System.Text.RegularExpressions.Regex Attachment = new(
        @"\b((quest[oi]|l['’])\s*allegat\w*|(la|della)\s+fattura\s+allegat\w*|(this|the)\s+attachment)\b", RuleOptions);

    private static readonly System.Text.RegularExpressions.Regex MailboxSearch = new(
        @"\b((cerca|trova|quali|quante|elenca|mostra)\w*\s+(tutt[ei]\s+)?(le\s+|i\s+)?(e-?mail|mail|messaggi)|nella\s+posta|ricevut[ei]|inviat[ei])\b", RuleOptions);

    /// <summary>
    /// True when the question is about the email in front of the user ("questa email", "analizza l'allegato"),
    /// not a mailbox search ("trova le email con allegato di Rossi").
    /// </summary>
    public static bool RefersToSelectedEmail(string? query)
    {
        var text = query ?? string.Empty;
        return ThisEmail.IsMatch(text) || (Attachment.IsMatch(text) && !MailboxSearch.IsMatch(text));
    }

    // A contact's email address is a contact lookup, not a search for messages.
    private static readonly System.Text.RegularExpressions.Regex ContactLookup = new(
        @"\b(contatt\w*|rubrica|telefon\w*|cellular\w*|phone|mobile|contact\s+(email|details)|address\s+book|indirizzo\s+(e-?mail|di)|e-?mail\s+(address|for)|chi\s+[èe]|who\s+is)\b" +
        @"|\b(azienda|company)\b.*\b(lavora|associated|work\w*)\b|\b(company)\b.*\bassociated\b" +
        @"|\b(carica|ruolo)\s+(di|del|della)\b|\b(che|quale)\s+(ruolo|carica|mansione)\b|\bjob\s+title\b", RuleOptions);

    private static readonly System.Text.RegularExpressions.Regex MailTerms = new(
        @"\b(e-?mails?|mail|posta|messaggi\w*|messages?|comunicazion\w*|discussioni|pec)\b", RuleOptions);

    private static readonly System.Text.RegularExpressions.Regex MailRequest = new(
        @"\b(cerc\w*|trov\w*|elenc\w*|mostr\w*|riassum\w*|riassunt\w*|riepilog\w*|sintesi|resoconto|ricevut\w*|inviat\w*|arrivat\w*|ultima|ultime|ultimi|ci\s+sono|search|find|show|list|summari[sz]e|received?|sent|any)\b" +
        @"|\b(quali|quante)\s+(e-?mail|mail|messaggi|comunicazioni|pec)\b", RuleOptions);

    private static readonly System.Text.RegularExpressions.Regex CalendarLookup = new(
        @"\b(impegn\w*|agenda|calendari\w*|calendar|riunion\w*|appuntament\w*|appointments?|meetings?|eventi|standup|allineamento)\b", RuleOptions);

    private static readonly System.Text.RegularExpressions.Regex ExplicitMailboxRequest = new(
        @"\b(cerc\w*|trov\w*|elenc\w*|mostr\w*|riassum\w*|search|find|show|list|summari[sz]e)\s+((for|all|the|tutte|tutti|le|i|mie|miei)\s+)*(e-?mails?|mail|posta|messaggi\w*|messages?|pec)\b", RuleOptions);

    // «Come configuro il calendario…», «Cosa significa la parola riunione?»: how-to or definition, nothing in Outlook to read.
    private static readonly System.Text.RegularExpressions.Regex HowTo = new(
        @"^\s*(come\s+(si\s+)?(configur\w*|impost\w*|cre\w*|usar\w*|aggiung\w*|condivid\w*|sincroniz\w*|pianific\w*|organizz\w*|fiss\w*)|(che\s+)?cosa\s+significa|how\s+(do|can|should|to)\b|what\s+does\b.*\bmean)", RuleOptions);

    // A mail noun that opens the sentence and is followed by a preposition: «Mail sulla riunione di domani».
    private static readonly System.Text.RegularExpressions.Regex LeadingMailNoun = new(
        @"^\s*(e-?mails?|mail|messaggi|messages?|pec)\s+(sul|sulla|sull['’]|sui|sugli|sulle|su|da|dal|dalla|dai|con|about|from|regarding)\b", RuleOptions);

    // «Who is the sender of the invoice mail?»: the sender of a message, not a lookup of a person.
    private static readonly System.Text.RegularExpressions.Regex AsksSender = new(
        @"\b(who\s+is\s+the\s+sender|who\s+sent|chi\s+[èe]\s+il\s+mittente|chi\s+(ha\s+)?(scritto|inviato|mandato))\b", RuleOptions);

    // «Qual è la mail di Mario Rossi?»: the address of a person is a contact lookup.
    private static readonly System.Text.RegularExpressions.Regex AddressOfPerson = new(
        @"\b(qual\s+[èe]|what\s+is|what's|dammi|dimmi)\s+(la\s+|the\s+|l['’]\s*)?(e-?mail|mail)\s+(di|del|della|of|for)\b", RuleOptions);

    public static UserIntentResult FallbackClassify(string query, DateTime referenceDate)
    {
        var text = query ?? string.Empty;
        var result = new UserIntentResult();

        // «scrivi una mail per spostare la riunione a domani» is a text to write, not the agenda.
        if (ChatWriting.IsWriteRequest(query))
        {
            result.Target = "write";
            return result;
        }

        if (AsksSender.IsMatch(text) && MailTerms.IsMatch(text))
        {
            result.Target = "email";
            result.Keywords = query;
            return result;
        }

        if ((ContactLookup.IsMatch(text) || AddressOfPerson.IsMatch(text)) && !ExplicitMailboxRequest.IsMatch(text))
        {
            result.Target = "contacts";
            result.PersonName = query;
            return result;
        }

        // Time words qualify a request; they never decide which Outlook data to read.
        if (ChatWriting.AsksMailSearch(text) || (MailTerms.IsMatch(text) && (MailRequest.IsMatch(text) || LeadingMailNoun.IsMatch(text))))
        {
            result.Target = "email";
            result.Keywords = query;
            return result;
        }

        if (CalendarLookup.IsMatch(text) && !HowTo.IsMatch(text))
        {
            result.Target = "calendar";
            result.StartDate = referenceDate.Date;
            result.EndDate = referenceDate.Date.AddDays(7);
            return result;
        }

        result.Target = "general";
        return result;
    }
}
