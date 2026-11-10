using System;
using System.Collections.Generic;

namespace OnlyWorkspace.Core.Models;

/// <summary>Upgrade retired presets in memory; callers retain control of persistence.</summary>
public static class ProviderCompatibility
{
    private static readonly HashSet<string> Local = new(StringComparer.OrdinalIgnoreCase)
        { "lmstudio", "llamacpp", "vllm", "localai", "jan" };
    private static readonly HashSet<string> Cloud = new(StringComparer.OrdinalIgnoreCase)
        { "mistral", "xai", "deepseek", "openrouter", "groq", "together", "fireworks" };

    public static bool IsRetired(string? type) => type != null && (Local.Contains(type.Trim()) || Cloud.Contains(type.Trim()));

    public static bool Normalize(WorkspaceConfig config)
    {
        bool changed = false;
        if (config.Profiles != null)
            foreach (var profile in config.Profiles) changed |= Normalize(profile);
        return changed;
    }

    public static bool Normalize(LlmProfileConfig profile)
    {
        if (!IsRetired(profile.Type)) return false;
        profile.External |= Cloud.Contains(profile.Type.Trim());
        profile.Type = ProviderCatalog.OpenAICompatible;
        profile.ModelCapabilities = null;
        profile.EgressAuthorizationKey = string.Empty;
        return true;
    }
}
