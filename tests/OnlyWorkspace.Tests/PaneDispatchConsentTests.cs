using System.Text.Json;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using OnlyWorkspace.Outlook.UI;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>
/// Pane messages as TaskPaneCoordinator dispatches them, applied to the real indexer and engine with a synthetic
/// Outlook (two open stores) without a model: no Outlook, network or provider is reached.
/// </summary>
public sealed class PaneDispatchConsentTests : IDisposable
{
    private const string Gmail = "gmail";
    private const string Work = "work";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_PaneConsent_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private readonly List<ArchiveEngine> _engines = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var copy in _copies) if (Directory.Exists(copy)) Directory.Delete(copy, true);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }







    [Fact]
    public void MailboxChoiceSavedOutsideOutlook_IsAppliedAndTheStoresArePublished()
    {
        RunOnUiThread(async () =>
        {
            await StartEngineAsync();
            var outlook = NewOutlook();
            using var indexer = NewIndexer(outlook);
            await PrepareAsync(indexer);
            var stores = ArchiveScope.ParseOpenStores(await Client().GetMetaAsync(ArchiveScope.OpenStoresKey));
            Assert.Equal(new[] { Gmail, Work }, stores.Select(s => s.Id).OrderBy(s => s));

            await Client().SetMetaAsync(ArchiveScope.StoresKey, JsonSerializer.Serialize(new[] { Gmail }));
            await TickUntilIdleAsync(indexer);
            var hits = await Client().SearchAsync(new ArchiveQuery { FromUtc = FakeOutlook.Newest.AddDays(-1), Max = 10 });
            Assert.Equal(new[] { "g1" }, hits.Select(h => h.EntryId));
        });
    }

    [Fact]
    public void UserTurnedTheArchiveOff_NothingIsCreated()
    {
        RunOnUiThread(async () =>
        {
            await StartEngineAsync();
            var outlook = NewOutlook();
            using var indexer = NewIndexer(outlook, archiveOff: true);
            for (int i = 0; i < 5; i++) await indexer.TickAsync();
            Assert.False(ArchiveDb.Exists(_directory, FakeOutlook.Profile));
            Assert.Equal("off", (await indexer.GetStatusAsync()).Phase);
        });
    }

    [Fact]
    public void DeletingTheArchive_KeepsItOffForTheUser()
    {
        RunOnUiThread(async () =>
        {
            await StartEngineAsync();
            var outlook = NewOutlook();
            var config = new WorkspaceConfig { Archive = new WorkspaceArchiveConfig { Enabled = true } };
            var settings = new TestConfig(config);
            using var indexer = new ArchiveIndexer(outlook, settings, _directory, idleMs: () => int.MaxValue, launchEngine: () => true, timerTicks: false);
            await PrepareAsync(indexer);
            Assert.True(ArchiveDb.Exists(_directory, FakeOutlook.Profile));

            using var doc = JsonDocument.Parse("{\"type\":\"archive_delete\"}");
            await PaneArchiveCommands.ApplyAsync(indexer, "archive_delete", doc.RootElement);
            for (int i = 0; i < 5; i++) await indexer.TickAsync();
            Assert.True(settings.GetUserFlag(ArchiveScope.UserOffFlag));
            Assert.False(ArchiveDb.Exists(_directory, FakeOutlook.Profile));
        });
    }

    [Theory]
    [InlineData("archive_activate")]
    [InlineData("archive_consent")]
    public async Task PaneNoLongerChangesMailboxesOrConsent(string type)
    {
        using var doc = JsonDocument.Parse("{\"type\":\"" + type + "\",\"granted\":true,\"stores\":[]}");
        Assert.Null(await PaneArchiveCommands.ApplyAsync(null!, type, doc.RootElement));
    }


    private ArchiveClient Client() => new(FakeOutlook.Profile, _directory);
    private FakeOutlook NewOutlook() => new(new Dictionary<string, ArchiveHeader[]>
    {
        [Gmail] = new[] { Header("g1", FakeOutlook.Newest) },
        [Work] = new[] { Header("w1", FakeOutlook.Newest.AddHours(-1)) }
    }, _copies);
    private ArchiveIndexer NewIndexer(FakeOutlook outlook, bool archiveOff = false)
    {
        var settings = new TestConfig(new WorkspaceConfig { Archive = new() { Enabled = true } });
        settings.Flags[ArchiveScope.UserOffFlag] = archiveOff;
        return new ArchiveIndexer(outlook, settings, _directory, () => int.MaxValue, () => true, timerTicks: false);
    }
    private static Task PrepareAsync(ArchiveIndexer indexer) => TickUntilIdleAsync(indexer);
    private async Task StartEngineAsync()
    {
        var engine = new ArchiveEngine(_directory);
        _engines.Add(engine);
        await engine.Ready;
    }
}
