using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace OnlyWorkspace.Core.Archive;

public static partial class ArchiveFactExtractor
{
    private static readonly Regex PecBlocks = Pattern(@"<postacert\b[\s\S]*?</postacert\s*>");
    private static readonly Regex PecFields = Pattern(@"<(?<tag>mittente|destinatari|identificativo|msgid|gestore-emittente|giorno|ora)\b[^>]*>(?<value>[^<]*)</\k<tag>\s*>");
    private sealed class CalendarLine
    {
        internal int Start, Length;
        internal string Text = "";
    }

    private static List<CalendarLine> CalendarLines(string text)
    {
        var lines = new List<CalendarLine>();
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
            string line = text.Substring(start, end - start).TrimEnd('\r');
            if ((line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal)) && lines.Count > 0)
            {
                var previous = lines[lines.Count - 1]; previous.Text += line.Substring(1); previous.Length = end - previous.Start;
            }
            else lines.Add(new CalendarLine { Start = start, Length = end - start - (end > start && text[end - 1] == '\r' ? 1 : 0), Text = line });
            start = end + 1;
        }
        return lines;
    }

    /// <summary>Structural validation for the isolated reader; never resolves URLs or executes calendar actions.</summary>
    public static bool IsCalendar(string text)
    {
        var stack = new Stack<string>(); bool calendar = false, item = false;
        foreach (var line in CalendarLines(text))
        {
            if (line.Text.Length == 0) continue;
            int colon = PropertyColon(line.Text); if (colon < 1) return false;
            string name = line.Text.Substring(0, colon).ToUpperInvariant(), value = line.Text.Substring(colon + 1).ToUpperInvariant();
            if (name == "BEGIN")
            {
                if (stack.Count == 0 && value != "VCALENDAR") return false;
                if (stack.Count == 0 && calendar) return false;
                if (value == "VCALENDAR") calendar = true;
                if (value == "VEVENT" || value == "VTODO") item = true;
                stack.Push(value);
            }
            else if (name == "END") { if (stack.Count == 0 || stack.Pop() != value) return false; }
            else if (stack.Count == 0) return false;
        }
        return calendar && item && stack.Count == 0;
    }

    private static int PropertyColon(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++) { if (line[i] == '"') quoted = !quoted; else if (line[i] == ':' && !quoted) return i; }
        return -1;
    }

    private static void ExtractStructured(string text, string sourceId, string name, List<ArchiveFact> facts, CancellationToken ct)
    {
        var stack = new Stack<string>();
        foreach (var line in CalendarLines(text))
        {
            ct.ThrowIfCancellationRequested();
            int colon = PropertyColon(line.Text); if (colon < 1) continue;
            string property = line.Text.Substring(0, colon), value = line.Text.Substring(colon + 1);
            string key = property.Split(';')[0].ToUpperInvariant();
            if (key == "BEGIN") { stack.Push(value.ToUpperInvariant()); continue; }
            if (key == "END") { if (stack.Count > 0) stack.Pop(); continue; }
            if (stack.Count == 0 || (stack.Peek() != "VEVENT" && stack.Peek() != "VTODO")) continue;
            if (!(new[] { "UID", "SUMMARY", "DTSTART", "DTEND", "DUE", "ORGANIZER", "ATTENDEE", "RRULE", "RECURRENCE-ID", "STATUS", "SEQUENCE", "LOCATION" }).Contains(key)) continue;
            string? normalized = value;
            string? date = null;
            if (key == "DTSTART" || key == "DTEND" || key == "DUE" || key == "RECURRENCE-ID")
            {
                string format = value.Length == 8 ? "yyyyMMdd" : value.EndsWith("Z", StringComparison.Ordinal) ? "yyyyMMdd'T'HHmmss'Z'" : "yyyyMMdd'T'HHmmss";
                if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    date = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    normalized = value.Length == 8 ? date : parsed.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (value.EndsWith("Z", StringComparison.Ordinal) ? "Z" : "");
                    var zone = Regex.Match(property, "(?:^|;)TZID=(\"[^\"]*\"|[^;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    bool dateType = Regex.IsMatch(property, @"(?:^|;)VALUE=DATE(?:;|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    bool invalid = dateType != (value.Length == 8) || (zone.Success && (value.Length == 8 || value.EndsWith("Z", StringComparison.Ordinal)));
                    if (invalid) { normalized = null; date = null; }
                    else if (zone.Success) normalized += ";TZID=" + zone.Groups[1].Value.Trim('"');
                    else if (value.Length != 8 && !value.EndsWith("Z", StringComparison.Ordinal)) normalized += ";floating";
                }
                else normalized = null;
            }
            facts.Add(Occurrence(text, sourceId, name, ArchiveFactKind.Calendar, line.Start, line.Length, normalized, key, date: date));
            if (key == "ORGANIZER" || key == "ATTENDEE") facts.Add(Occurrence(text, sourceId, name, ArchiveFactKind.Party, line.Start, line.Length, value, key));
            if (key == "DUE") facts.Add(Occurrence(text, sourceId, name, ArchiveFactKind.Deadline, line.Start, line.Length, normalized == null ? null : date, "DUE", date: normalized == null ? null : date));
        }

        // Keep the lexical XML unchanged: XML reader line coordinates are not offsets into a normalized document.
        if (text.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0) return;
        foreach (Match block in PecBlocks.Matches(text))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var reader = XmlReader.Create(new StringReader(block.Value), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
                string? type = null;
                while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "postacert") type = reader.GetAttribute("tipo");
                if (type != null)
                    facts.Add(Occurrence(text, sourceId, name, ArchiveFactKind.PecReceipt, block.Index, block.Length, type, "postacert.tipo"));
                foreach (Match field in PecFields.Matches(block.Value))
                {
                    string label = field.Groups["tag"].Value.ToLowerInvariant();
                    facts.Add(Occurrence(text, sourceId, name, label == "mittente" || label == "destinatari" ? ArchiveFactKind.Party : ArchiveFactKind.PecReceipt,
                        block.Index + field.Index, field.Length, System.Net.WebUtility.HtmlDecode(field.Groups["value"].Value), label));
                }
            }
            catch (XmlException) { /* Invalid/untrusted XML is not certification evidence. */ }
        }
    }
}
