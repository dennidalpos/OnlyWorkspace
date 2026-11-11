using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>The archive indicator and Stato page messages the pane posts, built from an indexer status; COM-free.</summary>
internal static class ArchiveStatusMessages
{
    public static StatusLight LightOf(ArchiveStatus status) =>
        StatusLights.Archive(status.Available, status.Active, status.Paused, status.Phase, status.Error,
            status.Stats.FoldersFailed, status.Stats.BodiesPending, Math.Max(0, status.Stats.Folders - status.Stats.FoldersDone));

    /// <summary>The phase alone: a failed folder makes the archive red, not the idle wait it is in.</summary>
    public static StatusLight PhaseLightOf(ArchiveStatus status) =>
        StatusLights.Archive(status.Available, status.Active, status.Paused, status.Phase, null,
            0, status.Stats.BodiesPending, Math.Max(0, status.Stats.Folders - status.Stats.FoldersDone));

    public static string? ReasonOf(ArchiveStatus status) => StatusLights.ArchiveReason(status.Phase, status.Error, status.Stats.FoldersFailed);

    public static object Light(ArchiveStatus? status)
    {
        var light = status == null ? StatusLight.Off : LightOf(status);
        return new
        {
            type = "archive_light",
            light,
            label = status?.Available == true ? StatusLights.Label(light) : "non disponibile",
            reason = status == null ? null : ReasonOf(status),
            phase = status == null ? null : ArchiveMetrics.PhaseLabel(status.Phase)
        };
    }

    public static object Unavailable() =>
        new { type = "archive_status", available = false, light = StatusLight.Off, label = "non disponibile" };

    /// <param name="stores">Every mailbox Outlook lists, closed ones too, so the user sees why they are not indexed.</param>
    public static object Status(ArchiveStatus status, IReadOnlyCollection<ArchiveStoreCoverage> coverage, IEnumerable<OutlookStoreInfo> stores)
    {
        var light = LightOf(status);
        var chosen = status.Stores;
        var byStore = coverage.ToDictionary(s => s.StoreId, StringComparer.OrdinalIgnoreCase);
        return new
        {
            type = "archive_status",
            light,
            label = status.Available ? StatusLights.Label(light) : "non disponibile",
            available = status.Available,
            active = status.Active,
            paused = status.Paused,
            indexingNow = status.IndexingNow,
            phase = ArchiveMetrics.PhaseLabel(status.Phase),
            phaseLight = PhaseLightOf(status),
            error = status.Error,
            reason = ReasonOf(status),
            metrics = ArchiveMetrics.Build(status.Stats),
            stores = status.Available
                ? stores.Where(s => s.StoreId.Length > 0)
                    .Select(s =>
                    {
                        bool selected = chosen.Allows(s.StoreId);
                        var c = byStore.TryGetValue(s.StoreId, out var found) ? found : null;
                        return ArchiveMetrics.Store(s.DisplayName, s.IsOpen, selected, c);
                    }).ToList()
                : null
        };
    }
}
