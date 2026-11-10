using System;
using System.Collections.Generic;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>
/// Store list kept between calls: the indexer, Stato and the search form asked Outlook for every store and every
/// account's delivery store several times a second, on the UI thread Outlook watches for slow add-ins. Read again when
/// Outlook adds or removes a store, and at most every <c>every</c> otherwise (a mailbox closing or going offline).
/// </summary>
internal sealed class StoreListCache
{
    private readonly TimeSpan _every;
    private readonly Func<DateTime> _utcNow;
    private IReadOnlyList<OutlookStoreInfo>? _stores;
    private DateTime _readUtc;

    public StoreListCache(TimeSpan every, Func<DateTime>? utcNow = null)
    {
        _every = every;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public IReadOnlyList<OutlookStoreInfo> Get(Func<IReadOnlyList<OutlookStoreInfo>> read)
    {
        var now = _utcNow();
        if (_stores != null && now - _readUtc < _every) return _stores;
        _stores = read();
        _readUtc = now;
        return _stores;
    }

    /// <summary>Outlook's StoreAdd and BeforeStoreRemove events: the next call reads the stores again.</summary>
    public void Invalidate() => _stores = null;
}
