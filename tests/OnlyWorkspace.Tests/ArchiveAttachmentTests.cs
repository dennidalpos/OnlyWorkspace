using System.Text;
using System.Security.Cryptography;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Archive;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveAttachmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_AttachmentIndex_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true); }

    [Fact]
    public async Task MixedPdf_RealArchiveReader_RejectsWholeTextAndCard()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "mixed.pdf");
        File.WriteAllBytes(path, PdfLayoutExtractionTests.MixedPdf(false).Build());
        var attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("mixed.pdf"), "1", path, default);
        Assert.Equal("scanned", attachment.State);
        var body = new ArchiveBody { Text = "Original email" };
        ArchiveAttachmentReader.Add(body, attachment);
        Assert.True(body.AttachmentsLimited);
        Assert.Throws<InvalidOperationException>(() => ArchiveAttachmentReader.WholeText(body));
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "f", "Inbox");
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "e", HasAttachments = true, ReceivedUtc = DateTime.UtcNow } }, true);
        long id = db.NextPendingBodies(1).Single().Id;
        db.SaveBody(id, body);
        Assert.Null(db.GetAttachmentText(attachment.Version!));
    }

    [Fact]
    public void SaveBodies_StoresTheWholeGroupInOneTransaction_AndRepeatedRequestsAreIdempotent()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "f", "Inbox");
        db.AddHeaders(folder, new[]
        {
            new ArchiveHeader { EntryId = "a", ReceivedUtc = DateTime.UtcNow },
            new ArchiveHeader { EntryId = "b", ReceivedUtc = DateTime.UtcNow.AddMinutes(-1) }
        }, true);
        var pending = db.NextPendingBodies(5);
        Assert.Equal(2, pending.Count);
        var items = pending.Select((p, i) => new ArchiveBodyItem { EmailId = p.Id, Body = new ArchiveBody { Text = "groupword" + i } }).ToList();
        db.SaveBodies(items, "00000000000000000000000000000001");
        db.SaveBodies(items, "00000000000000000000000000000001");
        Assert.Empty(db.NextPendingBodies(5));
        Assert.Single(db.Search(new ArchiveQuery { Text = "groupword0" }));
        Assert.Single(db.Search(new ArchiveQuery { Text = "groupword1" }));
    }

    [Fact]
    public void SaveBodies_FailureRollsBackEveryBodyOfTheGroup()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "f", "Inbox");
        db.AddHeaders(folder, new[]
        {
            new ArchiveHeader { EntryId = "a", ReceivedUtc = DateTime.UtcNow },
            new ArchiveHeader { EntryId = "b", ReceivedUtc = DateTime.UtcNow.AddMinutes(-1) }
        }, true);
        var pending = db.NextPendingBodies(5);
        var stale = new ArchiveBody { Text = "staleword" };
        stale.Attachments.Add(new ArchiveAttachment { Name = "x.txt", SourceId = "1", State = "indexed", Text = "t", ReaderRevision = ArchiveAttachment.CurrentReaderRevision - 1 });
        var items = new[]
        {
            new ArchiveBodyItem { EmailId = pending[0].Id, Body = new ArchiveBody { Text = "goodword" } },
            new ArchiveBodyItem { EmailId = pending[1].Id, Body = stale }
        };
        Assert.Throws<InvalidOperationException>(() => db.SaveBodies(items));
        Assert.Equal(2, db.NextPendingBodies(5).Count);
        Assert.Empty(db.Search(new ArchiveQuery { Text = "goodword" }));
    }

    [Fact]
    public void GlobalAttachmentSwitch_RemovesTermsAndRequeuesOriginals()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        db.ConfigureStorage(true);
        var folder = db.GetOrAddFolder("store", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "mail", HasAttachments = true, ReceivedUtc = DateTime.UtcNow } }, true);
        long id = db.NextPendingBodies(1).Single().Id;
        var body = new ArchiveBody { Text = "messagebody" };
        body.Attachments.Add(new ArchiveAttachment { Name = "report.txt", SourceId = "1", State = "indexed", Text = "uniqueattachmentterm" });
        db.SaveBody(id, body);
        Assert.Single(db.Search(new ArchiveQuery { Text = "uniqueattachmentterm" }));
        Assert.Empty(db.Search(new ArchiveQuery { Text = "uniqueattachmentterm", IncludeAttachmentText = false }));

        db.ConfigureStorage(false);
        Assert.Empty(db.Search(new ArchiveQuery { Text = "uniqueattachmentterm" }));
        Assert.Equal("report.txt", db.Search(new ArchiveQuery { Text = "messagebody" }).Single().Attachments.Single().Name);

        db.ConfigureStorage(true);
        Assert.Equal(id, db.NextPendingBodies(1).Single().Id);
        db.SaveBody(id, body);
        Assert.Single(db.Search(new ArchiveQuery { Text = "uniqueattachmentterm" }));
    }

    [Fact]
    public async Task RealExtractor_IndexesAttachmentWords_RetainsEncryptedOriginal_AndInvalidatesOnChange()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "document.txt");
        File.WriteAllText(path, "attachmentuniqueword and invoice GAMMA-3333", new UTF8Encoding(false));
        var attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("document.txt"), "part1", path, default);
        Assert.Equal("indexed", attachment.State); Assert.NotNull(attachment.Version);
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "f", "Inbox");
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "e", Subject = "Email", ReceivedUtc = DateTime.UtcNow } }, true);
        long id = db.NextPendingBodies(1).Single().Id;
        var body = new ArchiveBody { Text = "Email body" };
        ArchiveAttachmentReader.Add(body, attachment);
        db.SaveBody(id, body);
        var hit = db.Search(new ArchiveQuery { Text = "attachmentuniqueword" }).Single();
        Assert.Equal("s", hit.StoreId);
        Assert.Equal("e", hit.EntryId);
        Assert.Equal("part1", hit.Attachments.Single().SourceId);
        Assert.Equal(attachment.Version, hit.Attachments.Single().Version);
        Assert.Empty(hit.Attachments.Single().Text);
        Assert.Contains("GAMMA-3333", ArchiveAttachmentReader.WholeText(body));
        Assert.DoesNotContain("GAMMA-3333", ArchiveAttachmentReader.WholeText(body, false));
        string oldVersion = hit.BodyVersion!;
        File.WriteAllText(path, "replacementattachmentword and revised terms", new UTF8Encoding(false));
        var replacement = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("document.txt"), "part1", path, default);
        Assert.NotEqual(attachment.Version, replacement.Version);
        body.Attachments[0] = replacement;
        db.SaveBody(id, body);
        Assert.Empty(db.Search(new ArchiveQuery { Text = "attachmentuniqueword" }));
        hit = db.Search(new ArchiveQuery { Text = "replacementattachmentword" }).Single();
        Assert.Equal(replacement.Version, hit.Attachments.Single().Version);
        Assert.NotEqual(oldVersion, hit.BodyVersion);
    }

    [Fact]
    public async Task LongSyntheticDocument_IndexesWholeText_AndRecoversWholeOriginal()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "long-document.txt");
        File.WriteAllText(path, "leadingtoken " + new string('x', 61000) + " trailingtoken", new UTF8Encoding(false));
        var attachment = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("long-document.txt"), "part2", path, default);
        Assert.Equal("indexed", attachment.State);
        Assert.True(attachment.Text.Length > 60000);
        Assert.Equal(Convert.ToBase64String(SHA256.Create().ComputeHash(File.ReadAllBytes(path))), attachment.Version);

        var body = new ArchiveBody { Text = "Original email body" };
        ArchiveAttachmentReader.Add(body, attachment);
        using (var db = ArchiveDb.Open(_directory, "p"))
        {
            var folder = db.GetOrAddFolder("store", "inbox", "Inbox");
            db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "mail", ReceivedUtc = DateTime.UtcNow } }, true);
            db.SaveBody(db.NextPendingBodies(1).Single().Id, body);
            Assert.Single(db.Search(new ArchiveQuery { Text = "leadingtoken" }));
            Assert.Single(db.Search(new ArchiveQuery { Text = "trailingtoken" }));
            var hit = db.Search(new ArchiveQuery { Text = "leadingtoken" }).Single();
            Assert.Equal("store", hit.StoreId);
            Assert.Equal("mail", hit.EntryId);
            Assert.False(hit.Attachments.Single().TextLimited);
            Assert.Equal(attachment.Version, hit.Attachments.Single().Version);
            Assert.Equal(0, db.GetStats().AttachmentTextsLimited);
        }

        string whole = ArchiveAttachmentReader.WholeText(body);
        Assert.Contains("trailingtoken", whole);
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        var client = new ArchiveClient("p", _directory);
        await client.OpenAsync(true);
        await client.HeartbeatAsync("idle", null);
        string request = await client.RequestOriginalAsync("mail", "store");
        Assert.Equal(request, (await client.OriginalRequestsAsync()).Single().Id);
        await client.CompleteOriginalAsync(request, whole, null);
        Assert.Equal(whole, (await client.OriginalResultAsync(request)).Text);

        request = await client.RequestOriginalAsync("mail", "store");
        Assert.Equal(request, (await client.OriginalRequestsAsync()).Single().Id);
        await client.CancelOriginalAsync(request);
        await client.CompleteOriginalAsync(request, whole, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OriginalResultAsync(request));
    }

    [Fact]
    public async Task ExclusionsAndWholeTextLimit_AreExplicit_AndNeverReturnAPartialAttachment()
    {
        var unsupported = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("legacy.ppt"), "1", null, default);
        Assert.Equal("reference", unsupported.State);
        Assert.Equal("unsupported", unsupported.Problem);
        var blocked = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("document.txt") { ScanStatus = AttachmentScanStatus.Removed }, "2", null, default);
        Assert.Equal("excluded", blocked.State);
        var missing = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("document.txt"), "3", null, default);
        Assert.Equal("retryable", missing.State);
        var body = new ArchiveBody { Text = "body" };
        ArchiveAttachmentReader.Add(body, missing);
        Assert.True(body.AttachmentsLimited);
        Assert.Throws<InvalidOperationException>(() => ArchiveAttachmentReader.WholeText(body));
        Assert.Equal("body", ArchiveAttachmentReader.WholeText(body, false));

        var oversized = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("oversized.txt", 30L * 1024 * 1024 + 1), "4", null, default);
        Assert.Equal("limited", oversized.State);
        var combined = new ArchiveBody { Text = "body" };
        ArchiveAttachmentReader.Add(combined, new ArchiveAttachment
        {
            Name = "combined.txt",
            SourceId = "5",
            State = "indexed",
            Text = new string('x', 4_000_000)
        });
        Assert.True(combined.AttachmentsLimited);
        Assert.Equal("limited", combined.Attachments.Single().State);
        Assert.Empty(combined.Attachments.Single().Text);
        Assert.Throws<InvalidOperationException>(() => ArchiveAttachmentReader.WholeText(combined));
    }

    /// <summary>A21: the tray shows the body and the attachments read whole, and names those that were not.</summary>
    [Fact]
    public void ReadableText_ShowsTheBody_AndNamesTheAttachmentsNotReadWhole()
    {
        var body = new ArchiveBody { Text = "Corpo leggibile" };
        ArchiveAttachmentReader.Add(body, new ArchiveAttachment { SourceId = "1", Name = "letto.txt", State = "indexed", Text = "testo allegato" });
        ArchiveAttachmentReader.Add(body, new ArchiveAttachment { SourceId = "2", Name = "enorme.pdf", State = "limited", Problem = "oltre il limite di 30 MB" });
        ArchiveAttachmentReader.Add(body, new ArchiveAttachment { SourceId = "3", Name = "segreto.zip", State = "protected" });
        ArchiveAttachmentReader.Add(body, new ArchiveAttachment { SourceId = "4", Name = "logo.png", State = "excluded" });
        Assert.Throws<InvalidOperationException>(() => ArchiveAttachmentReader.WholeText(body));

        string text = ArchiveAttachmentReader.ReadableText(body);
        Assert.StartsWith("Corpo leggibile", text);
        Assert.Contains("--- Allegato: letto.txt ---\ntesto allegato", text);
        Assert.Contains("Allegati non letti per intero (2)", text);
        Assert.Contains("enorme.pdf: oltre il limite di 30 MB", text);
        Assert.Contains("segreto.zip: protetto da password", text);
        Assert.DoesNotContain("logo.png", text);
        Assert.Throws<InvalidOperationException>(() => ArchiveAttachmentReader.ReadableText(new ArchiveBody { Unavailable = true }));
    }

    [Fact]
    public async Task CancelledSyntheticRead_DoesNotProduceAnAttachment()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "cancelled.txt");
        File.WriteAllText(path, "private document", new UTF8Encoding(false));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("cancelled.txt"), "part1", path, stop.Token));
    }

    [Fact]
    public void AttachmentTermLimit_AndMetadataStayVisible_AfterRestart()
    {
        using (var db = ArchiveDb.Open(_directory, "p"))
        {
            var folder = db.GetOrAddFolder("s", "f", "Inbox");
            db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "e", ReceivedUtc = DateTime.UtcNow } }, true);
            long id = db.NextPendingBodies(1).Single().Id;
            var body = new ArchiveBody();
            ArchiveAttachmentReader.Add(body, new ArchiveAttachment { SourceId = "1", Name = "document.txt", State = "indexed", Version = "v", Text = "keyword " + new string('x', 61000) });
            db.SaveBody(id, body);
        }
        using var reopened = ArchiveDb.Open(_directory, "p");
        Assert.False(reopened.Search(new ArchiveQuery { Text = "keyword" }).Single().Attachments.Single().TextLimited);
    }
}
