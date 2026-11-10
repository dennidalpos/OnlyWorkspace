using System.Globalization;
using System.Text;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Tests;

/// <summary>PDF text by page position, through the real helper process (copied to "extractor" next to the tests).</summary>
public class PdfLayoutExtractionTests
{
    [Fact]
    public async Task Form_ValuesArePairedWithBoxAndLabel()
    {
        // File order of a filled-in form: every label first, every value last.
        var pdf = new MiniPdf()
            .Text(50, 700, "Imposta lorda").Text(220, 700, "Detrazioni").Text(400, 700, "Imposta netta")
            .Text(50, 686, "361").Text(220, 686, "362").Text(400, 686, "363")
            .Text(50, 640, "Giorni").Text(220, 640, "Ritenute")
            .Text(60, 672, "1.884,83").Text(230, 672, "1.200,00").Text(410, 672, "684,83")
            .Text(50, 626, "7").Text(220, 626, "8.194,93");

        var result = await ExtractAsync(pdf);

        Assert.Null(result.Problem);
        Assert.Contains("→ 361 Imposta lorda: 1.884,83 · 362 Detrazioni: 1.200,00 · 363 Imposta netta: 684,83", result.Text);
        Assert.Contains("Ritenute: 8.194,93", result.Text);
        // The digit of the box beside must not run into the amount.
        Assert.DoesNotContain("78.194,93", result.Text);
    }

    [Fact]
    public async Task Table_CellsKeepTheirColumns()
    {
        var pdf = new MiniPdf()
            .Text(50, 700, "Descrizione").Text(250, 700, "Quantita").Text(350, 700, "Prezzo").Text(450, 700, "Importo")
            .Text(50, 686, "Toner nero").Text(250, 686, "2").Text(350, 686, "45,00").Text(450, 686, "90,00")
            .Text(50, 672, "Carta A4").Text(250, 672, "10").Text(350, 672, "4,50").Text(450, 672, "45,00")
            .Text(250, 640, "Imponibile").Text(350, 640, "IVA").Text(450, 640, "Totale")
            .Text(250, 626, "135,00").Text(350, 626, "29,70").Text(450, 626, "164,70");

        var result = await ExtractAsync(pdf);
        var lines = Lines(result);

        var header = lines.Single(l => l.StartsWith("Descrizione"));
        var toner = lines.Single(l => l.StartsWith("Toner nero"));
        var paper = lines.Single(l => l.StartsWith("Carta A4"));
        Assert.Equal(header.IndexOf("Prezzo"), toner.IndexOf("45,00"));
        Assert.Equal(header.IndexOf("Prezzo"), paper.IndexOf("4,50"));
        Assert.Equal(header.IndexOf("Importo"), toner.IndexOf("90,00"));
        Assert.Equal(header.IndexOf("Importo"), paper.LastIndexOf("45,00"));
        Assert.Contains("→ Imponibile: 135,00 · IVA: 29,70 · Totale: 164,70", result.Text);
    }

    [Fact]
    public async Task TwoColumns_LinesStaySideBySide()
    {
        // Drawn column by column: the left one first, then the right one.
        var pdf = new MiniPdf()
            .Text(50, 700, "Cedente Alfa Srl").Text(50, 686, "Via Roma 1").Text(50, 672, "Milano")
            .Text(320, 700, "Cessionario Beta Spa").Text(320, 686, "Corso Italia 9").Text(320, 672, "Torino");

        var lines = Lines(await ExtractAsync(pdf));

        Assert.Equal(3, lines.Length);
        Assert.Equal("Cedente Alfa Srl Cessionario Beta Spa", Squeeze(lines[0]));
        Assert.Equal("Via Roma 1 Corso Italia 9", Squeeze(lines[1]));
        Assert.Equal(lines[0].IndexOf("Cessionario"), lines[1].IndexOf("Corso"));
        Assert.Equal(lines[0].IndexOf("Cessionario"), lines[2].IndexOf("Torino"));
    }

    [Fact]
    public async Task VerticalText_IsReadApartAfterThePage()
    {
        var pdf = new MiniPdf()
            .Vertical(20, 300, "Nota a margine del modello")
            .Text(50, 700, "Totale fattura").Text(250, 700, "1.220,00")
            .Text(50, 300, "Pagamento a trenta giorni");

        var lines = Lines(await ExtractAsync(pdf));

        Assert.Equal(3, lines.Length);
        Assert.Equal("Totale fattura 1.220,00", Squeeze(lines[0]));
        Assert.Equal("Pagamento a trenta giorni", lines[1]); // one space inside a phrase
        Assert.Equal("Nota a margine del modello", lines[2]);
    }

    [Fact]
    public async Task Pages_AreSeparated()
    {
        var pdf = new MiniPdf()
            .Text(50, 700, "Prima pagina del documento di prova")
            .Page().Text(50, 700, "Seconda pagina del documento di prova");

        var lines = Lines(await ExtractAsync(pdf));

        Assert.Equal(new[] { "Prima pagina del documento di prova", "--- pagina 2 ---", "Seconda pagina del documento di prova" }, lines);
    }

    [Fact]
    public async Task PageWithoutTextLayer_IsScanned()
    {
        var result = await ExtractAsync(new MiniPdf().Text(500, 30, "Pag. 1"));

        Assert.Equal("scanned", result.Problem);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task AlignmentSpaces_DoNotCountTowardsTheLimit()
    {
        var pdf = new MiniPdf().Text(50, 700, "Codice").Text(500, 700, "A1").Text(50, 686, "Seconda riga oltre il limite");

        var result = await ExtractAsync(pdf, maxChars: 12);

        Assert.True(result.Truncated);
        Assert.StartsWith("Codice", result.Text);
        Assert.EndsWith("A1", result.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedScan_ReportsIncomplete_RegardlessOfPageOrder(bool scanFirst)
    {
        var pdf = MixedPdf(scanFirst);
        var result = await ExtractAsync(pdf);
        Assert.Equal("scanned", result.Problem);
        Assert.Empty(result.Text);
        Assert.False(result.Truncated);
    }

    internal static MiniPdf MixedPdf(bool scanFirst)
    {
        var pdf = new MiniPdf();
        if (scanFirst) pdf.Raster(0, 0, 595, 842).Page();
        pdf.Text(50, 700, "Documento testuale completo con importi e scadenze leggibili.");
        if (!scanFirst) pdf.Page().Raster(0, 0, 595, 842);
        return pdf;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlankPagesAndDecorativeImages_DoNotInvalidateTextPages(bool image)
    {
        var pdf = new MiniPdf().Page().Text(50, 700, "Tutto il contenuto del documento si trova nel livello testuale.");
        if (image) pdf.Raster(20, 760, 20, 20);
        pdf.Page();
        var result = await ExtractAsync(pdf);
        Assert.Null(result.Problem);
        Assert.Contains("Tutto il contenuto", result.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseNonblankPage_IsIncompleteEvenBesideLongText(bool image)
    {
        var pdf = new MiniPdf().Text(50, 700, "Pagina testuale con abbastanza caratteri per essere letta.")
            .Page().Text(500, 30, "Pag. 2");
        if (image) pdf.Raster(0, 0, 595, 842);
        var result = await ExtractAsync(pdf);
        Assert.Equal("scanned", result.Problem);
        Assert.Empty(result.Text);
    }

    // Phrases far apart on the same line sit on a fixed-width grid: the padding between them varies.
    private static string Squeeze(string line) => string.Join(" ", line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

    private static string[] Lines(AttachmentExtraction result) => AttachmentExtractionTests.Lines(result);

    private static async Task<AttachmentExtraction> ExtractAsync(MiniPdf pdf, int maxChars = 20000)
    {
        var result = await AttachmentExtractionTests.ExtractAsync("documento.pdf", pdf.Build(), maxChars);
        Assert.Equal("pdf", result.Format);
        return result;
    }

    /// <summary>Smallest valid PDF: Helvetica strings at given positions, drawn in call order.</summary>
    internal sealed class MiniPdf
    {
        private readonly List<StringBuilder> _pages = new() { new StringBuilder() };

        public MiniPdf Page()
        {
            _pages.Add(new StringBuilder());
            return this;
        }

        public MiniPdf Text(double x, double y, string text) => Draw($"1 0 0 1 {N(x)} {N(y)}", text);

        public MiniPdf Raster(double x, double y, double width, double height)
        {
            _pages[^1].Append($"q {N(width)} 0 0 {N(height)} {N(x)} {N(y)} cm BI /W 1 /H 1 /BPC 8 /CS /RGB ID \u00aa\u00aa\u00aa EI Q\n");
            return this;
        }

        /// <summary>Rotated 90°, reading bottom to top.</summary>
        public MiniPdf Vertical(double x, double y, string text) => Draw($"0 1 -1 0 {N(x)} {N(y)}", text);

        private MiniPdf Draw(string matrix, string text)
        {
            var escaped = text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            _pages[^1].Append($"BT /F1 10 Tf {matrix} Tm ({escaped}) Tj ET\n");
            return this;
        }

        private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        public byte[] Build(string catalogEntries = "")
        {
            var latin1 = Encoding.GetEncoding(28591);
            var objects = new List<string>
            {
                $"<< /Type /Catalog /Pages 2 0 R {catalogEntries} >>",
                $"<< /Type /Pages /Count {_pages.Count} /Kids [{string.Join(" ", _pages.Select((_, i) => $"{4 + i * 2} 0 R"))}] >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
            };
            foreach (var page in _pages)
            {
                objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count + 2} 0 R >>");
                objects.Add($"<< /Length {latin1.GetByteCount(page.ToString())} >>\nstream\n{page}endstream");
            }

            var pdf = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (int i = 0; i < objects.Count; i++)
            {
                offsets.Add(latin1.GetByteCount(pdf.ToString()));
                pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            int xref = latin1.GetByteCount(pdf.ToString());
            pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
            pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return latin1.GetBytes(pdf.ToString());
        }
    }
}
