using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using OpenMcdf;
using OnlyWorkspace.Core.Security;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Util;
using A = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace OnlyWorkspace.AttachmentExtractor;

/// <summary>Plain text + macro presence for PDF, OOXML (Word/Excel/PowerPoint), attached emails (.msg, .eml), signed .p7m and legacy OLE files.</summary>
internal static partial class ContentReader
{
    // Each extraction runs in its own process; recursive email/signed-document reads share this mode.
    public static bool ArchiveMode { get; set; }
    /// <param name="depth">0 for the file given to the helper, +1 for each email it is nested in.</param>
    public static AttachmentExtraction Read(string path, int maxChars, int depth = 0, AttachmentExtraction? result = null)
    {
        if (depth == 0) _expandedBytes = 0;
        if (depth > MaxMsgDepth) return new AttachmentExtraction { Problem = "depth-limit", Truncated = true };
        var header = new byte[FileSignature.HeaderLength];
        int read;
        using (var fs = File.OpenRead(path)) read = fs.Read(header, 0, header.Length);

        var type = FileSignature.Detect(header, read);
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var text = new LimitedText(maxChars);
        result ??= new AttachmentExtraction();
        try
        {
            if (ArchiveMode && AttachmentTextFormats.ArchiveReferenceReason(ext, type) is { } reference)
            {
                result.Format = type ?? ext;
                result.Problem = reference;
                return result;
            }
            if (type == null && ext == "p7m") return ReadSigned(path, maxChars, depth);

            switch (type)
            {
                case "pdf":
                    result.Format = "pdf";
                    if (maxChars > 0) result.Problem = ReadPdf(path, text);
                    break;
                case "zip" when ext is "odt" or "ods" or "odp" or "ott" or "ots" or "otp":
                    result.Format = "odf";
                    if (maxChars > 0) ReadOpenDocument(path, text);
                    break;
                case "png" or "jpeg" or "gif":
                case null when ext is "tif" or "tiff" or "bmp":
                    result.Format = "image";
                    if (maxChars > 0) result.Problem = ReadImage(path, text);
                    break;
                case "rtf":
                    result.Format = "rtf";
                    if (maxChars > 0) text.AppendLine(RtfToText(path));
                    break;
                case null when ext == "eml":
                    result.Format = "eml";
                    using (var message = MimeKit.MimeMessage.Load(path))
                    {
                        result.HasMacros = ReadEml(message, Path.GetDirectoryName(path)!, text, depth);
                    }
                    break;
                case null when ext is "html" or "htm":
                    result.Format = "html";
                    if (maxChars > 0) text.AppendLine(HtmlToText(ReadTextFile(path)));
                    break;
                case null when ext is "xls" or "xlt":
                    result.Format = "xls";
                    if (maxChars > 0) result.Problem = ReadBinaryExcel(path, text);
                    break;
                case null when TextExtensions.Contains(ext):
                    result.Format = "text";
                    if (maxChars > 0)
                    {
                        var plain = ReadTextFile(path);
                        if (plain == null) result.Problem = "unsupported"; // binary content behind a text extension
                        else if (ext == "ics" && !OnlyWorkspace.Core.Archive.ArchiveFactExtractor.IsCalendar(plain)) result.Problem = "malformed-calendar";
                        else foreach (var line in plain.Split('\n')) if (!text.AppendLine(line)) break;
                    }
                    break;
                case "zip" when ext.StartsWith("doc") || ext.StartsWith("dot"):
                    result.Format = "docx";
                    result.HasMacros = ReadWord(path, text);
                    break;
                case "zip" when ext.StartsWith("xls") || ext.StartsWith("xlt") || ext == "xlam":
                    result.Format = "xlsx";
                    result.HasMacros = ReadExcel(path, text);
                    break;
                case "zip" when ext.StartsWith("ppt") || ext.StartsWith("pot") || ext.StartsWith("pps") || ext == "ppam":
                    result.Format = "pptx";
                    result.HasMacros = ReadPowerPoint(path, text);
                    break;
                case "ole":
                    using (var root = RootStorage.OpenRead(path))
                    {
                        if (root.ContainsEntry(MsgProperties))
                        {
                            result.Format = "msg";
                            result.HasMacros = ReadMessage(root, topLevel: true, Path.GetDirectoryName(path)!, text, depth);
                        }
                        else
                        {
                            // Legacy binary formats: only the VBA check, no text parser.
                            result.Format = "ole";
                            result.HasMacros = ContainsVbaStorage(root, depth: 0);
                            if (maxChars > 0 && root.ContainsEntry("WordDocument")) result.Problem = ReadBinaryWord(root, text);
                            else if (maxChars > 0 && (root.ContainsEntry("Workbook") || root.ContainsEntry("Book"))) result.Problem = ReadBinaryExcel(path, text);
                            else result.Problem = "unsupported";
                        }
                    }
                    break;
                case "zip":
                    result.Format = "zip";
                    result.HasMacros = ReadZip(path, text, depth);
                    break;
                default:
                    result.Problem = "unsupported";
                    break;
            }
        }
        finally
        {
            // Inspect retains known format/macros even if this static read fails.
            _inspection?.Visit(path, type, ext);
        }

        result.Problem ??= text.Problem;
        result.Text = text.ToString();
        result.Truncated = text.Truncated;
        return result;
    }

    private static readonly System.Collections.Generic.HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "csv", "tsv", "md", "log", "json", "xml", "yaml", "yml", "ini", "ics",
        "vcf", "sql", "srt", "cfg", "conf", "properties", "tex", "rst", "kml", "gpx"
    };

    // Apply to each nonblank page: a text page must never conceal an unread scan elsewhere.
    private const int MinCharsPerTextPage = 25;

    /// <returns>null when text was read, else "scanned", "ocr-off", "ocr-unavailable" or "protected".</returns>
    private static string? ReadPdf(string path, LimitedText text)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(path);
        }
        catch (Exception ex) when (ex.GetType().Name.IndexOf("Encrypt", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "protected";
        }

        // Text layer first; a nonblank page under the threshold is recognized whole with OCR (E8).
        var layout = new List<List<string>?>();
        using (document)
        {
            foreach (var page in document.GetPages())
            {
                int letters = page.Letters.Sum(l => l.Value.Count(char.IsLetterOrDigit));
                bool blank = page.Letters.All(l => string.IsNullOrWhiteSpace(l.Value)) &&
                    page.NumberOfImages == 0 && page.Paths.Count == 0;
                layout.Add(!blank && letters < MinCharsPerTextPage ? null : PageLayoutLines(page).ToList());
            }
        }
        var recognized = new Dictionary<int, List<string>>();
        var scans = Enumerable.Range(1, layout.Count).Where(n => layout[n - 1] == null).ToList();
        if (scans.Count > 0 && RecognizePdfPages(path, scans, recognized) is { } problem)
        {
            text.Clear();
            return problem;
        }

        for (int number = 1; number <= layout.Count; number++)
        {
            bool more = number == 1 || text.AppendLayoutLine($"--- pagina {number} ---");
            var lines = layout[number - 1] ?? new[] { OcrMarker }.Concat(recognized[number]).ToList();
            foreach (var line in lines)
            {
                if (!more) break;
                more = text.AppendLayoutLine(line);
            }
            if (!more) break;
        }
        return null;
    }

    /// <summary>
    /// Page text as it lies on the page: words grouped into lines by height and placed at their horizontal
    /// position, so a value stays under or beside its label and table cells keep their columns.
    /// Page.Text follows the file's internal order instead: a filled-in form (tax certificate, invoice template)
    /// lists every label first and every value last, glued together, and no reader can pair them again.
    /// </summary>
    private static IEnumerable<string> PageLayoutLines(Page page)
    {
        // Side notes printed vertically along the margin would scatter single letters over the grid: read apart, last.
        var horizontal = page.Letters.Where(l => l.TextOrientation == TextOrientation.Horizontal).ToList();
        var rotated = string.Concat(page.Letters.Where(l => l.TextOrientation != TextOrientation.Horizontal).Select(l => l.Value)).Trim();

        List<Word> words;
        try
        {
            // The default extractor keeps each drawn string as it is (numbers with their separators in place).
            words = DefaultWordExtractor.Instance.GetWords(horizontal).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        }
        catch (Exception)
        {
            words = new List<Word>();
        }
        if (words.Count == 0)
        {
            // Odd fonts or no usable geometry: the plain content-order text is better than nothing.
            return new[] { page.Text };
        }

        double charWidth = Median(words.Where(w => w.Text.Length > 1).Select(w => w.BoundingBox.Width / w.Text.Length));
        double lineHeight = Median(words.Select(w => w.BoundingBox.Height));
        if (charWidth <= 0) charWidth = Math.Max(1, lineHeight / 2);
        double tolerance = Math.Max(1, lineHeight * 0.5);
        double left = words.Min(w => w.BoundingBox.Left);

        var rows = new List<List<Word>>();
        double rowBottom = 0;
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            if (rows.Count == 0 || rowBottom - word.BoundingBox.Bottom > tolerance)
            {
                rows.Add(new List<Word>());
                rowBottom = word.BoundingBox.Bottom;
            }
            rows[rows.Count - 1].Add(word);
        }
        foreach (var row in rows) row.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));

        var lines = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            lines.Add(RenderLine(rows[i], left, charWidth));
            var pairs = PairValuesWithLabels(rows, i, charWidth);
            if (pairs != null) lines.Add("    → " + pairs);
        }
        if (rotated.Length > 0) lines.Add(rotated);
        return lines;
    }

    private static readonly System.Text.RegularExpressions.Regex ValueLike = new(@"^[€+\-]?\d[\d.,/\-:%]*$");
    private static readonly System.Text.RegularExpressions.Regex BoxNumber = new(@"^\d{1,4}$");

    /// <summary>
    /// Forms and tables print a row of bare values under their labels (often with a row of box numbers in between).
    /// The grid shows it, but small models misread wide grids: this spells the pairing out, "361 Imposta lorda: 1.884,83".
    /// Only for rows made of numbers and dates; null when the row is not one or nothing sits above it.
    /// </summary>
    private static string? PairValuesWithLabels(List<List<Word>> rows, int index, double charWidth)
    {
        var row = rows[index];
        // A value row: numbers and dates only, with at least one amount or date (bare integers alone are box numbers).
        if (index == 0 || row.Count > 12 || !row.All(w => ValueLike.IsMatch(w.Text)) || !row.Any(w => AmountOrDate.IsMatch(w.Text))) return null;
        // Box numbers of the row just above, unless that is a value row too (a table of figures).
        var above = rows[index - 1];
        var numbers = above.Any(w => AmountOrDate.IsMatch(w.Text)) ? new List<Word>() : above.Where(w => BoxNumber.IsMatch(w.Text)).ToList();

        var parts = new List<string>();
        // Amounts and dates only: bare integers in such rows (codes, day counts) pair less reliably.
        foreach (var value in row.Where(w => AmountOrDate.IsMatch(w.Text)))
        {
            var box = value.BoundingBox;
            var sb = new StringBuilder();
            // The box number sits at the left edge of its box: the last one not beyond the value.
            var number = numbers.LastOrDefault(n => n.BoundingBox.Left <= box.Left + charWidth);
            if (number != null) sb.Append(number.Text).Append(' ');

            // Nearest row above with a label over this value; the row above that one completes labels on two lines.
            for (int i = index - 1; i >= 0 && i >= index - MaxLabelRows; i--)
            {
                var label = Cells(rows[i], charWidth).OrderBy(c => c.DistanceTo(box.Left, box.Right)).FirstOrDefault();
                if (label == null || label.DistanceTo(box.Left, box.Right) > MaxLabelDistance * charWidth) continue;
                var first = i > 0 ? Cells(rows[i - 1], charWidth).FirstOrDefault(c => c.DistanceTo(label.Left, label.Right) == 0) : null;
                if (first != null) sb.Append(first.Text).Append(' ');
                sb.Append(label.Text);
                break;
            }
            if (sb.Length == 0) continue;
            parts.Add(sb.ToString().TrimEnd() + ": " + value.Text);
        }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static readonly System.Text.RegularExpressions.Regex AmountOrDate = new(@"\d[.,]\d|^\d{1,2}[/\-]\d{1,2}[/\-]\d{2,4}$");
    private const double MaxLabelDistance = 12;
    private const int MaxLabelRows = 5;

    /// <summary>Words of a row that run together (label of one box, cell of one column).</summary>
    private sealed class Cell
    {
        public string Text = string.Empty;
        public double Left;
        public double Right;

        /// <summary>Horizontal gap to a span; 0 when they overlap.</summary>
        public double DistanceTo(double left, double right) => Math.Max(0, Math.Max(Left - right, left - Right));
    }

    private static List<Cell> Cells(List<Word> row, double charWidth)
    {
        var cells = new List<Cell>();
        foreach (var word in row)
        {
            var last = cells.Count > 0 ? cells[cells.Count - 1] : null;
            if (last != null && word.BoundingBox.Left - last.Right <= 2.5 * charWidth)
            {
                last.Text += " " + word.Text;
                last.Right = word.BoundingBox.Right;
            }
            else
            {
                cells.Add(new Cell { Text = word.Text, Left = word.BoundingBox.Left, Right = word.BoundingBox.Right });
            }
        }
        // Box numbers and stray digits are not labels.
        cells.RemoveAll(c => !c.Text.Any(char.IsLetter));
        return cells;
    }

    /// <summary>
    /// Words of one line at their column (monospace grid of the page's usual character width), at least one space
    /// apart. The words of a phrase get one space: the font is proportional and the grid would spread them unevenly.
    /// </summary>
    private static string RenderLine(List<Word> words, double left, double charWidth)
    {
        var sb = new StringBuilder();
        Word? previous = null;
        foreach (var word in words)
        {
            int column = (int)Math.Round((word.BoundingBox.Left - left) / charWidth);
            bool samePhrase = previous != null && word.BoundingBox.Left - previous.BoundingBox.Right <= MaxWordGap * charWidth;
            int pad = sb.Length == 0 ? column : samePhrase ? 1 : Math.Max(1, column - sb.Length);
            sb.Append(' ', Math.Max(0, Math.Min(pad, MaxLinePadding))).Append(word.Text);
            previous = word;
        }
        return sb.ToString();
    }

    private const int MaxLinePadding = 200;
    // A printed space is about half a character wide; table columns are further apart.
    private const double MaxWordGap = 1.5;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v)).OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    /// <summary>ODF text, spreadsheet or presentation: paragraphs of content.xml (spreadsheet rows tab-separated).</summary>
    private static void ReadOpenDocument(string path, LimitedText text)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml");
        if (entry == null) return;

        using var stream = entry.Open();
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var line = new StringBuilder();
        int rowDepth = 0;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case System.Xml.XmlNodeType.Element when reader.LocalName == "table-row" && !reader.IsEmptyElement:
                    rowDepth++;
                    break;
                case System.Xml.XmlNodeType.Text:
                case System.Xml.XmlNodeType.SignificantWhitespace:
                    line.Append(reader.Value);
                    break;
                case System.Xml.XmlNodeType.EndElement when reader.LocalName == "table-row":
                    rowDepth = Math.Max(0, rowDepth - 1);
                    if (!Flush()) return;
                    break;
                case System.Xml.XmlNodeType.EndElement when reader.LocalName is "p" or "h":
                    if (rowDepth > 0) line.Append('\t');
                    else if (!Flush()) return;
                    break;
            }
        }
        Flush();

        bool Flush()
        {
            var value = line.ToString();
            line.Clear();
            return text.AppendLine(value);
        }
    }

    /// <summary>Decoded text (BOM, else UTF-8, else Windows-1252); null when the content is binary.</summary>
    private static string? ReadTextFile(string path)
    {
        // The process input/expansion limit bounds this allocation. Do not silently lose a tail
        // consisting mostly of whitespace or HTML markup before LimitedText can detect it.
        var buffer = File.ReadAllBytes(path);
        int length = buffer.Length;

        bool utf16 = length >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF));
        if (!utf16 && Array.IndexOf(buffer, (byte)0, 0, length) >= 0) return null;

        using var memory = new MemoryStream(buffer, 0, length);
        if (utf16 || (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF))
        {
            using var bomReader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return bomReader.ReadToEnd();
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, length);
        }
        catch (ArgumentException)
        {
            return Encoding.GetEncoding(1252).GetString(buffer, 0, length);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex HtmlHidden =
        new(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    private static readonly System.Text.RegularExpressions.Regex HtmlBreak =
        new(@"<(br|/p|/div|/tr|/li|/h[1-6])\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex HtmlTag = new(@"<[^>]+>");
    private static readonly System.Text.RegularExpressions.Regex Spaces = new(@"[ \t ]+");

    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var text = HtmlHidden.Replace(html, " ");
        text = HtmlBreak.Replace(text, "\n");
        text = System.Net.WebUtility.HtmlDecode(HtmlTag.Replace(text, " "));
        var lines = text.Split('\n').Select(l => Spaces.Replace(l, " ").Trim()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>RichTextBox does the RTF parsing; it needs an STA thread. A failure reaches Main, which reports it.</summary>
    private static string RtfToText(string path) => StaThread.Run(() =>
    {
        using var box = new System.Windows.Forms.RichTextBox();
        box.LoadFile(path, System.Windows.Forms.RichTextBoxStreamType.RichText);
        return box.Text;
    });

    private static bool ReadWord(string path, LimitedText text)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var main = document.MainDocumentPart;
        if (main?.Document?.Body != null && text.Enabled)
        {
            foreach (var paragraph in main.Document.Body.Descendants<W.Paragraph>())
            {
                if (!text.AppendLine(paragraph.InnerText)) break;
            }
        }
        return main?.VbaProjectPart != null;
    }

    private static bool ReadExcel(string path, LimitedText text)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbook = document.WorkbookPart;
        if (workbook?.Workbook?.Sheets != null && text.Enabled)
        {
            var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(i => i.InnerText).ToArray()
                                ?? Array.Empty<string>();
            foreach (var sheet in workbook.Workbook.Sheets.Elements<S.Sheet>())
            {
                if (sheet.Id?.Value == null || workbook.GetPartById(sheet.Id.Value) is not WorksheetPart part) continue;
                if (!text.AppendLine("[Foglio: " + sheet.Name + "]")) break;

                // Every row: only the character limit stops the reading, and then the result is marked Truncated,
                // so a partial sheet is never taken for the whole document.
                foreach (var row in part.Worksheet?.Descendants<S.Row>() ?? Enumerable.Empty<S.Row>())
                {
                    var cells = row.Elements<S.Cell>().Select(c => CellText(c, sharedStrings)).Where(v => v.Length > 0);
                    var line = string.Join("\t", cells);
                    if (line.Length > 0 && !text.AppendLine(line)) return workbook.VbaProjectPart != null;
                }
            }
        }
        return workbook?.VbaProjectPart != null;
    }

    private static string CellText(S.Cell cell, string[] sharedStrings)
    {
        var raw = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.SharedString && int.TryParse(raw, out var index) && index >= 0 && index < sharedStrings.Length)
        {
            return sharedStrings[index];
        }
        return raw;
    }

    private static bool ReadPowerPoint(string path, LimitedText text)
    {
        using var document = PresentationDocument.Open(path, false);
        var presentation = document.PresentationPart;
        var slideIds = presentation?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>();
        if (presentation != null && slideIds != null && text.Enabled)
        {
            int number = 0;
            foreach (var slideId in slideIds)
            {
                if (slideId.RelationshipId?.Value == null || presentation.GetPartById(slideId.RelationshipId.Value) is not SlidePart slide) continue;
                if (!text.AppendLine("[Diapositiva " + (++number) + "]")) break;
                foreach (var paragraph in slide.Slide?.Descendants<A.Paragraph>() ?? Enumerable.Empty<A.Paragraph>())
                {
                    if (!text.AppendLine(paragraph.InnerText)) return presentation.VbaProjectPart != null;
                }
            }
        }
        return presentation?.VbaProjectPart != null;
    }

    /// <summary>Word "Macros", Excel "_VBA_PROJECT_CUR" or any nested "VBA" storage.</summary>
    private static bool ContainsVbaStorage(Storage storage, int depth)
    {
        if (depth > 4) return false;
        foreach (var entry in storage.EnumerateEntries())
        {
            if (entry.Type != EntryType.Storage) continue;
            if (entry.Name.Equals("VBA", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Equals("Macros", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Equals("_VBA_PROJECT_CUR", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (storage.TryOpenStorage(entry.Name, out var child) && ContainsVbaStorage(child, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>Text buffer that stops accepting input at maxChars.</summary>
    private sealed class LimitedText
    {
        private readonly StringBuilder _sb = new();
        private readonly int _max;
        // Layout lines carry alignment padding that costs the model almost nothing: it does not count towards the limit.
        private int _padding;

        public LimitedText(int max) => _max = max;

        public bool Enabled => _max > 0;
        public bool Truncated { get; private set; }
        public string? Problem { get; private set; }
        public void Incomplete(string reason) { Problem ??= reason; Truncated = true; }
        public int Remaining => Truncated ? 0 : Math.Max(0, _max - (_sb.Length - _padding));

        public void AppendBlock(string? block)
        {
            if (string.IsNullOrEmpty(block)) return;
            foreach (var line in block!.Split('\n')) if (!AppendLine(line)) break;
        }

        public void Clear()
        {
            _sb.Clear();
            _padding = 0;
            Truncated = false;
        }

        /// <summary>A line whose indentation and inner spacing mean something (PDF layout): kept as is, runs of spaces counted once.</summary>
        /// <returns>false once the limit is reached.</returns>
        public bool AppendLayoutLine(string? line)
        {
            if (!Enabled || Truncated) return false;
            if (string.IsNullOrWhiteSpace(line)) return true;

            var value = line!.TrimEnd();
            int padding = 0;
            for (int i = 1; i < value.Length; i++) if (value[i] == ' ' && value[i - 1] == ' ') padding++;
            if (value.Length - padding >= Remaining)
            {
                Truncated = true;
                return false;
            }
            _padding += padding;
            _sb.AppendLine(value);
            return true;
        }

        /// <returns>false once the limit is reached.</returns>
        public bool AppendLine(string? line)
        {
            if (!Enabled || Truncated) return false;
            if (string.IsNullOrWhiteSpace(line)) return true;

            var value = line!.Trim();
            int room = Remaining;
            if (value.Length >= room)
            {
                _sb.Append(value, 0, Math.Max(0, room));
                Truncated = true;
                return false;
            }
            _sb.AppendLine(value);
            return true;
        }

        // Leading spaces of the first line are layout too: only blank lines and the tail are trimmed.
        public override string ToString() => _sb.ToString().Trim('\r', '\n').TrimEnd();
    }
}
