using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Context;

/// <summary>
/// Chat requests that ask for text, not for data (Danny, 20/10/2026): «scrivi una mail a Rossi per…» searched the
/// whole archive with the question as keywords. A writing request goes to the model once, without mail search;
/// in a compose window every request is writing unless it explicitly asks to look in the mail.
/// </summary>
public static class ChatWriting
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex WriteVerb = new(
        @"\b(scriv\w*|prepar\w*|compon\w*|redig\w*|riscriv\w*|riformul\w*|miglior\w*|corregg\w*|correggi|tradu\w*|accorci\w*|allung\w*|abbozz\w*|bozza|testo\s+per|aiutami\s+a\s+scrivere|write|compose|draft|rewrite|translate)\b", Options);

    // A writing verb about the mail itself («prepara un riepilogo delle email di ieri») is a mail question.
    private static readonly Regex MailDigest = new(
        @"\b(e-?mail|mail|posta|messaggi|pec)\b.*\b(ricevut\w*|arrivat\w*|inviat\w*|di\s+oggi|di\s+ieri|ultim\w+|settimana|mese)\b|\b(riepilog\w*|resoconto|riassunt\w*|sintesi|elenco|lista)\b.*\b(e-?mail|mail|posta|messaggi|pec)\b", Options);

    private static readonly Regex MailLookup = new(
        @"\b(cerc\w*|trov\w*|recuper\w*)\b.*\b(e-?mail|mail|messaggi|posta|pec|archivio|conversazion\w*)\b" +
        @"|\bnell[ae]\s+(mie\s+)?(e-?mail|mail|posta|pec|archivio)\b" +
        @"|\b(mi\s+ha|mi\s+hanno|ha|hanno)\s+(scritto|mandato|inviato|risposto)\b" +
        @"|\b(e-?mail|mail|messaggi|pec)\s+(ricevut|inviat|arrivat|precedent|scambiat)\w*", Options);

    private static readonly Regex AttachmentWord = new(@"\b(allegat\w*|attachment\w*|document[oi]|pdf|file)\b", Options);

    /// <summary>The request is about the files attached to the email being written.</summary>
    public static bool AsksAboutAttachments(string? question) => AttachmentWord.IsMatch(question ?? string.Empty);

    private static readonly Regex AnalysisVerb = new(@"\b(analizz\w*|legg\w*|riassum\w*|sintetizz\w*|spieg\w*|descriv\w*|verific\w*|controll\w*|estra\w*|elenc\w*|vedi|vedere|visto|cosa\s+(c.è|dice|contiene)|che\s+cosa|quali|quanto|quando)\b", Options);
    private static readonly Regex ComposeVerb = new(@"\b(scriv\w*|prepar\w*|compon\w*|redig\w*|riscriv\w*|riformul\w*|abbozz\w*|write|compose|draft|rewrite)\b", Options);

    /// <summary>A question about the attached files («analizza gli allegati»), not a text to write with them.</summary>
    public static bool AsksAttachmentAnalysis(string? question)
    {
        var text = question ?? string.Empty;
        return AttachmentWord.IsMatch(text) && AnalysisVerb.IsMatch(text) && !ComposeVerb.IsMatch(text);
    }

    /// <summary>The request asks for a text to write or rework, not for information from the mail.</summary>
    public static bool IsWriteRequest(string? question)
    {
        var text = question ?? string.Empty;
        return WriteVerb.IsMatch(text) && !MailDigest.IsMatch(text) && !MailLookup.IsMatch(text);
    }

    /// <summary>The request explicitly asks to look in the mail: the only way a compose window searches it.</summary>
    public static bool AsksMailSearch(string? question)
    {
        var text = question ?? string.Empty;
        return MailLookup.IsMatch(text) || MailDigest.IsMatch(text);
    }
}
