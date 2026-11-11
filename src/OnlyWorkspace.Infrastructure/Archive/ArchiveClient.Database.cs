using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveClient
{
    internal static readonly string[] DatabaseOperations =
    {
        nameof(ArchiveDb.GetOriginal),
        nameof(ArchiveDb.GetAttachmentText),
        nameof(ArchiveDb.ConfirmInitialInventory),
        nameof(ArchiveDb.GetBodyVersion),
        nameof(ArchiveDb.CounterpartyOfEmail),
        nameof(ArchiveDb.GetMeta),
        nameof(ArchiveDb.SetMeta),
        nameof(ArchiveDb.GetStats),
        nameof(ArchiveDb.OptimizeIndex),
        nameof(ArchiveDb.Compact),
        nameof(ArchiveDb.SetOwnAddresses),
        nameof(ArchiveDb.GetOrAddFolder),
        nameof(ArchiveDb.SetListedFolders),
        nameof(ArchiveDb.AddHeaders),
        nameof(ArchiveDb.RescanDue),
        nameof(ArchiveDb.RequestRescan),
        nameof(ArchiveDb.BeginRescan),
        nameof(ArchiveDb.UnseenCacheCopies),
        nameof(ArchiveDb.ConfirmCacheCopies),
        nameof(ArchiveDb.FinishRescan),
        nameof(ArchiveDb.SetFolderError),
        nameof(ArchiveDb.RemoveOtherStores),
        nameof(ArchiveDb.RemoveClosedStore),
        nameof(ArchiveDb.NextPendingBodies),
        nameof(ArchiveDb.SaveBody),
        nameof(ArchiveDb.SaveBodies),
        nameof(ArchiveDb.SetBodyState),
        nameof(ArchiveDb.Search),
        nameof(ArchiveDb.GetFactProgress),
        nameof(ArchiveDb.GetFlow),
        nameof(ArchiveDb.ExpandRelated),
        nameof(ArchiveDb.GetStoreCoverage),
        nameof(ArchiveDb.FindAddresses),
        nameof(ArchiveDb.GetPrivacyEntities),
        nameof(ArchiveDb.CounterpartyOf),
    };

    public Task ConfirmInitialInventoryAsync(CancellationToken ct = default) => CallAsync<object?>(nameof(ArchiveDb.ConfirmInitialInventory), Array.Empty<object?>(), ct);
    public Task<IReadOnlyList<OnlyWorkspace.Core.Privacy.PrivacyEntity>> GetPrivacyEntitiesAsync(long afterId, StoreScope stores, int take = 500, CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<OnlyWorkspace.Core.Privacy.PrivacyEntity>>(nameof(ArchiveDb.GetPrivacyEntities), new object?[] { afterId, stores, take }, ct);
    public Task<ArchiveBody?> GetOriginalAsync(string entryId, string storeId, CancellationToken ct = default) =>
        CallAsync<ArchiveBody?>(nameof(ArchiveDb.GetOriginal), new object?[] { entryId, storeId }, ct);

    public Task<string?> GetAttachmentTextAsync(string hash, CancellationToken ct = default) =>
        CallAsync<string?>(nameof(ArchiveDb.GetAttachmentText), new object?[] { hash }, ct);

    public Task<string?> GetBodyVersionAsync(long emailId, CancellationToken ct = default) => CallAsync<string?>(nameof(ArchiveDb.GetBodyVersion), new object?[] { emailId }, ct);

    public Task<long?> CounterpartyOfEmailAsync(string entryId, string? storeId = null, CancellationToken ct = default) =>
        CallAsync<long?>(nameof(ArchiveDb.CounterpartyOfEmail), new object?[] { entryId, storeId }, ct);

    public Task<string?> GetMetaAsync(string key, CancellationToken ct = default) =>
        CallAsync<string?>(nameof(ArchiveDb.GetMeta), new object?[] { key }, ct);

    public Task SetMetaAsync(string key, string? value, CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.SetMeta), new object?[] { key, value }, ct);

    public Task<ArchiveStats> GetStatsAsync(CancellationToken ct = default) =>
        CallAsync<ArchiveStats>(nameof(ArchiveDb.GetStats), new object?[] { }, ct);

    public Task OptimizeIndexAsync(CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.OptimizeIndex), Array.Empty<object?>(), ct);

    public Task CompactAsync(CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.Compact), Array.Empty<object?>(), ct);

    public Task SetOwnAddressesAsync(IEnumerable<string> addresses, CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.SetOwnAddresses), new object?[] { addresses }, ct);

    public Task<ArchiveFolder> GetOrAddFolderAsync(string storeId, string entryId, string path, CancellationToken ct = default) =>
        CallAsync<ArchiveFolder>(nameof(ArchiveDb.GetOrAddFolder), new object?[] { storeId, entryId, path }, ct);

    public Task SetListedFoldersAsync(IReadOnlyCollection<long> folderIds, IReadOnlyCollection<string>? completedStoreIds = null, IReadOnlyCollection<string>? openStoreIds = null, CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.SetListedFolders), new object?[] { folderIds, completedStoreIds, openStoreIds }, ct);

    public async Task<int> AddHeadersAsync(ArchiveFolder folder, IReadOnlyList<ArchiveHeader> rows, bool folderDone, bool verified = false, CancellationToken ct = default, bool canDeleteAbsent = true)
    {
        var commit = await CallAsync<ArchiveHeaderCommit>(nameof(ArchiveDb.AddHeaders), new object?[] { folder, rows, folderDone, verified, canDeleteAbsent }, ct).ConfigureAwait(false);
        folder.OldestUtc = commit.Folder.OldestUtc;
        folder.NewestUtc = commit.Folder.NewestUtc;
        folder.Done = commit.Folder.Done;
        folder.Error = commit.Folder.Error;
        return commit.Added;
    }

    public Task<bool> RescanDueAsync(TimeSpan every, CancellationToken ct = default) =>
        CallAsync<bool>(nameof(ArchiveDb.RescanDue), new object?[] { every }, ct);

    public Task RequestRescanAsync(CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.RequestRescan), new object?[] { }, ct);

    public Task BeginRescanAsync(IEnumerable<long> folderIds, CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.BeginRescan), new object?[] { folderIds }, ct);

    public Task<IReadOnlyList<ArchiveEmailRef>> UnseenCacheCopiesAsync(int max, CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<ArchiveEmailRef>>(nameof(ArchiveDb.UnseenCacheCopies), new object?[] { max }, ct);

    public Task ConfirmCacheCopiesAsync(IReadOnlyCollection<long> kept, IReadOnlyCollection<long> gone, CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.ConfirmCacheCopies), new object?[] { kept, gone }, ct);

    public Task<int?> FinishRescanAsync(CancellationToken ct = default) =>
        CallAsync<int?>(nameof(ArchiveDb.FinishRescan), new object?[] { }, ct);

    public Task SetFolderErrorAsync(long folderId, string error, CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.SetFolderError), new object?[] { folderId, error }, ct);

    public Task<int> RemoveOtherStoresAsync(StoreScope keep, int maxEmails, CancellationToken ct = default) =>
        CallAsync<int>(nameof(ArchiveDb.RemoveOtherStores), new object?[] { keep, maxEmails }, ct);

    public Task<int> RemoveClosedStoreAsync(string storeId, int maxEmails, CancellationToken ct = default) =>
        CallAsync<int>(nameof(ArchiveDb.RemoveClosedStore), new object?[] { storeId, maxEmails }, ct);

    public Task<IReadOnlyList<ArchiveEmailRef>> NextPendingBodiesAsync(int max, CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<ArchiveEmailRef>>(nameof(ArchiveDb.NextPendingBodies), new object?[] { max }, ct);

    public Task SaveBodyAsync(long emailId, ArchiveBody body, CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.SaveBody), new object?[] { emailId, body }, ct);

    public Task SaveBodiesAsync(IReadOnlyList<ArchiveBodyItem> items, CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.SaveBodies), new object?[] { items }, ct);

    public Task SetBodyStateAsync(long emailId, ArchiveBodyState state, CancellationToken ct = default) =>
        CallAsync<object?>(nameof(ArchiveDb.SetBodyState), new object?[] { emailId, state }, ct);

    public Task<IReadOnlyList<ArchiveHit>> SearchAsync(ArchiveQuery query, CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<ArchiveHit>>(nameof(ArchiveDb.Search), new object?[] { query }, ct);

    public Task<ArchiveFactProgress> GetFactProgressAsync(CancellationToken ct = default) =>
        CallAsync<ArchiveFactProgress>(nameof(ArchiveDb.GetFactProgress), Array.Empty<object?>(), ct);

    public Task<ArchiveRelatedResult> ExpandRelatedAsync(IReadOnlyList<long> ids, ArchiveFlowSeed policy, CancellationToken ct = default) =>
        CallAsync<ArchiveRelatedResult>(nameof(ArchiveDb.ExpandRelated), new object?[] { ids, policy }, ct);

    public Task<ArchiveFlow> GetFlowAsync(ArchiveFlowSeed seed, int max = 20000, CancellationToken ct = default) =>
        CallAsync<ArchiveFlow>(nameof(ArchiveDb.GetFlow), new object?[] { seed, max }, ct);

    public Task<IReadOnlyList<ArchiveStoreCoverage>> GetStoreCoverageAsync(CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<ArchiveStoreCoverage>>(nameof(ArchiveDb.GetStoreCoverage), Array.Empty<object?>(), ct);

    public Task<IReadOnlyList<long>> FindAddressesAsync(string? text, int max = 500, CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<long>>(nameof(ArchiveDb.FindAddresses), new object?[] { text, max }, ct);

    public Task<long?> CounterpartyOfAsync(string? address, CancellationToken ct = default) =>
        CallAsync<long?>(nameof(ArchiveDb.CounterpartyOf), new object?[] { address }, ct);
}
