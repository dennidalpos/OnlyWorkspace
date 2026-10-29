using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>
/// A store with many folders is listed a slice per tick: on WS-DP01 a PST with 3000 folders listed in one call
/// held Outlook's UI thread until Outlook disabled the add-in.
/// </summary>
public sealed class ArchiveIndexerFolderWalkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_FolderWalk_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private readonly List<ArchiveEngine> _engines = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var copy in _copies) if (Directory.Exists(copy)) Directory.Delete(copy, true);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void ManyFolders_AreListedAcrossTicks_ThenIndexed()
    {
        var rows = Enumerable.Range(0, 30).Select(i => Header("m" + i, FakeOutlook.Newest.AddHours(-i))).ToArray();
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            // 120 folders at 20 per step; each step takes 25 ms, so a 40 ms slice holds at most two.
            var outlook = new FakeOutlook(rows, _copies) { ExtraFoldersPerStore = 119, OnFolderStep = () => Thread.Sleep(25) };
            var stepsPerTick = new List<int>();
            using (var indexer = NewIndexer(outlook))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                for (int i = 0; i < 5000 && (await indexer.GetStatusAsync()).Phase != "idle"; i++)
                {
                    int before = outlook.Walks.Sum(w => w.Steps);
                    await indexer.TickAsync();
                    int steps = outlook.Walks.Sum(w => w.Steps) - before;
                    if (steps > 0) stepsPerTick.Add(steps);
                }
                var status = await indexer.GetStatusAsync();
                Assert.True(status.Phase == "idle", $"Phase {status.Phase}: {status.Error}");
            }

            var walk = Assert.Single(outlook.Walks);
            Assert.Equal(6, walk.Steps);
            Assert.True(walk.Disposed);
            Assert.True(stepsPerTick.Count >= 3, $"Listed in {stepsPerTick.Count} ticks.");
            Assert.All(stepsPerTick, steps => Assert.InRange(steps, 1, 2));
            var stats = await new ArchiveClient(FakeOutlook.Profile, _directory).GetStatsAsync();
            Assert.Equal(120, stats.Folders);
            Assert.Equal(120, stats.FoldersDone);
            Assert.Equal(rows.Length, stats.Emails);
            Assert.Equal(0, outlook.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    [Fact]
    public void QuitWhileListing_NoMoreOutlookCalls_AndTheWalkIsReleased()
    {
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(new[] { Header("q1", FakeOutlook.Newest) }, _copies) { ExtraFoldersPerStore = 300 };
            using (var indexer = NewIndexer(outlook))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                outlook.OnFolderStep = () => { if (outlook.Walks[0].Steps == 4) outlook.Quit(indexer); };
                await TickUntilAsync(indexer, () => Task.FromResult(outlook.Stopped));
                await indexer.TickAsync();

                Assert.Empty(outlook.CallsAfterQuit);
                Assert.False(outlook.Walks[0].Disposed, "Stop must not release Outlook objects.");
            }
            Assert.True(outlook.Walks[0].Disposed);
            Assert.Equal(0, outlook.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    /// <summary>
    /// A mailbox removed from the profile with its folders queued: one failed open, then its other folders leave the
    /// queues (17/10/2026: 1541 warnings in 6.5 minutes after removing a 3000-folder PST).
    /// </summary>
    [Fact]
    public void StoreClosedWithFoldersQueued_FailsOnce_AndIsListedNoMore()
    {
        var rows = new Dictionary<string, ArchiveHeader[]>
        {
            ["kept"] = new[] { Header("k1", FakeOutlook.Newest) },
            ["gone"] = new[] { Header("g1", FakeOutlook.Newest) }
        };
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(rows, _copies) { ExtraFoldersPerStore = 20 };
            // The store goes once its folders are listed and queued.
            outlook.OnFolderStep = () => { if (outlook.Walks.Count == 1) outlook.ClosedStores.Add("gone"); };
            using (var indexer = NewIndexer(outlook))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                await TickUntilIdleAsync(indexer);
            }

            Assert.Equal(1, outlook.FailedOpens);
            Assert.Equal(2, outlook.Walks.Count);
            var stats = await new ArchiveClient(FakeOutlook.Profile, _directory).GetStatsAsync();
            Assert.Equal(1, stats.Emails);
            Assert.Equal(0, outlook.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    /// <summary>A mailbox removed from the profile after its inventory: closed, out of the totals, its emails kept.</summary>
    [Fact]
    public void StoreRemovedAfterInventory_IsClosed_AndCountsAgainOnceReopened()
    {
        var rows = new Dictionary<string, ArchiveHeader[]>
        {
            ["kept"] = new[] { Header("k1", FakeOutlook.Newest) },
            ["gone"] = new[] { Header("g1", FakeOutlook.Newest) }
        };
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(rows, _copies) { ExtraFoldersPerStore = 2 };
            var client = new ArchiveClient(FakeOutlook.Profile, _directory);
            using (var indexer = NewIndexer(outlook))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                await TickUntilIdleAsync(indexer);
                Assert.Equal(6, (await client.GetStatsAsync()).Folders);

                outlook.ClosedStores.Add("gone");
                await client.SyncNowAsync();
                await TickUntilIdleAsync(indexer);
                var stats = await client.GetStatsAsync();
                Assert.Equal((3, 3, 0, 2), (stats.Folders, stats.FoldersDone, stats.FoldersFailed, stats.Emails));
                var gone = (await client.GetStoreCoverageAsync()).Single(c => c.StoreId == "gone");
                Assert.Equal(("chiusa", string.Empty, 1), (gone.State, gone.Error, gone.Emails));

                outlook.ClosedStores.Remove("gone");
                await client.SyncNowAsync();
                await TickUntilIdleAsync(indexer);
                Assert.Equal(6, (await client.GetStatsAsync()).Folders);
            }
            Assert.Equal(0, outlook.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    /// <summary>The reason a mailbox's folder list is incomplete shows in that mailbox's coverage row.</summary>
    [Fact]
    public void FolderIssue_ShowsInTheStoreCoverage()
    {
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(new Dictionary<string, ArchiveHeader[]>
            {
                ["big"] = new[] { Header("b1", FakeOutlook.Newest) },
                ["ok"] = new[] { Header("o1", FakeOutlook.Newest) }
            }, _copies);
            outlook.WalkIssues["big"] = "Elenco cartelle incompleto: superato il limite di 3000 cartelle per cassetta.";
            outlook.WalkIssues["unreachable"] = "Cassetta selezionata non raggiungibile per l'inventario.";
            using (var indexer = NewIndexer(outlook))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                await TickUntilIdleAsync(indexer);
            }

            var coverage = (await new ArchiveClient(FakeOutlook.Profile, _directory).GetStoreCoverageAsync()).ToDictionary(c => c.StoreId);
            Assert.Equal(outlook.WalkIssues["big"], coverage["big"].Error);
            Assert.Equal("errore", coverage["big"].State);
            Assert.Equal(string.Empty, coverage["ok"].Error);
            Assert.Equal(outlook.WalkIssues["unreachable"], coverage["unreachable"].Error);
            await StopEngineAsync(engine);
        });
    }

    private ArchiveIndexer NewIndexer(FakeOutlook outlook) =>
        new(outlook, new TestConfig(attachments: false), _directory, idleMs: () => int.MaxValue, launchEngine: () => true, timerTicks: false);

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
