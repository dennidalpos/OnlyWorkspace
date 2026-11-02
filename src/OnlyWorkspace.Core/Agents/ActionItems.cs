using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace OnlyWorkspace.Core.Agents;

public sealed class ExtractedActionItem
{
    /// <summary>"task" or "appointment".</summary>
    public string Kind { get; set; } = "task";
    public string Title { get; set; } = string.Empty;
    public DateTime? Due { get; set; }
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public bool AllDay { get; set; }
    public string Location { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    /// <summary>Email [Mn] of a scan pack the item comes from (0 = not stated).</summary>
    public int Source { get; set; }
    /// <summary>The date as the email writes it («venerdì alle 10», «entro il 30/11/2026»).</summary>
    public string DateText { get; set; } = string.Empty;
    /// <summary>What the code could not find in the cited email; empty when verified.</summary>
    public List<string> Unverified { get; } = new();

    public bool IsAppointment => Kind == "appointment";

    /// <summary>Short Italian description of the date/time, for the pane.</summary>
    public string WhenLabel
    {
        get
        {
            var it = CultureInfo.GetCultureInfo("it-IT");
            if (IsAppointment && Start.HasValue)
            {
                var text = AllDay
                    ? Start.Value.ToString("ddd d MMM yyyy", it) + " (tutto il giorno)"
                    : Start.Value.ToString("ddd d MMM yyyy, HH:mm", it) + (End.HasValue ? "–" + End.Value.ToString("HH:mm", it) : "");
                return Location.Length > 0 ? text + " · " + Location : text;
            }
            return Due.HasValue ? "Scadenza " + Due.Value.ToString("ddd d MMM yyyy", it) : "Senza scadenza";
        }
    }
}

/// <summary>Validates the model's JSON: drops malformed entries instead of failing the whole extraction.</summary>
public static class ActionItemParser
{

    private static readonly string[] DateTimeFormats =
    {
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"
    };

    private static readonly System.Text.RegularExpressions.Regex LocalDateTime =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static ExtractedActionItems Parse(string json)
    {
        var result = new ExtractedActionItems();
        using var doc = ModelOutput.ParseObject(json, "items");
        var items = doc.RootElement.GetProperty("items");

        foreach (var e in items.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) { result.Diagnostics.Discard("attività non oggetto"); continue; }

            var title = ModelOutput.ReadTrimmedString(e, "title");
            if (title.Length == 0) { result.Diagnostics.Discard("attività senza titolo valido"); continue; }

            var item = new ExtractedActionItem
            {
                Kind = ModelOutput.ReadTrimmedString(e, "type").Equals("appointment", StringComparison.OrdinalIgnoreCase) ? "appointment" : "task",
                Title = title,
                Location = ModelOutput.ReadTrimmedString(e, "location"),
                Notes = ModelOutput.ReadTrimmedString(e, "notes"),
                DateText = ModelOutput.ReadTrimmedString(e, "date_text")
            };
            if (e.TryGetProperty("source", out var source))
            {
                var digits = source.ValueKind == JsonValueKind.Number ? source.GetRawText()
                    : source.ValueKind == JsonValueKind.String ? System.Text.RegularExpressions.Regex.Replace(source.GetString() ?? string.Empty, @"[^\d]", string.Empty) : string.Empty;
                if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) item.Source = n;
            }

            if (item.IsAppointment)
            {
                var start = ParseDate(ModelOutput.ReadTrimmedString(e, "start"), out bool dateOnly);
                if (start == null)
                {
                    // No usable start: keep it as a task so the information is not lost.
                    item.Kind = "task";
                    item.Due = ParseDate(ModelOutput.ReadTrimmedString(e, "due"), out _);
                }
                else
                {
                    item.Start = start;
                    item.AllDay = dateOnly;
                    var end = ParseDate(ModelOutput.ReadTrimmedString(e, "end"), out _);
                    item.End = end.HasValue && end > start ? end : dateOnly ? start.Value.AddDays(1) : start.Value.AddHours(1);
                }
            }
            else
            {
                item.Due = ParseDate(ModelOutput.ReadTrimmedString(e, "due"), out _);
            }

            result.Add(item);
        }
        return result;
    }

    /// <summary>An item coming back from the pane: dates re-validated with the same rules before it touches Outlook; null without a title.</summary>
    public static ExtractedActionItem? FromPane(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var item = new ExtractedActionItem
        {
            Kind = Raw(e, "kind") == "appointment" ? "appointment" : "task",
            Title = Raw(e, "title"),
            Due = ParseDate(Raw(e, "due"), out _),
            Start = ParseDate(Raw(e, "start"), out _),
            End = ParseDate(Raw(e, "end"), out _),
            AllDay = e.TryGetProperty("allDay", out var ad) && ad.ValueKind == JsonValueKind.True,
            Location = Raw(e, "location"),
            Notes = Raw(e, "notes")
        };
        return item.Title.Length == 0 ? null : item;
    }

    private static string Raw(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    public static DateTime? ParseDate(string value, out bool dateOnly)
    {
        dateOnly = false;
        if (value.Length == 0) return null;
        // Emails state local times: whatever the model appends (fraction, "Z", offset) is dropped, not converted.
        var local = LocalDateTime.Match(value);
        if (local.Success) value = local.Value;
        if (!DateTime.TryParseExact(value, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) return null;
        dateOnly = value.Length == 10;
        return parsed;
    }

}
