using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>
/// 19/10/2026 (A18): an email moved out of a PST folder left the archive after 381 s, because the next full check
/// waited for the round of new mail that followed its due time. It now starts as soon as it is due.
/// </summary>
public sealed class ArchiveIndexerCheckScheduleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_CheckSchedule_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private readonly List<ArchiveEngine> _engines = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var copy in _copies) if (Directory.Exists(copy)) TestArtifacts.DeleteDirectory(copy, true);
        if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true);
    }

    [Fact]
    public void NextFullCheck_StartsWhenDue_WithoutWaitingForTheNextRoundOfNewMail()
    {
        var rows = new Dictionary<string, ArchiveHeader[]>
        {
            [FakeOutlook.DefaultStore] = new[] { "kept", "moved" }.Select((id, i) => Header(id, FakeOutlook.Newest.AddHours(-i))).ToArray()
        };
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(rows, _copies);
            var client = new ArchiveClient(FakeOutlook.Profile, _directory);
            using (var indexer = new ArchiveIndexer(outlook, new TestConfig(attachments: false), _directory, idleMs: () => int.MaxValue, launchEngine: () => true,
                timerTicks: false, rescanEvery: TimeSpan.FromSeconds(1)))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                await TickUntilIdleAsync(indexer);
                Assert.Equal(2, (await client.GetStatsAsync()).Emails);
                // A full check asked and closed in this session.
                string old = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds().ToString();
                await client.SetMetaAsync("check_time", old);
                await client.SyncNowAsync();
                await TickUntilAsync(indexer, async () => await client.GetMetaAsync("check_time") != old && await client.GetMetaAsync("check_open") == null);

                // Moved out of the folder after the check: the next one, due in a second, gives it up within the 5 seconds of ticks.
                rows[FakeOutlook.DefaultStore] = rows[FakeOutlook.DefaultStore].Take(1).ToArray();
                await TickUntilAsync(indexer, async () =>
                {
                    await Task.Delay(10);
                    return (await client.SearchAsync(new ArchiveQuery())).Count == 1;
                });
            }

            Assert.Equal(new[] { "kept" }, (await client.SearchAsync(new ArchiveQuery())).Select(h => h.EntryId));
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
