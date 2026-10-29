using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveIndexerUnreadableAttachmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ow-indexer-local-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private ArchiveEngine? _engine;
    public void Dispose()
    {
        _engine?.Dispose();
        foreach (var copy in _copies) if (Directory.Exists(copy)) Directory.Delete(copy, true);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
    private ArchiveClient Client() => new(FakeOutlook.Profile, _directory);
    private async Task StartAsync() { _engine = new ArchiveEngine(_directory); await _engine.Ready; }
    private ArchiveIndexer Indexer(FakeOutlook outlook) => new(outlook,
        new TestConfig(new WorkspaceConfig { Archive = new() { Enabled = true, IndexAttachmentText = true } }),
        _directory, () => int.MaxValue, () => true, timerTicks: false);

    [Theory]
    [InlineData("documenti.zip")]
    [InlineData("programma.exe")]
    [InlineData("dati.bin")]
    public void ArchiveReferencesAreNeverCopiedAndKeepTheirMetadataAcrossThePipe(string name)
    {
        RunOnUiThread(async () =>
        {
            await StartAsync();
            var outlook = new FakeOutlook(new[] { Header("reference", FakeOutlook.Newest) }, _copies) { AttachmentName = name };
            using var indexer = Indexer(outlook);
            await TickUntilAsync(indexer, async () => (await Client().GetStatsAsync()).BodiesIndexed == 1);
            var hit = Assert.Single(await Client().SearchAsync(new ArchiveQuery()));
            var attachment = Assert.Single(hit.Attachments);
            Assert.Equal(name, attachment.Name); Assert.Equal("reference", attachment.State);
            Assert.Equal(64, attachment.Size); Assert.Equal(Path.GetExtension(name).TrimStart('.'), attachment.Type);
            Assert.Equal(0, outlook.SavedAttachmentCopies); Assert.Equal(0, outlook.OffUiThread);
            Assert.All(outlook.CopyDirectories, directory => Assert.False(Directory.Exists(directory)));
        });
    }

    [Fact]
    public void UnreadableAttachmentDoesNotStopOtherOriginalsAndNoModelProfileIsNeeded()
    {
        RunOnUiThread(async () =>
        {
            await StartAsync();
            var outlook = new FakeOutlook(new[] { Header("large", FakeOutlook.Newest), Header("readable", FakeOutlook.Newest.AddHours(-1)) }, _copies);
            outlook.OversizedAttachments.Add("large");
            using var indexer = Indexer(outlook);
            await TickUntilAsync(indexer, async () => (await Client().GetStatsAsync()).BodiesIndexed == 2);
            var hits = await Client().SearchAsync(new ArchiveQuery());
            Assert.Equal("limited", hits.Single(h => h.EntryId == "large").Attachments.Single().State);
            Assert.Equal("indexed", hits.Single(h => h.EntryId == "readable").Attachments.Single().State);
            Assert.Equal(0, outlook.OffUiThread);
        });
    }

    [Fact]
    public void MoreThanTenAttachmentsAreReadWholeWithoutAModel()
    {
        RunOnUiThread(async () =>
        {
            await StartAsync();
            var outlook = new FakeOutlook(new[] { Header("many", FakeOutlook.Newest) }, _copies);
            outlook.ManyAttachments["many"] = 23;
            using var indexer = Indexer(outlook);
            await TickUntilAsync(indexer, async () => (await Client().GetStatsAsync()).BodiesIndexed == 1);
            var hit = Assert.Single(await Client().SearchAsync(new ArchiveQuery()));
            Assert.Equal(23, hit.Attachments.Count);
            Assert.All(hit.Attachments, a => Assert.Equal("indexed", a.State));
            var original = await Client().GetOriginalAsync(hit.EntryId, hit.StoreId);
            Assert.Contains("Allegato 23", ArchiveAttachmentReader.WholeText(original!));
            Assert.Equal(23, outlook.SavedAttachmentCopies);
            Assert.All(outlook.CopyDirectories, d => Assert.False(Directory.Exists(d)));
            Assert.Equal(0, outlook.OffUiThread);
        });
    }
}
