using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>
/// Outlook quits while ArchiveIndexer reads headers or attachments: no Outlook call or archive write follows,
/// and the next start (tray and Outlook) resumes. Real engine in-process, synthetic Outlook on an STA UI thread.
/// </summary>
public sealed class ArchiveIndexerInterruptionTests : IDisposable
{
    private static readonly DateTime Newest = FakeOutlook.Newest;
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") ?? Path.GetTempPath(), "OnlyWorkspace_Test_Indexer_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private readonly List<ArchiveEngine> _engines = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose(); // still open when an assertion failed mid-test
        if (Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") != null) return;
        foreach (var copy in _copies) if (Directory.Exists(copy)) Directory.Delete(copy, true);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void QuitDuringHeaderBatch_DropsThatBatch_AndRestartResumesFromTheCheckpoint()
    {
        var rows = Enumerable.Range(0, 250).Select(i => Header("h" + i, Newest.AddHours(-i))).ToArray();
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(rows, _copies);
            FakeReader backfill;
            using (var indexer = NewIndexer(outlook, attachments: false))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                outlook.OnBatch = (reader, n) => { if (!reader.Recent && n == 2) outlook.Quit(indexer); };
                await TickUntilAsync(indexer, () => Task.FromResult(outlook.Stopped));

                Assert.Empty(outlook.CallsAfterQuit);
                backfill = outlook.Readers.Single(r => !r.Recent);
                Assert.False(backfill.Disposed, "Stop must not release Outlook objects.");
            }
            Assert.All(outlook.Readers, r => Assert.True(r.Disposed));
            Assert.Equal(0, outlook.OffUiThread);
            var committed = backfill.Returned[0];
            var stats = await Client().GetStatsAsync();
            Assert.Equal(committed.Count, stats.Emails);
            Assert.Equal(0, stats.FoldersDone);
            await StopEngineAsync(engine);

            engine = await StartEngineAsync();
            var resumed = new FakeOutlook(rows, _copies);
            using (var indexer = NewIndexer(resumed, attachments: false)) await TickUntilIdleAsync(indexer);
            // The backfill restarts from the oldest committed row, not from the top of the folder.
            Assert.Equal(committed.Min(h => h.ReceivedUtc).AddMinutes(1), resumed.Readers.First(r => !r.Recent).OlderThan);
            stats = await Client().GetStatsAsync();
            Assert.Equal(rows.Length, stats.Emails);
            Assert.Equal(1, stats.FoldersDone);
            Assert.Equal(0, stats.BodiesPending);
            Assert.Equal(0, resumed.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    [Theory]
    [InlineData(true)]  // Quit inside the attachment copy (COM)
    [InlineData(false)] // Quit while the extractor reads the copy
    public void QuitDuringAttachmentRead_LeavesTheBodyPending_DeletesCopies_AndRestartIndexesIt(bool duringCopy)
    {
        var mail = Header("a1", Newest);
        mail.HasAttachments = true;
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(new[] { mail }, _copies);
            using (var indexer = NewIndexer(outlook, attachments: true))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                outlook.OnCopy = () =>
                {
                    if (duringCopy) outlook.Quit(indexer);
                    else SynchronizationContext.Current!.Post(_ => outlook.Quit(indexer), null); // runs at the first await
                };
                await TickUntilAsync(indexer, () => Task.FromResult(outlook.Stopped));
                // In particular no second ReadForArchive (the unchanged-message check) and no save.
                Assert.Empty(outlook.CallsAfterQuit);
            }
            Assert.Single(outlook.CopyDirectories);
            Assert.All(outlook.CopyDirectories, d => Assert.False(Directory.Exists(d), "Attachment copies must be deleted."));
            var stats = await Client().GetStatsAsync();
            Assert.Equal(1, stats.Emails);
            Assert.Equal(0, stats.BodiesIndexed);
            Assert.Equal(1, stats.BodiesPending);
            await StopEngineAsync(engine);

            engine = await StartEngineAsync();
            var resumed = new FakeOutlook(new[] { mail }, _copies);
            using (var indexer = NewIndexer(resumed, attachments: true)) await TickUntilIdleAsync(indexer);
            Assert.Equal(2, resumed.Calls.Count(c => c == "ReadForArchive a1"));
            Assert.All(resumed.CopyDirectories, d => Assert.False(Directory.Exists(d)));
            stats = await Client().GetStatsAsync();
            Assert.Equal(1, stats.BodiesIndexed);
            Assert.Equal(1, stats.AttachmentTextsIndexed);
            Assert.Equal("a1", Assert.Single(await Client().SearchAsync(new ArchiveQuery { Text = FakeOutlook.AttachmentWord })).EntryId);
            Assert.Equal(0, outlook.OffUiThread + resumed.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    [Fact]
    public void StepBodiesCorrectsSharedLimitedTextWithoutCollidingWithSearchJobs()
    {
        var a = Header("a", Newest); a.HasAttachments = true;
        var b = Header("b", Newest.AddMinutes(-1)); b.HasAttachments = true;
        const string extracted = "Allegato 1 con la parola " + FakeOutlook.AttachmentWord + ".";
        using (var db = ArchiveDb.Open(_directory, FakeOutlook.Profile))
        {
            var folder = db.GetOrAddFolder(FakeOutlook.DefaultStore, "inbox-" + FakeOutlook.DefaultStore, "Inbox");
            db.AddHeaders(folder, new[] { a, b }, true);
            long id = db.Search(new ArchiveQuery()).Single(mail => mail.EntryId == "b").Id;
            using var hash = System.Security.Cryptography.SHA256.Create();
            string version = Convert.ToBase64String(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(extracted)));
            var body = new ArchiveBody { Text = "Body of b", ModifiedUtc = Newest };
            // A partial extraction of these bytes is shared twice by B. It must not be used as a full cache hit.
            for (int i = 1; i <= 2; i++) body.Attachments.Add(new ArchiveAttachment
            {
                SourceId = i.ToString(), Name = "note" + i + ".txt", Version = version, State = "limited", Text = "Allegato"
            });
            db.SaveBody(id, body);
        }
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(new[] { a, b }, _copies);
            using (var indexer = NewIndexer(outlook, attachments: true))
            {
                await TickUntilAsync(indexer, async () =>
                {
                    var status = await indexer.GetStatusAsync();
                    Assert.True(string.IsNullOrEmpty(status.Error), status.Error);
                    return status.Phase == "idle";
                });
            }
            var client = Client();
            Assert.Equal(2, (await client.GetStatsAsync()).BodiesIndexed);
            var first = (await client.GetOriginalAsync("a", FakeOutlook.DefaultStore))!;
            Assert.Equal("Body of a", first.Text);
            Assert.Equal(extracted, Assert.Single(first.Attachments).Text);
            var shared = (await client.GetOriginalAsync("b", FakeOutlook.DefaultStore))!;
            Assert.Equal("Body of b", shared.Text);
            Assert.Equal(2, shared.Attachments.Count);
            Assert.All(shared.Attachments, file => Assert.Equal(extracted, file.Text));
            Assert.Contains(outlook.Calls, call => call == "ReadForArchive a");
            Assert.Equal(0, outlook.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    private ArchiveIndexer NewIndexer(FakeOutlook outlook, bool attachments) =>
        new(outlook, new TestConfig(attachments), _directory, idleMs: () => int.MaxValue, launchEngine: () => true, timerTicks: false);

    private ArchiveClient Client() => new(FakeOutlook.Profile, _directory);

    private async Task<ArchiveEngine> StartEngineAsync() => await Task.Run(async () =>
    {
        var engine = new ArchiveEngine(_directory);
        lock (_engines) _engines.Add(engine);
        await engine.Ready;
        return engine;
    });

    private Task StopEngineAsync(ArchiveEngine engine) => Task.Run(() =>
    {
        lock (_engines) _engines.Remove(engine);
        engine.Dispose();
    });
}
