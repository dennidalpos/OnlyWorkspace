using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OnlyWorkspace.Core.Agents;

public sealed class TriageEntry
{
    /// <summary>0-based index into the email list sent to the model.</summary>
    public int Index { get; set; }
    /// <summary>alta, media or bassa.</summary>
    public string Priority { get; set; } = "media";
    /// <summary>Existing Outlook category, empty when none fits.</summary>
    public string Category { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    public int PriorityRank => Priority switch { "alta" => 0, "media" => 1, _ => 2 };
}

public sealed class BriefingResult
{
    public string Summary { get; set; } = string.Empty;
    public List<TriageEntry> Emails { get; } = new();
}

/// <summary>Validates the briefing JSON: indexes in range, known priorities, only categories that already exist.</summary>
public static class BriefingParser
{
    public static BriefingResult Parse(string json, int emailCount, IReadOnlyCollection<string> categories)
    {
        var result = new BriefingResult();
        using var doc = ModelOutput.ParseObject(json, "emails");
        var root = doc.RootElement;
        result.Summary = ModelOutput.ReadTrimmedString(root, "summary");

        var seen = new HashSet<int>();
        foreach (var e in root.GetProperty("emails").EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !TryGetId(e, out int id)) continue;
            int index = id - 1; // the prompt numbers emails from 1
            if (index < 0 || index >= emailCount || !seen.Add(index)) continue;

            var priority = ModelOutput.ReadTrimmedString(e, "priority").ToLowerInvariant();
            var category = ModelOutput.ReadTrimmedString(e, "category");
            result.Emails.Add(new TriageEntry
            {
                Index = index,
                Priority = priority is "alta" or "media" or "bassa" ? priority : "media",
                Category = categories.FirstOrDefault(c => c.Equals(category, StringComparison.OrdinalIgnoreCase)) ?? string.Empty,
                Reason = ModelOutput.ReadTrimmedString(e, "reason")
            });
        }
        return result;
    }

    private static bool TryGetId(JsonElement e, out int id)
    {
        id = 0;
        if (!e.TryGetProperty("id", out var v)) return false;
        return v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out id) : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out id);
    }

}
