using System;
using System.Collections.Generic;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>
/// The Outlook calls of <see cref="ArchiveIndexer"/>, UI thread only. COM-free so tests can stand in for Outlook.
/// </summary>
public interface IArchiveMailSource
{
    string GetProfileName();
    IReadOnlyCollection<string> GetOwnAddressList();
    IArchiveFolderWalk WalkArchiveFolders(ICollection<string>? storeIds);
    IArchiveHeaderReader? OpenArchiveReader(string folderEntryId, string storeId, DateTime? newerThanUtc, DateTime? olderThanUtc, DateTime? modifiedSinceUtc = null, DateTime? modifiedUntilUtc = null);
    ArchiveBody? ReadForArchive(string entryId, string storeId);
    /// <summary>True when the item opens, false when Outlook reports it not found, null when it cannot tell.</summary>
    bool? ArchiveItemExists(string entryId, string storeId);
    AttachmentCopies? CopyAttachments(string entryId, string? storeId, Func<AttachmentFacts, bool> shouldSave, int fromIndex = 1, int maxCopies = int.MaxValue);
    IReadOnlyList<OutlookStoreInfo> EnumerateStores();
    /// <summary>Guess of the user's addresses in a store without an account; null when its Sent Items cannot be read.</summary>
    StoreOwnEntry? GuessStoreOwnAddresses(string storeId);
    void SetStoreOwnAddresses(IReadOnlyCollection<string> addresses, IReadOnlyCollection<string> names);
}

/// <summary>
/// Mail folders of the chosen stores, listed a few at a time so that no call holds Outlook's UI thread for long;
/// dispose on the UI thread.
/// </summary>
public interface IArchiveFolderWalk : IDisposable
{
    /// <summary>Lists up to <paramref name="max"/> more folders; true once every store has been listed.</summary>
    bool Step(int max);
    IReadOnlyList<MailFolderRef> Folders { get; }
    IReadOnlyCollection<string> AuthoritativeStores { get; }
    IReadOnlyDictionary<string, string> Issues { get; }
}

/// <summary>Rows of one folder, a batch at a time; dispose on the UI thread.</summary>
public interface IArchiveHeaderReader : IDisposable
{
    bool CanDeleteAbsent { get; }
    bool EndOfTable { get; }
    List<ArchiveHeader> ReadBatch(int max);
}
