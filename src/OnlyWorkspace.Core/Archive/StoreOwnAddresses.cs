using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnlyWorkspace.Core.Archive;

/// <summary>The user's addresses in one store without an account (a PST): guessed from its Sent Items, corrected in the ConfigTool.</summary>
public sealed class StoreOwnEntry
{
    [JsonPropertyName("guessed")] public List<string> Guessed { get; set; } = new();
    [JsonPropertyName("added")] public List<string> Added { get; set; } = new();
    [JsonPropertyName("removed")] public List<string> Removed { get; set; } = new();
    /// <summary>Names the guessed addresses sent as.</summary>
    [JsonPropertyName("names")] public List<string> Names { get; set; } = new();

    /// <summary>Guess plus the user's additions, minus the user's removals.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Effective =>
        Guessed.Concat(Added).Select(CounterpartyResolver.Normalize).Where(a => a.Length > 0)
            .Except(Removed.Select(CounterpartyResolver.Normalize), StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>Sender of one Sent Items row: the SMTP address when the store has it, else the sender address.</summary>
public readonly struct SentRow
{
    public SentRow(string? smtp, string? address, string? name) { Smtp = smtp; Address = address; Name = name; }
    public string? Smtp { get; }
    public string? Address { get; }
    public string? Name { get; }
}

/// <summary>
/// Archive meta <see cref="MetaKey"/>: JSON {storeId: StoreOwnEntry}. The indexer writes the guess, the ConfigTool the user's
/// choices; the effective addresses are the user's own (is_me) like the accounts' ones.
/// </summary>
public static class StoreOwnAddresses
{
    public const string MetaKey = "store_own_addresses";
    /// <summary>Share of the sent rows an address needs to count as the user's (aliases pass, a delegate's few emails do not).</summary>
    public const double MinShare = 0.2;
    public const int SentRows = 200;

    public static Dictionary<string, StoreOwnEntry> Parse(string? json)
    {
        var result = new Dictionary<string, StoreOwnEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(json)) return result;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, StoreOwnEntry>>(json!);
            if (parsed != null)
                foreach (var pair in parsed)
                    if (pair.Key.Length > 0 && pair.Value != null) result[pair.Key] = pair.Value;
        }
        catch (JsonException) { } // damaged meta value: treated as not stored, the indexer recomputes it
        return result;
    }

    public static string Serialize(Dictionary<string, StoreOwnEntry> entries) => JsonSerializer.Serialize(entries);

    /// <summary>Effective addresses of every store.</summary>
    public static IReadOnlyList<string> All(string? json) =>
        Parse(json).Values.SelectMany(e => e.Effective).Distinct(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string> Names(string? json) =>
        Parse(json).Values.SelectMany(e => e.Names).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A new guess for the store; the user's additions and removals stay.</summary>
    public static string WithGuess(string? json, string storeId, StoreOwnEntry guess)
    {
        var entries = Parse(json);
        entries.TryGetValue(storeId, out var current);
        entries[storeId] = new StoreOwnEntry
        {
            Guessed = guess.Guessed,
            Names = guess.Names,
            Added = current?.Added ?? new List<string>(),
            Removed = current?.Removed ?? new List<string>()
        };
        return Serialize(entries);
    }

    /// <summary>The entries without the store's (removed from the archive); null when it had none.</summary>
    public static string? Without(string? json, string storeId)
    {
        var entries = Parse(json);
        return entries.Remove(storeId) ? Serialize(entries) : null;
    }

    /// <summary>
    /// The addresses the user says are theirs in the store, kept as differences from the guess. An address taken out is
    /// taken out of every store too, and one chosen is no longer removed elsewhere: the ConfigTool shows only open stores,
    /// so a closed or moved PST's entry would otherwise keep it the user's.
    /// </summary>
    public static string WithChoice(string? json, string storeId, IEnumerable<string> mine)
    {
        var entries = Parse(json);
        if (!entries.TryGetValue(storeId, out var entry)) entries[storeId] = entry = new StoreOwnEntry();
        var chosen = mine.Select(CounterpartyResolver.Normalize).Where(a => a.IndexOf('@') > 0).Distinct(StringComparer.Ordinal).ToList();
        // Every address the store knows and the user left out, also one already removed here by an earlier save.
        var dropped = entry.Guessed.Concat(entry.Added).Concat(entry.Removed).Select(CounterpartyResolver.Normalize)
            .Where(a => a.Length > 0).Except(chosen, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToList();
        var guessed = entry.Guessed.Select(CounterpartyResolver.Normalize).ToList();
        entry.Added = chosen.Except(guessed, StringComparer.Ordinal).ToList();
        entry.Removed = guessed.Except(chosen, StringComparer.Ordinal).ToList();
        foreach (var other in entries.Values)
        {
            if (ReferenceEquals(other, entry)) continue;
            var otherGuessed = other.Guessed.Select(CounterpartyResolver.Normalize).ToList();
            other.Added = other.Added.Select(CounterpartyResolver.Normalize).Except(dropped, StringComparer.Ordinal).ToList();
            other.Removed = other.Removed.Select(CounterpartyResolver.Normalize).Except(chosen, StringComparer.Ordinal)
                .Concat(dropped.Intersect(otherGuessed, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        }
        return Serialize(entries);
    }

    /// <summary>Senders of at least <see cref="MinShare"/> of the rows; Exchange DNs (no '@') are left out.</summary>
    public static StoreOwnEntry Guess(IEnumerable<SentRow> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        int total = 0;
        foreach (var row in rows)
        {
            var addr = CounterpartyResolver.Normalize(row.Smtp);
            if (addr.IndexOf('@') <= 0) addr = CounterpartyResolver.Normalize(row.Address);
            if (addr.IndexOf('@') <= 0) continue;
            total++;
            counts[addr] = counts.TryGetValue(addr, out int n) ? n + 1 : 1;
            var name = (row.Name ?? string.Empty).Trim();
            if (name.Length == 0 || name.IndexOf('@') >= 0) continue;
            if (!names.TryGetValue(addr, out var byName)) names[addr] = byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            byName[name] = byName.TryGetValue(name, out int m) ? m + 1 : 1;
        }
        var kept = counts.Where(c => c.Value >= MinShare * total).OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key).ToList();
        return new StoreOwnEntry
        {
            Guessed = kept,
            Names = kept.Where(names.ContainsKey).Select(a => names[a].OrderByDescending(p => p.Value).First().Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }
}
