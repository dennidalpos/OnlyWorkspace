using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Agents;

/// <summary>Local pre-filter of the mailbox scan: only emails that look like a meeting or a deadline reach the model.</summary>
public static class CalendarCandidateFilter
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Keywords = new(
        @"\b(riunion\w*|call|meeting|appuntament\w*|incontr\w+|videochiamat\w*|videoconferenz\w*|sopralluog\w*|colloqui\w*|webinar|" +
        @"scadenz\w*|deadline|entro\s+(il|la|l'|le|fine|domani|oggi|luned|marted|mercoled|gioved|venerd)|" +
        @"ci\s+(vediamo|sentiamo|aggiorniamo)|disponibilit\w+|fissa(re|to|ta|ti|te)|prenotazion\w*)\b" +
        @"|teams\.microsoft\.com|meet\.google\.com|zoom\.us/", Options);

    private static readonly Regex Day = new(
        @"\b(\d{1,2}[/.-]\d{1,2}([/.-]\d{2,4})?" +
        @"|\d{1,2}\s+(gen|feb|mar|apr|mag|giu|lug|ago|set|ott|nov|dic|jan|may|jun|jul|aug|sep|oct|dec)\w*" +
        @"|luned[ìi]|marted[ìi]|mercoled[ìi]|gioved[ìi]|venerd[ìi]|sabato|domenica" +
        @"|monday|tuesday|wednesday|thursday|friday|saturday|sunday" +
        @"|domani|dopodomani|stasera|tomorrow|tonight)\b", Options);

    private static readonly Regex Time = new(@"\b((alle|ore|h|at)\s*\d{1,2}([:.]\d{2})?|\d{1,2}:\d{2})\b", Options);

    public static bool IsCandidate(string? subject, string? body)
    {
        var text = (subject ?? string.Empty) + "\n" + (body ?? string.Empty);
        return Keywords.IsMatch(text) || (Day.IsMatch(text) && Time.IsMatch(text));
    }
}

/// <summary>Certified mail (PEC): the provider wraps the real message in an envelope email.</summary>
public static class PecMessage
{
    /// <summary>Subject the PEC provider gives to the envelope around a certified (or anomalous) message.</summary>
    public static bool IsEnvelope(string subject) =>
        subject.StartsWith("POSTA CERTIFICATA:", StringComparison.OrdinalIgnoreCase) ||
        subject.StartsWith("ANOMALIA MESSAGGIO:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracted email without its leading Da/A/Data/Oggetto lines: the sent date alone would pass the calendar pre-filter.</summary>
    public static string WithoutHeader(string text)
    {
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            var line = end < 0 ? text.Substring(start) : text.Substring(start, end - start);
            if (!(line.StartsWith("Da: ", StringComparison.Ordinal) || line.StartsWith("A: ", StringComparison.Ordinal) ||
                  line.StartsWith("Data: ", StringComparison.Ordinal) || line.StartsWith("Oggetto: ", StringComparison.Ordinal))) break;
            start = end < 0 ? text.Length : end + 1;
        }
        return text.Substring(start);
    }
}

/// <summary>What the scan summary says about the request: sent once the request starts, even if Interrompi stops it (A121).</summary>
public static class CalendarScanSummary
{
    public static string Request(int candidates, bool requested, bool stopped)
    {
        string sent = candidates == 1 ? "1 inviata" : candidates + " inviate";
        if (requested && stopped) return $"{sent} al modello in una sola richiesta, interrotta prima della risposta: nessuna voce ricevuta. ";
        if (requested) return $"{sent} al modello in una sola richiesta perché parlano di incontri o scadenze. ";
        if (candidates > 0) return (candidates == 1 ? "1 email candidata" : $"{candidates} email candidate") + ", nessuna richiesta inviata al modello. ";
        return "nessuna parla di incontri o scadenze. ";
    }
}

/// <summary>Cleans up what the scan found: no past items, no duplicates, and a check against what Outlook already holds.</summary>
public static class ActionItemMerger
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "con", "per", "del", "della", "dei", "delle", "alla", "alle", "sul", "sulla", "the", "and", "with", "for"
    };

    /// <summary>
    /// Upcoming items only, duplicates across emails merged, sorted by date (undated tasks last).
    /// Of two duplicates the verified one stays (A121). <paramref name="datedOnly"/> drops what has no date: nothing to put in a calendar.
    /// </summary>
    public static IReadOnlyList<ExtractedActionItem> Merge(IEnumerable<ExtractedActionItem> items, DateTime now, bool datedOnly = false)
    {
        var kept = new List<ExtractedActionItem>();
        foreach (var item in items)
        {
            if (IsPast(item, now)) continue;
            if (datedOnly && When(item) == null) continue;
            int same = kept.FindIndex(k => k.Kind == item.Kind && When(k) == When(item) && TitlesSimilar(k.Title, item.Title));
            if (same < 0) kept.Add(item);
            else if (kept[same].Unverified.Count > 0 && item.Unverified.Count == 0) kept[same] = item;
        }
        return kept.OrderBy(i => When(i) ?? DateTime.MaxValue).ToList();
    }

    public static bool IsPast(ExtractedActionItem item, DateTime now)
    {
        if (item.IsAppointment && item.Start.HasValue)
        {
            return item.AllDay ? item.Start.Value.Date < now.Date : item.Start.Value < now;
        }
        return item.Due.HasValue && item.Due.Value.Date < now.Date;
    }

    /// <summary>True when the calendar already holds an appointment at that time with a similar title.</summary>
    public static bool ExistsInCalendar(ExtractedActionItem item, IEnumerable<AppointmentInfo> appointments)
    {
        if (!item.IsAppointment || !item.Start.HasValue) return false;
        var start = item.Start.Value;
        return appointments.Any(a =>
            (item.AllDay ? a.Start.Date == start.Date : (a.Start - start).Duration() <= StartTolerance) &&
            TitlesSimilar(a.Subject, item.Title));
    }

    /// <summary>True when an open task with a similar title and the same due date exists.</summary>
    public static bool ExistsInTasks(ExtractedActionItem item, IEnumerable<ExtractedActionItem> openTasks)
    {
        if (item.IsAppointment) return false;
        return openTasks.Any(t => t.Due?.Date == item.Due?.Date && TitlesSimilar(t.Title, item.Title));
    }

    /// <summary>Same words, or at least half of the shorter title's significant words in common.</summary>
    public static bool TitlesSimilar(string a, string b)
    {
        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0) return string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        int common = wa.Count(wb.Contains);
        return common * 2 >= Math.Min(wa.Count, wb.Count);
    }

    private static DateTime? When(ExtractedActionItem item) => item.IsAppointment ? item.Start : item.Due?.Date;

    private static HashSet<string> Words(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches((text ?? string.Empty).ToLowerInvariant(), @"[\p{L}\p{Nd}]{3,}"))
        {
            if (!StopWords.Contains(m.Value)) words.Add(m.Value);
        }
        return words;
    }
}
