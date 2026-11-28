using System;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// The archived original last read for a body: its attachments are read right after, and the same original (body
/// and attachment texts, which can be large) crossed the pipe twice. COM-free.
/// </summary>
internal sealed class ArchivedOriginalCache
{
    private (string EntryId, string StoreId, ArchiveBody? Original)? _last;

    public void Clear() => _last = null;

    public async Task<ArchiveBody?> GetAsync(string entryId, string storeId, Func<Task<ArchiveBody?>?> load)
    {
        if (_last is { } last && last.EntryId == entryId && last.StoreId == storeId) return last.Original;
        var task = load();
        var stored = task == null ? null : await task;
        _last = (entryId, storeId, stored);
        return stored;
    }
}
