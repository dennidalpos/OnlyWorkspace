using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Outlook.UI;
using MSOutlook = Microsoft.Office.Interop.Outlook;

namespace OnlyWorkspace.Outlook.Services;

public sealed partial class OutlookDataService
{
    /// <summary>All contacts in open stores, including contact subfolders. No search-result cap or silent partial result.</summary>
    public async Task<IReadOnlyList<PrivacyEntity>> ReadPrivacyEntitiesAsync(CancellationToken ct)
    {
        EnsureUiThread(); UiSync.Ensure(); int thread = Environment.CurrentManagedThreadId;
        var result = new List<PrivacyEntity>(); var slice = new UiSlice();
        MSOutlook.NameSpace? session = null; MSOutlook.Stores? stores = null;
        try
        {
            session = _application.Session; stores = session.Stores;
            for (int i = 1; i <= stores.Count; i++)
            {
                await slice.NextAsync(thread, ct);
                MSOutlook.Store? store = null; MSOutlook.MAPIFolder? folder = null;
                try
                {
                    store = stores[i];
                    // Other contact folders may be siblings of the default one. Traverse folder metadata,
                    // but open Items only on contact folders. Never silently turn a failed read into an empty dictionary.
                    folder = store.GetRootFolder();
                    if (folder != null) await ReadPrivacyFolderAsync(folder, result, slice, thread, ct);
                }
                finally
                {
                    ComObjects.Release(folder);
                    ComObjects.Release(store);
                }
            }
            return result;
        }
        finally
        {
            ComObjects.Release(stores);
            ComObjects.Release(session);
        }
    }

    private static async Task ReadPrivacyFolderAsync(MSOutlook.MAPIFolder folder, List<PrivacyEntity> result, UiSlice slice, int thread, CancellationToken ct)
    {
        MSOutlook.Items? items = null; MSOutlook.Folders? children = null;
        try
        {
            if (folder.DefaultItemType == MSOutlook.OlItemType.olContactItem)
            {
                items = folder.Items; int count = items.Count;
                result.AddRange(await PrivacyEntitySnapshot.ReadAsync(count, i =>
                {
                    var row = new List<PrivacyEntity>(); object? item = null;
                    try
                    {
                        item = items[i];
                        if (item is MSOutlook.ContactItem contact)
                        {
                            Add("PERSONA", contact.FullName); Add("AZIENDA", contact.CompanyName);
                            Add("EMAIL", contact.Email1Address); Add("EMAIL", contact.Email2Address); Add("EMAIL", contact.Email3Address);
                            Add("TEL", contact.MobileTelephoneNumber); Add("TEL", contact.BusinessTelephoneNumber);
                        }
                        return row;
                    }
                    finally { ComObjects.Release(item); }
                    void Add(string category, string? value)
                    {
                        if (!string.IsNullOrWhiteSpace(value)) row.Add(new PrivacyEntity { Category = category, Value = value!.Trim() });
                    }
                }, token => slice.NextAsync(thread, token), ct));
            }
            children = folder.Folders; int childCount = children.Count;
            for (int i = 1; i <= childCount; i++)
            {
                await slice.NextAsync(thread, ct); MSOutlook.MAPIFolder? child = null;
                try { child = children[i]; await ReadPrivacyFolderAsync(child, result, slice, thread, ct); }
                finally { ComObjects.Release(child); }
            }
        }
        finally
        {
            ComObjects.Release(children);
            ComObjects.Release(items);
        }
    }
}
