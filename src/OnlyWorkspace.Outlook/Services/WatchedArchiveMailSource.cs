using System;
using System.Collections.Generic;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>
/// The indexer's Outlook calls, each named for <see cref="UiThreadWatch"/> while it runs. A name held for the whole
/// tick also covered its awaits (a cloud card waits up to 5 minutes), so Outlook's own stalls in between were logged
/// as indexing (A149, 27/10/2026): only the synchronous call that holds the UI thread carries the name.
/// </summary>
internal sealed class WatchedArchiveMailSource : IArchiveMailSource
{
    private readonly IArchiveMailSource _inner;

    public WatchedArchiveMailSource(IArchiveMailSource inner) => _inner = inner;

    internal static T Named<T>(string call, Func<T> read)
    {
        using (UiThreadWatch.Activity("archive indexing: " + call)) return read();
    }

    private static void Named(string call, Action action)
    {
        using (UiThreadWatch.Activity("archive indexing: " + call)) action();
    }

    public string GetProfileName() => Named(nameof(GetProfileName), _inner.GetProfileName);
    public IReadOnlyCollection<string> GetOwnAddressList() => Named(nameof(GetOwnAddressList), _inner.GetOwnAddressList);
    public IReadOnlyList<OutlookStoreInfo> EnumerateStores() => Named(nameof(EnumerateStores), _inner.EnumerateStores);

    public IArchiveFolderWalk WalkArchiveFolders(ICollection<string>? storeIds) =>
        new Walk(Named(nameof(WalkArchiveFolders), () => _inner.WalkArchiveFolders(storeIds)));

    public IArchiveHeaderReader? OpenArchiveReader(string folderEntryId, string storeId, DateTime? newerThanUtc, DateTime? olderThanUtc, DateTime? modifiedSinceUtc = null, DateTime? modifiedUntilUtc = null) =>
        Named(nameof(OpenArchiveReader), () => _inner.OpenArchiveReader(folderEntryId, storeId, newerThanUtc, olderThanUtc, modifiedSinceUtc, modifiedUntilUtc)) is { } reader ? new Reader(reader) : null;

    public ArchiveBody? ReadForArchive(string entryId, string storeId) => Named(nameof(ReadForArchive), () => _inner.ReadForArchive(entryId, storeId));
    public bool? ArchiveItemExists(string entryId, string storeId) => Named(nameof(ArchiveItemExists), () => _inner.ArchiveItemExists(entryId, storeId));

    public AttachmentCopies? CopyAttachments(string entryId, string? storeId, Func<AttachmentFacts, bool> shouldSave, int fromIndex = 1, int maxCopies = int.MaxValue) =>
        Named(nameof(CopyAttachments), () => _inner.CopyAttachments(entryId, storeId, shouldSave, fromIndex, maxCopies));

    public StoreOwnEntry? GuessStoreOwnAddresses(string storeId) => Named(nameof(GuessStoreOwnAddresses), () => _inner.GuessStoreOwnAddresses(storeId));

    public void SetStoreOwnAddresses(IReadOnlyCollection<string> addresses, IReadOnlyCollection<string> names) =>
        Named(nameof(SetStoreOwnAddresses), () => _inner.SetStoreOwnAddresses(addresses, names));

    private sealed class Walk : IArchiveFolderWalk
    {
        private readonly IArchiveFolderWalk _inner;
        public Walk(IArchiveFolderWalk inner) => _inner = inner;
        public bool Step(int max) => Named("WalkArchiveFolders.Step", () => _inner.Step(max));
        public IReadOnlyList<MailFolderRef> Folders => _inner.Folders;
        public IReadOnlyCollection<string> AuthoritativeStores => _inner.AuthoritativeStores;
        public IReadOnlyDictionary<string, string> Issues => _inner.Issues;
        public void Dispose() => Named("WalkArchiveFolders.Dispose", _inner.Dispose);
    }

    private sealed class Reader : IArchiveHeaderReader
    {
        private readonly IArchiveHeaderReader _inner;
        public Reader(IArchiveHeaderReader inner) => _inner = inner;
        public bool CanDeleteAbsent => _inner.CanDeleteAbsent;
        public bool EndOfTable => Named("EndOfTable", () => _inner.EndOfTable);
        public List<ArchiveHeader> ReadBatch(int max) => Named("ReadBatch", () => _inner.ReadBatch(max));
        public void Dispose() => Named("Reader.Dispose", _inner.Dispose);
    }
}
