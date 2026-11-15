using System;
using System.Linq;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>What the history presenter needs from the pane coordinator: the page, the current conversation and the running work.</summary>
internal interface IHistoryPaneHost
{
    /// <summary>Read at each call: the storage of the coordinator can be replaced.</summary>
    IHistoryStorageService Storage { get; }
    void Post(object message);
    bool HistoryEnabled { get; }
    ConversationHistory Conversation { get; set; }
    LlmProfileConfig? ActiveProfile { get; }
    LlmProfileConfig? FindProfile(string profileId);
    void CancelActiveTask();
    void ForgetAnalysisEvidence();
    void ResetConversation(bool preservePage);
    void StartNewChat();
    void Toast(string message, string kind);
}

/// <summary>
/// The history drawer of the pane: list, open, delete, delete all and the check of a conversation whose email left the archive (#177).
/// No Outlook calls: it talks to the page and to the conversation only through <see cref="IHistoryPaneHost"/>.
/// </summary>
internal sealed class HistoryPanePresenter
{
    private readonly IHistoryPaneHost _host;
    private bool _checkingDeleted;

    public HistoryPanePresenter(IHistoryPaneHost host)
    {
        _host = host;
    }

    public static string DisplayNameOf(LlmProfileConfig profile) =>
        string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName;

    /// <summary>The email of a history entry as the pane shows it: subject, then sender and date.</summary>
    public static object? MailRefForPane(HistoryContextReference? email) => email == null ? null : new
    {
        entryId = email.EntryId,
        storeId = email.StoreId,
        subject = email.Subject,
        itemType = email.ItemType,
        meta = string.Join(" · ", new[] { email.Sender, email.Date?.ToString("dd/MM/yyyy HH:mm") }.Where(v => !string.IsNullOrWhiteSpace(v)))
    };

    public async Task SendListAsync(string? requestId = null, bool deleted = false)
    {
        try
        {
            var list = await _host.Storage.ListConversationsAsync();
            var items = list.Select(l => new
            {
                id = l.Id,
                title = l.Title,
                updatedAt = l.UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                messageCount = l.MessageCount,
                email = MailRefForPane(l.Email)
            }).ToList();

            _host.Post(new { type = "history_list", items, enabled = _host.HistoryEnabled, requestId, deleted });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to list conversation history");
            _host.Post(new { type = "history_list", items = Array.Empty<object>(), enabled = _host.HistoryEnabled, error = "Impossibile leggere lo storico.", requestId, deleted });
        }
    }

    public async Task LoadAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            var conv = await _host.Storage.LoadConversationAsync(id!);
            if (conv != null)
            {
                _host.CancelActiveTask();
                _host.Conversation = conv;
                _host.ForgetAnalysisEvidence();
                var msgs = conv.Messages.Select(m => new
                {
                    role = m.Role,
                    content = m.Content,
                    webSources = m.WebSources,
                    mailSources = m.MailSources,
                    email = MailRefForPane(m.Email),
                    action = m.Action,
                    documents = m.Documents,
                    security = m.Security
                }).ToList();
                // Another profile continues in a new conversation (BeginConversationTurn): the pane says so.
                var active = _host.ActiveProfile;
                var made = _host.FindProfile(conv.ProfileId);

                _host.Post(new
                {
                    type = "history_loaded",
                    title = conv.Title,
                    messages = msgs,
                    otherProfile = active != null && made != null && made.Id != active.Id ? DisplayNameOf(made) : null,
                    policyChanged = active != null && !ConversationPolicy.Matches(conv, active)
                });
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to load conversation {Id}", id);
            _host.Toast("Impossibile aprire la conversazione.", "error");
        }
    }

    public async Task DeleteAsync(string? id, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            if (!await _host.Storage.DeleteConversationAsync(id!))
                throw new InvalidOperationException("The requested conversation was not deleted.");
            if (_host.Conversation.Id == id)
            {
                _host.ResetConversation(preservePage: true);
            }
            await SendListAsync(requestId, deleted: true);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to delete conversation {Id}", id);
            _host.Post(new { type = "history_delete_failed", requestId });
            _host.Toast("Eliminazione non riuscita.", "error");
        }
    }

    public async Task DeleteAllAsync(string? requestId)
    {
        try
        {
            await _host.Storage.DeleteAllConversationsAsync();
            _host.ResetConversation(preservePage: true);
            await SendListAsync(requestId, deleted: true);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to delete all history");
            _host.Post(new { type = "history_delete_failed", requestId });
            _host.Toast("Eliminazione non riuscita.", "error");
        }
    }

    public async Task CheckDeletedAsync()
    {
        var conversation = _host.Conversation;
        if (_checkingDeleted || conversation.ContextReferences.Count == 0 && conversation.Messages.Count == 0 || _host.Storage is not IHistorySourceInvalidation invalidation) return;
        _checkingDeleted = true;
        try
        {
            if (!await invalidation.IsValidAsync(conversation) && ReferenceEquals(conversation, _host.Conversation))
            {
                _host.StartNewChat();
                _host.Post(new { type = "history_changed" });
                _host.Toast("Conversazione rimossa: usava un'email eliminata dall'archivio.", "info");
            }
        }
        catch (Exception ex) { OnlyWorkspaceLog.Logger.Warning("History invalidation check failed: {Kind}", ex.GetType().Name); }
        finally { _checkingDeleted = false; }
    }
}
