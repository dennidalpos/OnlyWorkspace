using System.Text.Json;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the history drawer logic outside TaskPaneCoordinator, driven through a fake host without Outlook.</summary>
public sealed class HistoryPanePresenterTests
{
    private sealed class Storage : IHistoryStorageService, IHistorySourceInvalidation
    {
        public Dictionary<string, ConversationHistory> Items = new();
        public bool FailDelete;
        public bool Valid = true;
        public Task SaveConversationAsync(ConversationHistory c) { Items[c.Id] = c; return Task.CompletedTask; }
        public Task<ConversationHistory?> LoadConversationAsync(string id) => Task.FromResult(Items.TryGetValue(id, out var c) ? c : null);
        public Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync() => Task.FromResult<IReadOnlyList<ConversationSummary>>(Items.Values.Select(c => c.ToSummary()).ToList());
        public Task<bool> DeleteConversationAsync(string id) => Task.FromResult(!FailDelete && Items.Remove(id));
        public Task DeleteAllConversationsAsync() { if (FailDelete) throw new IOException("locked"); Items.Clear(); return Task.CompletedTask; }
        public Task<int> CleanupExpiredConversationsAsync(int retentionDays, int maxConversations) => Task.FromResult(0);
        public Task<bool> IsValidAsync(ConversationHistory conversation) => Task.FromResult(Valid);
    }

    private sealed class Host : IHistoryPaneHost
    {
        public IHistoryStorageService Storage { get; set; } = null!;
        public List<string> Posted = new();
        public List<string> Toasts = new();
        public bool HistoryEnabled { get; set; } = true;
        public ConversationHistory Conversation { get; set; } = new();
        public LlmProfileConfig? ActiveProfile { get; set; }
        public List<LlmProfileConfig> Profiles = new();
        public int Cancelled, Forgot, Resets, NewChats;
        public bool LastResetKeptPage;
        public LlmProfileConfig? FindProfile(string profileId) => Profiles.FirstOrDefault(p => p.Id == profileId);
        public void Post(object message) => Posted.Add(JsonSerializer.Serialize(message));
        public void CancelActiveTask() => Cancelled++;
        public void ForgetAnalysisEvidence() => Forgot++;
        public void ResetConversation(bool preservePage) { Resets++; LastResetKeptPage = preservePage; }
        public void StartNewChat() => NewChats++;
        public void Toast(string message, string kind) => Toasts.Add(kind + ":" + message);
        public string Type(int i) => JsonDocument.Parse(Posted[i]).RootElement.GetProperty("type").GetString()!;
    }

    private static HistoryPanePresenter Presenter(IHistoryStorageService storage, Host host)
    {
        host.Storage = storage;
        return new HistoryPanePresenter(host);
    }

    private static ConversationHistory Conversation(string id, string title)
    {
        var c = new ConversationHistory { Id = id, Title = title, ProfileId = "p1" };
        c.Messages.Add(new HistoryChatMessage { Role = "user", Content = "ciao", Timestamp = DateTimeOffset.UtcNow });
        return c;
    }

    [Fact]
    public async Task List_PostsTheConversationsAndWhetherHistoryIsEnabled()
    {
        var storage = new Storage();
        storage.Items["a"] = Conversation("a", "Prima");
        var host = new Host { HistoryEnabled = false };
        await Presenter(storage, host).SendListAsync("r1");

        Assert.Equal("history_list", host.Type(0));
        using var doc = JsonDocument.Parse(host.Posted[0]);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("r1", doc.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("a", doc.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Load_ReplacesTheConversation_StopsTheRunningWork_AndForgetsTheEvidence()
    {
        var storage = new Storage();
        storage.Items["a"] = Conversation("a", "Prima");
        var host = new Host();
        await Presenter(storage, host).LoadAsync("a");

        Assert.Equal("a", host.Conversation.Id);
        Assert.Equal(1, host.Cancelled);
        Assert.Equal(1, host.Forgot);
        Assert.Equal("history_loaded", host.Type(0));
    }

    [Fact]
    public async Task Load_OfAnUnknownId_PostsNothing()
    {
        var host = new Host();
        await Presenter(new Storage(), host).LoadAsync("missing");
        Assert.Empty(host.Posted);
        Assert.Equal(0, host.Cancelled);
    }

    [Fact]
    public async Task Delete_OfTheOpenConversation_StartsANewOneAndRefreshesTheList()
    {
        var storage = new Storage();
        storage.Items["a"] = Conversation("a", "Prima");
        var host = new Host { Conversation = storage.Items["a"] };
        await Presenter(storage, host).DeleteAsync("a", "r2");

        Assert.Empty(storage.Items);
        Assert.Equal(1, host.Resets);
        Assert.True(host.LastResetKeptPage);
        Assert.Equal("history_list", host.Type(0));
    }

    [Fact]
    public async Task Delete_ThatFails_TellsThePageAndTheUser()
    {
        var storage = new Storage { FailDelete = true };
        storage.Items["a"] = Conversation("a", "Prima");
        var host = new Host();
        await Presenter(storage, host).DeleteAsync("a", "r3");

        Assert.Equal("history_delete_failed", host.Type(0));
        Assert.Equal(new[] { "error:Eliminazione non riuscita." }, host.Toasts);
        Assert.Equal(0, host.Resets);
    }

    [Fact]
    public async Task DeleteAll_ThatFails_KeepsTheConversationAndReports()
    {
        var storage = new Storage { FailDelete = true };
        var host = new Host();
        await Presenter(storage, host).DeleteAllAsync("r4");

        Assert.Equal("history_delete_failed", host.Type(0));
        Assert.Equal(0, host.Resets);
    }

    [Fact]
    public async Task CheckDeleted_StartsANewChat_OnlyWhenTheSourceIsGone()
    {
        var storage = new Storage { Valid = false };
        var host = new Host { Conversation = Conversation("a", "Prima") };
        await Presenter(storage, host).CheckDeletedAsync();
        Assert.Equal(1, host.NewChats);
        Assert.Equal("history_changed", host.Type(0));

        storage.Valid = true;
        var kept = new Host { Conversation = Conversation("b", "Seconda") };
        await Presenter(storage, kept).CheckDeletedAsync();
        Assert.Equal(0, kept.NewChats);
    }

    [Fact]
    public async Task CheckDeleted_OfAnEmptyConversation_DoesNotAskTheStorage()
    {
        var storage = new Storage { Valid = false };
        var host = new Host();
        await Presenter(storage, host).CheckDeletedAsync();
        Assert.Equal(0, host.NewChats);
    }
}
