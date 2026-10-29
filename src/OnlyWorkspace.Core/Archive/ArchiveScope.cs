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
    /// <summary>JSON list of StoreIDs; empty = every open mailbox, including those opened later.</summary>
    public const string StoresKey = "stores";
    public const string PausedKey = "paused";
    /// <summary>Mailboxes Outlook had open at the last pass (ArchiveOpenStore list), for the ConfigTool.</summary>
    public const string OpenStoresKey = "open_stores";
    /// <summary>preferences.json flag: this Windows user does not want the archive.</summary>
    public const string UserOffFlag = "archiveOff";

    /// <summary>Translate the user's saved choice to the DB contract: null=all, empty=deny-all.</summary>
    public static List<string>? ForQuery(IReadOnlyList<string> selected, bool authorized) =>
        !authorized ? new List<string>() : selected.Count == 0 ? null : new List<string>(selected);

    public static IReadOnlyList<string> ParseStores(string? json)
    {
        try { return string.IsNullOrEmpty(json) ? Array.Empty<string>() : JsonSerializer.Deserialize<List<string>>(json!) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

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
