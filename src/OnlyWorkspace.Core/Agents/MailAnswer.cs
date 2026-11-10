using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Agents;

/// <summary>One email as the final request reads it: its original or literal local facts.</summary>
public sealed class MailEvidence
{
    public ChatMailItem Mail { get; set; } = new();
    public string Text { get; set; } = string.Empty;
    /// <summary>The original was read whole (body and readable attachments), rather than only local facts.</summary>
    public bool Whole { get; set; }
    /// <summary>The original could not be read: its text only declares it, so it counts neither as whole nor as local facts.</summary>
    public bool Missing { get; set; }
}

/// <summary>
/// The emails of one answer, oldest first, numbered [M1]…[Mn] (E7, 25/10/2026): one model request reads them all,
/// instead of a note per email condensed in groups. Each citation is then checked against the text of its email.
/// </summary>
public sealed class MailEvidencePack
{
    public List<MailEvidence> Items { get; } = new();

    public static MailEvidencePack Chronological(IEnumerable<MailEvidence> items)
    {
        var pack = new MailEvidencePack();
        pack.Items.AddRange(items.OrderBy(e => e.Mail.Sent ?? e.Mail.Received));
        return pack;
    }

    public int Count => Items.Count;
    public int Whole => Items.Count(i => i.Whole);
    public int Missing => Items.Count(i => i.Missing && !i.Whole);
    public int Facts => Count - Whole - Missing;

    /// <summary>The email blocks for the prompt; restricted facts omit sender, subject and correlation reasons.</summary>
    public string Render(bool includeDetails = true) => string.Join("\n\n", Items.Select((e, i) => Note(i, includeDetails)));

    /// <summary>What the model reads of email [M<paramref name="index"/>+1]: citations are checked on exactly this text.</summary>
    public string Note(int index, bool includeDetails = true) => ChatMailDigest.Note(index + 1, Items[index].Mail, Items[index].Text, includeDetails);

    public List<HistoryContextReference> Sources() => Items.Select(e => new HistoryContextReference
    {
        EntryId = e.Mail.EntryId,
        StoreId = e.Mail.StoreId,
        Subject = e.Mail.Subject,
        MessageId = e.Mail.MessageId,
        ItemType = "email"
    }).ToList();
}

/// <summary>Requests answered by the local list alone, and the explicit request to read the originals whole.</summary>
public static class MailRequest
{
    private static readonly Regex MailNoun = new(@"\b(?:e-?mails?|mail|messaggi|posta)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ListOrCount = new(@"^\s*(?:(?:mi\s+)?(?:elenca(?:mi)?|elenco|lista|mostra(?:mi)?|fammi\s+vedere|quali\s+sono|quante|quanti|conta(?:mi)?|list|show(?:\s+me)?|how\s+many|count)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Any of these asks for content, not for the list: «elenca le mail con le scadenze e riassumi».
    private static readonly Regex AsksContent = new(@"\b(?:riassum\w*|riepilog\w*|sintesi|sintetizz\w*|analizz\w*|spiega\w*|perch[eé]|cosa\s+(?:dic|chied|propon)\w*|che\s+cosa|quale\s+importo|importi?|scadenz\w*|punti\s+aperti|decision\w*|summari[sz]e|summary|analy[sz]e|explain|why|what\s+(?:do|does|did))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Whole = new(@"\b(?:per\s+intero|integral\w*|leggi\s+tutto|testo\s+completo|read\s+(?:them\s+)?in\s+full|full\s+text)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>«Quante mail…», «elenca le email…»: the list found locally is the whole answer, no model call.</summary>
    public static bool IsListOrCount(string request) =>
        !string.IsNullOrWhiteSpace(request) && ListOrCount.IsMatch(request) && MailNoun.IsMatch(request) && !AsksContent.IsMatch(request);

    public static bool WantsWhole(string request) => !string.IsNullOrWhiteSpace(request) && Whole.IsMatch(request);
}

public sealed class MailAnswerPoint
{
    public string Text { get; set; } = string.Empty;
    /// <summary>Email numbers as written in the citations (1 = [M1]).</summary>
    public List<int> Sources { get; set; } = new();
    /// <summary>Numbers, dates and references not found in the cited emails.</summary>
    public List<string> Unverified { get; set; } = new();
    /// <summary>Cited numbers outside the pack: the point has no valid source.</summary>
    public List<int> InvalidSources { get; set; } = new();
    /// <summary>Web results the point rests on (1 = [W1]): until 27/10/2026 «W1» was read as [M1], a web fact credited to an email.</summary>
    public List<int> WebSources { get; set; } = new();
}

public sealed class MailAnswerSection
{
    public string Title { get; set; } = string.Empty;
    public List<MailAnswerPoint> Points { get; set; } = new();
}

/// <summary>Structured final answer: sections of cited points, plus what the emails do not settle.</summary>
public sealed class MailAnswer
{
    public List<MailAnswerSection> Sections { get; set; } = new();
    public List<string> Gaps { get; set; } = new();

    public IEnumerable<MailAnswerPoint> Points => Sections.SelectMany(s => s.Points);

    private static readonly Regex Citation = new(@"\[\s*M\s*(\d{1,4})\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WebCitation = new(@"\[\s*W\s*(\d{1,4})\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static MailAnswer Parse(string json)
    {
        var answer = new MailAnswer();
        using var doc = ModelOutput.ParseObject(json, "sections");
        foreach (var s in doc.RootElement.GetProperty("sections").EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var section = new MailAnswerSection { Title = s.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? string.Empty).Trim() : string.Empty };
            if (s.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
                foreach (var p in points.EnumerateArray())
                {
                    var point = ParsePoint(p);
                    if (point != null) section.Points.Add(point);
                }
            if (section.Points.Count > 0) answer.Sections.Add(section);
        }
        if (doc.RootElement.TryGetProperty("gaps", out var gaps) && gaps.ValueKind == JsonValueKind.Array)
            foreach (var g in gaps.EnumerateArray())
                if (g.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(g.GetString())) answer.Gaps.Add(g.GetString()!.Trim());
        return answer;
    }

    internal static MailAnswerPoint? ParsePoint(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) return null;
        var text = (t.GetString() ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        var point = new MailAnswerPoint();
        if (p.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            foreach (var s in sources.EnumerateArray()) AddSource(point, s);
        else if (p.TryGetProperty("source", out var single)) AddSource(point, single);
        // Citations written in the text count as sources; the canonical list is appended when rendering.
        foreach (Match m in Citation.Matches(text)) AddNumber(point.Sources, m.Groups[1].Value);
        foreach (Match m in WebCitation.Matches(text)) AddNumber(point.WebSources, m.Groups[1].Value);
        point.Text = WebCitation.Replace(Citation.Replace(text, string.Empty), string.Empty).Replace("  ", " ").Trim();
        return point;
    }

    private static void AddSource(MailAnswerPoint point, JsonElement s)
    {
        if (s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n)) AddNumber(point.Sources, n.ToString(CultureInfo.InvariantCulture));
        else if (s.ValueKind == JsonValueKind.String)
        {
            var source = (s.GetString() ?? string.Empty).Trim().TrimStart('[');
            var target = source.StartsWith("W", StringComparison.OrdinalIgnoreCase) ? point.WebSources : point.Sources;
            AddNumber(target, Regex.Replace(source, @"[^\d]", string.Empty));
        }
    }

    private static void AddNumber(List<int> sources, string digits)
    {
        if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && !sources.Contains(n)) sources.Add(n);
    }
}

/// <summary>Table answer: one row per record, each from one email.</summary>
public sealed class MailTable
{
    public const int MaxColumns = 8;
    public List<string> Columns { get; } = new();
    public List<MailTableRow> Rows { get; } = new();

    public static MailTable Parse(string json)
    {
        var table = new MailTable();
        using var doc = ModelOutput.ParseObject(json, "columns");
        foreach (var c in doc.RootElement.GetProperty("columns").EnumerateArray())
        {
            var name = c.ValueKind == JsonValueKind.String ? (c.GetString() ?? string.Empty).Trim() : string.Empty;
            if (name.Length > 0 && !table.Columns.Contains(name, StringComparer.OrdinalIgnoreCase)) table.Columns.Add(name);
            if (table.Columns.Count == MaxColumns) break;
        }
        if (!doc.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return table;
        foreach (var r in rows.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            var row = new MailTableRow { Cells = new string[table.Columns.Count] };
            if (r.TryGetProperty("source", out var s))
            {
                var digits = s.ValueKind == JsonValueKind.Number ? s.GetRawText() : Regex.Replace(s.GetString() ?? string.Empty, @"[^\d]", string.Empty);
                if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) row.Source = n;
            }
            var cells = r.TryGetProperty("cells", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().ToList() : new List<JsonElement>();
            for (int i = 0; i < row.Cells.Length; i++)
                row.Cells[i] = i < cells.Count ? cells[i].ValueKind switch
                {
                    JsonValueKind.String => (cells[i].GetString() ?? string.Empty).Trim(),
                    JsonValueKind.Number => cells[i].GetRawText(),
                    _ => string.Empty
                } : string.Empty;
            if (row.Cells.Any(v => v.Length > 0)) table.Rows.Add(row);
        }
        return table;
    }
}

public sealed class MailTableRow
{
    public int Source { get; set; }
    public string[] Cells { get; set; } = Array.Empty<string>();
    public List<string> Unverified { get; set; } = new();
}

/// <summary>
/// Checks in code what the model may copy wrong: amounts, dates and references must appear in the cited email
/// (same normalisation as the archive facts), or the point is marked as not verified.
/// </summary>
public static class MailAnswerVerifier
{
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Dates = Pattern(@"(?<![\w/.])(?:\d{4}-\d{2}-\d{2}|\d{1,2}[/.]\d{1,2}(?:[/.]\d{2,4})?|\d{1,2}\s+(?:gennaio|febbraio|marzo|aprile|maggio|giugno|luglio|agosto|settembre|ottobre|novembre|dicembre|January|February|March|April|May|June|July|August|September|October|November|December)(?:\s+\d{4})?)(?![\w/])");
    private static readonly Regex Numbers = Pattern(@"(?<![\w.,/-])[+-]?\d{1,3}(?:[.'  ]\d{3})+(?:,\d{1,4})?(?![\w]|[.,]\d)|(?<![\w.,/-])[+-]?\d+[.,]\d{1,4}(?![\w]|[.,]\d)|(?<=(?:€|\bEUR|\beuro|\$|£)\s?)\d+(?![\w.,])|(?<![\w.,/-])\d+(?=\s?(?:€|EUR\b|euro\b))");
    private static readonly Regex Codes = Pattern(@"(?<![\w/-])(?=[A-Za-z0-9/.-]*\d)[A-Za-z0-9]+(?:[-/.][A-Za-z0-9]+)*(?![\w/-])");
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    public static void Verify(MailAnswer answer, MailEvidencePack pack)
    {
        var cache = new Dictionary<int, SourceTokens>();
        foreach (var point in answer.Points)
        {
            Check(point.Text, point.Sources, point.Unverified, point.InvalidSources, pack, cache);
            // A value the point takes from the web cannot be looked up in the emails.
            if (point.WebSources.Count > 0) point.Unverified.Clear();
        }
    }

    public static void Verify(MailTable table, MailEvidencePack pack)
    {
        var cache = new Dictionary<int, SourceTokens>();
        foreach (var row in table.Rows)
        {
            var invalid = new List<int>();
            Check(string.Join(" | ", row.Cells), new List<int> { row.Source }, row.Unverified, invalid, pack, cache);
        }
    }

    /// <summary>
    /// Calendar items of one scan request (A109): the date as written must appear in the cited email, and an explicit
    /// date there must be the one the item carries. Use the same includeDetails as the request. Relative dates
    /// are resolved by the model and checked only as text.
    /// </summary>
    public static void Verify(IEnumerable<ExtractedActionItem> items, MailEvidencePack pack, bool includeDetails = true)
    {
        var cache = new Dictionary<int, SourceTokens>();
        foreach (var item in items)
        {
            item.Unverified.Clear();
            if (item.Source < 1 || item.Source > pack.Count) { item.Unverified.Add("email di origine non indicata"); continue; }
            if (item.DateText.Length == 0) { item.Unverified.Add("data non citata dall'email"); continue; }
            if (!cache.TryGetValue(item.Source, out var tokens)) cache[item.Source] = tokens = new SourceTokens(pack.Note(item.Source - 1, includeDetails));
            if (!tokens.Contains(item.DateText)) { item.Unverified.Add($"«{item.DateText}» non trovata nell'email citata"); continue; }
            var written = Dates.Matches(item.DateText).Cast<Match>().Select(m => ArchiveFactExtractor.NormalizeDate(m.Value)).OfType<string>().ToList();
            var carried = new[] { item.Start, item.End, item.Due }.Where(d => d.HasValue).Select(d => d!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
            if (written.Count > 0 && !written.Any(carried.Contains)) item.Unverified.Add($"data diversa da «{item.DateText}»");
        }
    }

    /// <summary>The values to check in a text: dates, amounts and codes with a digit; citations and counts are left out.</summary>
    public static List<string> Values(string text)
    {
        var values = new List<(int Start, int End, string Value)>();
        bool Free(int start, int end) => !values.Any(v => start < v.End && v.Start < end);
        foreach (Match m in Dates.Matches(text)) if (Free(m.Index, m.Index + m.Length)) values.Add((m.Index, m.Index + m.Length, m.Value));
        foreach (Match m in Numbers.Matches(text)) if (Free(m.Index, m.Index + m.Length)) values.Add((m.Index, m.Index + m.Length, m.Value));
        foreach (Match m in Codes.Matches(text))
        {
            var v = m.Value.TrimEnd('.');
            bool letter = v.Any(char.IsLetter), separator = v.IndexOfAny(new[] { '-', '/' }) >= 0;
            // A plain count, a year or a citation is not a value to check; codes have letters, separators or five digits.
            if (Regex.IsMatch(v, @"^[MW]\d+$", RegexOptions.IgnoreCase) || (!letter && !separator && v.Length < 5) || (letter && v.Length < 4) || v.Count(char.IsDigit) == 0) continue;
            if (Free(m.Index, m.Index + v.Length)) values.Add((m.Index, m.Index + v.Length, v));
        }
        return values.OrderBy(v => v.Start).Select(v => v.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Check(string text, List<int> sources, List<string> unverified, List<int> invalid, MailEvidencePack pack, Dictionary<int, SourceTokens> cache)
    {
        unverified.Clear();
        invalid.Clear();
        invalid.AddRange(sources.Where(s => s < 1 || s > pack.Count));
        var cited = sources.Where(s => s >= 1 && s <= pack.Count).ToList();
        foreach (var value in Values(text))
        {
            bool found = false;
            foreach (var s in cited)
            {
                if (!cache.TryGetValue(s, out var tokens)) cache[s] = tokens = new SourceTokens(pack.Note(s - 1));
                if (tokens.Contains(value)) { found = true; break; }
            }
            if (!found) unverified.Add(value);
        }
    }

    private sealed class SourceTokens
    {
        private readonly string _text, _compact;
        private readonly HashSet<string> _dates = new(StringComparer.Ordinal), _amounts = new(StringComparer.Ordinal);

        public SourceTokens(string text)
        {
            _text = Spaces.Replace(text, " ");
            _compact = Compact(text);
            // A pattern past its 2 s timeout on a huge original (A136) only leaves its normalized values out: a value
            // not found in the text is then marked not verified, and the paid answer is kept.
            Scan(() =>
            {
                foreach (var f in ArchiveFactExtractor.Extract(text))
                {
                    if (f.Date != null) _dates.Add(f.Date);
                    if (f.Kind == ArchiveFactKind.Amount && f.Value != null) _amounts.Add(f.Value);
                }
            });
            Scan(() => { foreach (Match m in Dates.Matches(text)) if (ArchiveFactExtractor.NormalizeDate(m.Value) is { } d) _dates.Add(d); });
            Scan(() => { foreach (Match m in Numbers.Matches(text)) if (ArchiveFactExtractor.NormalizeAmount(m.Value) is { } a) _amounts.Add(a); });
        }

        private static void Scan(Action scan)
        {
            try { scan(); }
            catch (RegexMatchTimeoutException) { } // fact extraction is best effort: a slow text adds no facts
        }

        public bool Contains(string value)
        {
            if (_text.IndexOf(Spaces.Replace(value, " "), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (ArchiveFactExtractor.NormalizeDate(value) is { } date && _dates.Contains(date)) return true;
            if (Dates.IsMatch(value) && DayMonth(value) is { } partial && _dates.Any(d => d.EndsWith(partial, StringComparison.Ordinal))) return true;
            if (!Dates.IsMatch(value) && ArchiveFactExtractor.NormalizeAmount(Regex.Replace(value, @"[^\d.,'+-]", string.Empty)) is { } amount && _amounts.Contains(amount)) return true;
            // «7.769,00» for «7.769» in a tax form: a whole amount is often written without decimals.
            var whole = Regex.Match(value, @"^(.*\d)[.,]0{1,2}$", RegexOptions.CultureInvariant);
            if (whole.Success && Regex.IsMatch(_text, @"(?<![\d.,])" + Regex.Escape(whole.Groups[1].Value) + @"(?!\d|[.,]\d)", RegexOptions.CultureInvariant)) return true;
            // Codes only («ORD 2026/001» for «ORD-2026-001»): an amount must not match any run of digits.
            if (!value.Any(char.IsLetter) && value.IndexOfAny(new[] { '-', '/' }) < 0) return false;
            var compact = Compact(value);
            return compact.Length >= 4 && _compact.IndexOf(compact, StringComparison.Ordinal) >= 0;
        }

        /// <summary>«12/03» or «12 marzo» without the year: the month and day of a source date.</summary>
        private static string? DayMonth(string value)
        {
            var m = Regex.Match(value, @"^(\d{1,2})[/.](\d{1,2})$", RegexOptions.CultureInvariant);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var d) && int.TryParse(m.Groups[2].Value, out var mo) && d <= 31 && mo <= 12)
                return $"-{mo:00}-{d:00}";
            var named = ArchiveFactExtractor.NormalizeDate(value + " 2000");
            return named?.Substring(4);
        }

        private static string Compact(string text) => new string(text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }
}

/// <summary>Markdown of a verified answer for the pane and the history; coverage is stated by the code, not the model.</summary>
public static class MailAnswerRenderer
{
    public static string Render(MailAnswer answer)
    {
        var sb = new StringBuilder();
        foreach (var section in answer.Sections)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            if (section.Title.Length > 0) sb.Append("**").Append(section.Title.Trim('*', ' ')).Append("**\n\n");
            foreach (var p in section.Points) sb.Append("- ").Append(Point(p)).Append('\n');
        }
        if (answer.Gaps.Count > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("**Limiti e punti non chiariti**\n\n");
            foreach (var g in answer.Gaps) sb.Append("- ").Append(g).Append('\n');
        }
        return sb.Length == 0 ? "Le email trovate non contengono informazioni per questa richiesta." : sb.ToString().TrimEnd();
    }

    public static string Point(MailAnswerPoint p)
    {
        var valid = p.Sources.Where(s => !p.InvalidSources.Contains(s)).OrderBy(s => s).ToList();
        var cited = valid.Select(s => $"[M{s}]").Concat(p.WebSources.OrderBy(s => s).Select(s => $"[W{s}]")).ToList();
        var text = p.Text + (cited.Count > 0 ? " " + string.Concat(cited) : string.Empty);
        if (cited.Count == 0) text += " — ⚠ *senza fonte tra le email lette*";
        if (p.Unverified.Count > 0) text += " — ⚠ *non verificato nelle email citate: " + string.Join("; ", p.Unverified) + "*";
        return text;
    }

    public static int UnverifiedCount(MailAnswer answer) => answer.Points.Count(p => p.Unverified.Count > 0 || (p.WebSources.Count == 0 && p.Sources.All(p.InvalidSources.Contains)));

    /// <summary>Closing line on what the answer rests on.</summary>
    public static string Coverage(MailEvidencePack pack, MailAnswer answer)
    {
        var cited = answer.Points.SelectMany(p => p.Sources.Where(s => !p.InvalidSources.Contains(s))).Distinct().Count();
        int unverified = UnverifiedCount(answer);
        return $"Copertura: {Read(pack)}, {cited} citate nella risposta. " +
               (unverified == 0 ? "Importi, date e riferimenti citati trovati nelle email indicate." : unverified == 1 ? "1 punto non verificato nelle email indicate." : $"{unverified} punti non verificati nelle email indicate.");
    }

    public static string Coverage(MailEvidencePack pack, MailTable table)
    {
        int unverified = table.Rows.Count(r => r.Unverified.Count > 0 || r.Source < 1 || r.Source > pack.Count);
        return $"Copertura: {Read(pack)}. " +
               (unverified == 0 ? "Valori delle righe trovati nelle email indicate." : unverified == 1 ? "1 riga con valori non verificati nell'email indicata." : $"{unverified} righe con valori non verificati nelle email indicate.");
    }

    // An original not available was not read: it is declared apart, never as local facts.
    private static string Read(MailEvidencePack pack) =>
        $"{pack.Count - pack.Missing} email lette in ordine cronologico ({pack.Whole} per intero, {pack.Facts} dai fatti locali)" +
        (pack.Missing == 0 ? string.Empty : pack.Missing == 1 ? ", 1 originale non disponibile" : $", {pack.Missing} originali non disponibili");

    /// <summary>Table rows with the email of each row, plus a marker on the rows not verified.</summary>
    public static List<string[]> TableRows(MailTable table, MailEvidencePack pack) =>
        table.Rows.Select(r =>
        {
            var mail = r.Source >= 1 && r.Source <= pack.Count ? pack.Items[r.Source - 1].Mail : null;
            var email = mail == null ? "⚠ email non indicata"
                : $"[M{r.Source}] {(mail.Sent ?? mail.Received):dd/MM/yyyy} · {(mail.Subject.Length > 60 ? mail.Subject.Substring(0, 60) : mail.Subject)}";
            if (r.Unverified.Count > 0) email += " · ⚠ non verificato: " + string.Join("; ", r.Unverified);
            return r.Cells.Concat(new[] { email }).ToArray();
        }).ToList();
}
