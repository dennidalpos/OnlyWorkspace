using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>Mail archive section of the pane: status, pause, deletion (choices live in the ConfigTool).</summary>
public partial class TaskPaneCoordinator
{
    private static StatusLight LightOf(ArchiveStatus status) =>
        StatusLights.Archive(status.Available, status.Active, status.Paused, status.Phase, status.Error,
            status.Stats.FoldersFailed, status.Stats.BodiesPending, Math.Max(0, status.Stats.Folders - status.Stats.FoldersDone));

    /// <summary>The phase alone: a failed folder makes the archive red, not the idle wait it is in.</summary>
    private static StatusLight PhaseLightOf(ArchiveStatus status) =>
        StatusLights.Archive(status.Available, status.Active, status.Paused, status.Phase, null,
            0, status.Stats.BodiesPending, Math.Max(0, status.Stats.Folders - status.Stats.FoldersDone));

    private static string? ReasonOf(ArchiveStatus status) => StatusLights.ArchiveReason(status.Phase, status.Error, status.Stats.FoldersFailed);

    /// <summary>
    /// The top bar's Archivio indicator, refreshed while the pane is visible: the indexer's own status only,
    /// without the mailbox list (Object Model) or the per-store coverage that the Stato page reads.
    /// </summary>
    private async Task SendArchiveLightAsync()
    {
        var indexer = ArchiveIndexer.Current;
        try
        {
            var status = indexer == null ? null : await indexer.GetStatusAsync();
            if (_isDisposed) return;
            var light = status == null ? StatusLight.Off : LightOf(status);
            _control.PostMessage(new
            {
                type = "archive_light",
                light,
                label = status?.Available == true ? StatusLights.Label(light) : "non disponibile",
                reason = status == null ? null : ReasonOf(status),
                phase = status == null ? null : ArchiveMetrics.PhaseLabel(status.Phase)
            });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning("Archive light status failed: {Kind}", ex.GetType().Name);
            if (!_isDisposed) _control.PostMessage(new { type = "archive_light", light = StatusLight.Error, label = "errori", reason = "Stato archivio non disponibile: " + ex.Message });
        }
    }

    private async Task SendArchiveStatusAsync()
    {
        var indexer = ArchiveIndexer.Current;
        if (indexer == null)
        {
            _control.PostMessage(new { type = "archive_status", available = false, light = StatusLight.Off, label = "non disponibile" });
            return;
        }

        try
        {
            var status = await indexer.GetStatusAsync();
            if (_isDisposed) return;
            var light = LightOf(status);
            var chosen = new HashSet<string>(status.StoreIds, StringComparer.OrdinalIgnoreCase);
            var coverage = indexer.Db == null ? Array.Empty<ArchiveStoreCoverage>() : (await indexer.Db.GetStoreCoverageAsync()).ToArray();
            var byStore = coverage.ToDictionary(s => s.StoreId, StringComparer.OrdinalIgnoreCase);
            _control.PostMessage(new
            {
                type = "archive_status",
                light,
                label = status.Available ? StatusLights.Label(light) : "non disponibile",
                available = status.Available,
                active = status.Active,
                paused = status.Paused,
                phase = ArchiveMetrics.PhaseLabel(status.Phase),
                phaseLight = PhaseLightOf(status),
                error = status.Error,
                reason = ReasonOf(status),
                metrics = ArchiveMetrics.Build(status.Stats),
                stores = status.Available
                    // Closed mailboxes are listed too, so the user sees why they are not indexed.
                    ? _dataService.EnumerateStores().Where(s => s.StoreId.Length > 0)
                        .Select(s =>
                        {
                            bool selected = chosen.Count == 0 || chosen.Contains(s.StoreId);
                            var c = byStore.TryGetValue(s.StoreId, out var found) ? found : null;
                            return ArchiveMetrics.Store(s.DisplayName, s.IsOpen, selected, c);
                        }).ToList()
                    : null
            });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to read the archive status");
            if (!_isDisposed) _control.PostMessage(new { type = "archive_status_error", message = "Stato archivio non disponibile: " + ex.Message });
        }
    }

    private async Task HandleArchiveCommandAsync(string type, JsonElement message)
    {
        var indexer = ArchiveIndexer.Current;
        if (indexer == null) return;

        try
        {
            var notice = await PaneArchiveCommands.ApplyAsync(indexer, type, message);
            if (notice != null) Toast(notice, "info");
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Archive command {Command} failed", type);
            Toast("Operazione sull'archivio non riuscita: " + ex.Message, "error");
        }
        await SendArchiveStatusAsync();
    }

    // --- counterparty page ---------------------------------------------------------------------------------

    private const int MaxPartyResults = 30;

    private static string Day(DateTime? utc) => utc.HasValue ? utc.Value.ToLocalTime().ToString("dd/MM/yyyy") : string.Empty;

    /// <summary>Ribbon: the list of counterparties, or where to activate the archive when it is not.</summary>
    public void ShowCounterparties(bool toggle = false) => _ = GuardArchiveUiAsync(() => ShowCounterpartiesAsync(toggle));

    private async Task GuardArchiveUiAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Archive pane operation failed");
            if (!_isDisposed) Toast("Archivio non disponibile: " + ex.Message, "error");
        }
    }

    private async Task ShowCounterpartiesAsync(bool toggle)
    {
        if (ArchiveIndexer.Current == null || await ArchiveIndexer.Current.OpenForSearchAsync() == null)
        {
            Toast("L'archivio delle email non è attivo: si attiva da Stato → Archivio delle email.", "info", "open_archive");
            return;
        }
        _control.PostMessage(new { type = "toggle_party", toggle });
    }

    /// <summary>Right-click on an email: the page of its sender, or of its recipient when the user sent it.</summary>
    public void ShowCounterpartyOf(string? entryId, string? storeId) => _ = GuardArchiveUiAsync(() => ShowCounterpartyOfAsync(entryId, storeId));

    private async Task ShowCounterpartyOfAsync(string? entryId, string? storeId)
    {
        var db = ArchiveIndexer.Current is { } archiveIndexer ? await archiveIndexer.OpenForSearchAsync() : null;
        if (_isDisposed) return;
        if (db == null)
        {
            Toast("L'archivio delle email non è attivo: si attiva da Stato → Archivio delle email.", "info", "open_archive");
            return;
        }
        if (string.IsNullOrEmpty(entryId))
        {
            _control.PostMessage(new { type = "toggle_party" });
            return;
        }
        try
        {
            var email = _dataService.GetEmailByEntryId(entryId!, storeId);
            long? id = email == null ? null : await db.CounterpartyOfEmailAsync(email.EntryId, email.StoreId) ?? await db.CounterpartyOfAsync(email.SenderEmail);
            if (id == null)
            {
                // Not indexed yet, or an email among the user's own addresses: let the user look it up.
                _control.PostMessage(new { type = "toggle_party" });
                return;
            }
            await SendCounterpartyAsync(db, id.Value, open: true);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to open the counterparty page");
            Toast("Scheda controparte non disponibile: " + ex.Message, "error");
        }
    }

    private async Task HandleCounterpartyCommandAsync(string type, JsonElement message)
    {
        try
        {
            if (type == "archive_open_email")
            {
                var entryId = GetString(message, "entryId");
                if (string.IsNullOrEmpty(entryId) || !_dataService.DisplayEmail(entryId!, GetString(message, "storeId")))
                {
                    if (ArchiveIndexer.Current is { } missingIndexer) await missingIndexer.ReportMissingAsync();
                    Toast("L'email non è più dove l'archivio l'aveva trovata (spostata o eliminata): l'archivio la cerca di nuovo nei prossimi momenti liberi.", "error");
                }
                return;
            }

            var db = ArchiveIndexer.Current is { } archiveIndexer ? await archiveIndexer.OpenForSearchAsync() : null;
            if (db == null)
            {
                _control.PostMessage(new { type = "archive_parties", inactive = true });
                return;
            }
            long id = message.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var parsed) ? parsed : 0;
            switch (type)
            {
                case "archive_party_search":
                    _control.PostMessage(new
                    {
                        type = "archive_parties",
                        items = (await db.FindCounterpartiesAsync(GetString(message, "text"), MaxPartyResults))
                            .Select(c => new { id = c.Id, name = c.Name, key = c.Key, important = c.Important, emails = c.Emails, last = Day(c.LastUtc) }).ToList()
                    });
                    break;

                case "archive_party_important":
                    await db.SetImportantAsync(id, message.TryGetProperty("important", out var flag) && flag.ValueKind == JsonValueKind.True);
                    await SendCounterpartyAsync(db, id, open: false);
                    break;

                case "archive_party_get":
                    await SendCounterpartyAsync(db, id, open: false);
                    break;

                // The user's own grouping: remembered in the archive, applied to the addresses met later too.
                case "archive_party_merge":
                    long other = message.TryGetProperty("other", out var otherElement) && otherElement.TryGetInt64(out var otherId) ? otherId : 0;
                    if (await db.MergeCounterpartiesAsync(id, other)) Toast("Controparti unite. «Ripristina il raggruppamento automatico» nella scheda le separa di nuovo.", "info");
                    await SendCounterpartyAsync(db, id, open: false);
                    break;

                case "archive_party_split":
                    await SendCounterpartyAsync(db, await db.SplitAddressAsync(id, GetString(message, "address")) ?? id, open: false);
                    break;

                case "archive_party_reset":
                    await SendCounterpartyAsync(db, await db.ResetCounterpartyAsync(id), open: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Counterparty command {Command} failed", type);
            Toast("Operazione sulla scheda controparte non riuscita: " + ex.Message, "error");
        }
    }

    private async Task SendCounterpartyAsync(ArchiveClient db, long id, bool open)
    {
        var p = await db.GetCounterpartyAsync(id);
        object Item(CounterpartyItem i) => new { title = i.Title, date = Day(i.DateUtc), emails = i.Emails, entryId = i.EntryId, storeId = i.StoreId };
        _control.PostMessage(new
        {
            type = "archive_party",
            open,
            party = p == null ? null : new
            {
                id = p.Id,
                name = p.Name,
                key = p.Key,
                important = p.Important,
                custom = p.Custom,
                emails = p.Emails,
                fromThem = p.FromThem,
                first = Day(p.FirstUtc),
                last = Day(p.LastUtc),
                addresses = p.Addresses.Select(a => new { address = a.Address, name = a.Name, emails = a.Emails }).ToList(),
                years = p.Years.Select(y => new { year = y.Key, count = y.Value }).ToList(),
                threads = p.Threads.Select(Item).ToList(),
                documents = p.Documents.Select(Item).ToList()
            }
        });
    }
}
