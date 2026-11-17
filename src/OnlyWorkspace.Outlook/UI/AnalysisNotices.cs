using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OnlyWorkspace.Core.Agents;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// The sentences and the table message an analysis of the mailbox closes with (#177): what was cut, what the exposure level kept
/// out, what the archive could not find again. No Outlook, no model: the coordinator counts, this only words it.
/// </summary>
internal static class AnalysisNotices
{
    /// <summary>The Outlook conversation was cut to its most recent messages: say how many were left out.</summary>
    public static string Conversation(int read, int total) => total > read
        ? $"La conversazione ha {total} messaggi: letti i {read} più recenti, {total - read} più vecchi esclusi. "
        : string.Empty;

    /// <summary>What the profile's exposure level kept out of the request, for the coverage line; empty when nothing.</summary>
    public static string Exposure(EvidenceReading reading) =>
        (reading.NoFacts.Count > 0 ? $"{reading.NoFacts.Count} email senza fatti locali non inviate (il livello di esposizione non invia originali). " : string.Empty) +
        (reading.OverBudget.Count > 0 ? $"{reading.OverBudget.Count} email oltre il limite di dati del livello di esposizione non inviate. " : string.Empty);

    /// <summary>Emails the archive found by subject and sender only, because their text is still being indexed.</summary>
    public static string ArchivePending(int bodiesPending) =>
        $"\nArchivio ancora in costruzione: {bodiesPending} email sono trovate solo per oggetto e mittente, non per testo e destinatari.";

    /// <summary>Outlook returned only the newest messages of a visible conversation.</summary>
    public const string FlowLimited =
        "\nOutlook ha restituito solo i 20 messaggi più recenti della conversazione visibile. L'archivio può mostrarne altri; la copertura del flusso resta incerta.";

    /// <summary>Emails the archive had found but that are no longer where it found them (moved or deleted).</summary>
    public static string MissingFromArchive(int missing) => missing == 1
        ? "\n1 email non è più dove l'archivio l'aveva trovata (spostata o eliminata) e non è stata letta: l'archivio la cerca di nuovo nei prossimi momenti liberi."
        : $"\n{missing} email non sono più dove l'archivio le aveva trovate (spostate o eliminate) e non sono state lette: l'archivio le cerca di nuovo nei prossimi momenti liberi.";

    /// <summary>Table of the analysis for the pane, copyable as TSV and saved as CSV with the list separator of the user's locale.</summary>
    /// <param name="listSeparator">The locale's list separator; null reads the current culture.</param>
    public static object TableMessage(IReadOnlyList<string> columns, List<string[]> rows, string? listSeparator = null)
    {
        var headers = columns.Concat(new[] { "Email" }).ToArray();
        var separator = (listSeparator ?? CultureInfo.CurrentCulture.TextInfo.ListSeparator) == ";" ? ';' : ',';
        return new
        {
            documentType = "analisi",
            fields = Array.Empty<string[]>(),
            lineHeaders = headers,
            lines = rows,
            tsv = AnalysisTable.ToDelimited(headers, rows, '\t', quote: false),
            csv = AnalysisTable.ToDelimited(headers, rows, separator, quote: true)
        };
    }
}
