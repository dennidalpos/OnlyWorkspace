using System.Collections.Generic;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Interfaces;

public interface IHistoryStorageService
{
    Task SaveConversationAsync(ConversationHistory conversation);
    Task<ConversationHistory?> LoadConversationAsync(string id);
    Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync();
    Task<bool> DeleteConversationAsync(string id);
    Task DeleteAllConversationsAsync();
    Task<int> CleanupExpiredConversationsAsync(int retentionDays, int maxConversations);
}
