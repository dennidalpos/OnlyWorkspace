using System.Collections.Generic;
using System.Linq;
using System.Text;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Context;

/// <summary>One estimate for the full model input, not only the email. Never alters the chosen messages.</summary>
public static class ModelRequestContext
{
    public static List<ChatMessage> Prepare(LlmProfileConfig profile, IEnumerable<ChatMessage> messages, string? schemaOrTools = null)
    {
        var snapshot = messages.ToList();
        int budget = ProviderCatalog.PromptBudget(profile);
        if (budget <= 0) return snapshot;
        var text = new StringBuilder();
        foreach (var message in snapshot) text.Append(message.Role).Append(':').AppendLine(message.Content);
        if (!string.IsNullOrEmpty(schemaOrTools)) text.AppendLine(schemaOrTools);
        new ContextBuilder().EnsureFits(text.ToString(), budget);
        return snapshot;
    }
}
