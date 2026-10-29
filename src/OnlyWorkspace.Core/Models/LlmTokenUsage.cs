namespace OnlyWorkspace.Core.Models;

public sealed class LlmTokenUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
}

/// <summary>Provider completion status, available even when token counts are not reported.</summary>
public sealed class LlmCompletion
{
    public bool IsTruncated { get; set; }
    public string? Reason { get; set; }
}
