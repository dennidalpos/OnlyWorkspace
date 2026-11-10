using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>A PST without account: the sender of its Sent Items is the user, not a counterparty.</summary>
public sealed class ArchiveIndexerOwnAddressesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_OwnAddresses_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private readonly List<ArchiveEngine> _engines = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var copy in _copies) if (Directory.Exists(copy)) TestArtifacts.DeleteDirectory(copy, true);
        if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true);
    }

    [Fact]
    public void PstGuess_IsSavedAndApplied_AndTheUsersRemovalWins()
    {
        var old = Header("p1", FakeOutlook.Newest);
        old.SenderAddress = "danny@vecchia.it";
        var rows = new Dictionary<string, ArchiveHeader[]> { [FakeOutlook.DefaultStore] = new[] { Header("m1", FakeOutlook.Newest) }, ["pst"] = new[] { old } };
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(rows, _copies);
            outlook.SentGuesses["pst"] = new StoreOwnEntry { Guessed = { "danny@vecchia.it" }, Names = { "Danny" } };
            var client = new ArchiveClient(FakeOutlook.Profile, _directory);
            using (var indexer = NewIndexer(outlook))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                await TickUntilIdleAsync(indexer);

                Assert.Equal(new[] { "danny@vecchia.it" }, StoreOwnAddresses.All(await client.GetMetaAsync(StoreOwnAddresses.MetaKey)));
                Assert.Equal(new[] { "danny@vecchia.it" }, outlook.OwnFromStores);
                Assert.Null(await client.CounterpartyOfAsync("danny@vecchia.it"));
                Assert.Single(outlook.Calls, c => c == nameof(FakeOutlook.GuessStoreOwnAddresses));

                // Le mie cassette: the user says it is not theirs.
                await client.SetMetaAsync(StoreOwnAddresses.MetaKey, StoreOwnAddresses.WithChoice(await client.GetMetaAsync(StoreOwnAddresses.MetaKey), "pst", Array.Empty<string>()));
                await indexer.TickAsync();

                Assert.Empty(outlook.OwnFromStores);
                Assert.NotNull(await client.CounterpartyOfAsync("danny@vecchia.it"));
                Assert.Null(await client.CounterpartyOfAsync("me@example.test"));
            }
            Assert.Equal(0, outlook.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    [Fact]
    public void PstGuess_WaitsForTheUserToBeAway()
    {
        // 200 Sent Items rows read on Outlook's UI thread: like the rest of the indexing, never while the user types or clicks.
        var rows = new Dictionary<string, ArchiveHeader[]> { [FakeOutlook.DefaultStore] = new[] { Header("m1", FakeOutlook.Newest) }, ["pst"] = Array.Empty<ArchiveHeader>() };
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(rows, _copies);
            outlook.SentGuesses["pst"] = new StoreOwnEntry { Guessed = { "danny@vecchia.it" }, Names = { "Danny" } };
            int idle = 0;
            using (var indexer = new ArchiveIndexer(outlook, new TestConfig(attachments: false), _directory, idleMs: () => idle, launchEngine: () => true, timerTicks: false))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                for (int i = 0; i < 3; i++) await indexer.TickAsync();
                Assert.DoesNotContain(nameof(FakeOutlook.GuessStoreOwnAddresses), outlook.Calls);

                idle = int.MaxValue;
                // Under load the engine can still be busy with the earlier ticks: allow a few more.
                for (int i = 0; i < 20 && !outlook.Calls.Contains(nameof(FakeOutlook.GuessStoreOwnAddresses)); i++)
                {
                    await indexer.TickAsync();
                    await Task.Delay(50);
                }
                Assert.Single(outlook.Calls, c => c == nameof(FakeOutlook.GuessStoreOwnAddresses));
            }
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
