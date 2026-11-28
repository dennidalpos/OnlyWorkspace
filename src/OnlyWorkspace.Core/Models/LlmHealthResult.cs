using System.Collections.Generic;

namespace OnlyWorkspace.Core.Models;

public sealed class LlmHealthResult
{
    public bool IsHealthy { get; }
    public bool IsModelMissing { get; private set; }
    public string Version { get; }
    public IReadOnlyList<string> AvailableModels { get; }
    public string? ErrorMessage { get; }

    public LlmHealthResult(bool isHealthy, string version, IReadOnlyList<string> availableModels, string? errorMessage = null)
    {
        IsHealthy = isHealthy;
        Version = version ?? string.Empty;
        AvailableModels = availableModels ?? new List<string>();
        ErrorMessage = errorMessage;
    }

    public static LlmHealthResult Success(string version, IReadOnlyList<string> models)
        => new(true, version, models);

    public static LlmHealthResult Failure(string errorMessage)
        => new(false, string.Empty, new List<string>(), errorMessage);

    /// <summary>The service answered with its model list, but the configured model is unavailable.</summary>
    public static LlmHealthResult ModelMissing(string version, IReadOnlyList<string> models, string errorMessage)
        => new(false, version, models, errorMessage) { IsModelMissing = true };
}
