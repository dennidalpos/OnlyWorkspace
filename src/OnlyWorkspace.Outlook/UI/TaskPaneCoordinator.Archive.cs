using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>Mail archive section of the pane: status, pause, deletion (choices live in the ConfigTool).</summary>
public partial class TaskPaneCoordinator
{
    /// <summary>
    /// The top bar's Archivio indicator, refreshed while the pane is visible: the indexer's own status only,
    /// without the mailbox list (Object Model) or the per-store coverage that the Stato page reads.
    /// </summary>
    private async Task SendArchiveLightAsync()
    {
        var indexer = CurrentArchive;
        try
        {
            var status = indexer == null ? null : await indexer.GetStatusAsync();
            if (_isDisposed) return;
            _control.PostMessage(ArchiveStatusMessages.Light(status));
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Archive light status failed");
            if (!_isDisposed) _control.PostMessage(new { type = "archive_light", light = StatusLight.Error, label = "errori", reason = "Stato archivio non disponibile. Riprova; se il problema continua, consulta il log." });
        }
    }

    private async Task SendArchiveStatusAsync()
    {
        var indexer = CurrentArchive;
        if (indexer == null)
        {
            _control.PostMessage(ArchiveStatusMessages.Unavailable());
            return;
        }

        try
        {
            var status = await indexer.GetStatusAsync();
            if (_isDisposed) return;
            var coverage = indexer.Db == null ? Array.Empty<ArchiveStoreCoverage>() : (await indexer.Db.GetStoreCoverageAsync()).ToArray();
            _control.PostMessage(ArchiveStatusMessages.Status(status, coverage, status.Available ? _dataService.EnumerateStores() : Array.Empty<OutlookStoreInfo>()));
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to read the archive status");
            if (!_isDisposed) _control.PostMessage(new { type = "archive_status_error", message = "Stato archivio non disponibile. Riprova; se il problema continua, consulta il log." });
        }
    }

    private async Task HandleArchiveCommandAsync(string type, JsonElement message)
    {
        var indexer = CurrentArchive;
        if (indexer == null) return;

        try
        {
            var notice = await PaneArchiveCommands.ApplyAsync(indexer, type, message);
            if (notice != null) Toast(notice, "info");
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Archive command {Command} failed", type);
            Toast("Operazione sull'archivio non riuscita. Riprova; se il problema continua, consulta il log.", "error");
        }
        await SendArchiveStatusAsync();
    }

    private async Task GuardArchiveUiAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Archive pane operation failed");
            if (!_isDisposed) Toast("Archivio non disponibile. Riprova; se il problema continua, consulta il log.", "error");
        }
    }

    private async Task OpenArchivedEmailAsync(JsonElement message)
    {
        try
        {
            var entryId = GetString(message, "entryId");
            if (string.IsNullOrEmpty(entryId) || !_dataService.DisplayEmail(entryId!, GetString(message, "storeId")))
            {
                if (CurrentArchive is { } missingIndexer) await missingIndexer.ReportMissingAsync();
                Toast("L'email non è più dove l'archivio l'aveva trovata (spostata o eliminata): l'archivio la cerca di nuovo nei prossimi momenti liberi.", "error");
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Opening an archived email failed");
            Toast("Apertura dell'email non riuscita. Verifica che Outlook sia disponibile e riprova.", "error");
        }
    }
}
