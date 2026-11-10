using System.Text.Json;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Outlook.Services;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>Responsibilities taken out of TaskPaneCoordinator (ARC-01): each runs without Outlook or a pane.</summary>
public sealed class PaneCoordinatorSplitTests
{
    [Fact]
    public void Coordinator_DoesNotReadTheProcessWideIndexer()
    {
        string dir = Path.Combine(TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.Outlook", "UI");
        foreach (string file in Directory.GetFiles(dir, "TaskPaneCoordinator*.cs"))
            Assert.DoesNotContain("ArchiveIndexer.Current", File.ReadAllText(file));
    }

    [Fact]
    public void MailInputTracker_KeepsOneEntryPerMessage_AndResolvesTheMissingMessageIdOnce()
    {
        var added = new List<HistoryContextReference>();
        int lookups = 0;
        var tracker = new MailInputTracker((e, s) => { lookups++; return "id-" + e; }, () => "profilo", () => new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), added.Add);

        tracker.Track("", "s1");
        tracker.Track("e1", "s1");
        tracker.Track("e1", "s1");
        tracker.Track("e1", "s1", "msg-explicit");
        tracker.Track("e1", "s2");

        Assert.Equal(2, tracker.Sources.Count);
        Assert.Equal(2, added.Count);
        Assert.Equal(2, lookups);
        Assert.Equal("msg-explicit", tracker.Sources[0].MessageId);
        Assert.Equal("profilo", tracker.Sources[1].OutlookProfile);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), tracker.Sources[1].CapturedUtc);

        tracker.Clear();
        Assert.Empty(tracker.Sources);
    }

    [Fact]
    public async Task ArchivedOriginalCache_ReadsTheSameOriginalOnce_UntilCleared()
    {
        var cache = new ArchivedOriginalCache();
        int loads = 0;
        Task<ArchiveBody?>? Load() { loads++; return Task.FromResult<ArchiveBody?>(new ArchiveBody { MessageId = "m" + loads }); }

        var first = await cache.GetAsync("e", "s", Load);
        var again = await cache.GetAsync("e", "s", Load);
        Assert.Same(first, again);
        Assert.Equal(1, loads);

        await cache.GetAsync("e2", "s", Load);
        Assert.Equal(2, loads);

        cache.Clear();
        await cache.GetAsync("e2", "s", Load);
        Assert.Equal(3, loads);
    }

    [Fact]
    public async Task ArchivedOriginalCache_RemembersAnAbsentOriginal_WithoutAsking_Again()
    {
        var cache = new ArchivedOriginalCache();
        int loads = 0;
        Task<ArchiveBody?>? NoArchive() { loads++; return null; }

        Assert.Null(await cache.GetAsync("e", "s", NoArchive));
        Assert.Null(await cache.GetAsync("e", "s", NoArchive));
        Assert.Equal(1, loads);
    }

    [Fact]
    public void ArchiveLight_WithoutIndexer_IsOffAndUnavailable()
    {
        using var doc = Json(ArchiveStatusMessages.Light(null));
        var root = doc.RootElement;
        Assert.Equal("archive_light", root.GetProperty("type").GetString());
        Assert.Equal("non disponibile", root.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("phase").ValueKind);
    }

    [Fact]
    public void ArchiveStatus_ListsEveryStore_MarkingTheChosenOnes()
    {
        var status = new ArchiveStatus { Available = true, Active = true, Phase = "idle", StoreIds = new[] { "a" } };
        var stores = new[]
        {
            new OutlookStoreInfo { StoreId = "a", DisplayName = "Posta A", IsOpen = true },
            new OutlookStoreInfo { StoreId = "b", DisplayName = "Posta B", IsOpen = false },
            new OutlookStoreInfo { StoreId = "", DisplayName = "Senza id", IsOpen = true }
        };

        using var doc = Json(ArchiveStatusMessages.Status(status, Array.Empty<ArchiveStoreCoverage>(), stores));
        var root = doc.RootElement;

        Assert.Equal("archive_status", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal(2, root.GetProperty("stores").GetArrayLength());
        Assert.Contains("Posta A", root.GetProperty("stores").GetRawText());
        Assert.Contains("Posta B", root.GetProperty("stores").GetRawText());
        Assert.DoesNotContain("Senza id", root.GetProperty("stores").GetRawText());
    }

    [Fact]
    public void ArchiveStatus_WhenUnavailable_ReadsNoStores_AndPublishesNone()
    {
        var status = new ArchiveStatus { Available = false };
        var throwing = Enumerable.Range(0, 1).Select<int, OutlookStoreInfo>(_ => throw new InvalidOperationException("must not be listed"));

        using var doc = Json(ArchiveStatusMessages.Status(status, Array.Empty<ArchiveStoreCoverage>(), throwing));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("stores").ValueKind);
        Assert.Equal("non disponibile", doc.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public void ArchiveUnavailable_MessageIsOff()
    {
        using var doc = Json(ArchiveStatusMessages.Unavailable());
        Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("non disponibile", doc.RootElement.GetProperty("label").GetString());
    }

    private static JsonDocument Json(object message) => JsonDocument.Parse(JsonSerializer.Serialize(message));
}
