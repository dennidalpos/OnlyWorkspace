using System.Globalization;
using System.Text;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Tests;

/// <summary>
/// #286: a form laid out like a public-office module (labels in a row, values under them, boxed dates, inline identifiers),
/// built as a real PDF with invented data, read by the attachment extractor and masked. It qualifies the text-layer path
/// on a simulated layout only: not a scanned module, not a real ministry form, not Outlook.
/// </summary>
public sealed class SyntheticFormPdfMaskingTests
{
    private static readonly (int X, int Y, string Text)[] Page =
    {
        (50, 780, "MODULO DI DOMANDA - AMMINISTRAZIONE DI ESEMPIO"),
        (50, 740, "Cognome"), (230, 740, "Nome"), (380, 740, "Data di nascita"),
        (50, 722, "Verdelli"), (230, 722, "Giordano"), (380, 722, "04 07 1983"),
        (50, 690, "Codice fiscale: VRDGDN83L04F205X"),
        (50, 672, "Residente in Via Giacomo Leopardi 14, 20100 Milano (MI)"),
        (50, 654, "Telefono +39 333 0000123 - email: giordano.verdelli@esempio.test"),
        (50, 636, "IBAN IT60X0542811101000000123456 intestato a Verdelli Giordano"),
        (50, 618, "Allegato: delega della Edilsud S.r.l. firmata da Anna Bellandi"),
        (50, 580, "Protocollo n. 2026/118 - esito comunicato il 12/03/2026"),
    };

    private static readonly string[] Originals =
    {
        "VRDGDN83L04F205X", "Leopardi", "0000123", "giordano.verdelli@esempio.test",
        "IT60X0542811101000000123456", "Edilsud",
    };

    [Fact]
    public async Task FormPdf_IsReadAndEveryStructuredValueLeavesMasked()
    {
        var read = await AttachmentExtractionTests.ExtractAsync("modulo.pdf", BuildPdf(Page));
        Assert.Null(read.Problem);
        Assert.Equal("pdf", read.Format);
        Assert.Contains("VRDGDN83L04F205X", read.Text);

        var mask = new Pseudonymizer();
        string outgoing = Assert.Single(mask.Mask(new[] { ChatMessage.User(read.Text) })).Content;
        foreach (string original in Originals) Assert.True(!outgoing.Contains(original), original + " leaked. Extracted:\n" + read.Text + "\nMasked:\n" + outgoing);
        foreach (string category in new[] { "CF", "EMAIL", "IBAN", "TEL", "INDIRIZZO", "AZIENDA" })
            Assert.Contains("[" + category + "_", outgoing);
        Assert.Equal(read.Text, mask.Restore(outgoing));
    }

    [Fact]
    public async Task FormPdf_NamesUnderTheirLabelsAreMasked()
    {
        var read = await AttachmentExtractionTests.ExtractAsync("modulo.pdf", BuildPdf(Page));
        var mask = new Pseudonymizer();
        string outgoing = Assert.Single(mask.Mask(new[] { ChatMessage.User(read.Text) })).Content;
        // Label-aligned cells are the form-field strategy's job. Known limits, not asserted: the same name in running prose
        // ("intestato a ...", "firmata da ...") and numeric dates stay in clear unless the local entity index or the dates option applies.
        var row = outgoing.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).First(line => line.Contains("[PERSONA_"));
        Assert.DoesNotContain("Verdelli", row);
        Assert.DoesNotContain("Giordano", row);
    }

    [Theory]
    [InlineData("Data (a caselle): 1 4 0 7 1 9 8 2", "1 4 0 7 1 9 8 2")]
    [InlineData("Data (a caselle): 1 407 1 982", "1 407 1 982")]
    [InlineData("Data di nascita: 1 4 0 7 1 9 8 2", "1 4 0 7 1 9 8 2")]
    [InlineData("Data: 14071982", "14071982")]
    public void BoxedDatesOnOneLine_AreMasked(string line, string original)
    {
        var mask = new Pseudonymizer(maskDates: true);
        string outgoing = Assert.Single(mask.Mask(new[] { ChatMessage.User(line) })).Content;
        Assert.DoesNotContain(original, outgoing);
        Assert.Contains("[DATA_1]", outgoing);
        Assert.Equal(line, mask.Restore(outgoing));
    }

    [Theory]
    [InlineData("Data (a caselle): 1 4 0 7 1 9 8 2 3")]
    [InlineData("Data: 333 000 0123")]
    [InlineData("Data: 1500 20 12")]
    [InlineData("Telefono: 1 4 0 7 1 9 8 2")]
    public void BoxedDateRule_LeavesOtherNumbersAlone(string line)
    {
        var mask = new Pseudonymizer(maskDates: true);
        string outgoing = Assert.Single(mask.Mask(new[] { ChatMessage.User(line) })).Content;
        Assert.DoesNotContain("[DATA_", outgoing);
    }

    private static byte[] BuildPdf((int X, int Y, string Text)[] items)
    {
        var latin1 = Encoding.GetEncoding(28591);
        var content = new StringBuilder();
        foreach (var (x, y, text) in items)
            content.Append("BT /F1 10 Tf 1 0 0 1 ").Append(x.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(y.ToString(CultureInfo.InvariantCulture))
                .Append(" Tm (").Append(text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj ET\n");
        string stream = content.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return latin1.GetBytes(pdf.ToString());
    }
}
