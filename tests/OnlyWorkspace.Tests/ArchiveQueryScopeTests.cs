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
        Assert.True(ArchiveScope.ForQuery(StoreScope.All(), true).IsAll);
        var selected = ArchiveScope.ForQuery(StoreScope.Only(new[] { "a" }), true);
        Assert.Equal(StoreScopeKind.Selected, selected.Kind);
        Assert.Equal(new[] { "a" }, selected.StoreIds);
        Assert.True(ArchiveScope.ForQuery(StoreScope.All(), false).IsNone);
        Assert.True(ArchiveScope.ForQuery(StoreScope.Only(new[] { "a" }), false).IsNone);
    }

    [Fact]
    public void EmptySelection_IsNone_NeverAll()
    {
        Assert.Equal(StoreScopeKind.None, StoreScope.Only(Array.Empty<string>()).Kind);
        Assert.True(new StoreScope { Kind = StoreScopeKind.Selected }.IsNone);
        Assert.True(new StoreScope { Kind = (StoreScopeKind)9 }.IsNone);
        Assert.False(StoreScope.None().Allows("a"));
        Assert.True(StoreScope.All().Allows("a"));
        Assert.True(StoreScope.Only(new[] { "A" }).Allows("a"));
        Assert.False(StoreScope.Only(new[] { "A" }).Allows("b"));
    }

    [Theory]
    [InlineData(null, StoreScopeKind.All)]
    [InlineData("", StoreScopeKind.All)]
    [InlineData("[]", StoreScopeKind.All)] // first format, bare list: empty was «all»
    [InlineData("[\"a\"]", StoreScopeKind.Selected)]
    [InlineData("{\"kind\":\"all\"}", StoreScopeKind.All)]
    [InlineData("{\"kind\":\"none\"}", StoreScopeKind.None)]
    [InlineData("{\"kind\":\"selected\",\"stores\":[\"a\",\"b\"]}", StoreScopeKind.Selected)]
    [InlineData("{\"kind\":\"selected\",\"stores\":[]}", StoreScopeKind.None)] // read fail-closed, not as «all»
    [InlineData("{\"kind\":\"everything\"}", StoreScopeKind.None)]
    [InlineData("[42]", StoreScopeKind.None)]
    [InlineData("not json", StoreScopeKind.None)]
    public void SavedChoice_ParsesToAnExplicitKind(string? json, StoreScopeKind expected) =>
        Assert.Equal(expected, StoreScope.Parse(json).Kind);

    [Theory]
    [InlineData("{\"kind\":\"selected\",\"stores\":[]}")]
    [InlineData("{\"kind\":\"selected\"}")]
    [InlineData("{\"kind\":\"everything\"}")]
    [InlineData("[42]")]
    [InlineData("\"all\"")]
    public void StrictParse_RefusesWhatItCannotRead(string json) =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => StoreScope.ParseStrict(json));

    [Fact]
    public void SavedShape_RoundTripsForAllThreeKinds()
    {
        foreach (var scope in new[] { StoreScope.All(), StoreScope.None(), StoreScope.Only(new[] { "a", "b" }) })
        {
            var back = StoreScope.ParseStrict(scope.ToJson());
            Assert.Equal(scope.Kind, back.Kind);
            Assert.Equal(scope.StoreIds, back.StoreIds);
        }
        Assert.Equal("{\"kind\":\"all\"}", StoreScope.All().ToJson());
    }

    [Fact]
    public void Pipe_CarriesTheKind_AndAMissingScopeMatchesNothing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ow-scope-pipe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = ArchiveDb.Open(directory, "p");
            var folder = db.GetOrAddFolder("a", "inbox", "Inbox");
            db.AddHeaders(folder, new[] { Header("a1", FakeOutlook.Newest) }, folderDone: true);
            Assert.Single(db.Search(new ArchiveQuery { Stores = StoreScope.All() }));
            Assert.Single(db.Search(new ArchiveQuery { Stores = StoreScope.Only(new[] { "a" }) }));
            Assert.Empty(db.Search(new ArchiveQuery { Stores = StoreScope.Only(new[] { "b" }) }));
            Assert.Empty(db.Search(new ArchiveQuery { Stores = StoreScope.None() }));
            Assert.Empty(db.Search(new ArchiveQuery { Stores = null! }));
            Assert.Empty(db.GetFlow(new ArchiveFlowSeed { Stores = StoreScope.None() }).Timeline);
            Assert.Empty(db.GetPrivacyEntities(0, StoreScope.None(), 10));
            // Only a selected scope removes mailboxes; none (also a damaged saved choice) never empties the archive.
            Assert.Equal(0, db.RemoveOtherStores(StoreScope.None(), 10));
            Assert.Equal(0, db.RemoveOtherStores(StoreScope.All(), 10));
            Assert.Single(db.Search(new ArchiveQuery()));

            var json = System.Text.Json.JsonSerializer.Serialize(new ArchiveQuery { Stores = StoreScope.None() }, ArchivePipe.Json);
            var back = System.Text.Json.JsonSerializer.Deserialize<ArchiveQuery>(json, ArchivePipe.Json)!;
            Assert.Equal(StoreScopeKind.None, back.Stores.Kind);
        }
        finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, true); }
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
                Assert.True(all.IsAll);
                Assert.Equal(2, (await db.SearchAsync(new ArchiveQuery { Stores = all })).Count);
                await db.SetMetaAsync(ArchiveScope.StoresKey, StoreScope.Only(new[] { "a" }).ToJson());
                var chosen = await indexer.GetAllowedSearchStoresAsync();
                Assert.Equal(StoreScopeKind.Selected, chosen.Kind);
                Assert.Equal(new[] { "a" }, chosen.StoreIds);
                Assert.Single(await db.SearchAsync(new ArchiveQuery { Stores = chosen }));
                await db.SetMetaAsync(ArchiveScope.StoresKey, StoreScope.All().ToJson());
                outlook.ClosedStores.Add("b");
                Assert.True((await indexer.GetAllowedSearchStoresAsync()).IsAll);
                // Closed stores remain searchable offline; selection does not derive from open-store enumeration.
                Assert.Equal(2, (await db.SearchAsync(new ArchiveQuery { Stores = await indexer.GetAllowedSearchStoresAsync() })).Count);
                config.Flags[ArchiveScope.UserOffFlag] = true;
                Assert.True((await indexer.GetAllowedSearchStoresAsync()).IsNone);
                Assert.Equal(0, outlook.OffUiThread);
            });
        }
        finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, true); }
    }
}
