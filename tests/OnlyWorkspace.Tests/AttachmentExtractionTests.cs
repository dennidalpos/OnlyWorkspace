using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OpenMcdf;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

/// <summary>Attachment formats through the real helper process (copied to "extractor" next to the tests).</summary>
public class AttachmentExtractionTests
{
    [Fact]
    public async Task Text_IsReadAsUtf8()
    {
        var result = await ExtractAsync("nota.txt", Encoding.UTF8.GetBytes("Scadenza: venerdì\r\nImporto: 1.220,00 €"));

        Assert.Equal("text", result.Format);
        Assert.Equal(new[] { "Scadenza: venerdì", "Importo: 1.220,00 €" }, Lines(result));
    }

    /// <summary>#151: further plain-text formats are read like .txt, and the archive treats them as readable documents.</summary>
    [Theory]
    [InlineData("vcf")]
    [InlineData("sql")]
    [InlineData("srt")]
    [InlineData("cfg")]
    [InlineData("conf")]
    [InlineData("properties")]
    [InlineData("tex")]
    [InlineData("rst")]
    [InlineData("kml")]
    [InlineData("gpx")]
    public async Task AdditionalTextFormats_AreReadAsText(string extension)
    {
        var result = await ExtractAsync("dati." + extension, Encoding.UTF8.GetBytes("Cliente: Rossi\r\nImporto: 1.220,00 €"));

        Assert.Equal("text", result.Format);
        Assert.Null(result.Problem);
        Assert.Equal(new[] { "Cliente: Rossi", "Importo: 1.220,00 €" }, Lines(result));
        Assert.True(OnlyWorkspace.Core.Security.AttachmentTextFormats.HasTextReader(extension));
        Assert.Null(OnlyWorkspace.Core.Security.AttachmentTextFormats.ArchiveReferenceReason(extension));
    }

    [Theory]
    [InlineData("utf8-bom.txt", "UTF8BOM")]
    [InlineData("utf16le.txt", "UTF16LE")]
    [InlineData("utf16be.txt", "UTF16BE")]
    [InlineData("ansi.csv", "CP1252")]
    public async Task Text_EncodingsAreDetected(string name, string encoding)
    {
        const string content = "Importo: 1.220,00 \u20ac\r\nScadenza: venerd\u00ec";
        byte[] body = encoding switch
        {
            "UTF8BOM" => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(content)).ToArray(),
            "UTF16LE" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(content)).ToArray(),
            "UTF16BE" => Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(content)).ToArray(),
            // Not valid UTF-8, so Windows-1252: 0x80 is the euro sign, 0xEC is "i" with a grave accent.
            _ => Encoding.ASCII.GetBytes("Importo: 1.220,00 ").Concat(new byte[] { 0x80 }).Concat(Encoding.ASCII.GetBytes("\r\nScadenza: venerd")).Concat(new byte[] { 0xEC }).ToArray(),
        };

        var result = await ExtractAsync(name, body);

        Assert.Equal("text", result.Format);
        Assert.Null(result.Problem);
        Assert.Equal(new[] { "Importo: 1.220,00 \u20ac", "Scadenza: venerd\u00ec" }, Lines(result));
    }

    [Fact]
    public async Task Calendar_WithoutVcalendarMarkup_IsMalformed()
    {
        var result = await ExtractAsync("invito.ics", Encoding.UTF8.GetBytes("Riunione domani alle 10"));

        Assert.Equal("malformed-calendar", result.Problem);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task BinaryBehindTextExtension_IsUnsupported()
    {
        var result = await ExtractAsync("nota.txt", new byte[] { 0x41, 0x00, 0x01, 0x02, 0x42 });

        Assert.Equal("unsupported", result.Problem);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task Html_DropsScriptsAndKeepsParagraphs()
    {
        var html = "<html><head><title>x</title><style>p{color:red}</style></head><body><p>Gentile cliente,</p><script>alert('no')</script><div>saldo &egrave; di 50&nbsp;&euro;</div></body></html>";

        var result = await ExtractAsync("avviso.html", Encoding.UTF8.GetBytes(html));

        Assert.Equal("html", result.Format);
        Assert.Equal(new[] { "Gentile cliente,", "saldo è di 50 €" }, Lines(result));
    }

    [Fact]
    public async Task Word_ParagraphsAreRead()
    {
        var result = await ExtractAsync("lettera.docx", Docx("Oggetto: sollecito", "Restiamo in attesa del saldo."));

        Assert.Equal("docx", result.Format);
        Assert.False(result.HasMacros);
        Assert.Equal(new[] { "Oggetto: sollecito", "Restiamo in attesa del saldo." }, Lines(result));
    }

    [Fact]
    public async Task Eml_HeadersBodyAndAttachmentAreRead()
    {
        var eml = string.Join("\r\n",
            "From: Mario Rossi <mario@example.com>",
            "To: Anna Bianchi <anna@example.com>",
            "Subject: Offerta 12",
            "Date: Mon, 05 Oct 2026 09:30:00 +0200",
            "MIME-Version: 1.0",
            "Content-Type: multipart/mixed; boundary=\"b1\"",
            "",
            "--b1",
            "Content-Type: text/plain; charset=utf-8",
            "",
            "In allegato le condizioni.",
            "--b1",
            "Content-Type: text/plain; charset=utf-8; name=\"condizioni.txt\"",
            "Content-Disposition: attachment; filename=\"condizioni.txt\"",
            "",
            "Pagamento a 30 giorni.",
            "--b1",
            "Content-Type: application/octet-stream; name=\"dati.bin\"",
            "Content-Disposition: attachment; filename=\"dati.bin\"",
            "Content-Transfer-Encoding: base64",
            "",
            "AAECAwQ=",
            "--b1--",
            "");

        var result = await ExtractAsync("offerta.eml", Encoding.ASCII.GetBytes(eml));
        var lines = Lines(result);

        Assert.Equal("eml", result.Format);
        Assert.Contains(lines, l => l.StartsWith("Da: ") && l.Contains("mario@example.com"));
        Assert.Contains(lines, l => l.StartsWith("A: ") && l.Contains("anna@example.com"));
        Assert.Contains("Oggetto: Offerta 12", lines);
        Assert.Contains("In allegato le condizioni.", lines);
        Assert.Equal("Pagamento a 30 giorni.", lines[Array.IndexOf(lines, "[Allegato: condizioni.txt]") + 1]);
        Assert.Contains("[Allegato: dati.bin — non letto: formato non supportato]", lines);
    }

    [Fact]
    public async Task Excel_RowsAreTabSeparatedUnderTheSheetName()
    {
        var result = await ExtractAsync("listino.xlsx", Xlsx());

        Assert.Equal("xlsx", result.Format);
        Assert.False(result.HasMacros);
        Assert.Equal(new[] { "[Foglio: Listino]", "Articolo\tPrezzo", "Vite M8\t12.5" }, Lines(result));
    }

    [Fact]
    public async Task Excel_EverySheetIsReadPastTwoThousandRows()
    {
        var rows = Enumerable.Range(1, 2500).Select(i => i == 2500 ? "MARCATORE-FINALE" : "Riga " + i).ToArray();
        var result = await ExtractAsync("registro.xlsx", Xlsx(("Primo", rows), ("Secondo", new[] { "Riga A", "ULTIMA-RIGA-SECONDO" })),
            AttachmentTextExtractor.WholeDocumentChars);

        Assert.False(result.Truncated);
        var lines = Lines(result);
        Assert.Equal(2500 + 2 + 2, lines.Length);
        Assert.Contains("MARCATORE-FINALE", lines);
        Assert.Equal("ULTIMA-RIGA-SECONDO", lines[^1]);
    }

    [Fact]
    public async Task Excel_PastTheCharacterLimit_IsMarkedIncomplete()
    {
        var rows = Enumerable.Range(1, 3000).Select(i => "Riga numero " + i).ToArray();
        var result = await ExtractAsync("registro.xlsx", Xlsx(("Primo", rows), ("Secondo", new[] { "MARCATORE-FINALE" })), maxChars: 20000);

        // A partial sheet must never pass for the whole workbook: model and archive treat Truncated as not read.
        Assert.True(result.Truncated);
        Assert.DoesNotContain("MARCATORE-FINALE", result.Text);
    }

    [Fact]
    public async Task PowerPoint_SlidesAreNumbered()
    {
        var result = await ExtractAsync("piano.pptx", Pptx("Piano 2027", "Obiettivi di vendita"));

        Assert.Equal("pptx", result.Format);
        Assert.Equal(new[] { "[Diapositiva 1]", "Piano 2027", "Obiettivi di vendita" }, Lines(result));
    }

    [Fact]
    public async Task OpenDocument_ParagraphsAndTableRowsAreRead()
    {
        var result = await ExtractAsync("verbale.odt", Odt());

        Assert.Equal("odf", result.Format);
        Assert.Equal(new[] { "Verbale", "Presenti: Rossi, Bianchi.", "Voce\tImporto" }, Lines(result));
    }

    [Fact]
    public async Task Rtf_IsReadAsPlainText()
    {
        var rtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Arial;}}\f0 Oggetto: sollecito\par Saldo entro venerd\'ec.\par}";

        var result = await ExtractAsync("lettera.rtf", Encoding.ASCII.GetBytes(rtf));

        Assert.Equal("rtf", result.Format);
        Assert.Equal(new[] { "Oggetto: sollecito", "Saldo entro venerdì." }, Lines(result));
    }

    [Fact]
    public async Task Msg_HeadersBodyAndAttachmentAreRead()
    {
        var sent = new DateTime(2026, 10, 5, 9, 30, 0, DateTimeKind.Local);

        var result = await ExtractAsync("offerta.msg", Msg(sent, "condizioni.txt", Encoding.UTF8.GetBytes("Pagamento a 30 giorni.")));

        Assert.Equal("msg", result.Format);
        Assert.Equal(new[]
        {
            "Da: Mario Rossi <mario@example.com>", "A: Anna Bianchi", "Data: 05/10/2026 09:30", "Oggetto: Offerta 12",
            "In allegato le condizioni.", "[Allegato: condizioni.txt]", "Pagamento a 30 giorni."
        }, Lines(result));
    }

    [Fact]
    public async Task SignedP7m_IsUnwrapped_DerOrBase64()
    {
        var der = Signed(Encoding.UTF8.GetBytes("Atto firmato\r\nImporto: 300,00 €"));
        var pem = "-----BEGIN PKCS7-----\n" + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks) + "\n-----END PKCS7-----\n";

        foreach (var content in new[] { der, Encoding.ASCII.GetBytes(pem) })
        {
            var result = await ExtractAsync("atto.txt.p7m", content);

            Assert.Equal("text", result.Format);
            Assert.Equal(new[] { "Atto firmato", "Importo: 300,00 €" }, Lines(result));
        }

        var broken = await ExtractAsync("atto.pdf.p7m", new byte[] { 0x30, 0x03, 0x02, 0x01, 0x05 });
        Assert.Equal("p7m", broken.Format);
        Assert.Equal("unsupported", broken.Problem);
    }

    [Fact]
    public async Task ProtectedPdf_IsReportedWithoutText()
    {
        var result = await ExtractAsync("contratto.pdf", ProtectedPdf());

        Assert.Equal("pdf", result.Format);
        Assert.Equal("protected", result.Problem);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task MacroCheckOnly_ReturnsNoText()
    {
        var result = await ExtractAsync("lettera.docx", Docx("Testo che non serve"), maxChars: 0);

        Assert.Equal("docx", result.Format);
        Assert.Equal(string.Empty, result.Text);
    }

    internal static string[] Lines(AttachmentExtraction result) =>
        result.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    /// <summary>Writes the file in a folder of its own (emails unpack their attachments beside them) and runs the helper.</summary>
    internal static async Task<AttachmentExtraction> ExtractAsync(string fileName, byte[] content, int maxChars = 20000, bool ocr = true)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ow-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path = Path.Combine(dir, fileName);
            File.WriteAllBytes(path, content);
            var result = await AttachmentTextExtractor.ExtractAsync(path, maxChars, TimeSpan.FromSeconds(60), CancellationToken.None, ocr);
            Assert.Null(result.Error);
            Assert.Single(Directory.GetFiles(dir)); // nothing left behind
            return result;
        }
        finally
        {
            TestArtifacts.DeleteDirectory(dir, recursive: true);
        }
    }

    internal const string OfficeRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string OfficeTypes = "application/vnd.openxmlformats-officedocument.";

    /// <summary>Smallest package Word opens: content types, root relationship, document.</summary>
    internal static byte[] Docx(params string[] paragraphs)
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return Zip(
            ContentTypes(("/word/document.xml", "wordprocessingml.document.main+xml")),
            Rels("_rels/.rels", ("officeDocument", "word/document.xml")),
            ("word/document.xml",
                $"<w:document xmlns:w=\"{w}\"><w:body>" +
                string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>")) +
                "</w:body></w:document>"));
    }

    /// <summary>One sheet: a header row of shared strings, then an inline string and a number.</summary>
    private static byte[] Xlsx()
    {
        const string s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return Zip(
            ContentTypes(("/xl/workbook.xml", "spreadsheetml.sheet.main+xml"), ("/xl/worksheets/sheet1.xml", "spreadsheetml.worksheet+xml"), ("/xl/sharedStrings.xml", "spreadsheetml.sharedStrings+xml")),
            Rels("_rels/.rels", ("officeDocument", "xl/workbook.xml")),
            Rels("xl/_rels/workbook.xml.rels", ("worksheet", "worksheets/sheet1.xml"), ("sharedStrings", "sharedStrings.xml")),
            ("xl/workbook.xml", $"<workbook xmlns=\"{s}\" xmlns:r=\"{OfficeRels}\"><sheets><sheet name=\"Listino\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/sharedStrings.xml", $"<sst xmlns=\"{s}\" count=\"2\" uniqueCount=\"2\"><si><t>Articolo</t></si><si><t>Prezzo</t></si></sst>"),
            ("xl/worksheets/sheet1.xml",
                $"<worksheet xmlns=\"{s}\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Vite M8</t></is></c><c r=\"B2\"><v>12.5</v></c></row>" +
                "</sheetData></worksheet>"));
    }

    /// <summary>Workbook with one inline-string cell per row in each sheet.</summary>
    private static byte[] Xlsx(params (string Name, string[] Rows)[] sheets)
    {
        const string s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheetParts = sheets.Select((sheet, i) => ($"xl/worksheets/sheet{i + 1}.xml",
            $"<worksheet xmlns=\"{s}\"><sheetData>" +
            string.Concat(sheet.Rows.Select((text, r) => $"<row r=\"{r + 1}\"><c r=\"A{r + 1}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(text)}</t></is></c></row>")) +
            "</sheetData></worksheet>")).ToArray();
        var parts = new List<(string, string)>
        {
            ContentTypes(new[] { ("/xl/workbook.xml", "spreadsheetml.sheet.main+xml") }
                .Concat(sheetParts.Select(p => ("/" + p.Item1, "spreadsheetml.worksheet+xml"))).ToArray()),
            Rels("_rels/.rels", ("officeDocument", "xl/workbook.xml")),
            Rels("xl/_rels/workbook.xml.rels", sheetParts.Select(p => ("worksheet", p.Item1.Substring("xl/".Length))).ToArray()),
            ("xl/workbook.xml", $"<workbook xmlns=\"{s}\" xmlns:r=\"{OfficeRels}\"><sheets>" +
                string.Concat(sheets.Select((sheet, i) => $"<sheet name=\"{sheet.Name}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) +
                "</sheets></workbook>")
        };
        parts.AddRange(sheetParts);
        return Zip(parts.ToArray());
    }

    private static byte[] Pptx(params string[] paragraphs)
    {
        const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        return Zip(
            ContentTypes(("/ppt/presentation.xml", "presentationml.presentation.main+xml"), ("/ppt/slides/slide1.xml", "presentationml.slide+xml")),
            Rels("_rels/.rels", ("officeDocument", "ppt/presentation.xml")),
            Rels("ppt/_rels/presentation.xml.rels", ("slide", "slides/slide1.xml")),
            ("ppt/presentation.xml", $"<p:presentation xmlns:p=\"{p}\" xmlns:r=\"{OfficeRels}\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst></p:presentation>"),
            ("ppt/slides/slide1.xml",
                $"<p:sld xmlns:p=\"{p}\" xmlns:a=\"{a}\"><p:cSld><p:spTree><p:sp><p:txBody><a:bodyPr/>" +
                string.Concat(paragraphs.Select(t => $"<a:p><a:r><a:t>{System.Security.SecurityElement.Escape(t)}</a:t></a:r></a:p>")) +
                "</p:txBody></p:sp></p:spTree></p:cSld></p:sld>"));
    }

    /// <summary>ODF text document: a heading, a paragraph and a table of one row.</summary>
    private static byte[] Odt()
    {
        const string ns = "urn:oasis:names:tc:opendocument:xmlns:";
        return Zip(("content.xml",
            $"<office:document-content xmlns:office=\"{ns}office:1.0\" xmlns:text=\"{ns}text:1.0\" xmlns:table=\"{ns}table:1.0\"><office:body><office:text>" +
            "<text:h>Verbale</text:h><text:p>Presenti: Rossi, Bianchi.</text:p>" +
            "<table:table><table:table-row><table:table-cell><text:p>Voce</text:p></table:table-cell><table:table-cell><text:p>Importo</text:p></table:table-cell></table:table-row></table:table>" +
            "</office:text></office:body></office:document-content>"));
    }

    internal static (string Name, string Xml) ContentTypes(params (string Part, string Type)[] parts) => ("[Content_Types].xml",
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        string.Concat(parts.Select(p => $"<Override PartName=\"{p.Part}\" ContentType=\"{OfficeTypes}{p.Type}\"/>")) + "</Types>");

    internal static (string Name, string Xml) Rels(string name, params (string Type, string Target)[] targets) => (name,
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        string.Concat(targets.Select((t, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"{OfficeRels}/{t.Type}\" Target=\"{t.Target}\"/>")) + "</Relationships>");

    internal static byte[] Zip(params (string Name, string Xml)[] parts)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml);
            }
        }
        return memory.ToArray();
    }

    /// <summary>CMS envelope (as a .p7m carries it) around the content, signed with a throw-away certificate.</summary>
    internal static byte[] Signed(byte[] content)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test OnlyWorkspace", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var cms = new SignedCms(new ContentInfo(content));
        cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate));
        return cms.Encode();
    }

    /// <summary>Outlook .msg (MS-OXMSG): string properties as streams, the sent time in the property stream, one attachment.</summary>
    internal static byte[] Msg(DateTime sent, string attachmentName, byte[] attachment)
    {
        var path = Path.Combine(Path.GetTempPath(), "ow-test-" + Guid.NewGuid().ToString("N") + ".msg");
        try
        {
            using (var root = RootStorage.Create(path))
            {
                // 32-byte header, then 16-byte entries: tag, flags, value (PR_CLIENT_SUBMIT_TIME).
                var properties = new byte[48];
                BitConverter.GetBytes(0x00390040u).CopyTo(properties, 32);
                BitConverter.GetBytes(sent.ToFileTimeUtc()).CopyTo(properties, 40);
                Write(root, "__properties_version1.0", properties);
                Text(root, "0037", "Offerta 12");
                Text(root, "0C1A", "Mario Rossi");
                Text(root, "0C1F", "mario@example.com");
                Text(root, "0E04", "Anna Bianchi");
                Text(root, "1000", "In allegato le condizioni.");

                var file = root.CreateStorage("__attach_version1.0_#00000000");
                Text(file, "3707", attachmentName);
                Write(file, "__substg1.0_37010102", attachment);
            }
            return File.ReadAllBytes(path);
        }
        finally
        {
            TestArtifacts.DeleteFile(path);
        }

        static void Text(Storage storage, string id, string value) => Write(storage, "__substg1.0_" + id + "001F", Encoding.Unicode.GetBytes(value));

        static void Write(Storage storage, string name, byte[] bytes)
        {
            using var stream = storage.CreateStream(name);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>One empty page behind the standard security handler, with a password nobody knows.</summary>
    private static byte[] ProtectedPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
            $"<< /Filter /Standard /V 1 /R 2 /Length 40 /P -64 /O <{new string('1', 64)}> /U <{new string('2', 64)}> >>"
        };
        // ASCII only: characters are bytes, so the lengths are the offsets of the cross-reference table.
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Encrypt 4 0 R /ID [<{new string('A', 32)}> <{new string('A', 32)}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
