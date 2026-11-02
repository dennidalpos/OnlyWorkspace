using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>
/// 18/10/2026: emails taken out of Gmail's Inbox stayed in the archive as present, because an IMAP folder table is a
/// cache and cannot prove absence. The full check now asks Outlook about each email it did not see.
/// </summary>
public sealed class ArchiveIndexerCacheCheckTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_CacheCheck_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private readonly List<ArchiveEngine> _engines = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var copy in _copies) if (Directory.Exists(copy)) TestArtifacts.DeleteDirectory(copy, true);
        if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true);
    }

    [Fact]
    public void FullCheck_OnACacheStore_GivesUpOnlyWhatOutlookReportsNotFound()
    {
        var rows = new Dictionary<string, ArchiveHeader[]>
        {
            [FakeOutlook.DefaultStore] = new[] { "kept", "gone", "unsure", "opens" }.Select((id, i) => Header(id, FakeOutlook.Newest.AddHours(-i))).ToArray()
        };
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(rows, _copies);
            outlook.CacheStores.Add(FakeOutlook.DefaultStore);
            var client = new ArchiveClient(FakeOutlook.Profile, _directory);
            using (var indexer = NewIndexer(outlook))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                await TickUntilIdleAsync(indexer);
                Assert.Equal(4, (await client.GetStatsAsync()).Emails);

                // Out of the folder table: one not found, one Outlook cannot tell about, one that still opens by EntryID.
                rows[FakeOutlook.DefaultStore] = rows[FakeOutlook.DefaultStore].Take(1).ToArray();
                outlook.NotFound.Add("gone");
                outlook.Unknown.Add("unsure");
                int asked = outlook.Calls.Count;
                await client.SetMetaAsync("check_time", DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds().ToString());
                await client.SyncNowAsync();
                await TickUntilAsync(indexer, async () => outlook.Calls.Skip(asked).Any(c => c.StartsWith("ArchiveItemExists")) && await client.GetMetaAsync("check_open") == null);
                Assert.Equal(new[] { "ArchiveItemExists gone", "ArchiveItemExists opens", "ArchiveItemExists unsure" },
                    outlook.Calls.Skip(asked).Where(c => c.StartsWith("ArchiveItemExists")).OrderBy(c => c));
            }

            Assert.Equal(new[] { "kept", "opens", "unsure" }, (await client.SearchAsync(new ArchiveQuery())).Select(h => h.EntryId).OrderBy(e => e));
            Assert.Equal(0, outlook.OffUiThread);
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
