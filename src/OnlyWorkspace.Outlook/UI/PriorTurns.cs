using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// The conversation sent with a chat question (#177): system message, then the prior turns from the newest back within a third
/// of the budget and what the data left free (a long history loses its oldest turns), then the question with its data.
/// </summary>
internal static class PriorTurns
{
    /// <param name="messages">The prompt of the question: its system message and its last user message (the data).</param>
    /// <param name="history">Messages of the current conversation, the question just added last; null when the conversation does not belong to the active profile.</param>
    /// <param name="external">The active profile leaves the PC: the email tables already on screen do not go back with every turn.</param>
    public static List<ChatMessage> Build(IReadOnlyList<ChatMessage> messages, string userInstruction, int budget,
        IReadOnlyList<HistoryChatMessage>? history, bool external)
    {
        var result = new List<ChatMessage>();
        var system = messages.FirstOrDefault(m => m.Role == "system");
        if (system != null) result.Add(system);

        var question = messages.LastOrDefault(m => m.Role == "user");
        int dataChars = question?.Content.Length ?? 0;
        int historyRoom = Math.Min(ContextBuilder.MaxChars(budget / 3), Math.Max(0, ContextBuilder.MaxChars(budget) - dataChars));
        var prior = new List<ChatMessage>();
        for (int i = history != null ? history.Count - 2 : -1; i >= 0; i--)
        {
            var turn = history![i];
            bool fromAssistant = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase);
            if (!fromAssistant && !string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
            // The email tables already on screen do not go back to an external provider with every turn.
            string permitted = turn.ModelContent ?? turn.Content;
            string content = fromAssistant && external ? ChatMailDigest.WithoutTables(permitted) : permitted;
            if (content.Length == 0) continue;
            if (content.Length > historyRoom) break;
            historyRoom -= content.Length;
            prior.Add(fromAssistant ? ChatMessage.Assistant(content) : ChatMessage.User(content));
        }
        prior.Reverse();
        // Providers expect the conversation to open with a user turn.
        if (prior.Count > 0 && prior[0].Role == "assistant") prior.RemoveAt(0);
        result.AddRange(prior);

        result.Add(question ?? ChatMessage.User(userInstruction));
        return result;
    }
}
