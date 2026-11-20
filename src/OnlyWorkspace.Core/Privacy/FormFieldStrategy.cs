using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>Finds personal data next to explicit labels, including values aligned under PDF form columns.</summary>
internal sealed class FormFieldStrategy : IPseudonymizationStrategy
{
    private const string NameLabel = @"(?:cognome[ \t]+e[ \t]+nome|nome[ \t]+e[ \t]+cognome|cognome|nome)";
    private const string DateLabel = @"(?:data[ \t]+(?:di[ \t]+)?nascita|nat[oa][ \t]+il|data)";
    private static readonly Regex Field = PseudonymizationCatalog.Regex(
        $@"\A(?:(?<person>(?i:{NameLabel}))|(?<date>(?i:{DateLabel})))(?:[ \t]*:[ \t]*|\z)");
    private static readonly Regex Cells = PseudonymizationCatalog.Regex(@"\S+(?: \S+)*");
    private static readonly Regex Lines = PseudonymizationCatalog.Regex(@"[^\r\n]+");
    private static readonly Regex Name = PseudonymizationCatalog.Regex(
        @"\A\p{L}[\p{L}\p{M}'’\-]*(?: \p{L}[\p{L}\p{M}'’\-]*){0,4}\z");
    private static readonly Regex Date = PseudonymizationCatalog.Regex(
        @"\A(?:(?:0?[1-9]|[12][0-9]|3[01])[ \t]+(?:0?[1-9]|1[0-2])[ \t]+(?:19|20)[0-9]{2}|" +
        @"(?:0[ \t]+[1-9]|[12][ \t]+[0-9]|3[ \t]+[01])[ \t]+(?:0[ \t]+[1-9]|1[ \t]+[0-2])[ \t]+" +
        @"(?:1[ \t]+9|2[ \t]+0)[ \t]+[0-9][ \t]+[0-9])(?=\z|[ \t])");
    private static readonly Regex BoxNumber = PseudonymizationCatalog.Regex(@"\A[0-9]{1,4}\z");
    private const int AlignmentTolerance = 3;

    public FormFieldStrategy(string revision) => Revision = revision;
    public string Id => "form-fields";
    public string Revision { get; }
    public int Priority => 81;

    public IEnumerable<PseudonymMatch> Detect(string text, PseudonymizationContext context)
    {
        var rows = Lines.Matches(text).Cast<Match>().ToArray();
        for (int i = 0; i < rows.Length; i++)
        {
            context.Check();
            var row = rows[i];
            var cells = Cells.Matches(row.Value).Cast<Match>().ToArray();
            for (int column = 0; column < cells.Length; column++)
            {
                var label = cells[column];
                var field = Field.Match(label.Value);
                if (!field.Success) continue;
                bool isDate = field.Groups["date"].Success;
                if (isDate && !context.MaskDates) continue;

                if (field.Length < label.Length)
                {
                    var value = MatchValue(row, label.Index + field.Length, label.Index + label.Length, isDate);
                    if (value != null) yield return value;
                    continue;
                }

                if (label.Value.EndsWith(":", StringComparison.Ordinal) && column + 1 < cells.Length)
                {
                    var inline = cells[column + 1];
                    var value = MatchValue(row, inline.Index, inline.Index + inline.Length, isDate);
                    if (value != null)
                    {
                        yield return value;
                        continue;
                    }
                }

                int right = column + 1 < cells.Length ? cells[column + 1].Index : int.MaxValue;
                int next = i + 1;
                if (next >= rows.Length || !Adjacent(text, row, rows[next])) continue;
                var candidate = AlignedCell(rows[next], label.Index, right);
                if (candidate == null) continue;
                var match = MatchCell(rows[next], candidate, right, isDate);
                if (match == null && BoxNumber.IsMatch(candidate.Value) && next + 1 < rows.Length &&
                    Adjacent(text, rows[next], rows[next + 1]) && AlignedCell(rows[next + 1], label.Index, right) is { } afterNumber)
                    match = MatchCell(rows[next + 1], afterNumber, right, isDate);
                if (match != null) yield return match;
            }
        }
    }

    // A blank line or a page marker ends the form row. CRLF counts as a single line break.
    private static bool Adjacent(string text, Match first, Match second)
    {
        int end = first.Index + first.Length;
        int gap = second.Index - end;
        return gap == 1 || (gap == 2 && text[end] == '\r' && text[end + 1] == '\n');
    }

    private static Match? AlignedCell(Match row, int left, int right) =>
        Cells.Matches(row.Value).Cast<Match>()
            .FirstOrDefault(cell => Math.Abs(cell.Index - left) <= AlignmentTolerance && cell.Index + cell.Length <= right);

    private static PseudonymMatch? MatchCell(Match row, Match cell, int right, bool isDate) =>
        MatchValue(row, cell.Index, isDate ? Math.Min(right, row.Length) : cell.Index + cell.Length, isDate);

    private static PseudonymMatch? MatchValue(Match row, int start, int end, bool isDate)
    {
        string value = row.Value.Substring(start, end - start).TrimEnd();
        if (isDate)
        {
            var date = Date.Match(value);
            return date.Success ? new PseudonymMatch(row.Index + start, date.Length, "DATA") : null;
        }

        return Name.IsMatch(value) && !Field.IsMatch(value)
            ? new PseudonymMatch(row.Index + start, value.Length, "PERSONA") : null;
    }
}
