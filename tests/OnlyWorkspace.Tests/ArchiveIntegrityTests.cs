using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using Xunit;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveIntegrityTests : IDisposable
{
    [Fact]
    public void InterruptedCopy_StagingFilesAreRemovedAtTheNextOpen()
    {
        using (ArchiveDb.Open(_directory, "p")) { }
        string engine = ArchiveDb.EngineFileOf(_directory, "p");
        string staging = engine + ".copy-0123456789abcdef";
        foreach (var file in new[] { staging, staging + "-wal", staging + "-shm" }) File.WriteAllText(file, "partial");
        string other = ArchiveDb.EngineFileOf(_directory, "q") + ".copy-0123456789abcdef";
        File.WriteAllText(other, "another profile's copy");

        using (ArchiveDb.Open(_directory, "p")) { }

        Assert.Empty(Directory.GetFiles(_directory, Path.GetFileName(engine) + "*.copy-*"));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void LargeBatch_ReplaysIdempotentlyWithoutConfiguredSizeLimit()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "f", "Inbox");
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "confirmed", ReceivedUtc = DateTime.UtcNow } }, true);
        var rows = new[] { new ArchiveHeader { EntryId = "new", Subject = new string('x', 2000000), ReceivedUtc = DateTime.UtcNow } };
        db.AddHeaders(folder, rows, true, requestId: "failed-batch");
        db.AddHeaders(folder, rows, true, requestId: "failed-batch");
        Assert.Equal(2, db.GetStats().Emails);
    }

    [Fact]
    public void ExplicitAcknowledgements_CompactOnlyConfirmedBatches_AndSurviveRestartWithGaps()
    {
        string peer = Guid.NewGuid().ToString("N");
        var row = new[] { new ArchiveHeader { EntryId = "e", Subject = "original", ReceivedUtc = DateTime.UtcNow } };
        using (var db = ArchiveDb.Open(_directory, "p"))
        {
            var folder = db.GetOrAddFolder("s", "f", "Inbox");
            db.AddHeaders(folder, row, true, requestId: peer + ":1");
            db.AddHeaders(folder, row, true, requestId: peer + ":2");
            db.AddHeaders(folder, row, true, requestId: peer + ":3");
            db.AcknowledgeBatches(peer, new long[] { 1, 3 });
        }
        using var reopened = ArchiveDb.Open(_directory, "p");
        var same = reopened.GetOrAddFolder("s", "f", "Inbox");
        Assert.Throws<InvalidOperationException>(() => reopened.AddHeaders(same, row, true, requestId: peer + ":1"));
        reopened.AddHeaders(same, row, true, requestId: peer + ":2");
        reopened.AcknowledgeBatches(peer, new long[] { 2 });
        Assert.Throws<InvalidOperationException>(() => reopened.AddHeaders(same, row, true, requestId: peer + ":2"));
        Assert.Single(reopened.Search(new ArchiveQuery()));
        Assert.Contains("\"Last\":3", reopened.GetMeta("rpc_ack:" + peer));
    }

    [Fact]
    public void NativePagedBackup_CanCancelBetweenPages_AndSourceRemainsReadable()
    {
        using var source = new SqliteConnection("Data Source=:memory:");
        using var target = new SqliteConnection("Data Source=:memory:");
        source.Open(); target.Open();
        using var command = source.CreateCommand();
        command.CommandText = "create table data(value blob); insert into data values(zeroblob(1048576))";
        command.ExecuteNonQuery();
        using var stop = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => ArchiveDb.CopyPages(source, target, stop.Token, remaining => { Assert.True(remaining > 0); stop.Cancel(); }));
        command.CommandText = "select length(value) from data";
        Assert.Equal(1048576L, command.ExecuteScalar());
        ArchiveDb.CopyPages(source, target, default);
        using var check = target.CreateCommand();
        check.CommandText = "select length(value) from data";
        Assert.Equal(1048576L, check.ExecuteScalar());
    }

    [Fact]
    public void DiskThresholds_AndMaintenancePreserveSearch()
    {
        long free = ArchiveCapacity.WarningBytes + 1;
        using var db = ArchiveDb.Open(_directory, "p", availableFreeSpace: _ => free);
        var folder = db.GetOrAddFolder("store", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "kept", ReceivedUtc = DateTime.UtcNow } }, true);
        long id = db.NextPendingBodies(1).Single().Id;
        var body = new ArchiveBody { Text = "preservedword" };
        db.SaveBody(id, body, "original-request");
        Assert.Equal(ArchiveCapacityState.Full, ArchiveCapacity.Evaluate(ArchiveCapacity.ReserveBytes - 1));
        Assert.Equal(ArchiveCapacityState.Warning, ArchiveCapacity.Evaluate(ArchiveCapacity.WarningBytes - 1));
        Assert.Equal(ArchiveCapacityState.Available, ArchiveCapacity.Evaluate(ArchiveCapacity.WarningBytes));
        free = ArchiveCapacity.WarningBytes - 1;
        Assert.Equal(ArchiveCapacityState.Warning, db.GetStats().DiskState);
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "warning", ReceivedUtc = DateTime.UtcNow } }, true);
        free = ArchiveCapacity.ReserveBytes - 1;
        Assert.Equal(ArchiveCapacityState.Full, db.GetStats().DiskState);
        Assert.Throws<InvalidOperationException>(() => db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "blocked", ReceivedUtc = DateTime.UtcNow } }, true));
        Assert.Throws<InvalidOperationException>(() => db.SaveBody(id, new ArchiveBody { Text = "blockedword" }));
        free = ArchiveCapacity.WarningBytes + 1;
        db.SaveBody(id, new ArchiveBody { Text = "preservedword" });
        db.OptimizeIndex();
        db.Compact();
        Assert.Single(db.Search(new ArchiveQuery { Text = "preservedword" }));
    }

    [Fact]
    public void VerifiedAbsence_PurgesRowsWhileCacheOnlyInventoryKeepsThem()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("store", "inbox", "Inbox");
        db.AddHeaders(folder, new[]
        {
            new ArchiveHeader { EntryId = "gone", ReceivedUtc = DateTime.UtcNow },
            new ArchiveHeader { EntryId = "kept", ReceivedUtc = DateTime.UtcNow }
        }, true, verified: true);
        db.BeginRescan(new[] { folder.Id });
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "kept", ReceivedUtc = DateTime.UtcNow } }, true, verified: true, canDeleteAbsent: false);
        // A cache folder: Outlook is asked first, and here the email still opens.
        Assert.Null(db.FinishRescan());
        db.ConfirmCacheCopies(db.UnseenCacheCopies(10).Select(e => e.Id).ToList(), Array.Empty<long>());
        Assert.Equal(0, db.FinishRescan());
        using (var connection = Connection(db.FilePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "select count(*) from emails where entry_id='gone'";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        db.BeginRescan(new[] { folder.Id });
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "kept", ReceivedUtc = DateTime.UtcNow } }, true, verified: true, canDeleteAbsent: true);
        Assert.Equal(1, db.FinishRescan());
        using (var connection = Connection(db.FilePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "select count(*) from emails where entry_id='gone'";
            Assert.Equal(0L, command.ExecuteScalar());
        }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_Integrity_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true); }

    private SqliteConnection Connection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Password = ArchiveKey.LoadOrCreate(Path.Combine(_directory, "archive.key")),
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static ArchiveHeader Header(string id = "e", string subject = "Subject") => new()
    {
        EntryId = id,
        Subject = subject,
        SenderAddress = "sender@example.com",
        ReceivedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void FailedBatch_DoesNotMoveEitherBookmark_AndTheBatchCanBeReplayed()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "inbox", "Inbox");
        using (var connection = Connection(db.FilePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "create trigger fail_bookmark before update on folders begin select raise(abort,'fixture write failure'); end";
            command.ExecuteNonQuery();
            Assert.Throws<SqliteException>(() => db.AddHeaders(folder, new[] { Header() }, true, true));
            Assert.Null(folder.NewestUtc);
            Assert.False(folder.Done);
            Assert.Equal(0, db.GetStats().Emails);
            command.CommandText = "drop trigger fail_bookmark";
            command.ExecuteNonQuery();
            Assert.Null(db.GetOrAddFolder("s", "inbox", "Inbox").NewestUtc);
        }
        Assert.Equal(1, db.AddHeaders(folder, new[] { Header() }, true, true));
        Assert.Equal(0, db.AddHeaders(folder, new[] { Header() }, true, true));
        Assert.True(folder.Done);
    }

    [Fact]
    public void VerifiedEmptyFolder_RemovesAbsentEmails_AndFailedInventoryDoesNot()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { Header() }, true, true);
        db.BeginRescan(new[] { folder.Id });
        db.SetFolderError(folder.Id, "offline");
        Assert.Null(db.FinishRescan());
        Assert.Single(db.Search(new ArchiveQuery()));
        Assert.Equal(1, db.GetStats().FoldersFailed);
        folder = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.AddHeaders(folder, Array.Empty<ArchiveHeader>(), true, true);
        Assert.Equal(1, db.FinishRescan());
        Assert.Empty(db.Search(new ArchiveQuery()));
        Assert.Equal(0, db.GetStats().FoldersFailed);
    }

    [Fact]
    public void PartialFolderList_NeverMeansThatMissingFoldersWereDeleted()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var first = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.GetOrAddFolder("s", "other", "Other");
        db.SetListedFolders(new[] { first.Id });
        Assert.Equal(2, db.GetStats().Folders);
        db.SetListedFolders(new[] { first.Id }, new[] { "s" });
        Assert.Equal(1, db.GetStats().Folders);
    }

    /// <summary>17/10/2026: a PST removed from the profile kept its 3000 folders to do and its row in error.</summary>
    [Fact]
    public void MailboxNoLongerOpen_IsClosed_OutOfTheTotals_WithItsEmailsKept()
    {
        var day = new DateTime(2024, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_directory, "p");
        var kept = db.GetOrAddFolder("s", "inbox", "\\\\Posta\\Inbox");
        var gone = db.GetOrAddFolder("pst", "inbox", "\\\\PST\\Inbox");
        var todo = db.GetOrAddFolder("pst", "big", "\\\\PST\\Grande");
        db.AddHeaders(kept, new[] { new ArchiveHeader { EntryId = "k1", Subject = "Ordine", ReceivedUtc = day } }, true);
        db.AddHeaders(gone, new[] { new ArchiveHeader { EntryId = "g1", Subject = "Storico", ReceivedUtc = day } }, true);
        db.SetFolderError(todo.Id, "Cartella non disponibile");
        db.SetListedFolders(new[] { kept.Id, gone.Id, todo.Id }, new[] { "s", "pst" }, new[] { "s", "pst" });
        Assert.Equal((3, 1), (db.GetStats().Folders, db.GetStats().FoldersFailed));

        db.SetListedFolders(new[] { kept.Id }, new[] { "s" }, new[] { "s" });
        var stats = db.GetStats();
        Assert.Equal((1, 1, 0), (stats.Folders, stats.FoldersDone, stats.FoldersFailed));
        var pst = db.GetStoreCoverage().Single(c => c.StoreId == "pst");
        Assert.True(pst.Closed);
        Assert.Equal("chiusa", pst.State);
        Assert.Equal((0, string.Empty, 1), (pst.Folders, pst.Error, pst.Emails));
        Assert.Equal(new[] { "Ordine", "Storico" }, db.Search(new ArchiveQuery()).Select(h => h.Subject).OrderBy(s => s));

        // A full check closed meanwhile does not give up the closed mailbox's emails.
        db.BeginRescan(new[] { kept.Id });
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "\\\\Posta\\Inbox"), new[] { new ArchiveHeader { EntryId = "k1", Subject = "Ordine", ReceivedUtc = day } }, true, true);
        db.FinishRescan();
        Assert.Equal(2, db.Search(new ArchiveQuery()).Count);

        // Reopened: its folders count again.
        db.SetListedFolders(new[] { kept.Id, gone.Id, todo.Id }, new[] { "s", "pst" }, new[] { "s", "pst" });
        Assert.Equal(3, db.GetStats().Folders);
        Assert.False(db.GetStoreCoverage().Single(c => c.StoreId == "pst").Closed);
    }

    [Fact]
    public void UnavailableBody_IsRetriedAfterRecovery_WithoutLosingItsIdentity()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { Header() }, true, true);
        long id = db.NextPendingBodies(10).Single().Id;
        db.SaveBody(id, new ArchiveBody { Unavailable = true, Retryable = true });
        Assert.Empty(db.NextPendingBodies(10));
        Assert.Equal(1, db.GetStats().BodiesRetryable);
        db.AddHeaders(folder, new[] { Header() }, true, true);
        Assert.Equal(id, db.NextPendingBodies(10).Single().Id);
        db.SaveBody(id, new ArchiveBody { Text = "Recovered original text" });
        Assert.Equal(id, db.Search(new ArchiveQuery { Text = "recovered" }).Single().Id);
        Assert.Equal(0, db.GetStats().BodiesRetryable);
    }

    [Fact]
    public void ChangedBody_UpdatesItsFingerprintAndSearchableOriginal()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { Header() }, true, true);
        long id = db.NextPendingBodies(10).Single().Id;
        db.SaveBody(id, new ArchiveBody { Text = "Old warranty" });
        var oldVersion = db.Search(new ArchiveQuery()).Single().BodyVersion;
        db.SaveBody(id, new ArchiveBody { Text = "New instructions" });
        Assert.Empty(db.Search(new ArchiveQuery { Text = "warranty" }));
        var newVersion = db.Search(new ArchiveQuery()).Single().BodyVersion;
        Assert.NotEqual(oldVersion, newVersion);
        db.SaveBody(id, new ArchiveBody { Text = "New instructions" });
        Assert.Equal(newVersion, db.GetBodyVersion(id));
        Assert.Equal("New instructions", db.GetOriginal("e", "s")!.Text);
    }

    [Fact]
    public void UpdatedHeaders_ClearStaleSubjectAndSender_AndLimitsAreVisible()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { Header(subject: "Old subject") }, true, true);
        long id = db.NextPendingBodies(10).Single().Id;
        db.SaveBody(id, new ArchiveBody { Text = "obsolete" });
        var changed = Header(subject: "Replacement subject");
        changed.SenderAddress = "other@example.net";
        db.AddHeaders(folder, new[] { changed }, true, true);
        Assert.Empty(db.Search(new ArchiveQuery { Text = "obsolete" }));
        var hit = db.Search(new ArchiveQuery()).Single();
        Assert.Equal("other@example.net", hit.SenderAddress);
        Assert.Equal(ArchiveBodyState.Pending, hit.BodyState);
        db.SaveBody(id, new ArchiveBody { Text = new string('a', 60001), RecipientsLimited = true, AttachmentsLimited = true });
        hit = db.Search(new ArchiveQuery()).Single();
        Assert.False(hit.TextLimited);
        Assert.True(hit.RecipientsLimited && hit.AttachmentsLimited);
        Assert.Equal(0, db.GetStats().TextsLimited);
        Assert.Equal(1, db.GetStats().RecipientsLimited);
        Assert.Equal(1, db.GetStats().AttachmentsLimited);
    }

    [Fact]
    public void ArchiveHasOneOwner_AndSanitizedProfileNamesDoNotCollide()
    {
        using var db = ArchiveDb.Open(_directory, "Profile/1");
        Assert.Throws<IOException>(() => ArchiveDb.Open(_directory, "Profile/1"));
        Assert.Throws<IOException>(() => ArchiveDb.Delete(_directory, "Profile/1"));
        using var other = ArchiveDb.Open(_directory, "Profile_1");
        Assert.NotEqual(db.FilePath, other.FilePath);
    }

    [Fact]
    public void OriginalsRemainAvailableOnUnchangedRescan_AndRefreshRechecksTheirSource()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { Header() }, true, true);
        long id = db.NextPendingBodies(10).Single().Id;
        db.SaveBody(id, new ArchiveBody { Text = "Current original" });
        Assert.Single(db.Search(new ArchiveQuery { Text = "Current" }));
        db.BeginRescan(new[] { folder.Id });
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "Inbox"), new[] { Header() }, true, true);
        Assert.Equal(1, db.GetStats().BodiesIndexed);
        Assert.Empty(db.NextPendingBodies(10));

        var changed = Header();
        changed.RefreshBody = true;
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "Inbox"), new[] { changed }, true, true);
        Assert.Empty(db.Search(new ArchiveQuery { Text = "Current" }));
        db.SaveBody(id, new ArchiveBody { Text = "Current original" });
        Assert.Single(db.Search(new ArchiveQuery { Text = "Current" }));
    }

    [Fact]
    public void CompletedCacheInventoryCannotEstablishRemoteDeletion_OnlyOutlookNotFindingTheEmailCan()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { Header("e1"), Header("e2") }, true, true, canDeleteAbsent: false);
        db.BeginRescan(new[] { folder.Id });
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "Inbox"), new[] { Header("e1") }, true, true, canDeleteAbsent: false);
        // The check waits for Outlook's answer on the email it did not see.
        Assert.Null(db.FinishRescan());
        var unseen = Assert.Single(db.UnseenCacheCopies(10));
        Assert.Equal(("e2", "s"), (unseen.EntryId, unseen.StoreId));
        db.ConfirmCacheCopies(new[] { unseen.Id }, Array.Empty<long>());
        Assert.Empty(db.UnseenCacheCopies(10));
        Assert.Equal(0, db.FinishRescan());
        Assert.Equal(2, db.Search(new ArchiveQuery()).Count);
        Assert.True(Assert.Single(db.GetStoreCoverage()).CacheChecked);

        db.BeginRescan(new[] { folder.Id });
        Assert.Empty(db.UnseenCacheCopies(10));
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "Inbox"), Array.Empty<ArchiveHeader>(), true, true, canDeleteAbsent: false);
        var both = db.UnseenCacheCopies(10).ToDictionary(e => e.EntryId, e => e.Id);
        db.ConfirmCacheCopies(new[] { both["e1"] }, new[] { both["e2"] });
        Assert.Equal(1, db.FinishRescan());
        Assert.Equal("e1", Assert.Single(db.Search(new ArchiveQuery())).EntryId);
    }

    [Fact]
    public void EntryIdIsScopedToItsStore_WhenFindingACounterparty()
    {
        using var db = ArchiveDb.Open(_directory, "p");
        var first = db.GetOrAddFolder("s1", "inbox", "Inbox");
        var second = db.GetOrAddFolder("s2", "inbox", "Inbox");
        var header = Header();
        db.AddHeaders(first, new[] { header }, true, true);
        header = Header();
        header.SenderAddress = "another@example.net";
        db.AddHeaders(second, new[] { header }, true, true);
        Assert.Equal(db.CounterpartyOf("sender@example.com"), db.CounterpartyOfEmail("e", "s1"));
        Assert.Equal(db.CounterpartyOf("another@example.net"), db.CounterpartyOfEmail("e", "s2"));
        Assert.Null(db.CounterpartyOfEmail("e", "absent"));
    }
}
