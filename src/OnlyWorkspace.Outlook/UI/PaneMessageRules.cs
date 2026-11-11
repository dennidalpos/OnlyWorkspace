namespace OnlyWorkspace.Outlook.UI;

/// <summary>What the coordinator decides about a pane message before and after running it, with no Outlook or WebView2.</summary>
internal static class PaneMessageRules
{
    /// <summary>Actions that read the selected or opened email: without one the pane asks to select it.</summary>
    public static bool NeedsEmail(string? type) =>
        type is "summary" or "email_followup" or "reply" or "security" or "action_items" or "extract_data" or "custom_reply";

    public const string NoEmailToast = "Seleziona o apri un'email per questa azione.";

    public const string NoProfileMessage = "Nessun profilo IA configurato o configurazione non valida.";

    /// <summary>Result of the connection check shown in the pane («connection_health_result»).</summary>
    public static object ConnectionHealth(bool isHealthy, string? version, int modelsCount, long latencyMs, string? errorMessage) => new
    {
        type = "connection_health_result",
        isHealthy,
        version,
        modelsCount,
        latencyMs,
        errorMessage
    };

    public static object ConnectionFailed(string errorMessage) => new
    {
        type = "connection_health_result",
        isHealthy = false,
        errorMessage
    };
}
