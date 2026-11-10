using System.Text.Json;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using OnlyWorkspace.Outlook.UI;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>
/// #146: after the rebuild of 9/11 the archive filled only while nobody touched the PC. Indicizza ora indexes
/// without waiting for 10 s of idleness until the work runs out, then the indexer waits for an idle PC again.
/// </summary>
public sealed class ArchiveIndexerIndexNowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_IndexNow_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private readonly List<ArchiveEngine> _engines = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var copy in _copies) if (Directory.Exists(copy)) TestArtifacts.DeleteDirectory(copy, true);
        if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true);
    }

    [Fact]
    public void IndexNow_IndexesWhileTheUserIsActive_ThenWaitsForIdlenessAgain()
    {
        var rows = new Dictionary<string, ArchiveHeader[]>
        {
            [FakeOutlook.DefaultStore] = new[] { "a", "b" }.Select((id, i) => Header(id, FakeOutlook.Newest.AddHours(-i))).ToArray()
        };
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(rows, _copies);
            var client = new ArchiveClient(FakeOutlook.Profile, _directory);
            // The user is typing the whole time.
            using (var indexer = new ArchiveIndexer(outlook, new TestConfig(attachments: false), _directory, idleMs: () => 0, launchEngine: () => true, timerTicks: false))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                for (int i = 0; i < 5; i++) await indexer.TickAsync();
                Assert.Equal(0, (await client.GetStatsAsync()).Emails);
                Assert.Equal("waiting", (await indexer.GetStatusAsync()).Phase);

                // The pane button, through the same command path the coordinator uses.
                using (var doc = JsonDocument.Parse("{\"type\":\"archive_index_now\"}"))
                    Assert.NotNull(await PaneArchiveCommands.ApplyAsync(indexer, "archive_index_now", doc.RootElement));
                Assert.True((await indexer.GetStatusAsync()).IndexingNow);

                await TickUntilIdleAsync(indexer);
                var stats = await client.GetStatsAsync();
                Assert.Equal(2, stats.Emails);
                Assert.Equal(0, stats.BodiesPending);
                Assert.False((await indexer.GetStatusAsync()).IndexingNow);

                // Done: new mail waits for an idle PC like before.
                rows[FakeOutlook.DefaultStore] = rows[FakeOutlook.DefaultStore].Append(Header("c", FakeOutlook.Newest.AddMinutes(1))).ToArray();
                await client.SyncNowAsync();
                for (int i = 0; i < 5; i++) await indexer.TickAsync();
                Assert.Equal(2, (await client.GetStatsAsync()).Emails);
                Assert.False((await indexer.GetStatusAsync()).IndexingNow);
            }
            Assert.Equal(0, outlook.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    [Fact]
    public void IndexNow_ResumesAPausedArchive_AndStillYieldsToPaneOperations()
    {
        var rows = new Dictionary<string, ArchiveHeader[]> { [FakeOutlook.DefaultStore] = new[] { Header("a", FakeOutlook.Newest) } };
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(rows, _copies);
            var client = new ArchiveClient(FakeOutlook.Profile, _directory);
            using (var indexer = new ArchiveIndexer(outlook, new TestConfig(attachments: false), _directory, idleMs: () => 0, launchEngine: () => true, timerTicks: false))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                await indexer.SetPausedAsync(true);
                await indexer.IndexNowAsync();
                Assert.False((await indexer.GetStatusAsync()).Paused);

                ArchiveIndexer.InteractiveBegin();
                try
                {
                    for (int i = 0; i < 5; i++) await indexer.TickAsync();
                    Assert.Equal(0, (await client.GetStatsAsync()).Emails);
                }
                finally { ArchiveIndexer.InteractiveEnd(); }

                await TickUntilIdleAsync(indexer);
                Assert.Equal(1, (await client.GetStatsAsync()).Emails);
            }
            Assert.Equal(0, outlook.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    /// <summary>#149: Indicizza ora reads the emails with attachments too, whether or not the PC is idle.</summary>
    [Fact]
    public void IndexNow_WithTheUserActive_CopiesAttachmentsToo()
    {
        var withAttachment = Header("b", FakeOutlook.Newest);
        withAttachment.HasAttachments = true;
        var rows = new Dictionary<string, ArchiveHeader[]>
        {
            [FakeOutlook.DefaultStore] = new[] { withAttachment, Header("a", FakeOutlook.Newest.AddHours(-1)) }
        };
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(rows, _copies);
            var client = new ArchiveClient(FakeOutlook.Profile, _directory);
            using (var indexer = new ArchiveIndexer(outlook, new TestConfig(attachments: true), _directory, idleMs: () => 0, launchEngine: () => true, timerTicks: false))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                await indexer.IndexNowAsync();
                await TickUntilIdleAsync(indexer);
                var stats = await client.GetStatsAsync();
                Assert.Equal(2, stats.Emails);
                Assert.Equal(0, stats.BodiesPending);
                Assert.Contains(outlook.Calls, call => call == nameof(FakeOutlook.CopyAttachments) + " b");
                Assert.Contains(outlook.Calls, call => call == nameof(FakeOutlook.ReadForArchive) + " a");
            }
            Assert.Equal(0, outlook.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

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
