using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace OnlyWorkspace.Core.Archive;

public enum ArchiveFactKind { Date, Deadline, Amount, Order, Invoice, Protocol, Iban, Vat, TaxCode, PecReceipt, Calendar, Party }

/// <summary>An occurrence in the stored source text. Offsets count UTF-16 code units, not PDF coordinates.</summary>
public sealed class ArchiveFact
{
    public long EmailId { get; set; }
    public string BodyVersion { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "Corpo email";
    public ArchiveFactKind Kind { get; set; }
    public string Label { get; set; } = "";
    public string Raw { get; set; } = "";
    public string? Value { get; set; }
    public string? Currency { get; set; }
    public string? Date { get; set; }
    public int Start { get; set; }
    public int Length { get; set; }
    public string Context { get; set; } = "";
    public bool Resolved { get; set; }
}

public sealed class ArchiveFactFilter
{
    public ArchiveFactKind? Kind { get; set; }
    public string? Value { get; set; }
    public decimal? AmountMin { get; set; }
    public decimal? AmountMax { get; set; }
    public string? Currency { get; set; }
    /// <summary>Inclusive calendar dates, yyyy-MM-dd; unrelated to the email's received date.</summary>
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }
}

public sealed class ArchiveFactProgress
{
    public int Done { get; set; }
    public int Pending { get; set; }
    public int Failed { get; set; }
}

/// <summary>Pure local extraction: a failed/expired pass never represents complete coverage.</summary>
public static partial class ArchiveFactExtractor
{
    public const int Revision = 3;
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    private static readonly Regex Dates = Pattern(@"(?<![\w/])(?:\d{4}-\d{2}-\d{2}|\d{1,2}[/.]\d{1,2}(?:[/.]\d{4})?|\d{1,2}\s+(?:gennaio|febbraio|marzo|aprile|maggio|giugno|luglio|agosto|settembre|ottobre|novembre|dicembre|January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{4}|(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4})(?![\w/])");
    private static readonly Regex Deadlines = Pattern(@"\b(?:entro|(?:data\s+(?:di\s+)?)?scadenza(?:\s+(?:pagamento|fattura))?|scade|pagare\s+entro|due(?:\s+date)?|deadline|pay\s+by|no\s+later\s+than|by)\s*(?::|il|on)?\s*$");
    // Try the complete labelled money expression before the label-only alternative. Otherwise
    // the earlier match at "Importo" consumes the number before the trailing currency is visited.
    private static readonly Regex Amounts = Pattern(@"(?<![\w.,])(?:(?:importo|totale|amount|total)\s*:?\s*)?(?:(?<currency>EUR|USD|GBP|CHF|€|\$|£)\s*(?<number>[+-]?\d+(?:[.,'\u00a0 ]\d+)*[.,]\d{1,4}|[+-]?\d+)(?!\w)(?![.,]\d)|(?<number>[+-]?\d+(?:[.,'\u00a0 ]\d+)*[.,]\d{1,4}|[+-]?\d+)\s*(?<currency>EUR|USD|GBP|CHF|€|\$|£)(?!\w))|\b(?:importo|totale|amount|total)\s*:?\s*(?<number>[+-]?\d+(?:[.,'\u00a0 ]\d+)*[.,]\d{1,4}|[+-]?\d+)(?![\w.,])");
    private static readonly Regex References = Pattern(@"\b(?<label>ordine|order|fattura|invoice|protocollo|protocol)\s*(?:(?:n(?:umero)?\.?|no\.?|number|nr\.?)\s*)?[:#-]?\s*(?<value>(?=[\w/-]*\d)[\w]+(?:[-/][\w]+)*)(?!\w)");
    private static readonly Regex Ibans = Pattern(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]){11,30}\b");
    private static readonly Regex Fiscal = Pattern(@"\b(?<label>P\.?\s*IVA|partita\s+IVA|VAT|C\.?F\.?|codice\s+fiscale|tax\s+code)\s*:?\s*(?<value>[A-Z0-9]{11,16})\b");
    private static readonly Regex Parties = Pattern(@"\b(?<label>mittente|destinatario|fornitore|cliente|sender|recipient|supplier|customer)\s*:\s*(?<value>[^\r\n;<>]{2,160})");
    private static readonly Regex Receipts = Pattern(@"\b(?:ricevuta\s+di\s+(?:accettazione|avvenuta\s+consegna)|avviso\s+di\s+(?:mancata\s+consegna|non\s+accettazione))\b");
    private static readonly string[] DateFormats = { "yyyy-MM-dd", "d MMMM yyyy", "MMMM d yyyy", "MMMM d, yyyy" };

    public static List<ArchiveFact> Extract(string text, string sourceId = "", string sourceName = "Corpo email", CancellationToken ct = default)
    {
        var facts = new List<ArchiveFact>();
        void Add(ArchiveFactKind kind, Match match, string? value, string label = "", string? currency = null, string? date = null) =>
            facts.Add(Occurrence(text, sourceId, sourceName, kind, match.Index, match.Length, value, label, currency, date));
        foreach (Match m in Dates.Matches(text))
        {
            ct.ThrowIfCancellationRequested();
            string? date = NormalizeDate(m.Value);
            string prefix = text.Substring(Math.Max(0, m.Index - 55), Math.Min(55, m.Index));
            Add(Deadlines.IsMatch(prefix) ? ArchiveFactKind.Deadline : ArchiveFactKind.Date, m, date, date: date);
        }
        foreach (Match m in Amounts.Matches(text))
        {
            ct.ThrowIfCancellationRequested();
            string currency = m.Groups["currency"].Value.ToUpperInvariant();
            currency = currency == "€" ? "EUR" : currency == "£" ? "GBP" : currency == "$" ? "" : currency;
            Add(ArchiveFactKind.Amount, m, NormalizeAmount(m.Groups["number"].Value), currency: currency.Length == 0 ? null : currency);
        }
        foreach (Match m in References.Matches(text))
        {
            ct.ThrowIfCancellationRequested();
            string label = m.Groups["label"].Value.ToLowerInvariant();
            Add(label == "ordine" || label == "order" ? ArchiveFactKind.Order : label == "fattura" || label == "invoice" ? ArchiveFactKind.Invoice : ArchiveFactKind.Protocol, m, m.Groups["value"].Value.ToUpperInvariant());
        }
        foreach (Match m in Ibans.Matches(text))
        {
            ct.ThrowIfCancellationRequested();
            string value = m.Value.Replace(" ", "").ToUpperInvariant();
            // A greedy spaced match may include trailing prose: accept the longest valid IBAN prefix only.
            int length = IbanLength(value.Substring(0, 2));
            if (length > 0 && value.Length >= length)
            {
                int end = m.Index, letters = 0;
                while (end < m.Index + m.Length && letters < length) if (text[end++] != ' ') letters++;
                bool boundary = end == text.Length || !char.IsLetterOrDigit(text[end]);
                facts.Add(Occurrence(text, sourceId, sourceName, ArchiveFactKind.Iban, m.Index, end - m.Index,
                    boundary && ValidIban(value.Substring(0, length)) ? value.Substring(0, length) : null));
            }
            else facts.Add(Occurrence(text, sourceId, sourceName, ArchiveFactKind.Iban, m.Index, m.Length,
                length == 0 && value.Length >= 15 && value.Length <= 34 && ValidIban(value) ? value : null, "checksum; formato nazionale non verificato"));
        }
        foreach (Match m in Fiscal.Matches(text))
        {
            ct.ThrowIfCancellationRequested();
            string value = m.Groups["value"].Value.ToUpperInvariant();
            bool vat = m.Groups["label"].Value.IndexOf("iva", StringComparison.OrdinalIgnoreCase) >= 0 || m.Groups["label"].Value.Equals("vat", StringComparison.OrdinalIgnoreCase);
            Add(vat ? ArchiveFactKind.Vat : ArchiveFactKind.TaxCode, m, (vat ? ValidVat(value) : ValidTaxCode(value)) ? value : null);
        }
        foreach (Match m in Parties.Matches(text)) { ct.ThrowIfCancellationRequested(); Add(ArchiveFactKind.Party, m, m.Groups["value"].Value.Trim(), m.Groups["label"].Value.ToLowerInvariant()); }
        foreach (Match m in Receipts.Matches(text)) { ct.ThrowIfCancellationRequested(); Add(ArchiveFactKind.PecReceipt, m, m.Value.ToLowerInvariant()); }
        ExtractStructured(text, sourceId, sourceName, facts, ct);
        return facts.OrderBy(f => f.Start).ThenBy(f => f.Kind).ToList();
    }

    private static ArchiveFact Occurrence(string text, string sourceId, string name, ArchiveFactKind kind, int start, int length, string? value, string label = "", string? currency = null, string? date = null)
    {
        int first = text.LastIndexOf('\n', Math.Max(0, start - 1)); first = first < 0 ? 0 : first + 1;
        int last = text.IndexOf('\n', start + length); if (last < 0) last = text.Length;
        first = Math.Max(first, start - 120); last = Math.Min(last, start + length + 120);
        return new ArchiveFact { SourceId = sourceId, SourceName = name, Kind = kind, Raw = text.Substring(start, length), Value = value,
            Label = label, Currency = currency, Date = date, Start = start, Length = length, Resolved = value != null, Context = text.Substring(first, last - first) };
    }

    public static string? NormalizeAmount(string raw)
    {
        string s = raw.Replace("'", "").Replace(" ", "").Replace("\u00a0", "");
        int dot = s.LastIndexOf('.'), comma = s.LastIndexOf(',');
        if (dot >= 0 && comma >= 0)
        {
            char dec = dot > comma ? '.' : ',';
            string integer = s.Substring(0, Math.Max(dot, comma));
            char group = dec == '.' ? ',' : '.';
            if (!ValidGrouping(integer, group)) return null;
            s = integer.Replace(group.ToString(), "") + "." + s.Substring(Math.Max(dot, comma) + 1);
        }
        else if (dot >= 0 || comma >= 0)
        {
            int pos = Math.Max(dot, comma); char sep = s[pos];
            // Three trailing digits alone are ambiguous (1.000 / 1,000).
            if (s.Length - pos - 1 == 3) return null;
            if (s.IndexOf(sep) != pos) return null;
            s = s.Replace(',', '.');
        }
        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return null;
        string normalized = amount.ToString("0.############################", CultureInfo.InvariantCulture);
        string unsigned = s.TrimStart('+', '-'); var digits = unsigned.Split('.');
        string whole = digits[0].TrimStart('0'); if (whole.Length == 0) whole = "0";
        string fraction = digits.Length == 2 ? digits[1].TrimEnd('0') : "";
        string exact = whole + (fraction.Length > 0 ? "." + fraction : "");
        if (s.StartsWith("-", StringComparison.Ordinal) && exact != "0") exact = "-" + exact;
        return normalized == exact ? normalized : null;
    }

    private static bool ValidGrouping(string s, char separator)
    {
        var groups = s.TrimStart('+', '-').Split(separator);
        return groups[0].Length >= 1 && groups[0].Length <= 3 && groups.All(g => g.All(char.IsDigit)) && groups.Skip(1).All(g => g.Length == 3);
    }

    public static string? NormalizeDate(string raw)
    {
        DateTime date;
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("it-IT"), CultureInfo.GetCultureInfo("en-US") })
            if (DateTime.TryParseExact(raw, DateFormats, culture, DateTimeStyles.None, out date)) return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var parts = raw.Split('/', '.');
        if (parts.Length != 3 || parts[2].Length != 4 || !int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b)) return null;
        if (a <= 12 && b <= 12 && a != b) return null;
        int day = a > 12 ? a : b, month = a > 12 ? b : a;
        return DateTime.TryParseExact(parts[2] + "-" + month.ToString("00") + "-" + day.ToString("00"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    private static int IbanLength(string country) => country switch { "IT" => 27, "GB" => 22, "DE" => 22, "FR" => 27, "ES" => 24, "CH" => 21, "NL" => 18, "BE" => 16, "AT" => 20, "IE" => 22, "PT" => 25, _ => 0 };
    private static bool ValidIban(string value)
    {
        int rem = 0;
        foreach (char c in value.Substring(4) + value.Substring(0, 4))
        {
            if (c >= '0' && c <= '9') rem = (rem * 10 + c - '0') % 97;
            else if (c >= 'A' && c <= 'Z') rem = (rem * 100 + c - 'A' + 10) % 97;
            else return false;
        }
        return rem == 1;
    }
    private static bool ValidVat(string value)
    {
        if (value.Length != 11 || !value.All(c => c >= '0' && c <= '9')) return false;
        int sum = 0;
        for (int i = 0; i < 10; i++) { int n = value[i] - '0'; if (i % 2 == 1) { n *= 2; if (n > 9) n -= 9; } sum += n; }
        return (10 - sum % 10) % 10 == value[10] - '0' && value.Any(c => c != '0');
    }
    private static bool ValidTaxCode(string value)
    {
        if (value.Length == 11) return ValidVat(value);
        if (!Regex.IsMatch(value, @"^[A-Z]{6}[0-9LMNPQRSTUV]{2}[ABCDEHLMPRST][0-9LMNPQRSTUV]{2}[A-Z][0-9LMNPQRSTUV]{3}[A-Z]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
        int sum = 0; int[] odd = { 1,0,5,7,9,13,15,17,19,21,2,4,18,20,11,3,6,8,12,14,16,10,22,25,24,23 };
        for (int i = 0; i < 15; i++) { int n = value[i] >= 'A' ? value[i] - 'A' : value[i] - '0'; sum += i % 2 == 0 ? odd[n] : n; }
        return value[15] == 'A' + sum % 26;
    }
}
