using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveQueryScopeTests
{
    [Fact]
    public void AllSelectedAndDenied_AreDistinct()
    {
        Assert.Null(ArchiveScope.ForQuery(Array.Empty<string>(), true));
        Assert.Equal(new[] { "a" }, ArchiveScope.ForQuery(new[] { "a" }, true));
        Assert.Empty(ArchiveScope.ForQuery(Array.Empty<string>(), false)!);
        Assert.Empty(ArchiveScope.ForQuery(new[] { "a" }, false)!);
    }

    [Fact]
    public void IndexerQueryScope_PreservesAllSelectionAndDenial()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ow-query-scope-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunOnUiThread(async () =>
            {
                using var engine = new ArchiveEngine(directory);
                await engine.Ready;
                var outlook = new FakeOutlook(new Dictionary<string, ArchiveHeader[]> { ["a"] = new[] { Header("a", FakeOutlook.Newest) }, ["b"] = new[] { Header("b", FakeOutlook.Newest) } }, new());
                var config = new TestConfig(attachments: false);
                using var indexer = new ArchiveIndexer(outlook, config, directory, idleMs: () => int.MaxValue, launchEngine: () => true, timerTicks: false);
                await indexer.ActivateAsync(Array.Empty<string>());
                await TickUntilIdleAsync(indexer);
                var db = (await indexer.OpenForSearchAsync())!;
                var all = await indexer.GetAllowedSearchStoresAsync();
                Assert.Null(all);
                Assert.Equal(2, (await db.SearchAsync(new ArchiveQuery { AllowedStoreIds = all })).Count);
                await db.SetMetaAsync(ArchiveScope.StoresKey, "[\"a\"]");
                Assert.Equal(new[] { "a" }, await indexer.GetAllowedSearchStoresAsync());
                Assert.Single(await db.SearchAsync(new ArchiveQuery { AllowedStoreIds = await indexer.GetAllowedSearchStoresAsync() }));
                await db.SetMetaAsync(ArchiveScope.StoresKey, "[]");
                outlook.ClosedStores.Add("b");
                Assert.Null(await indexer.GetAllowedSearchStoresAsync());
                // Closed stores remain searchable offline; selection does not derive from open-store enumeration.
                Assert.Equal(2, (await db.SearchAsync(new ArchiveQuery { AllowedStoreIds = await indexer.GetAllowedSearchStoresAsync() })).Count);
                config.Flags[ArchiveScope.UserOffFlag] = true;
                Assert.Empty((await indexer.GetAllowedSearchStoresAsync())!);
                Assert.Equal(0, outlook.OffUiThread);
            });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
