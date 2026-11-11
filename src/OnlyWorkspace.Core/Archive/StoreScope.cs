using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OnlyWorkspace.Core.Archive;

public enum StoreScopeKind
{
    /// <summary>Every mailbox, including those opened later.</summary>
    All = 0,
    /// <summary>Only the listed mailboxes.</summary>
    Selected = 1,
    /// <summary>No mailbox: nothing is archived or returned.</summary>
    None = 2
}

/// <summary>
/// Which mailboxes the archive covers (#202), in the saved choice, in queries and on the pipe. The kind is explicit:
/// an empty list is never «all» in one place and «none» in another. Anything unknown or missing is read as none.
/// </summary>
public sealed class StoreScope
{
    public StoreScopeKind Kind { get; set; } = StoreScopeKind.All;
    public List<string> StoreIds { get; set; } = new();

    public static StoreScope All() => new() { Kind = StoreScopeKind.All };

    public static StoreScope None() => new() { Kind = StoreScopeKind.None };

    /// <summary>The listed mailboxes; an empty list selects nothing, so it is <see cref="None"/>.</summary>
    public static StoreScope Only(IEnumerable<string> storeIds)
    {
        var ids = storeIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        return ids.Count == 0 ? None() : new StoreScope { Kind = StoreScopeKind.Selected, StoreIds = ids };
    }

    public bool IsAll => Kind == StoreScopeKind.All;

    /// <summary>True when no mailbox can match: none, an unknown kind, or a selection without mailboxes.</summary>
    public bool IsNone => Kind switch
    {
        StoreScopeKind.All => false,
        StoreScopeKind.Selected => (StoreIds?.Count ?? 0) == 0,
        _ => true
    };

    public bool Allows(string? storeId) => Kind switch
    {
        StoreScopeKind.All => true,
        StoreScopeKind.Selected => storeId != null && (StoreIds ?? new List<string>()).Contains(storeId, StringComparer.OrdinalIgnoreCase),
        _ => false
    };

    /// <summary>Same wire/saved shape on every layer: {"kind":"all"|"selected"|"none","stores":[…]}.</summary>
    public string ToJson()
    {
        string kind = IsAll ? "all" : IsNone ? "none" : "selected";
        return kind == "selected"
            ? JsonSerializer.Serialize(new { kind, stores = StoreIds })
            : JsonSerializer.Serialize(new { kind });
    }

    /// <summary>
    /// Reads a saved choice. Nothing saved yet is all (the default of a new archive); a damaged value is none, so an
    /// archive never widens by mistake. The first format, a bare JSON list of StoreIDs, is still read: empty = all.
    /// </summary>
    public static StoreScope Parse(string? json)
    {
        try { return ParseStrict(json); }
        catch (JsonException) { return None(); }
    }

    /// <exception cref="JsonException">The value is neither a saved scope nor a list of StoreIDs.</exception>
    public static StoreScope ParseStrict(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return All();
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            var ids = Strings(root);
            return ids.Count == 0 ? All() : Only(ids);
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            throw new JsonException("Selezione delle cassette non valida.");
        switch (kindElement.GetString())
        {
            case "all": return All();
            case "none": return None();
            case "selected":
                if (!root.TryGetProperty("stores", out var stores) || stores.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Selezione delle cassette non valida.");
                var chosen = Strings(stores);
                if (chosen.Count == 0) throw new JsonException("Selezione delle cassette non valida.");
                return Only(chosen);
            default:
                throw new JsonException("Selezione delle cassette non valida.");
        }
    }

    private static List<string> Strings(JsonElement array)
    {
        if (array.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())))
            throw new JsonException("Selezione delle cassette non valida.");
        return array.EnumerateArray().Select(v => v.GetString()!).ToList();
    }

    public override string ToString() => ToJson();
}
