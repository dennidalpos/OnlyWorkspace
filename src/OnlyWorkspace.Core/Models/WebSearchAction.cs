using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Core.Models;

/// <summary>Pane actions that may run a web search when the profile enables it for them.</summary>
public static class WebSearchAction
{
    public const string Chat = "chat";
    public const string Summary = "summary";
    public const string Reply = "reply";
    public const string FollowUp = "followup";
    public const string Security = "security";
    public const string Actions = "actions";
    public const string Extract = "extract";
    public const string Briefing = "briefing";
    public const string Analysis = "analysis";

    public static IReadOnlyList<KeyValuePair<string, string>> All { get; } = new[]
    {
        new KeyValuePair<string, string>(Chat, "Chat"),
        new KeyValuePair<string, string>(Summary, "Sintesi"),
        new KeyValuePair<string, string>(Reply, "Risposte"),
        new KeyValuePair<string, string>(FollowUp, "Follow-up"),
        new KeyValuePair<string, string>(Security, "Verifica sicurezza"),
        new KeyValuePair<string, string>(Actions, "Attività e scadenze"),
        new KeyValuePair<string, string>(Extract, "Estrai dati"),
        new KeyValuePair<string, string>(Briefing, "Briefing"),
        new KeyValuePair<string, string>(Analysis, "Analisi della posta")
    };

    /// <summary>Actions that look for current public facts: the default for a newly enabled profile.</summary>
    public static List<string> Defaults() => new() { Chat, FollowUp, Security, Briefing };

    /// <summary>Action of a pane message type; null for messages that never search the web.</summary>
    public static string? Of(string? messageType) => messageType switch
    {
        "chat" => Chat,
        "summary" => Summary,
        "reply" or "custom_reply" or "refine_reply" => Reply,
        "email_followup" or "followup_draft" => FollowUp,
        "security" => Security,
        "action_items" => Actions,
        "extract_data" => Extract,
        "briefing" => Briefing,
        "mail_analysis" or "analysis_question" => Analysis,
        _ => null
    };

    public static bool Uses(LlmProfileConfig profile, string? action) =>
        action != null && profile.WebSearchEnabled && ProviderCatalog.Get(profile.Type).WebSearch != WebSearchMode.None &&
        (profile.WebSearchActions ?? new List<string>()).Contains(action, StringComparer.OrdinalIgnoreCase) &&
        ExposurePolicy.Of(profile).AllowsWeb(action);
}
