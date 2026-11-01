using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

public partial class TaskPaneCoordinator
{
    // Isolated pane hosts supply plain snapshots and an isolated ledger, without constructing an Outlook application.
    private readonly Func<CancellationToken, Task<IReadOnlyList<PrivacyEntity>>>? _privacyEntitySource = null;
    private readonly IPseudonymizationAuditStore? _privacyAudit = null;

    private Task<IReadOnlyList<PrivacyEntity>> ReadPrivacyEntitiesOnUiAsync(SynchronizationContext ui, CancellationToken ct)
    {
        if (ReferenceEquals(SynchronizationContext.Current, ui)) return (_privacyEntitySource ?? ReadPrivacyEntitiesAsync)(ct);
        var completion = new TaskCompletionSource<IReadOnlyList<PrivacyEntity>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(async _ =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                completion.TrySetResult(await (_privacyEntitySource ?? ReadPrivacyEntitiesAsync)(ct));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, null);
        return completion.Task;
    }
    private async Task<IReadOnlyList<PrivacyEntity>> ReadPrivacyEntitiesAsync(CancellationToken ct)
    {
        UiSync.Ensure();
        var result = new List<PrivacyEntity>(await _dataService.ReadPrivacyEntitiesAsync(ct));
        var indexer = ArchiveIndexer.Current;
        if (_config?.Archive?.Enabled == true)
        {
            if (indexer == null) throw new DataEgressBlockedException("Dizionario locale dell'archivio non disponibile: nessun dato inviato.");
            var stores = await indexer.GetAllowedSearchStoresAsync();
            var db = await indexer.OpenForSearchAsync();
            if (db == null) throw new DataEgressBlockedException("Dizionario locale dell'archivio non disponibile: nessun dato inviato.");
            long cursor = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await db.GetPrivacyEntitiesAsync(cursor, stores, ct: ct);
                if (page.Count == 0) break;
                if (page.Any(e => e.Id <= cursor)) throw new DataEgressBlockedException("Dizionario locale incompleto: nessun dato inviato.");
                result.AddRange(page); cursor = page.Max(e => e.Id);
            }
        }
        return result;
    }
}
