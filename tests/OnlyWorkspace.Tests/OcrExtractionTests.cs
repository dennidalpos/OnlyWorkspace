using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

/// <summary>
/// E8: synthetic scans through the real helper process and Windows.Media.Ocr. Where Windows has no recognizer for the
/// user's languages the helper must say so instead of reading nothing as complete.
/// </summary>
public class OcrExtractionTests
{
    private const string Line1 = "Fattura FT-2026/77 del 12/10/2026";
    private const string Line2 = "Scadenza 30/11/2026 per la fornitura";
    private const string Line3 = "Totale 1.250,00 EUR";

    private static readonly Lazy<bool> OcrAvailable = new(() =>
    {
        var start = new ProcessStartInfo(AttachmentTextExtractor.FindHelper()!, "--ocr-languages") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return JsonDocument.Parse(output).RootElement.GetProperty("used").ValueKind == JsonValueKind.String;
    });

    [Fact]
    public async Task Image_TextIsRecognized()
    {
        var result = await AttachmentExtractionTests.ExtractAsync("scansione.png", Png(Line1, Line2, Line3));
        Assert.Equal("image", result.Format);
        if (!OcrAvailable.Value) { Assert.Equal("ocr-unavailable", result.Problem); Assert.Empty(result.Text); return; }
        Assert.Null(result.Problem);
        Assert.StartsWith("[testo riconosciuto con OCR]", result.Text);
        Assert.Contains("FT-2026/77", result.Text);
        Assert.Contains("30/11/2026", result.Text);
        // Cents read as letters ("1.250,OO") are turned back into zeros.
        Assert.Contains("1.250,00", result.Text);
    }

    [Fact]
    public async Task ImageWithoutText_IsNotRead()
    {
        var result = await AttachmentExtractionTests.ExtractAsync("foto.png", Png());
        Assert.Equal(OcrAvailable.Value ? "scanned" : "ocr-unavailable", result.Problem);
        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task MultipageTiff_EveryPageIsRead()
    {
        var result = await AttachmentExtractionTests.ExtractAsync("fax.tif", Tiff(Bitmap(Line1), Bitmap(Line2)));
        Assert.Equal("image", result.Format);
        if (!OcrAvailable.Value) { Assert.Equal("ocr-unavailable", result.Problem); return; }
        Assert.Null(result.Problem);
        Assert.Contains("--- pagina 2 ---", result.Text);
        Assert.True(result.Text.IndexOf("FT-2026/77", StringComparison.Ordinal) < result.Text.IndexOf("30/11/2026", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScannedAndTextPages_AreReadInPageOrder()
    {
        var pdf = new ScanPdf().TextPage("Prima pagina con il livello testuale del documento di prova.").ScanPage(Jpeg(Line1, Line2));
        var result = await AttachmentExtractionTests.ExtractAsync("documento.pdf", pdf.Build());
        Assert.Equal("pdf", result.Format);
        if (!OcrAvailable.Value) { Assert.Equal("ocr-unavailable", result.Problem); Assert.Empty(result.Text); return; }
        Assert.Null(result.Problem);
        var lines = AttachmentExtractionTests.Lines(result);
        Assert.Equal("Prima pagina con il livello testuale del documento di prova.", lines[0]);
        Assert.Equal(new[] { "--- pagina 2 ---", "[testo riconosciuto con OCR]" }, lines.Skip(1).Take(2));
        Assert.Contains("FT-2026/77", result.Text);
    }

    [Fact]
    public async Task OnePageWithoutRecognizableText_LeavesTheWholePdfUnread()
    {
        var pdf = new ScanPdf().ScanPage(Jpeg(Line1, Line2)).ScanPage(Jpeg());
        var result = await AttachmentExtractionTests.ExtractAsync("documento.pdf", pdf.Build());
        Assert.Equal(OcrAvailable.Value ? "scanned" : "ocr-unavailable", result.Problem);
        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task OcrOff_LeavesScansAndImagesUnread_AndTextPagesRead()
    {
        // The administrator's setting reaches the helper as --no-ocr: nothing recognized, nothing partial.
        var image = await AttachmentExtractionTests.ExtractAsync("scansione.png", Png(Line1, Line2), ocr: false);
        Assert.Equal(("image", "ocr-off", ""), (image.Format, image.Problem, image.Text));
        var mixed = await AttachmentExtractionTests.ExtractAsync("documento.pdf",
            new ScanPdf().TextPage("Prima pagina con il livello testuale del documento di prova.").ScanPage(Jpeg(Line1, Line2)).Build(), ocr: false);
        Assert.Equal(("ocr-off", ""), (mixed.Problem, mixed.Text));
        var text = await AttachmentExtractionTests.ExtractAsync("testo.pdf", new ScanPdf().TextPage("Pagina con il solo livello testuale del documento.").Build(), ocr: false);
        Assert.Null(text.Problem);
        Assert.Contains("solo livello testuale", text.Text);
    }

    private static Bitmap Bitmap(params string[] lines)
    {
        // A4 at 150 dpi: what an office scanner produces at its lowest common setting.
        var bitmap = new Bitmap(1240, 1754);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        using var font = new Font("Arial", 18);
        for (int i = 0; i < lines.Length; i++) graphics.DrawString(lines[i], font, Brushes.Black, 120, 200 + i * 80);
        return bitmap;
    }

    internal static byte[] Png(params string[] lines) => Encode(Bitmap(lines), ImageFormat.Png);
    private static byte[] Jpeg(params string[] lines) => Encode(Bitmap(lines), ImageFormat.Jpeg);

    private static byte[] Encode(Bitmap bitmap, ImageFormat format)
    {
        using (bitmap)
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream, format);
            return stream.ToArray();
        }
    }

    private static byte[] Tiff(Bitmap first, Bitmap second)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/tiff");
        using var parameters = new EncoderParameters(1);
        using var stream = new MemoryStream();
        using (first)
        using (second)
        {
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            first.Save(stream, codec, parameters);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
            first.SaveAdd(second, parameters);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.Flush);
            first.SaveAdd(parameters);
        }
        return stream.ToArray();
    }

    /// <summary>A4 pages: Helvetica text, or a full-page JPEG with no text layer, as a scanner writes it.</summary>
    private sealed class ScanPdf
    {
        private readonly List<(string? Text, byte[]? Jpeg)> _pages = new();

        public ScanPdf TextPage(string text) { _pages.Add((text, null)); return this; }
        public ScanPdf ScanPage(byte[] jpeg) { _pages.Add((null, jpeg)); return this; }

        public byte[] Build()
        {
            var latin1 = Encoding.GetEncoding(28591);
            var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>" };
            var kids = new List<string>();
            foreach (var (text, jpeg) in _pages)
            {
                int page = objects.Count + 1;
                kids.Add(page + " 0 R");
                if (text != null)
                {
                    string content = $"BT /F1 12 Tf 1 0 0 1 50 700 Tm ({text}) Tj ET\n";
                    objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {page + 1} 0 R >>");
                    objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
                }
                else
                {
                    using var image = new Bitmap(new MemoryStream(jpeg!));
                    string content = "q 595 0 0 842 0 0 cm /Im1 Do Q\n";
                    objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im1 {page + 2} 0 R >> >> /Contents {page + 1} 0 R >>");
                    objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
                    objects.Add($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg!.Length} >>\n" +
                        $"stream\n{latin1.GetString(jpeg)}\nendstream");
                }
            }
            objects[1] = $"<< /Type /Pages /Count {_pages.Count} /Kids [{string.Join(" ", kids)}] >>";
            var pdf = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (int i = 0; i < objects.Count; i++)
            {
                offsets.Add(pdf.Length);
                pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            int xref = pdf.Length;
            pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return latin1.GetBytes(pdf.ToString());
        }
    }
}
