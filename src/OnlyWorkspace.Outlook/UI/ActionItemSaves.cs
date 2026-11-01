using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OnlyWorkspace.Core.Agents;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>UI-thread ledger: the host owns the proposed values and remembers successful saves across page reloads.</summary>
internal sealed class ActionItemSaves
{
    private sealed class Entry
    {
        public ExtractedActionItem? Item;
        public bool Saved, Saving;
    }
    private readonly Dictionary<string, Dictionary<string, Entry>> _results = new();
    private readonly Queue<string> _order = new();

    public object Register(object items)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(items));
        string resultId = Guid.NewGuid().ToString("N");
        var entries = new Dictionary<string, Entry>();
        var output = new List<Dictionary<string, object?>>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var fields = item.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            if (item.TryGetProperty("selectable", out var selectable) && selectable.ValueKind == JsonValueKind.True)
            {
                string id = output.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                entries[id] = new Entry { Item = ActionItemParser.FromPane(item) };
                fields["saveResultId"] = resultId;
                fields["saveItemId"] = id;
            }
            output.Add(fields);
        }
        _results[resultId] = entries;
        _order.Enqueue(resultId);
        // Expired results are refused, never treated as new requests that could duplicate an Outlook item.
        while (_order.Count > 100) _results.Remove(_order.Dequeue());
        return output;
    }

    public object Save(JsonElement request, Action<ExtractedActionItem> save, Action<Exception> log)
    {
        string resultId = Read(request, "resultId"), requestId = Read(request, "requestId");
        var outcomes = new List<object>();
        if (request.TryGetProperty("itemIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ids.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Distinct())
            {
                bool saved = false;
                string? error = null;
                if (!_results.TryGetValue(resultId, out var result) || !result.TryGetValue(id, out var entry))
                    error = "Risultato non più disponibile: ripeti la ricerca prima di salvare.";
                else if (entry.Saved) saved = true;
                else if (Read(request, "type") == "get_action_item_states") { }
                else if (entry.Saving) error = "Salvataggio già in corso.";
                else if (entry.Item == null) error = "Voce non valida: manca un titolo utilizzabile.";
                else
                {
                    entry.Saving = true;
                    try { save(entry.Item); entry.Saved = saved = true; }
                    catch (Exception ex) { log(ex); error = "Voce non salvata in Outlook. Riprova o apri la bozza."; }
                    finally { entry.Saving = false; }
                }
                outcomes.Add(new { itemId = id, saved, error });
            }
        }
        return new { type = "action_items_saved", requestId, resultId, outcomes };
    }

    private static string Read(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
}
