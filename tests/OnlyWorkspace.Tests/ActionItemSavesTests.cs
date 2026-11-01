using System.Text.Json;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

public sealed class ActionItemSavesTests
{
    private static JsonElement Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    [Fact]
    public void PartialFailureRetriesOnlyFailedItemsAndReloadQueriesNeverSave()
    {
        var ledger = new ActionItemSaves();
        var items = Json(ledger.Register(new[] { new { selectable = true, title = "Prima", kind = "task" }, new { selectable = true, title = "Seconda", kind = "task" } }));
        string resultId = items[0].GetProperty("saveResultId").GetString()!;
        var request = Json(new { type = "save_action_items", requestId = "r1", resultId, itemIds = new[] { "0", "0", "1" } });
        var saved = new List<string>();
        bool fail = true;
        void Save(OnlyWorkspace.Core.Agents.ExtractedActionItem item) { if (item.Title == "Seconda" && fail) throw new IOException("synthetic"); saved.Add(item.Title); }
        var response = Json(ledger.Save(request, Save, _ => { }));
        Assert.True(response.GetProperty("outcomes")[0].GetProperty("saved").GetBoolean());
        Assert.False(response.GetProperty("outcomes")[1].GetProperty("saved").GetBoolean());
        Assert.DoesNotContain("synthetic", response.ToString());
        var query = Json(new { type = "get_action_item_states", requestId = "reload", resultId, itemIds = new[] { "0", "1" } });
        ledger.Save(query, _ => throw new Exception("query must not save"), _ => { });
        Assert.Single(saved);
        fail = false;
        ledger.Save(request, Save, _ => { });
        ledger.Save(request, Save, _ => { });
        Assert.Equal(new[] { "Prima", "Seconda" }, saved);
    }

    [Fact]
    public void InvalidExpiredAndUnknownItemsCannotWriteOutlook()
    {
        var ledger = new ActionItemSaves();
        var items = Json(ledger.Register(new[] { new { selectable = true, title = "" } }));
        string resultId = items[0].GetProperty("saveResultId").GetString()!;
        object Request(string id) => new { type = "save_action_items", requestId = "r", resultId, itemIds = new[] { id } };
        var invalid = Json(ledger.Save(Json(Request("0")), _ => throw new Exception(), _ => { }));
        Assert.Contains("non valida", invalid.ToString());
        for (int i = 0; i < 100; i++) ledger.Register(new[] { new { selectable = true, title = "Nuova" } });
        var expired = Json(ledger.Save(Json(Request("0")), _ => throw new Exception(), _ => { }));
        Assert.Contains("non più disponibile", expired.GetProperty("outcomes")[0].GetProperty("error").GetString());
        Assert.Contains("non più disponibile", Json(ledger.Save(Json(Request("missing")), _ => throw new Exception(), _ => { })).GetProperty("outcomes")[0].GetProperty("error").GetString());
    }
}
