using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveAttachmentPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ow-archive-policy-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) TestArtifacts.DeleteDirectory(_dir, true); }

    [Theory]
    [InlineData("ZIP", "archive-container")]
    [InlineData("rar", "archive-container")]
    [InlineData("7z", "archive-container")]
    [InlineData("tar", "archive-container")]
    [InlineData("exe", "executable")]
    [InlineData("msi", "executable")]
    [InlineData("bin", "unsupported")]
    public async Task ReferenceDoesNotCopyParseOrConsultTheTextCache(string extension, string reason)
    {
        bool cacheRead = false;
        var attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("original." + extension, 123), "part1", null, default,
            (_, _) => { cacheRead = true; return Task.FromResult<string?>("old extracted content"); });
        Assert.Equal("reference", attachment.State);
        Assert.Equal(reason, attachment.Problem);
        Assert.Equal(123, attachment.Size);
        Assert.Equal(extension.ToLowerInvariant(), attachment.Type);
        Assert.Empty(attachment.Text);
        Assert.Null(attachment.Version);
        Assert.False(cacheRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignatureCannotTurnRenamedArchiveOrExecutableIntoAnIndexedText(bool executable)
    {
        string path = Write("document.txt", executable ? new byte[] { 0x4d, 0x5a, 1, 2 } : Zip("secret.txt", "hiddencontent"));
        var attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("document.txt", new FileInfo(path).Length), "1", path, default,
            (_, _) => throw new InvalidOperationException("An excluded signature must not use the cache."));
        Assert.Equal("reference", attachment.State);
        Assert.Equal(executable ? "executable" : "archive-container", attachment.Problem);
        Assert.Empty(attachment.Text);
    }

    [Fact]
    public async Task ReferencesKeepEmailIdentityAndMetadataAcrossReopenAndStoragePolicyChanges()
    {
        var attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("contratto.zip", 321), "part7", null, default);
        var body = new ArchiveBody { Text = "Readable body" };
        ArchiveAttachmentReader.Add(body, attachment);
        Assert.False(body.AttachmentsLimited);
        Assert.Contains("321 byte; contenuto non estratto", ArchiveAttachmentReader.WholeText(body));
        using (var db = ArchiveDb.Open(_dir, "p"))
        {
            db.ConfigureStorage(true);
            long id = Add(db, body);
            Assert.Equal("reference", db.Search(new ArchiveQuery { Text = "contratto" }).Single().Attachments.Single().State);
            Assert.Equal(0, db.AttachmentContentCount);
            Assert.DoesNotContain(db.NextPendingBodies(10), mail => mail.Id == id);
        }
        using var reopened = ArchiveDb.Open(_dir, "p");
        var hit = reopened.Search(new ArchiveQuery { Text = "contratto" }).Single();
        Assert.Equal("store", hit.StoreId); Assert.Equal("email", hit.EntryId);
        var saved = hit.Attachments.Single();
        Assert.Equal("part7", saved.SourceId); Assert.Equal(321, saved.Size); Assert.Equal("zip", saved.Type);
        var original = reopened.GetOriginal("email", "store")!;
        Assert.Equal(321, original.Attachments.Single().Size);
        Assert.Contains("Solo riferimento", ArchiveAttachmentReader.ReadableText(original));
        reopened.ConfigureStorage(false);
        saved = reopened.GetOriginal("email", "store")!.Attachments.Single();
        Assert.Equal(321, saved.Size); Assert.Equal("zip", saved.Type);
    }

    [Theory]
    [InlineData("eml")]
    [InlineData("msg")]
    [InlineData("p7m")]
    public async Task AttachedEmailAndSignedDocumentRetainReadableTextAndOnlyNameCompressedChildren(string format)
    {
        byte[] zip = Zip("inner.txt", "hiddencompressedcontent");
        byte[] contents = format switch
        {
            "msg" => AttachmentExtractionTests.Msg(DateTime.UtcNow, "documenti.zip", zip),
            "p7m" => AttachmentExtractionTests.Signed(Encoding.UTF8.GetBytes("Readable signed content with a finalmarker")),
            _ => Eml("documenti.zip", zip)
        };
        string name = format == "p7m" ? "atto.txt.p7m" : "posta." + format;
        string path = Write(name, contents);
        var attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts(name, contents.Length), "1", path, default);
        Assert.Equal("indexed", attachment.State);
        Assert.False(attachment.TextLimited);
        Assert.DoesNotContain("hiddencompressedcontent", attachment.Text);
        if (format == "p7m") Assert.Contains("finalmarker", attachment.Text);
        else
        {
            Assert.Contains("documenti.zip", attachment.Text);
            Assert.Contains("solo riferimento", attachment.Text);
            Assert.Contains("contenuto non estratto", attachment.Text);
            Assert.Contains(format == "eml" ? "Readable email body" : "In allegato le condizioni", attachment.Text);
        }
        using var db = ArchiveDb.Open(_dir, "p");
        var body = new ArchiveBody { Text = "Main body" };
        ArchiveAttachmentReader.Add(body, attachment);
        Add(db, body);
        Assert.Empty(db.Search(new ArchiveQuery { Text = "hiddencompressedcontent" }));
        Assert.Single(db.Search(new ArchiveQuery { Text = format == "p7m" ? "finalmarker" : "documenti" }));
    }

    [Fact]
    public async Task OfficePackagesAndReadableChildrenAreStillReadToTheEnd()
    {
        string path = Write("posta.eml", Eml("document.txt", Encoding.UTF8.GetBytes("leadingmarker " + new string('x', 61000) + " finalmarker")));
        var attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("posta.eml"), "1", path, default);
        Assert.Equal("indexed", attachment.State);
        Assert.Contains("finalmarker", attachment.Text);
        Assert.Null(AttachmentTextFormats.ArchiveReferenceReason("docx", "zip"));
        Assert.Null(AttachmentTextFormats.ArchiveReferenceReason("xlsx", "zip"));
        Assert.Null(AttachmentTextFormats.ArchiveReferenceReason("odt", "zip"));
        // A real OOXML package follows the document reader, rather than being treated as a generic ZIP.
        var docx = AttachmentExtractionTests.Docx("Readable office finalmarker");
        path = Write("document.docx", docx);
        attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("document.docx"), "2", path, default);
        Assert.Equal("indexed", attachment.State);
        Assert.Contains("office finalmarker", attachment.Text);
    }

    [Fact]
    public async Task SecurityInspectionStillVisitsCompressedContent()
    {
        string path = Write("documenti.zip", Zip("pagina.html", "<a href=\"https://example.invalid/login\">Accedi</a>"));
        var attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("documenti.zip"), "1", path, default);
        Assert.Equal("reference", attachment.State);
        var inspection = await AttachmentTextExtractor.InspectAsync(path, TimeSpan.FromSeconds(30), default);
        Assert.Null(inspection.Error);
        Assert.False(inspection.Truncated);
        Assert.Contains(inspection.Links, link => link.Destination == "https://example.invalid/login");
    }

    [Fact]
    public async Task SignedCompressedDocumentIsAReferenceAndNeverContributesText()
    {
        byte[] signed = AttachmentExtractionTests.Signed(Zip("hidden.txt", "hiddencompressedcontent"));
        string path = Write("documenti.zip.p7m", signed);
        var attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("documenti.zip.p7m", signed.Length), "1", path, default);
        Assert.Equal("reference", attachment.State);
        Assert.Equal("archive-container", attachment.Problem);
        Assert.Empty(attachment.Text);
    }

    [Fact]
    public void ReferenceIsNeverReportedAsAReadDocument()
    {
        var outcome = new DocumentOutcome { Name = "documenti.zip", Status = DocumentStatus.Reference };
        Assert.False(outcome.WasRead);
        Assert.Contains("contenuto non estratto", outcome.Reason);
    }

    [Fact]
    public void UpgradeDoesNotServeOldCompressedTermsAndPreservesTheOriginal()
    {
        using (var db = ArchiveDb.Open(_dir, "p"))
        {
            var body = new ArchiveBody { Text = "preservedbody" };
            body.Attachments.Add(new ArchiveAttachment { Name = "documenti.zip", SourceId = "1", Version = "oldhash", State = "indexed", Text = "oldcompressedterm ORD-12345" });
            long id = Add(db, body);
            while (db.NextFactWork() is { } fact) Assert.True(db.CommitFacts(fact, ArchiveDb.ExtractFacts(fact, default)));
            while (db.NextSearchWork() is { } search)
                Assert.True(db.CommitSearch(search, ArchiveSearchText.CodeTerms(search.Body), ArchiveSearchText.CodeTerms(search.Attachments)));
            Assert.Single(db.Search(new ArchiveQuery { Text = "ORD-12345" }));
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ArchiveDb.EngineFileOf(_dir, "p"), Password = ArchiveKey.LoadOrCreate(Path.Combine(_dir, "archive.key")), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // Reproduce the exact previous metadata/reader contract, including absent new columns.
            command.CommandText = "alter table attachments drop column size; alter table attachments drop column type; update attachments set reader_revision=1; update attachment_contents set reader_revision=1; update meta set value='1' where key='attachment_reader_revision';";
            command.ExecuteNonQuery();
        }
        using var reopened = ArchiveDb.Open(_dir, "p");
        Assert.Empty(reopened.Search(new ArchiveQuery { Text = "oldcompressedterm" }));
        Assert.Empty(reopened.Search(new ArchiveQuery { Text = "ORD-12345" }));
        Assert.Single(reopened.Search(new ArchiveQuery { Text = "preservedbody" }));
        Assert.Null(reopened.GetAttachmentText("oldhash"));
        var original = reopened.GetOriginal("email", "store")!;
        Assert.Equal("preservedbody", original.Text);
        Assert.Null(original.Attachments.Single().Size);
        Assert.Empty(original.Attachments.Single().Type);
        Assert.Contains(reopened.NextPendingBodies(10), mail => mail.EntryId == "email");

    }

    private string Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static long Add(ArchiveDb db, ArchiveBody body)
    {
        var folder = db.GetOrAddFolder("store", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "email", HasAttachments = true, ReceivedUtc = DateTime.UtcNow } }, true);
        long id = db.NextPendingBodies(1).Single().Id;
        db.SaveBody(id, body);
        return id;
    }

    private static byte[] Zip(string name, string text)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var entry = zip.CreateEntry(name).Open())
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            entry.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private static byte[] Eml(string name, byte[] bytes) => Encoding.ASCII.GetBytes(
        "From: sender@example.invalid\r\nTo: recipient@example.invalid\r\nSubject: Attached documents\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=part\r\n\r\n" +
        "--part\r\nContent-Type: text/plain\r\n\r\nReadable email body\r\n--part\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"" + name + "\"\r\n" +
        "Content-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(bytes) + "\r\n--part--\r\n");
}
