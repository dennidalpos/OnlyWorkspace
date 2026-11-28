using System;
using System.Globalization;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Context;

/// <summary>
/// #201: the line the pane shows next to a chat answer, saying how the question was read: which Outlook data it looks in,
/// and the period, the sender or the name it kept. The reading is made by local rules, so the person can tell a wrong
/// reading from a wrong answer and rephrase.
/// </summary>
public static class IntentInterpretation
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private const int MaxQuoted = 80;

    public static string Describe(UserIntentResult intent, string question)
    {
        switch (intent.Target)
        {
            case "write":
                return "Interpretata come richiesta di un testo da scrivere: nessuna ricerca in posta, calendario o contatti.";
            case "email":
                {
                    var parts = new System.Collections.Generic.List<string>();
                    if (!string.IsNullOrWhiteSpace(intent.Sender)) parts.Add("da " + Quote(intent.Sender!));
                    if (intent.StartDate != null || intent.EndDate != null) parts.Add(Period(intent.StartDate, intent.EndDate));
                    string words = !string.IsNullOrWhiteSpace(intent.Keywords) ? intent.Keywords!
                        : parts.Count == 0 ? question : string.Empty;
                    if (words.Length > 0) parts.Add("parole: " + Quote(words));
                    return "Interpretata come ricerca nella posta" + (parts.Count > 0 ? " (" + string.Join("; ", parts) + ")" : string.Empty) + ".";
                }
            case "calendar":
                return "Interpretata come domanda sul calendario, " + (intent.PeriodStated
                    ? Period(intent.StartDate, intent.EndDate)
                    : "periodo non indicato: " + Period(intent.StartDate, intent.EndDate) + " (oggi e la settimana successiva)") + ".";
            case "contacts":
                {
                    string name = !string.IsNullOrWhiteSpace(intent.PersonName) ? intent.PersonName! : question;
                    return "Interpretata come ricerca nei contatti: " + Quote(name) + ".";
                }
            default:
                return "Interpretata come domanda generale: nessuna ricerca in posta, calendario o contatti.";
        }
    }

    public static string ForSelectedEmail() => "Interpretata come domanda sull'email selezionata.";

    public static string ForDraft() => "Interpretata come domanda sulla bozza aperta.";

    public static string ForRetry() => "Ripresa sulle email già lette per questa richiesta.";

    /// <summary>«dal 12/11/2026 al 19/11/2026», «il 12/11/2026 dalle 09:00 alle 12:59», «dal 12/11/2026».</summary>
    internal static string Period(DateTime? start, DateTime? end)
    {
        if (start == null && end == null) return "periodo non limitato";
        if (start == null) return "fino al " + Day(end!.Value);
        if (end == null) return "dal " + Day(start.Value);
        var a = start.Value; var b = end.Value;
        bool wholeDays = a.TimeOfDay == TimeSpan.Zero && (b.TimeOfDay == TimeSpan.Zero || b.TimeOfDay >= new TimeSpan(23, 59, 0));
        if (a.Date == b.Date && !wholeDays)
            return "il " + Day(a) + " dalle " + a.ToString("HH:mm", Italian) + " alle " + b.ToString("HH:mm", Italian);
        if (a.Date == b.Date) return "il " + Day(a);
        return "dal " + Day(a) + " al " + Day(b);
    }

    private static string Day(DateTime value) => value.ToString("dd/MM/yyyy", Italian);

    private static string Quote(string text)
    {
        string flat = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return "«" + (flat.Length > MaxQuoted ? flat.Substring(0, MaxQuoted - 1) + "…" : flat) + "»";
    }
}
