using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Context;

/// <summary>
/// Chat questions about the mail that need no model to be understood: a summary or list of the emails of a period
/// (with hours: «della mattina»), of a sender, or the latest ones. Any other word (a topic, a filter not known here)
/// leaves the strict fast path; the full local classifier keeps the remaining content and known filters.
/// </summary>
public sealed class LocalChatIntent
{
    /// <summary>Emails read when the question names no period («le ultime mail», «le mail di Rossi»).</summary>
    public const int LatestCount = 10;

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public DateTime? From { get; private set; }
    /// <summary>Last second included.</summary>
    public DateTime? To { get; private set; }
    public string? Sender { get; private set; }
    public string? Keywords { get; private set; }
    /// <summary>No period: only the <see cref="Count"/> newest emails.</summary>
    public bool Latest => From == null && To == null;
    /// <summary>Newest emails read without a period: «le ultime 5 mail», otherwise <see cref="LatestCount"/>.</summary>
    public int Count { get; private set; } = LatestCount;

    public EmailSearchQuery ToEmailSearch() => new()
    {
        KeywordsAny = true,
        Sender = Sender,
        StartDate = From,
        EndDate = To
    };

    private static readonly Regex MailNoun = new(@"\b(e-?mail|mail|posta|messaggi|messaggio|pec)\b", Options);

    private static readonly Regex Request = new(
        @"\b(resoconto|riassun\w*|riassum\w*|riepilog\w*|sintesi|sintetizz\w*|sommario|panoramica|elenc\w*|lista|quali|quante|mostr\w*|vedere|novit[aà]|arrivat\w*|ricevut\w*)\b", Options);

    /// <summary>Questions about the user's own sent mail go to the model: the archive filter here is by sender.</summary>
    private static readonly Regex Sent = new(@"\b(inviat\w*|mandat\w*|spedit\w*|ho scritto|ho risposto)\b(?!\s+da\b)", Options);

    private static readonly Regex Address = new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", Options);

    // «da/di» + a capitalised name (one or two words) or a domain; after an explicit verb any word is a name.
    private static readonly Regex NamedSender = new(
        @"\b(?:from|sent\s+by)\s+(?<name>[\p{L}][\p{L}'.-]*(?:\s+\p{Lu}[\p{L}'-]*)?)|\b(?<verb>(?:inviat|mandat|ricevut|scritt|arrivat)[ae]\s+da|da\s+parte\s+di)\s+(?<name>[\p{L}][\p{L}'.-]*(?:\s+\p{Lu}[\p{L}'-]*)?)" +
        @"|\b(?:da|di|dal|dalla)\s+(?<name>\p{Lu}[\p{L}'.-]*(?:\s+\p{Lu}[\p{L}'-]*)?|[\p{L}0-9-]+\.[\p{L}]{2,})",
        RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, int> Numbers = new(StringComparer.Ordinal)
    {
        ["un"] = 1,
        ["una"] = 1,
        ["due"] = 2,
        ["tre"] = 3,
        ["quattro"] = 4,
        ["cinque"] = 5,
        ["sei"] = 6,
        ["sette"] = 7,
        ["otto"] = 8,
        ["nove"] = 9,
        ["dieci"] = 10,
        ["dodici"] = 12,
        ["quindici"] = 15,
        ["venti"] = 20,
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["ventiquattro"] = 24,
        ["trenta"] = 30,
        ["quarantotto"] = 48
    };

    private static readonly Regex LastN = new(
        @"\b(?:nelle|negli|delle|degli|le|gli)?\s*ultim[ei]\s+(?<n>\d{1,3}|" + string.Join("|", Numbers.Keys) + @")\s+(?<unit>ore|giorni|settimane)\b|\blast\s+(?<n>\d{1,3}|one|two|three|four|five|six|seven)\s+(?<unit>hours?|days?|weeks?)\b", Options);
    private static readonly Regex LastMails = new(
        @"\bultim[ei]\s+(?<n>\d{1,3}|" + string.Join("|", Numbers.Keys) + @")\s+(?=e-?mail|mail|messaggi|pec)", Options);
    private static readonly Regex LastOne = new(@"\b(?:nell'|dell'|l')?\s*ultim[oa]\s+(?<unit>ora|giorno|settimana|mese)\b", Options);
    private static readonly Regex ThisWeek = new(@"\b(questa\s+settimana|settimana\s+corrente|in\s+settimana|this\s+week)\b", Options);
    private static readonly Regex LastWeek = new(@"\b(settimana\s+(scorsa|passata)|scorsa\s+settimana|last\s+week)\b", Options);
    private static readonly Regex ThisMonth = new(@"\b(questo\s+mese|mese\s+corrente|this\s+month)\b", Options);
    private static readonly Regex LastMonth = new(@"\b(mese\s+(scorso|passato)|scorso\s+mese|last\s+month)\b", Options);
    private static readonly Regex DayBeforeYesterday = new(@"\b(l'altro\s*ieri|l'altroieri|altroieri)\b", Options);
    private static readonly Regex Yesterday = new(@"\b(ieri|yesterday)\b", Options);
    private static readonly Regex Today = new(@"\b(oggi|odiern[ei]|giornata|today)\b", Options);
    private static readonly Regex Morning = new(@"\b(stamattina|stamani|stamane|mattina|mattino|mattinata|morning)\b", Options);
    private static readonly Regex Afternoon = new(@"\b(pomeriggio|afternoon)\b", Options);
    private static readonly Regex Evening = new(@"\b(stasera|sera|serata|evening)\b", Options);
    private static readonly Regex Recent = new(@"\b(ultim[ei]|recenti|nuov[ei])\b", Options);

    /// <summary>Words that carry no filter of their own.</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "find", "search", "for", "show", "list", "summarize", "summarise", "emails", "messages", "the", "all", "my", "please", "about", "regarding", "received", "sent",
        "fammi", "fai", "fare", "farmi", "mi", "me", "puoi", "potresti", "vorrei", "voglio", "dammi", "dimmi", "dai", "fa",
        "un", "uno", "una", "il", "lo", "la", "i", "gli", "le", "l", "di", "da", "del", "dello", "della", "dei", "degli", "delle", "dell",
        "a", "al", "allo", "alla", "ai", "agli", "alle", "all", "in", "nel", "nella", "nei", "negli", "nelle", "nell",
        "su", "sul", "sulla", "sulle", "sull", "per", "con", "tra", "fra", "e", "ed", "o", "che", "cosa", "c", "ci", "è",
        "ho", "hai", "ha", "abbiamo", "sono", "sto", "stato", "stati", "state", "mie", "miei", "mia", "mio", "nostre", "nostri",
        "tutte", "tutti", "tutto", "breve", "brevemente", "veloce", "rapido", "rapida", "sintetico", "sintetica", "favore", "grazie",
        "ultime", "ultimi", "ultima", "ultimo", "recenti", "nuove", "nuovi", "entrata", "arrivo", "casella", "inbox", "oggi",
        "e-mail", "email", "mail", "posta", "messaggi", "messaggio", "pec", "ricevute", "ricevuti", "ricevuta", "ricevuto",
        "arrivate", "arrivati", "arrivata", "arrivato", "giunte", "inviate", "inviati", "mandate", "mandati", "scritte", "scritti", "parte", "quali", "quante", "lista", "elenco", "elenca", "elencami",
        "trova", "cerca", "mostra", "mostrami", "vedere", "resoconto", "riassunto", "riassumi", "riassumimi", "riepilogo", "riepiloga", "sintesi",
        "sintetizza", "sommario", "panoramica", "novità", "novita", "ora", "adesso", "finora", "fino", "qui", "qua", "po"
    };

    /// <summary>PEC denotes a mailbox scope, which the fast path cannot apply.</summary>
    public static bool NamesPecMailbox(string question) => Regex.IsMatch(question, @"\bpec\b", Options);

    public static LocalChatIntent? TryParse(string? question, DateTime now) => Parse(question, now, strict: true);

    internal static LocalChatIntent? ParseFilters(string? question, DateTime now) => Parse(question, now, strict: false);

    private static LocalChatIntent? Parse(string? question, DateTime now, bool strict)
    {
        var original = (question ?? string.Empty).Replace('’', '\'').Replace('`', '\'').Trim();
        if (original.Length == 0 || (strict && original.Length > 200)) return null;
        if (strict && NamesPecMailbox(original)) return null;
        if (strict && (!MailNoun.IsMatch(original) || !Request.IsMatch(original) || Sent.IsMatch(original))) return null;

        var result = new LocalChatIntent();
        var rest = original;
        var literals = new List<string>();
        if (!strict)
            rest = Regex.Replace(rest, @"""[^""\r\n]+""|«[^»\r\n]+»", match =>
            {
                string marker = "\uE000" + literals.Count.ToString(CultureInfo.InvariantCulture) + "\uE001";
                literals.Add(match.Value);
                return marker;
            });

        var address = Address.Match(rest);
        if (address.Success)
        {
            result.Sender = address.Value;
            rest = rest.Remove(address.Index, address.Length);
        }
        else
        {
            var named = NamedSender.Match(rest);
            while (named.Success)
            {
                var name = named.Groups["name"].Value.Trim('.', '\'', '-');
                var first = name.Split(' ')[0].ToLowerInvariant();
                if (name.Length > 1 && !Filler.Contains(first) && !IsPeriodWord(first))
                {
                    result.Sender = name;
                    rest = rest.Remove(named.Index, named.Length);
                    break;
                }
                named = named.NextMatch();
            }
        }

        rest = " " + rest + " ";
        var count = LastMails.Match(rest);
        if (count.Success)
        {
            result.Count = Math.Max(1, Math.Min(100, Number(count.Groups["n"].Value)));
            rest = Strip(rest, count);
        }
        bool explicitPeriod = !strict && ReadExplicitPeriod(result, ref rest, now);
        bool period = explicitPeriod;
        if (!explicitPeriod) period = ReadPeriod(result, ref rest, now);
        else
        {
            var hours = new LocalChatIntent();
            if (ReadPeriod(hours, ref rest, result.From ?? now)) { result.From = hours.From; result.To = hours.To; }
        }
        bool recent = count.Success || Recent.IsMatch(rest);
        if (strict && !period && result.Sender == null && !recent) return null;

        // The strict fast path accepts only known filters. General local classification keeps every other clue.
        var words = Regex.Split(rest, @"[\s'?!.,;:()""«»\[\]/]+").Where(w => w.Length > 0);
        if (strict && words.Any(w => !Filler.Contains(w.ToLowerInvariant()))) return null;
        if (!strict)
        {
            // Quoted terms are literal data, including filler words, punctuation and code separators.
            var tokens = Regex.Matches(rest, @"""[^""\r\n]+""|«[^»\r\n]+»|[^\s?!,;:()]+")
                .Cast<Match>().Select(m => m.Value).Where(w => !Filler.Contains(w.Trim('.').ToLowerInvariant()));
            var remainder = string.Join(" ", tokens);
            for (int i = 0; i < literals.Count; i++)
                remainder = remainder.Replace("\uE000" + i.ToString(CultureInfo.InvariantCulture) + "\uE001", literals[i]);
            result.Keywords = remainder.Length == 0 ? null : remainder;
        }
        return result;
    }

    private static bool IsPeriodWord(string word) =>
        Today.IsMatch(word) || Yesterday.IsMatch(word) || Morning.IsMatch(word) || Afternoon.IsMatch(word) || Evening.IsMatch(word) ||
        word is "questa" or "questo" or "quest" or "settimana" or "mese" or "ultime" or "ultimi" or "ultima" or "ultimo" or "me" or "tutte" or "tutti";

    /// <summary>Reads one period, removing its words from <paramref name="text"/>; false when there is none.</summary>
    private static bool ReadPeriod(LocalChatIntent result, ref string text, DateTime now)
    {
        var today = now.Date;
        Match m;
        if ((m = LastN.Match(text)).Success)
        {
            int n = Number(m.Groups["n"].Value);
            if (n <= 0) return false;
            result.From = m.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "ore" or "hour" or "hours" => now.AddHours(-n),
                "settimane" or "week" or "weeks" => now.AddDays(-7 * n),
                _ => now.AddDays(-n)
            };
            text = Strip(text, m);
            return true;
        }
        if ((m = LastOne.Match(text)).Success)
        {
            result.From = m.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "ora" => now.AddHours(-1),
                "giorno" => now.AddDays(-1),
                "settimana" => now.AddDays(-7),
                _ => now.AddMonths(-1)
            };
            text = Strip(text, m);
            return true;
        }
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if ((m = ThisWeek.Match(text)).Success)
        {
            result.From = monday;
            text = Strip(text, m);
            return true;
        }
        if ((m = LastWeek.Match(text)).Success)
        {
            result.From = monday.AddDays(-7);
            result.To = monday.AddSeconds(-1);
            text = Strip(text, m);
            return true;
        }
        var month = new DateTime(today.Year, today.Month, 1);
        if ((m = ThisMonth.Match(text)).Success)
        {
            result.From = month;
            text = Strip(text, m);
            return true;
        }
        if ((m = LastMonth.Match(text)).Success)
        {
            result.From = month.AddMonths(-1);
            result.To = month.AddSeconds(-1);
            text = Strip(text, m);
            return true;
        }

        DateTime? day = null;
        if ((m = DayBeforeYesterday.Match(text)).Success) { day = today.AddDays(-2); text = Strip(text, m); }
        else if ((m = Yesterday.Match(text)).Success) { day = today.AddDays(-1); text = Strip(text, m); }
        else if ((m = Today.Match(text)).Success) { day = today; text = Strip(text, m); }

        (int From, int To)? hours = null;
        if ((m = Morning.Match(text)).Success) { hours = (0, 13); text = Strip(text, m); }
        else if ((m = Afternoon.Match(text)).Success) { hours = (13, 19); text = Strip(text, m); }
        else if ((m = Evening.Match(text)).Success) { hours = (19, 24); text = Strip(text, m); }

        if (day == null && hours == null) return false;
        var date = day ?? today;
        result.From = date.AddHours(hours?.From ?? 0);
        result.To = date.AddHours(hours?.To ?? 24).AddSeconds(-1);
        return true;
    }

    private static bool ReadExplicitPeriod(LocalChatIntent result, ref string text, DateTime now)
    {
        var tomorrow = Regex.Match(text, @"\b(domani|tomorrow)\b", Options);
        if (tomorrow.Success) { result.From = now.Date.AddDays(1); result.To = now.Date.AddDays(2).AddSeconds(-1); text = Strip(text, tomorrow); return true; }
        var next = Regex.Match(text, @"\b(prossima\s+settimana|settimana\s+prossima|next\s+week)\b", Options);
        if (next.Success)
        {
            var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7) + 7);
            result.From = monday; result.To = monday.AddDays(7).AddSeconds(-1); text = Strip(text, next); return true;
        }
        var dates = Regex.Matches(text, @"(?<!\d)(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4})(?!\d)");
        if (dates.Count is 1 or 2)
        {
            var values = new List<DateTime>();
            foreach (Match date in dates)
            {
                if (!DateTime.TryParseExact(date.Value, new[] { "yyyy-MM-dd", "d/M/yyyy", "dd/MM/yyyy" }, CultureInfo.GetCultureInfo("it-IT"), DateTimeStyles.None, out var value)) return false;
                values.Add(value);
            }
            if (values.Count == 2 && values[1] < values[0]) return false;
            result.From = values[0]; result.To = values[values.Count - 1].AddDays(1).AddSeconds(-1);
            foreach (Match date in dates.Cast<Match>().Reverse()) text = Strip(text, date);
            text = Regex.Replace(text, @"\b(dal|al|from|to|between|and)\b", " ", Options);
            return true;
        }
        string[] days = { "domenica|sunday", "luned[iì]|monday", "marted[iì]|tuesday", "mercoled[iì]|wednesday", "gioved[iì]|thursday", "venerd[iì]|friday", "sabato|saturday" };
        for (int i = 0; i < days.Length; i++)
        {
            var day = Regex.Match(text, @"\b(?:next\s+)?(?:" + days[i] + @")(?:\s+(?:prossimo|next))?\b", Options);
            if (!day.Success) continue;
            int delta = (i - (int)now.DayOfWeek + 7) % 7;
            if (delta == 0 && Regex.IsMatch(day.Value, @"prossimo|next", Options)) delta = 7;
            result.From = now.Date.AddDays(delta); result.To = result.From.Value.AddDays(1).AddSeconds(-1); text = Strip(text, day); return true;
        }
        return false;
    }

    private static int Number(string raw) =>
        Numbers.TryGetValue(raw.ToLowerInvariant(), out var word) ? word : int.Parse(raw, CultureInfo.InvariantCulture);

    private static string Strip(string text, Match m) => text.Remove(m.Index, m.Length).Insert(m.Index, " ");
}
