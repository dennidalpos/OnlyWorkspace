using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OnlyWorkspace.Core.Archive;

/// <summary>
/// Per-user archive choices kept in the archive's meta table, written by the ConfigTool and applied by the
/// Outlook indexer: which mailboxes are archived.
/// </summary>
public static class ArchiveScope
{
    /// <summary>Saved <see cref="StoreScope"/> as JSON: all (also mailboxes opened later), selected StoreIDs or none.</summary>
    public const string StoresKey = "stores";
    public const string PausedKey = "paused";
    /// <summary>Mailboxes Outlook had open at the last pass (ArchiveOpenStore list), for the ConfigTool.</summary>
    public const string OpenStoresKey = "open_stores";
    /// <summary>preferences.json flag: this Windows user does not want the archive.</summary>
    public const string UserOffFlag = "archiveOff";

    /// <summary>The scope a query may use: the saved choice when the archive is authorized, otherwise none.</summary>
    public static StoreScope ForQuery(StoreScope saved, bool authorized) =>
        !authorized ? StoreScope.None() : new StoreScope { Kind = saved.Kind, StoreIds = new List<string>(saved.StoreIds ?? new List<string>()) };

    public static StoreScope ParseStores(string? json) => StoreScope.Parse(json);

    public static IReadOnlyList<ArchiveOpenStore> ParseOpenStores(string? json)
    {
        try { return string.IsNullOrEmpty(json) ? Array.Empty<ArchiveOpenStore>() : JsonSerializer.Deserialize<List<ArchiveOpenStore>>(json!) ?? new List<ArchiveOpenStore>(); }
        catch (JsonException) { return Array.Empty<ArchiveOpenStore>(); }
    }
}

public sealed class ArchiveOpenStore
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Open { get; set; }
    /// <summary>No Outlook account delivers to it (a PST added by hand): its own addresses are guessed.</summary>
    public bool HasAccount { get; set; }
    public bool IsDataFileStore { get; set; }
}
