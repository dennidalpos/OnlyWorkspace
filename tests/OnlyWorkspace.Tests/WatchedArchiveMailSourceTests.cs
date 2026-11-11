using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Tests;

/// <summary>#096: every Outlook call of the indexer is named for the UI-thread watch while it runs, and only then.</summary>
public sealed class WatchedArchiveMailSourceTests
{
    private sealed class Spy : IArchiveMailSource
    {
        public List<string?> Seen { get; } = new();
        public bool WalkDisposed, ReaderDisposed;
        public IArchiveHeaderReader? Reader;

        private T Note<T>(T value) { Seen.Add(UiThreadWatch.CurrentActivity); return value; }

        public string GetProfileName() => Note("profile");
        public IReadOnlyCollection<string> GetOwnAddressList() => Note<IReadOnlyCollection<string>>(new[] { "a@x.it" });
        public IReadOnlyList<OutlookStoreInfo> EnumerateStores() => Note<IReadOnlyList<OutlookStoreInfo>>(Array.Empty<OutlookStoreInfo>());
        public IArchiveFolderWalk WalkArchiveFolders(StoreScope stores) => Note<IArchiveFolderWalk>(new SpyWalk(this));
        public IArchiveHeaderReader? OpenArchiveReader(string folderEntryId, string storeId, DateTime? newerThanUtc, DateTime? olderThanUtc, DateTime? modifiedSinceUtc = null, DateTime? modifiedUntilUtc = null) =>
            Note(Reader);
        public ArchiveBody? ReadForArchive(string entryId, string storeId) => Note<ArchiveBody?>(null);
        public bool? ArchiveItemExists(string entryId, string storeId) => Note<bool?>(false);
        public AttachmentCopies? CopyAttachments(string entryId, string? storeId, Func<AttachmentFacts, bool> shouldSave, int fromIndex = 1, int maxCopies = int.MaxValue) =>
            Note<AttachmentCopies?>(null);
        public StoreOwnEntry? GuessStoreOwnAddresses(string storeId) => Note<StoreOwnEntry?>(null);
        public void SetStoreOwnAddresses(IReadOnlyCollection<string> addresses, IReadOnlyCollection<string> names) => Note(0);
    }

    private sealed class SpyWalk : IArchiveFolderWalk
    {
        private readonly Spy _spy;
        public SpyWalk(Spy spy) => _spy = spy;
        public bool Step(int max) { _spy.Seen.Add(UiThreadWatch.CurrentActivity); return true; }
        public IReadOnlyList<MailFolderRef> Folders { get; } = Array.Empty<MailFolderRef>();
        public IReadOnlyCollection<string> CompleteStores { get; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, string> Issues { get; } = new Dictionary<string, string>();
        public void Dispose() { _spy.Seen.Add(UiThreadWatch.CurrentActivity); _spy.WalkDisposed = true; }
    }

    private sealed class SpyReader : IArchiveHeaderReader
    {
        private readonly Spy _spy;
        public SpyReader(Spy spy) => _spy = spy;
        public bool CanDeleteAbsent => true;
        public bool EndOfTable { get { _spy.Seen.Add(UiThreadWatch.CurrentActivity); return true; } }
        public List<ArchiveHeader> ReadBatch(int max) { _spy.Seen.Add(UiThreadWatch.CurrentActivity); return new(); }
        public void Dispose() { _spy.Seen.Add(UiThreadWatch.CurrentActivity); _spy.ReaderDisposed = true; }
    }

    [Fact]
    public void EveryCallCarriesItsNameWhileItRunsAndLeavesNoneBehind()
    {
        var spy = new Spy();
        var source = new WatchedArchiveMailSource(spy);

        Assert.Equal("profile", source.GetProfileName());
        Assert.Equal(new[] { "a@x.it" }, source.GetOwnAddressList());
        Assert.Empty(source.EnumerateStores());
        Assert.Null(source.ReadForArchive("e", "s"));
        Assert.False(source.ArchiveItemExists("e", "s"));
        Assert.Null(source.CopyAttachments("e", "s", _ => true));
        Assert.Null(source.GuessStoreOwnAddresses("s"));
        source.SetStoreOwnAddresses(new[] { "a" }, new[] { "n" });

        Assert.Equal(new[]
        {
            "GetProfileName", "GetOwnAddressList", "EnumerateStores", "ReadForArchive", "ArchiveItemExists",
            "CopyAttachments", "GuessStoreOwnAddresses", "SetStoreOwnAddresses"
        }.Select(n => "archive indexing: " + n), spy.Seen);
        Assert.Null(UiThreadWatch.CurrentActivity);
    }

    [Fact]
    public void WalkAndReaderAreWrappedSoEachStepAndDisposeIsNamed()
    {
        var spy = new Spy();
        spy.Reader = new SpyReader(spy);
        var source = new WatchedArchiveMailSource(spy);

        using (var walk = source.WalkArchiveFolders(StoreScope.All()))
        {
            Assert.True(walk.Step(5));
            Assert.Empty(walk.Folders);
        }
        var reader = source.OpenArchiveReader("f", "s", null, null);
        Assert.NotNull(reader);
        Assert.True(reader!.CanDeleteAbsent);
        Assert.True(reader.EndOfTable);
        Assert.Empty(reader.ReadBatch(10));
        reader.Dispose();

        Assert.Equal(new[]
        {
            "archive indexing: WalkArchiveFolders", "archive indexing: WalkArchiveFolders.Step", "archive indexing: WalkArchiveFolders.Dispose",
            "archive indexing: OpenArchiveReader", "archive indexing: EndOfTable", "archive indexing: ReadBatch", "archive indexing: Reader.Dispose"
        }, spy.Seen);
        Assert.True(spy.WalkDisposed);
        Assert.True(spy.ReaderDisposed);
        Assert.Null(UiThreadWatch.CurrentActivity);
    }

    [Fact]
    public void ANullReaderStaysNullAndANameIsRestoredWhenTheInnerCallThrows()
    {
        var spy = new Spy();
        var source = new WatchedArchiveMailSource(spy);
        Assert.Null(source.OpenArchiveReader("f", "s", null, null));

        Assert.Throws<InvalidOperationException>(() => WatchedArchiveMailSource.Named<int>("boom", () => throw new InvalidOperationException()));
        Assert.Null(UiThreadWatch.CurrentActivity);
    }
}
