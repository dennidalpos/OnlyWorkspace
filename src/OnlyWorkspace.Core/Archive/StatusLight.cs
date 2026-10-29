using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace OnlyWorkspace.Core.Archive;

/// <summary>
/// Traffic light of the archive and of the model service: green verified, yellow only while work really runs,
/// red an error with its reason, grey (off, paused, waiting) when nothing runs. Never colour alone: each has its symbol.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StatusLight { Off, Ok, Working, Error, Paused, Waiting }

public static class StatusLights
{
    /// <summary>Indexer phases that mean work in progress (see ArchiveIndexer._phase).</summary>
    private static readonly string[] WorkingPhases = { "starting", "cleaning", "headers", "bodies", "cards" };

    /// <summary>Severity order for an aggregate: an error anywhere is the state, then work, then a queue that waits.</summary>
    private static readonly StatusLight[] Severity = { StatusLight.Error, StatusLight.Working, StatusLight.Waiting, StatusLight.Paused, StatusLight.Ok, StatusLight.Off };

    public static string Label(StatusLight light) => light switch
    {
        StatusLight.Ok => "sincronizzato",
        StatusLight.Working => "in lavorazione",
        StatusLight.Error => "errori",
        StatusLight.Paused => "in pausa",
        StatusLight.Waiting => "in attesa",
        _ => "spento"
    };

    /// <summary>The whole archive of the Outlook profile. Work still queued while the indexer waits is grey, not yellow.</summary>
    public static StatusLight Archive(bool available, bool active, bool paused, string? phase, string? error, int foldersFailed, int bodiesPending, int foldersPending)
    {
        if (!available || !active) return StatusLight.Off;
        if (!string.IsNullOrWhiteSpace(error) || phase is "error" or "limit" || foldersFailed > 0) return StatusLight.Error;
        if (paused) return StatusLight.Paused;
        if (Array.IndexOf(WorkingPhases, phase) >= 0) return StatusLight.Working;
        if (phase == "waiting" || bodiesPending > 0 || foldersPending > 0) return StatusLight.Waiting;
        return StatusLight.Ok;
    }

    /// <summary>Why the archive light is red, in words; null when it is not. The indexer's own error comes first.</summary>
    public static string? ArchiveReason(string? phase, string? error, int foldersFailed) =>
        !string.IsNullOrWhiteSpace(error) ? error
        : phase == "limit" ? "limite di spazio raggiunto"
        : phase == "error" ? "errore dell'archivio"
        : foldersFailed == 1 ? "1 cartella non leggibile"
        : foldersFailed > 1 ? $"{foldersFailed} cartelle non leggibili"
        : null;

    /// <summary>Several archives (tray): the most severe light.</summary>
    public static StatusLight Worst(IEnumerable<StatusLight> lights)
    {
        var all = lights.ToList();
        return Severity.FirstOrDefault(all.Contains);
    }
}
