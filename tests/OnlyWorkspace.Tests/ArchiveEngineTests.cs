using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using Xunit;
using Xunit.Abstractions;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveEngineTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewStore_ProviderFailureReleasesOwnership_AndRetryKeepsRegisteredInstance(bool existing)
    {
        if (existing) { using var seed = ArchiveDb.Open(_directory, "p"); seed.SetMeta("sentinel", "kept"); }
        bool fail = true;
        using var engine = new ArchiveEngine(_directory, null, () => fail ? throw new InvalidOperationException("synthetic policy error") : true);
        await engine.Ready;
        var client = new ArchiveClient("p", _directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenAsync(true));
        using (var probe = ArchiveDb.Open(_directory, "p"))
            if (existing) Assert.Equal("kept", probe.GetMeta("sentinel"));
        fail = false;
        await client.OpenAsync(true);
        await client.SetMetaAsync("counter", "1");
        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenAsync());
        // A registered instance remains owned even when its configuration provider fails.
        Assert.Throws<IOException>(() => ArchiveDb.Open(_directory, "p"));
        fail = false;
        Assert.Equal("1", await client.GetMetaAsync("counter"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewStore_ConfigureStorageFailureReleasesOwnerAndConnection(bool existing)
    {
        if (existing) { using var seed = ArchiveDb.Open(_directory, "p"); seed.SetMeta("sentinel", "kept"); }
        void Sql(string sql)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {
                DataSource = ArchiveDb.EngineFileOf(_directory, "p"), Pooling = false,
                Password = ArchiveKey.LoadOrCreate(Path.Combine(_directory, "archive.key")) }.ToString());
            connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        bool inject = true;
        using var engine = new ArchiveEngine(_directory, null, () => {
            if (inject) { Sql("create trigger fail_storage before insert on meta when new.key='index_attachment_text' begin select raise(abort,'synthetic storage error'); end"); inject = false; }
            return true;
        });
        await engine.Ready;
        var client = new ArchiveClient("p", _directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenAsync(true));
        using (var probe = ArchiveDb.Open(_directory, "p")) if (existing) Assert.Equal("kept", probe.GetMeta("sentinel"));
        Sql("drop trigger fail_storage");
        await client.OpenAsync();
        if (existing) Assert.Equal("kept", await client.GetMetaAsync("sentinel"));
    }

    [Fact]
    public async Task StoragePolicy_UpdatesAttachmentPolicyThroughPipeAndPreservesTheBody()
    {
        bool enabled = true;
        using var engine = new ArchiveEngine(_directory, null, () => enabled);
        await engine.Ready;
        var client = new ArchiveClient("p", _directory);
        await client.OpenAsync(true);
        var folder = await client.GetOrAddFolderAsync("s", "f", "Inbox");
        await client.AddHeadersAsync(folder, new[] { new ArchiveHeader { EntryId = "mail", HasAttachments = true, ReceivedUtc = DateTime.UtcNow } }, true);
        long id = (await client.NextPendingBodiesAsync(1)).Single().Id;
        var body = new ArchiveBody { Text = "whole body" };
        body.Attachments.Add(new ArchiveAttachment { Name = "a.txt", State = "indexed", Text = "attachmentuniquepolicyterm", Version = "policy-file" });
        await client.SaveBodyAsync(id, body);
        Assert.Single(await client.SearchAsync(new ArchiveQuery { Text = "attachmentuniquepolicyterm" }));
        enabled = false;
        await client.OpenAsync();
        Assert.Empty(await client.SearchAsync(new ArchiveQuery { Text = "attachmentuniquepolicyterm" }));
        Assert.Equal("whole body", (await client.GetOriginalAsync("mail", "s"))!.Text);
        Assert.Null(await client.GetAttachmentTextAsync("policy-file"));
        enabled = true; await client.OpenAsync();
        Assert.Contains(await client.NextPendingBodiesAsync(10), email => email.Id == id);
        await client.SaveBodyAsync(id, body); // Re-enabling schedules a reread; only the serving state was pending.
        Assert.Single(await client.SearchAsync(new ArchiveQuery { Text = "attachmentuniquepolicyterm" }));
        Assert.Equal("whole body", (await client.GetOriginalAsync("mail", "s"))!.Text);
    }


    private readonly ITestOutputHelper _output;

    public ArchiveEngineTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Maintenance_RunsOutsideTheEngineLock_AndLeavesNoPauseBehind()
    {
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        var client = new ArchiveClient("m", _directory);
        var other = new ArchiveClient("o", _directory);
        await client.OpenAsync(true);
        await other.OpenAsync(true);
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        engine.MaintenanceProbe = _ => { inside.Set(); release.Wait(TimeSpan.FromSeconds(20)); };

        var compact = Task.Run(() => client.CompactAsync());
        try
        {
            Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));
            var elapsed = Stopwatch.StartNew();
            Assert.True((await client.EngineStatusAsync()).Paused);
            Assert.Equal("1", await client.GetMetaAsync(ArchiveScope.PausedKey));
            await Assert.ThrowsAsync<ArchiveCollectorBusyException>(() => client.HeartbeatAsync("idle", null));
            await other.SetMetaAsync("probe", "1");
            Assert.Equal("1", await other.GetMetaAsync("probe"));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), elapsed.Elapsed.ToString());
        }
        finally { release.Set(); }

        await compact;
        Assert.Null(await client.GetMetaAsync(ArchiveScope.PausedKey));
        Assert.False((await client.EngineStatusAsync()).Paused);
    }

    [Fact]
    public async Task Maintenance_TimerAndBudgetsSkipTheLockedArchive_AndItsDeletionsRunAfterwards()
    {
        using (var db = ArchiveDb.Open(_directory, "m"))
        {
            var folder = db.GetOrAddFolder("s", "f", "Inbox");
            db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "e", MessageId = "<e@example.test>", ReceivedUtc = DateTime.UtcNow } }, true, true);
            db.SaveBody(db.NextPendingBodies(1).Single().Id, new ArchiveBody { Text = "Complete original" });
            db.BeginRescan(new[] { folder.Id });
            db.AddHeaders(folder, Array.Empty<ArchiveHeader>(), true, true);
            db.FinishRescan();
            Assert.NotEmpty(db.PendingDeletions());
        }
        // An unreadable history file keeps the deletion pending until it is removed.
        string corrupt = Path.Combine(_directory, "history", "corrupt.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
        File.WriteAllBytes(corrupt, new byte[] { 1 });
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        var client = new ArchiveClient("m", _directory);
        var other = new ArchiveClient("o", _directory);
        await client.OpenAsync();
        await other.OpenAsync(true);
        Assert.NotEqual(0, (await client.EngineStatusAsync()).Stats!.PendingDeletions);
        File.Delete(corrupt);
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Held under the database lock, as the VACUUM of a large archive holds it.
        engine.MaintenanceProbe = _ => { inside.Set(); release.Wait(TimeSpan.FromSeconds(20)); };

        var compact = Task.Run(() => client.CompactAsync());
        Task sweep;
        try
        {
            Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));
            sweep = Task.Run(engine.Sweep);
            var elapsed = Stopwatch.StartNew();
            Assert.True((await client.EngineStatusAsync()).Paused);
            await other.SetMetaAsync("probe", "1");
            Assert.Same(sweep, await Task.WhenAny(sweep, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), elapsed.Elapsed.ToString());
        }
        finally { release.Set(); }

        await compact;
        Assert.Equal(0, (await client.EngineStatusAsync()).Stats!.PendingDeletions);
    }

    [Fact]
    public async Task FlowAndStoreCoverage_TravelThroughTheArchivePipe()
    {
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        var client = new ArchiveClient("flow", _directory);
        await client.OpenAsync(true);
        var folder = await client.GetOrAddFolderAsync("pst", "inbox", "Inbox");
        await client.AddHeadersAsync(folder, new[]
        {
            new ArchiveHeader { EntryId = "root", MessageId = "<root>", Subject = "Progetto", ReceivedUtc = DateTime.UtcNow },
            new ArchiveHeader { EntryId = "reply", MessageId = "<reply>", InReplyTo = "<root>", Subject = "RE: Progetto", ReceivedUtc = DateTime.UtcNow.AddMinutes(1) }
        }, true, verified: true);

        var flow = await client.GetFlowAsync(new ArchiveFlowSeed { EntryId = "root", StoreId = "pst" });
        Assert.Equal(2, flow.Timeline.Count);
        Assert.Contains(flow.Timeline, mail => mail.EntryId == "reply" && mail.ParentEntryId == "root");
        var coverage = Assert.Single(await client.GetStoreCoverageAsync());
        Assert.Equal("pst", coverage.StoreId);
        Assert.Equal(2, coverage.Emails);
        Assert.Equal("inventario verificato", coverage.State);
    }






    [Fact]
    public async Task SegmentedMessageBeyond64MiB_IsWhole_AndTruncatedSegmentsAreRejected()
    {
        string value = new('x', 64 * 1024 * 1024 + 1);
        using var stream = new MemoryStream();
        await ArchivePipe.WriteAsync(stream, value, default);
        stream.Position = 0;
        Assert.Equal(value, await ArchivePipe.ReadAsync<string>(stream, default));
        using var partial = new MemoryStream();
        partial.Write(BitConverter.GetBytes(-1), 0, 4);
        partial.Write(BitConverter.GetBytes(100), 0, 4);
        partial.Write(new byte[1], 0, 1);
        partial.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => ArchivePipe.ReadAsync<string>(partial, default));
    }

    [Fact]
    public async Task SequentialClientBatches_CompactAcknowledgedReceiptsThroughTheRealPipe()
    {
        using (var engine = new ArchiveEngine(_directory))
        {
            await engine.Ready;
            var client = new ArchiveClient("p", _directory);
            await client.OpenAsync(true);
            var folder = await client.GetOrAddFolderAsync("s", "f", "Folder");
            for (int i = 0; i < 20; i++)
                await client.AddHeadersAsync(folder, new[] { new ArchiveHeader { EntryId = "e" + i, ReceivedUtc = DateTime.UtcNow } }, true);
        }
        string key = ArchiveKey.LoadOrCreate(Path.Combine(_directory, "archive.key"))!;
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = ArchiveDb.EngineFileOf(_directory, "p"), Password = key, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "select count(*) from rpc_batches";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_Engine_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true); }

    [Fact]
    public async Task SearchSurvivesOutlookDisconnectionAndEngineRestart_WithOnlyOneWriter()
    {
        var client = new ArchiveClient("p", _directory);
        using (var engine = new ArchiveEngine(_directory))
        {
            await engine.Ready;
            Assert.Throws<IOException>(() => new ArchiveEngine(_directory));
            await client.OpenAsync(true);
            var folder = await client.GetOrAddFolderAsync("s", "inbox", "Inbox");
            await client.AddHeadersAsync(folder, new[] { new ArchiveHeader { EntryId = "e", Subject = "Private subject", ReceivedUtc = DateTime.UtcNow } }, true, true);
            Assert.True(folder.Done);
            Assert.NotNull(folder.NewestUtc);
            long id = (await client.NextPendingBodiesAsync(5)).Single().Id;
            var body = new ArchiveBody { Text = "Full original content" };
            body.Recipients.Add(new ArchiveRecipient { Address = "recipient@example.org", Name = "Recipient" });
            body.AttachmentNames.Add("document.pdf");
            await client.SaveBodyAsync(id, body);
            await client.SetMetaAsync("paused", "1");
            Assert.Single(await client.FindAddressesAsync("recipient"));
            Assert.NotNull(await client.CounterpartyOfAsync("recipient@example.org"));
            Assert.Single(await client.SearchAsync(new ArchiveQuery { Text = "original" }));
            Assert.False((await client.EngineStatusAsync()).OutlookConnected);
            Assert.Null((await client.EngineStatusAsync()).LastCheckUtc);
            await client.ConfirmInitialInventoryAsync();
            Assert.NotNull((await client.EngineStatusAsync()).LastCheckUtc);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestOriginalAsync("e", "s"));
            await client.BeginRescanAsync(new[] { folder.Id });
            folder = await client.GetOrAddFolderAsync("s", "inbox", "Inbox");
            await client.AddHeadersAsync(folder, new[] { new ArchiveHeader { EntryId = "e", Subject = "Private subject", ReceivedUtc = body.ModifiedUtc ?? DateTime.UtcNow } }, true, true);
            Assert.Equal(0, await client.FinishRescanAsync());
            await client.SaveBodyAsync(id, body);
        }
        using var restarted = new ArchiveEngine(_directory);
        await restarted.Ready;
        var status = (await client.ProfilesAsync()).Single();
        Assert.Equal("p", status.Profile);
        Assert.True(status.Paused);
        Assert.False(status.OutlookConnected);
        Assert.Single(await client.SearchAsync(new ArchiveQuery { Text = "original" }));
        await client.SyncNowAsync();
        Assert.Equal("1", await client.GetMetaAsync("sync_asked"));
        Assert.Empty(await client.SearchAsync(new ArchiveQuery { Text = "original", Offset = 1 }));
    }

    [Fact]
    public async Task CommittedBatchCanBeReplayedAfterRestart_WithoutRevertingNewerContent()
    {
        var client = new ArchiveClient("p", _directory);
        string headerId = Guid.NewGuid().ToString("N"), bodyId = Guid.NewGuid().ToString("N");
        ArchiveFolder folder;
        var header = new ArchiveHeader { EntryId = "e", Subject = "Original", ReceivedUtc = DateTime.UtcNow };
        long id;
        using (var engine = new ArchiveEngine(_directory))
        {
            await engine.Ready;
            await client.OpenAsync(true);
            folder = await client.GetOrAddFolderAsync("s", "f", "Folder");
            var first = await client.CallAsync<ArchiveHeaderCommit>(nameof(ArchiveDb.AddHeaders), new object?[] { folder, new[] { header }, true, true, true }, requestId: headerId);
            Assert.Equal(1, first.Added);
            id = (await client.NextPendingBodiesAsync(5)).Single().Id;
            await client.CallAsync<object?>(nameof(ArchiveDb.SaveBody), new object?[] { id, new ArchiveBody { Text = "Old words" } }, requestId: bodyId);
            await client.SaveBodyAsync(id, new ArchiveBody { Text = "Current words" });
        }
        using var restarted = new ArchiveEngine(_directory);
        await restarted.Ready;
        Assert.Equal(1, (await client.CallAsync<ArchiveHeaderCommit>(nameof(ArchiveDb.AddHeaders), new object?[] { folder, new[] { header }, true, true, true }, requestId: headerId)).Added);
        await client.CallAsync<object?>(nameof(ArchiveDb.SaveBody), new object?[] { id, new ArchiveBody { Text = "Old words" } }, requestId: bodyId);
        Assert.Single(await client.SearchAsync(new ArchiveQuery { Text = "current" }));
        Assert.Empty(await client.SearchAsync(new ArchiveQuery { Text = "old" }));
        header.Subject = "Changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync<ArchiveHeaderCommit>(nameof(ArchiveDb.AddHeaders), new object?[] { folder, new[] { header }, true, true, true }, requestId: headerId));
    }

    [Fact]
    public async Task InterruptedPipeReconciliation_KeepsUnseenMailUntilVerifiedInventoryCompletes()
    {
        const int count = 1000;
        var client = new ArchiveClient("p", _directory);
        var rows = Enumerable.Range(0, count).Select(i => new ArchiveHeader
        {
            EntryId = "mail-" + i,
            Subject = "Synthetic mail " + i,
            ReceivedUtc = DateTime.UtcNow.AddMinutes(-i)
        }).ToArray();
        ArchiveFolder folder;
        using (var engine = new ArchiveEngine(_directory))
        {
            await engine.Ready;
            await client.OpenAsync(true);
            folder = await client.GetOrAddFolderAsync("store", "inbox", "Inbox");
            for (int i = 0; i < count; i += 100)
                await client.AddHeadersAsync(folder, rows.Skip(i).Take(100).ToArray(), i + 100 == count, verified: i + 100 == count);
            Assert.Equal(count, (await client.GetStatsAsync()).Emails);
            await client.BeginRescanAsync(new[] { folder.Id });
            folder = await client.GetOrAddFolderAsync("store", "inbox", "Inbox");
            await client.AddHeadersAsync(folder, rows.Take(100).ToArray(), folderDone: false);
        }

        using var restarted = new ArchiveEngine(_directory);
        await restarted.Ready;
        Assert.Null(await client.FinishRescanAsync());
        Assert.Equal(count, (await client.GetStatsAsync()).Emails);

        folder = await client.GetOrAddFolderAsync("store", "inbox", "Inbox");
        var timer = Stopwatch.StartNew();
        for (int i = 100; i < count - 1; i += 100)
        {
            var batch = rows.Skip(i).Take(Math.Min(100, count - 1 - i)).ToArray();
            await client.AddHeadersAsync(folder, batch, i + batch.Length == count - 1, verified: i + batch.Length == count - 1,
                canDeleteAbsent: false);
        }
        // A cache inventory: the unseen email is asked about first, and still opens.
        Assert.Null(await client.FinishRescanAsync());
        var unseen = Assert.Single(await client.UnseenCacheCopiesAsync(10));
        await client.ConfirmCacheCopiesAsync(new[] { unseen.Id }, Array.Empty<long>());
        Assert.Equal(0, await client.FinishRescanAsync());
        timer.Stop();
        _output.WriteLine("Pipe v2 reconciliation: {0} synthetic headers in {1} ms; local-only inventory retained {2} emails.",
            count - 1, timer.ElapsedMilliseconds, (await client.GetStatsAsync()).Emails);
        Assert.Equal(count, (await client.GetStatsAsync()).Emails);
        Assert.True(Assert.Single(await client.GetStoreCoverageAsync()).CacheChecked);

        await client.BeginRescanAsync(new[] { folder.Id });
        folder = await client.GetOrAddFolderAsync("store", "inbox", "Inbox");
        timer.Restart();
        for (int i = 0; i < count - 1; i += 100)
        {
            var batch = rows.Skip(i).Take(Math.Min(100, count - 1 - i)).ToArray();
            await client.AddHeadersAsync(folder, batch, i + batch.Length == count - 1, verified: i + batch.Length == count - 1);
        }
        Assert.Equal(1, await client.FinishRescanAsync());
        timer.Stop();
        _output.WriteLine("Pipe v2 verified reconciliation: {0} synthetic headers in {1} ms; one absence confirmed.",
            count - 1, timer.ElapsedMilliseconds);
        Assert.Equal(count - 1, (await client.GetStatsAsync()).Emails);
        Assert.Empty(await client.SearchAsync(new ArchiveQuery { Subject = "Synthetic mail 999" }));
    }

    [Fact]
    public async Task OutlookClosesDuringSyncAndOriginalRequest_ThenReopensWithoutLosingMail()
    {
        var client = new ArchiveClient("p", _directory);
        ArchiveFolder folder;
        string originalRequest;
        var headers = new[]
        {
            new ArchiveHeader { EntryId = "first", Subject = "First", ReceivedUtc = DateTime.UtcNow },
            new ArchiveHeader { EntryId = "second", Subject = "Second", ReceivedUtc = DateTime.UtcNow }
        };
        using (var engine = new ArchiveEngine(_directory))
        {
            await engine.Ready;
            await client.OpenAsync(true);
            folder = await client.GetOrAddFolderAsync("store", "inbox", "Inbox");
            await client.AddHeadersAsync(folder, headers, folderDone: true, verified: true);
            await client.HeartbeatAsync("headers", null);
            originalRequest = await client.RequestOriginalAsync("first", "store");
            await client.BeginRescanAsync(new[] { folder.Id });
            folder = await client.GetOrAddFolderAsync("store", "inbox", "Inbox");
            await client.AddHeadersAsync(folder, headers.Take(1).ToArray(), folderDone: false);
            await client.SyncNowAsync();
        }

        using var restarted = new ArchiveEngine(_directory);
        await restarted.Ready;
        Assert.False((await client.EngineStatusAsync()).OutlookConnected);
        Assert.Equal("1", await client.GetMetaAsync("sync_asked"));
        Assert.Null(await client.FinishRescanAsync());
        Assert.Equal(2, (await client.GetStatsAsync()).Emails);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OriginalResultAsync(originalRequest));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestOriginalAsync("first", "store"));

        await client.HeartbeatAsync("headers", null);
        string reopenedRequest = await client.RequestOriginalAsync("first", "store");
        await client.CancelOriginalAsync(reopenedRequest);
        await client.CompleteOriginalAsync(reopenedRequest, "late text", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OriginalResultAsync(reopenedRequest));
        await client.SetMetaAsync("sync_asked", null);
        folder = await client.GetOrAddFolderAsync("store", "inbox", "Inbox");
        await client.AddHeadersAsync(folder, headers, folderDone: true, verified: true);
        Assert.Equal(0, await client.FinishRescanAsync());
        Assert.Equal(2, (await client.GetStatsAsync()).Emails);
    }

    [Fact]
    public async Task OriginalTextIsReturnedOnlyForTheMatchingProfile_AndCancellationDropsIt()
    {
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        var first = new ArchiveClient("first", _directory);
        var second = new ArchiveClient("second", _directory);
        await first.OpenAsync(true);
        await second.OpenAsync(true);
        await first.HeartbeatAsync("idle", null);
        string id = await first.RequestOriginalAsync("email", "store");
        Assert.False((await first.OriginalResultAsync(id)).Ready);
        Assert.Empty(await second.OriginalRequestsAsync());
        Assert.Equal(id, (await first.OriginalRequestsAsync()).Single().Id);
        await second.CompleteOriginalAsync(id, "wrong profile", null);
        Assert.False((await first.OriginalResultAsync(id)).Ready);
        await first.CompleteOriginalAsync(id, "Full original, without truncation", null);
        var result = await first.OriginalResultAsync(id);
        Assert.True(result.Ready);
        Assert.Equal("Full original, without truncation", result.Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.OriginalResultAsync(id));
        id = await first.RequestOriginalAsync("email", "store");
        await first.CancelOriginalAsync(id);
        Assert.Empty(await first.OriginalRequestsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.OriginalResultAsync(id));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(12)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(999)]
    public async Task IncompatibleProtocolAndUnknownOperationsAreRejected_WithoutChangingData(int version)
    {
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        var client = new ArchiveClient("p", _directory);
        await client.OpenAsync(true);
        using var pipe = new NamedPipeClientStream(".", ArchivePipe.Name(_directory), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(3000);
        await ArchivePipe.WriteAsync(pipe, new ArchivePipeRequest { Profile = "p", Version = version, Operation = nameof(ArchiveDb.SetMeta), Arguments = new[] { JsonSerializer.SerializeToElement("paused"), JsonSerializer.SerializeToElement("1") } }, CancellationToken.None);
        var response = await ArchivePipe.ReadAsync<ArchivePipeResponse>(pipe, CancellationToken.None);
        Assert.Contains("protocol", response.Error);
        Assert.Null(await client.GetMetaAsync("paused"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync<object?>("Backup", new object?[] { "untrusted-path" }));
    }

    /// <summary>Clients that connect and close at once (a cancelled request) must not stop the listener.</summary>
    [Fact]
    public async Task ConnectionsClosedImmediately_DoNotStopTheEngine()
    {
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        for (int i = 0; i < 200; i++)
        {
            using var pipe = new NamedPipeClientStream(".", ArchivePipe.Name(_directory), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(3000);
        }
        var client = new ArchiveClient("p", _directory);
        await client.OpenAsync(true);
        Assert.Equal(0, (await client.GetStatsAsync()).Emails);
    }

    [Fact]
    public async Task ClientWaitsWhileNoInstanceListens_InsteadOfFailingOnNetFramework()
    {
        // A120: with 7 connections served the engine listens on no instance; .NET Framework then threw
        // "The semaphore timeout period has expired" after 50 ms instead of waiting (CI run 40183473771).
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        var idle = new System.Collections.Generic.List<NamedPipeClientStream>();
        try
        {
            for (int i = 0; i < 7; i++)
            {
                var pipe = new NamedPipeClientStream(".", ArchivePipe.Name(_directory), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                idle.Add(pipe);
                await pipe.ConnectAsync(3000);
            }
            var client = new ArchiveClient("p", _directory);
            var call = client.OpenAsync(true);
            await Task.Delay(500);
            Assert.False(call.IsCompleted, call.Exception?.ToString());
            idle[0].Dispose();
            await call;
            Assert.Equal(0, (await client.GetStatsAsync()).Emails);
        }
        finally { foreach (var pipe in idle) pipe.Dispose(); }
    }

    [Fact]
    public async Task MalformedFrameAndCancelledConnection_DoNotStopTheEngine()
    {
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        using (var pipe = new NamedPipeClientStream(".", ArchivePipe.Name(_directory), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation))
        {
            await pipe.ConnectAsync(3000);
            await pipe.WriteAsync(BitConverter.GetBytes(int.MaxValue), 0, 4);
        }
        var client = new ArchiveClient("p", _directory);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.OpenAsync(true, cancelled.Token));
        await client.OpenAsync(true);
        Assert.Equal(0, (await client.GetStatsAsync()).Emails);
    }


    [Fact]
    public async Task TwoOutlookCollectorsCannotRunTheSameProfilePipeline()
    {
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        var first = new ArchiveClient("p", _directory);
        var second = new ArchiveClient("p", _directory);
        await first.OpenAsync(true);
        await first.HeartbeatAsync("headers", null);
        await Assert.ThrowsAsync<ArchiveCollectorBusyException>(() => second.HeartbeatAsync("headers", null));
        await Assert.ThrowsAsync<ArchiveCollectorBusyException>(() => second.GetOrAddFolderAsync("s", "f", "Folder"));
        Assert.Equal(0, (await second.GetStatsAsync()).Emails);
        Assert.Empty(await second.SearchAsync(new ArchiveQuery()));
        await second.SetMetaAsync("paused", "1");
        Assert.True((await first.EngineStatusAsync()).Paused);
    }

    [Fact]
    public async Task ExplicitDeletionWorksForAnUnreadableArchive_AndCanBeRepeated()
    {
        using (var db = ArchiveDb.Open(_directory, "p")) db.SetMeta("fixture", "kept");
        File.Delete(Path.Combine(_directory, "archive.key"));
        using var engine = new ArchiveEngine(_directory);
        await engine.Ready;
        var client = new ArchiveClient("p", _directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenAsync());
        await client.DeleteAsync();
        await client.DeleteAsync();
        Assert.False(ArchiveDb.Exists(_directory, "p"));
        Assert.False(File.Exists(Path.Combine(_directory, "archive.key")));
    }

    [Fact]
    public async Task DisabledAdministrativePolicyPreventsArchiveCreation()
    {
        using var engine = new ArchiveEngine(_directory, () => false);
        await engine.Ready;
        var client = new ArchiveClient("p", _directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenAsync(true));
        Assert.False(ArchiveDb.Exists(_directory, "p"));
    }
}
