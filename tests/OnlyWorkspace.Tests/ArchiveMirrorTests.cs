using System.Security.Cryptography;
using System.Text;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveMirrorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ow-mirror-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) TestArtifacts.DeleteDirectory(_dir, true); }
    private static ArchiveHeader Header(string id, string mid = "<shared@example.test>") => new()
    { EntryId = id, MessageId = mid, Subject = "Synthetic", ReceivedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
    private static ArchiveBody Body(string text = "Complete original")
    {
        var body = new ArchiveBody { Text = text };
        body.Attachments.Add(new ArchiveAttachment { SourceId = "1", Name = "shared.txt", State = "indexed", Version = "file-sha256", Text = "sharedattachmentmarker" });
        return body;
    }

    [Fact]
    public void WholeOriginalSurvivesRestart_AndBothIndexesSearchPast60000()
    {
        string text = "beginning " + new string('x', 61000) + " finalbodymarker";
        using (var db = ArchiveDb.Open(_dir, "p"))
        {
            var f = db.GetOrAddFolder("s", "f", "Inbox");
            db.AddHeaders(f, new[] { Header("e") }, true, true);
            var body = Body(text);
            body.Attachments[0].Text += " " + new string('y', 61000) + " finalattachmentmarker";
            db.SaveBody(db.NextPendingBodies(1).Single().Id, body);
        }
        using var reopened = ArchiveDb.Open(_dir, "p");
        Assert.Equal(text, reopened.GetOriginal("e", "s")!.Text);
        Assert.Equal("<shared@example.test>", reopened.GetOriginal("e", "s")!.MessageId);
        Assert.Single(reopened.Search(new ArchiveQuery { Text = "finalbodymarker" }));
        Assert.Single(reopened.Search(new ArchiveQuery { Text = "finalattachmentmarker" }));
        Assert.Contains("finalattachmentmarker", reopened.GetOriginal("e", "s")!.Attachments.Single().Text);
    }

    [Fact]
    public void LastCopyOwnsLifetime_SharedAttachmentSurvivesUntilThen()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        var a = db.GetOrAddFolder("s1", "inbox", "Inbox");
        var b = db.GetOrAddFolder("s2", "trash", "Posta eliminata");
        db.AddHeaders(a, new[] { Header("a") }, true, true);
        db.AddHeaders(b, new[] { Header("b") }, true, true);
        foreach (var item in db.NextPendingBodies(10))
        {
            db.SaveBody(item.Id, Body());
        }
        Assert.Equal(1, db.AttachmentContentCount);
        db.BeginRescan(new[] { a.Id, b.Id });
        db.AddHeaders(a, Array.Empty<ArchiveHeader>(), true, true);
        db.AddHeaders(b, new[] { Header("b") }, true, true);
        Assert.Equal(1, db.FinishRescan());
        Assert.Null(db.GetOriginal("a", "s1"));
        Assert.Single(db.Search(new ArchiveQuery()));
        Assert.Empty(db.PendingDeletions());
        Assert.Equal(1, db.AttachmentContentCount);
        db.BeginRescan(new[] { a.Id, b.Id });
        db.AddHeaders(a, Array.Empty<ArchiveHeader>(), true, true);
        db.AddHeaders(b, Array.Empty<ArchiveHeader>(), true, true);
        Assert.Equal(1, db.FinishRescan());
        Assert.Empty(db.Search(new ArchiveQuery()));
        Assert.Equal(0, db.AttachmentContentCount);
        Assert.Equal(2, db.PendingDeletions().Count);
        Assert.All(db.PendingDeletions(), source => Assert.Equal("p", source.OutlookProfile));
    }

    [Fact]
    public async Task DeletionLedgerRemovesWholeConversation_RejectsStaleSave_AllowsNewAcquisition()
    {
        var storage = new DpapiHistoryStorageService(_dir);
        var before = DateTime.UtcNow.AddMinutes(-1);
        var conversation = new ConversationHistory();
        conversation.ContextReferences.Add(new HistoryContextReference { EntryId = "e", StoreId = "s", OutlookProfile = "p", MessageId = "m", CapturedUtc = before });
        conversation.Messages.Add(new HistoryChatMessage { Role = "assistant", Content = "Private paraphrase" });
        var independent = new ConversationHistory();
        independent.Messages.Add(new HistoryChatMessage { Role = "user", Content = "Independent text" });
        await storage.SaveConversationAsync(conversation);
        await storage.SaveConversationAsync(independent);
        var deletion = new ArchiveDeletedSource { OutlookProfile = "p", StoreId = "s", EntryId = "e", MessageId = "m", DeletedUtc = DateTime.UtcNow };
        await storage.InvalidateSourcesAsync(new[] { deletion });
        Assert.Null(await storage.LoadConversationAsync(conversation.Id));
        Assert.False(File.Exists(Path.Combine(_dir, conversation.Id + ".bin")));
        Assert.NotNull(await storage.LoadConversationAsync(independent.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveConversationAsync(conversation));
        var reopened = new DpapiHistoryStorageService(_dir);
        Assert.False(await reopened.IsValidAsync(conversation));
        conversation.ContextReferences[0].CapturedUtc = deletion.DeletedUtc.AddSeconds(1);
        await reopened.SaveConversationAsync(conversation);
        Assert.NotNull(await reopened.LoadConversationAsync(conversation.Id));
        Assert.DoesNotContain("Private", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_dir, "deleted-sources.dat"))));
    }

    [Fact]
    public async Task IndependentHistoryWritersRacingDeletionCannotResurrectTheConversation()
    {
        var conversation = new ConversationHistory();
        conversation.ContextReferences.Add(new HistoryContextReference { EntryId = "e", StoreId = "s", OutlookProfile = "p", CapturedUtc = DateTime.UtcNow.AddMinutes(-1) });
        conversation.Messages.Add(new HistoryChatMessage { Content = "Synthetic concurrent paraphrase" });
        var storage = new DpapiHistoryStorageService(_dir);
        await storage.SaveConversationAsync(conversation);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try { await new DpapiHistoryStorageService(_dir).SaveConversationAsync(conversation); }
            catch (InvalidOperationException) { }
        })).ToArray();
        var deletion = Task.Run(async () =>
        {
            await start.Task;
            await storage.InvalidateSourcesAsync(new[] { new ArchiveDeletedSource { EntryId = "e", StoreId = "s", OutlookProfile = "p", DeletedUtc = DateTime.UtcNow } });
        });
        start.SetResult(true);
        await Task.WhenAll(writers.Append(deletion));
        Assert.Null(await storage.LoadConversationAsync(conversation.Id));
        Assert.False(File.Exists(Path.Combine(_dir, conversation.Id + ".bin")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorruptHistory_DoesNotBlockReadableCleanup_AndRetryAfterRestartRecovers(bool corruptFirst)
    {
        var storage = new DpapiHistoryStorageService(_dir);
        var dependent = new ConversationHistory { Id = "dependent" };
        dependent.ContextReferences.Add(new HistoryContextReference { OutlookProfile = "p", StoreId = "s", EntryId = "e", CapturedUtc = DateTime.UtcNow.AddMinutes(-1) });
        var unrelated = new ConversationHistory { Id = "unrelated" };
        var recoverable = new ConversationHistory { Id = "recoverable" };
        Directory.CreateDirectory(_dir);
        string corrupt = Path.Combine(_dir, "recoverable.bin");
        if (corruptFirst) await storage.SaveConversationAsync(recoverable);
        await storage.SaveConversationAsync(dependent);
        await storage.SaveConversationAsync(unrelated);
        if (!corruptFirst) await storage.SaveConversationAsync(recoverable);
        byte[] backup = File.ReadAllBytes(corrupt);
        File.WriteAllBytes(corrupt, new byte[] { 1, 2, 3 });
        var sources = new[] { new ArchiveDeletedSource { OutlookProfile = "p", StoreId = "s", EntryId = "e", DeletedUtc = DateTime.UtcNow } };
        for (int retry = 0; retry < 2; retry++)
        {
            var error = await Assert.ThrowsAsync<HistoryCleanupIncompleteException>(() => storage.InvalidateSourcesAsync(sources));
            Assert.Contains("recoverable.bin", error.Message);
            Assert.False(File.Exists(Path.Combine(_dir, "dependent.bin")));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(corrupt));
            Assert.NotNull(await storage.LoadConversationAsync(unrelated.Id));
            Assert.False(await storage.IsValidAsync(dependent));
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveConversationAsync(dependent));
            storage = new DpapiHistoryStorageService(_dir);
        }
        File.WriteAllBytes(corrupt, backup);
        await storage.InvalidateSourcesAsync(sources);
        Assert.NotNull(await storage.LoadConversationAsync(recoverable.Id));
        dependent.ContextReferences[0].CapturedUtc = sources[0].DeletedUtc.AddSeconds(1);
        await storage.SaveConversationAsync(dependent);
        Assert.NotNull(await storage.LoadConversationAsync(dependent.Id));
    }

    [Fact]
    public void BusyWalCheckpointKeepsDeletionPendingUntilTheReaderReleasesItsSnapshot()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        var folder = db.GetOrAddFolder("s", "f", "Inbox");
        db.AddHeaders(folder, new[] { Header("e") }, true, true);
        db.SaveBody(db.NextPendingBodies(1).Single().Id, Body());
        using var reader = OpenRaw(); using var command = reader.CreateCommand();
        command.CommandText = "begin; select count(*) from emails"; command.ExecuteScalar();
        db.BeginRescan(new[] { folder.Id }); db.AddHeaders(folder, Array.Empty<ArchiveHeader>(), true, true); db.FinishRescan();
        var ids = db.PendingDeletions().Select(d => d.Id).ToArray();
        Assert.False(db.CompleteDeletions(ids));
        Assert.Single(db.PendingDeletions());
        command.CommandText = "rollback"; command.ExecuteNonQuery();
        Assert.True(db.CompleteDeletions(ids));
        Assert.Empty(db.PendingDeletions());
    }

    [Fact]
    public async Task Engine_DoesNotAcknowledgeUnverifiedHistory_AndRestartCompletesAfterRecovery()
    {
        using (var db = ArchiveDb.Open(_dir, "p"))
        {
            var folder = db.GetOrAddFolder("s", "f", "Inbox");
            db.AddHeaders(folder, new[] { Header("e") }, true, true);
            db.SaveBody(db.NextPendingBodies(1).Single().Id, Body());
            db.BeginRescan(new[] { folder.Id });
            db.AddHeaders(folder, Array.Empty<ArchiveHeader>(), true, true);
            db.FinishRescan();
            Assert.NotEmpty(db.PendingDeletions());
        }
        string historyDir = Path.Combine(_dir, "history");
        var storage = new DpapiHistoryStorageService(historyDir);
        var dependent = new ConversationHistory { Id = "dependent" };
        dependent.ContextReferences.Add(new HistoryContextReference { OutlookProfile = "p", EntryId = "e", StoreId = "s", CapturedUtc = DateTime.UtcNow.AddHours(-1) });
        await storage.SaveConversationAsync(dependent);
        File.WriteAllBytes(Path.Combine(historyDir, "corrupt.bin"), new byte[] { 1 });
        using (var engine = new ArchiveEngine(_dir))
        {
            await engine.Ready;
            await new ArchiveClient("p", _dir).OpenAsync();
        }
        Assert.False(File.Exists(Path.Combine(historyDir, "dependent.bin")));
        using (var db = ArchiveDb.Open(_dir, "p")) Assert.NotEmpty(db.PendingDeletions());
        // Explicit fixture recovery: only this known synthetic damaged file is removed.
        File.Delete(Path.Combine(historyDir, "corrupt.bin"));
        using (var engine = new ArchiveEngine(_dir))
        {
            await engine.Ready;
            await new ArchiveClient("p", _dir).OpenAsync();
        }
        using (var db = ArchiveDb.Open(_dir, "p")) Assert.Empty(db.PendingDeletions());
        Assert.False(await new DpapiHistoryStorageService(historyDir).IsValidAsync(dependent));
    }

    [Fact]
    public void IncompleteInventoryDoesNotPurge_AndClosedStoreKeepsOriginal()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        var f = db.GetOrAddFolder("s", "f", "Inbox");
        db.AddHeaders(f, new[] { Header("e", "") }, true, true);
        db.SaveBody(db.NextPendingBodies(1).Single().Id, Body());
        db.BeginRescan(new[] { f.Id });
        db.AddHeaders(f, Array.Empty<ArchiveHeader>(), true, false);
        db.FinishRescan();
        Assert.NotNull(db.GetOriginal("e", "s"));
        db.SetListedFolders(Array.Empty<long>(), Array.Empty<string>(), new[] { "different-open-store" });
        Assert.NotNull(db.GetOriginal("e", "s"));
        Assert.Empty(db.PendingDeletions());
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("limited")]
    [InlineData("protected")]
    public void NoPartialOrUnreadableAttachmentCanReachAModel(string state)
    {
        var body = Body(); body.Attachments[0].State = state;
        Assert.Throws<InvalidOperationException>(() => ArchiveAttachmentReader.WholeText(body));
    }

    [Fact]
    public void PartialCopyCannotReplaceSharedCompleteText()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        var f = db.GetOrAddFolder("s", "f", "Inbox");
        db.AddHeaders(f, new[] { Header("complete", "a"), Header("partial", "b") }, true, true);
        foreach (var item in db.NextPendingBodies(10).OrderBy(item => item.EntryId))
        {
            var body = Body();
            if (item.EntryId == "partial") { body.Attachments[0].Text = "shared"; body.Attachments[0].State = "limited"; }
            db.SaveBody(item.Id, body);
        }
        Assert.Equal("sharedattachmentmarker", db.GetOriginal("complete", "s")!.Attachments.Single().Text);
        Assert.Equal("sharedattachmentmarker", db.GetAttachmentText("file-sha256"));
        Assert.Equal(1, db.AttachmentContentCount);
    }

    [Fact]
    public void InventoryReusesUnmodifiedOriginal_AndInvalidatesChangedOne()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        var f = db.GetOrAddFolder("s", "f", "Inbox");
        var modified = DateTime.UtcNow;
        var header = Header("e"); header.ModifiedUtc = modified;
        db.AddHeaders(f, new[] { header }, true, true);
        var original = Body(); original.ModifiedUtc = modified;
        db.SaveBody(db.NextPendingBodies(1).Single().Id, original);
        header.RefreshBody = true;
        db.BeginRescan(new[] { f.Id });
        db.AddHeaders(f, new[] { header }, true, true);
        db.FinishRescan();
        Assert.NotNull(db.GetOriginal("e", "s"));
        Assert.Empty(db.NextPendingBodies(10));
        header.ModifiedUtc = modified.AddSeconds(1);
        db.AddHeaders(f, new[] { header }, true, true);
        Assert.Null(db.GetOriginal("e", "s"));
        Assert.Single(db.NextPendingBodies(10));
    }

    [Fact]
    public void BodyAboveModelLimitRemainsPersistedAndSearchable_ButCannotEnterAModelAlone()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        var folder = db.GetOrAddFolder("s", "f", "Inbox");
        db.AddHeaders(folder, new[] { Header("e") }, true, true);
        var body = new ArchiveBody { Text = new string('x', AttachmentTextExtractor.WholeDocumentChars) + " bodylimitmarker" };
        db.SaveBody(db.NextPendingBodies(1).Single().Id, body);
        var stored = db.GetOriginal("e", "s")!;
        Assert.Equal(body.Text, stored.Text);
        Assert.Single(db.Search(new ArchiveQuery { Text = "bodylimitmarker" }));
        Assert.Equal(1, db.GetStats().TextsLimited);
        Assert.Throws<InvalidOperationException>(() => ArchiveAttachmentReader.WholeText(stored, false));
    }


    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ArchiveDb.EngineFileOf(_dir, "p"),
            Password = ArchiveKey.LoadOrCreate(Path.Combine(_dir, "archive.key")), Pooling = false }.ToString());
        connection.Open(); return connection;
    }




    [Fact]
    public async Task CachedAttachmentUsesFileDigestAndAvoidsAnotherExtraction()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "source.txt");
        File.WriteAllText(path, "original file");
        string digest;
        using (var sha = SHA256.Create()) digest = Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
        var read = await ArchiveAttachmentReader.ReadAsync(new AttachmentFacts("source.txt"), "1", path, default, (hash, token) =>
        {
            Assert.Equal(digest, hash);
            return Task.FromResult<string?>("cached complete extraction");
        });
        Assert.Equal("indexed", read.State);
        Assert.Equal("cached complete extraction", read.Text);
        Assert.Equal(digest, read.Version);
    }

    [Fact]
    public async Task CommittedDeletionSurvivesRestart_EngineRemovesHistoryBeforeAcknowledgement()
    {
        var history = new DpapiHistoryStorageService(Path.Combine(_dir, "history"));
        var conversation = new ConversationHistory();
        conversation.ContextReferences.Add(new HistoryContextReference { EntryId = "e", StoreId = "s" });
        conversation.Messages.Add(new HistoryChatMessage { Content = "synthetic private extract" });
        await history.SaveConversationAsync(conversation);
        using (var db = ArchiveDb.Open(_dir, "p"))
        {
            var f = db.GetOrAddFolder("s", "f", "Inbox");
            db.AddHeaders(f, new[] { Header("e") }, true, true);
            db.BeginRescan(new[] { f.Id });
            db.AddHeaders(f, Array.Empty<ArchiveHeader>(), true, true);
            db.FinishRescan();
            Assert.Single(db.PendingDeletions());
        }
        using var engine = new ArchiveEngine(_dir);
        await engine.Ready;
        var client = new ArchiveClient("p", _dir);
        await client.OpenAsync();
        Assert.Null(await history.LoadConversationAsync(conversation.Id));
        Assert.Equal(0, (await client.GetStatsAsync()).PendingDeletions);
    }

    [Fact]
    public void DeletedCacheFolderStillRequiresOutlookProof_AndUnknownResultKeepsTheOriginal()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        var removed = db.GetOrAddFolder("cache", "removed", "Old folder");
        var kept = db.GetOrAddFolder("cache", "kept", "Inbox");
        db.AddHeaders(removed, new[] { Header("e") }, true, true, false);
        db.AddHeaders(kept, Array.Empty<ArchiveHeader>(), true, true, false);
        long id = db.NextPendingBodies(1).Single().Id;
        db.SaveBody(id, Body());
        db.SetListedFolders(new[] { kept.Id }, new[] { "cache" }, new[] { "cache" });
        db.BeginRescan(new[] { kept.Id });
        db.AddHeaders(kept, Array.Empty<ArchiveHeader>(), true, true, false);
        Assert.Null(db.FinishRescan());
        Assert.Equal(id, db.UnseenCacheCopies(10).Single().Id);
        db.ConfirmCacheCopies(new[] { id }, Array.Empty<long>());
        Assert.Equal(0, db.FinishRescan());
        Assert.NotNull(db.GetOriginal("e", "cache"));
    }

    [Fact]
    public void SecureDeletionRemovesTextFromFreedPagesAsWellAsTablesAndFts()
    {
        string marker = "syntheticsecureremovalmarker";
        string path;
        using (var db = ArchiveDb.Open(_dir, "p"))
        {
            path = db.FilePath;
            var f = db.GetOrAddFolder("s", "f", "Inbox");
            db.AddHeaders(f, new[] { Header("e") }, true, true);
            db.SaveBody(db.NextPendingBodies(1).Single().Id, Body(string.Join(" ", Enumerable.Repeat(marker, 5000))));
            Assert.Contains(marker, DecryptedPages(path));
            db.BeginRescan(new[] { f.Id });
            db.AddHeaders(f, Array.Empty<ArchiveHeader>(), true, true);
            db.FinishRescan();
            Assert.Empty(db.Search(new ArchiveQuery { Text = marker }));
            Assert.True(db.CompleteDeletions(db.PendingDeletions().Select(d => d.Id).ToArray()));
        }
        Assert.DoesNotContain(marker, DecryptedPages(path));
        Assert.False(File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0);
    }

    private string DecryptedPages(string path)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
            Password = ArchiveKey.LoadOrCreate(Path.Combine(_dir, "archive.key")), Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        source.Open();
        IntPtr memory = sqlite3_serialize(source.Handle!.DangerousGetHandle(), "main", out long length, 0);
        Assert.NotEqual(IntPtr.Zero, memory);
        try
        {
            byte[] bytes = new byte[checked((int)length)];
            Marshal.Copy(memory, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { sqlite3_free(memory); }
    }

    [DllImport("sqlite3mc", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_serialize(IntPtr database, [MarshalAs(UnmanagedType.LPStr)] string schema, out long size, uint flags);
    [DllImport("sqlite3mc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr memory);
}
