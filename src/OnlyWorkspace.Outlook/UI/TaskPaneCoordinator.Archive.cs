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

    private async Task GuardArchiveUiAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Archive pane operation failed");
            if (!_isDisposed) Toast("Archivio non disponibile: " + ex.Message, "error");
        }
    }

    private async Task OpenArchivedEmailAsync(JsonElement message)
    {
        try
        {
            var entryId = GetString(message, "entryId");
            if (string.IsNullOrEmpty(entryId) || !_dataService.DisplayEmail(entryId!, GetString(message, "storeId")))
            {
                if (ArchiveIndexer.Current is { } missingIndexer) await missingIndexer.ReportMissingAsync();
                Toast("L'email non è più dove l'archivio l'aveva trovata (spostata o eliminata): l'archivio la cerca di nuovo nei prossimi momenti liberi.", "error");
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Opening an archived email failed");
            Toast("Apertura dell'email non riuscita: " + ex.Message, "error");
        }
    }
}