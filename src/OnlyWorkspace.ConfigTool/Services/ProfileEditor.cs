using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.ConfigTool.Services;

public enum RenameOutcome { Unchanged, Renamed, Invalid }

public enum RemoveOutcome { Removed, LastProfile }

/// <summary>
/// Profile list and in-memory keys of the ConfigTool: add, rename, remove and key lookup, with no WinForms control.
/// The configuration is read through a delegate because the form replaces it on every reload.
/// </summary>
public sealed class ProfileEditor
{
    private readonly Func<WorkspaceConfig> _config;

    public ProfileEditor(Func<WorkspaceConfig> config, Dictionary<string, string> secrets, Dictionary<string, string> webSecrets)
    {
        _config = config;
        Secrets = secrets;
        WebSecrets = webSecrets;
    }

    public Dictionary<string, string> Secrets { get; }
    public Dictionary<string, string> WebSecrets { get; }

    public bool IsDefault(LlmProfileConfig profile) =>
        string.Equals(profile.Id, _config().DefaultProfile, StringComparison.OrdinalIgnoreCase);

    /// <summary>Key typed for the profile, or null when absent or blank.</summary>
    public string? SecretFor(string profileId) => NonBlank(Secrets, profileId);

    public string? WebSecretFor(string profileId) => NonBlank(WebSecrets, profileId);

    private static string? NonBlank(Dictionary<string, string> map, string id) =>
        map.TryGetValue(id, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>A new local profile with the first free «profile-N» id.</summary>
    public LlmProfileConfig Add()
    {
        var config = _config();
        int n = config.Profiles.Count + 1;
        while (config.Profiles.Any(p => string.Equals(p.Id, "profile-" + n, StringComparison.OrdinalIgnoreCase))) n++;
        string id = "profile-" + n;
        // LlmProfileConfig defaults = local Ollama from ProviderCatalog.
        var profile = new LlmProfileConfig { Id = id, DisplayName = "Nuovo Profilo (" + id + ")" };
        config.Profiles.Add(profile);
        return profile;
    }

    /// <summary>
    /// Changes the id: keys typed under the old id follow it, the vault references are dropped (re-created at Salva)
    /// and the default profile keeps pointing at the same profile. An empty or duplicate id leaves everything as it was.
    /// </summary>
    public RenameOutcome Rename(LlmProfileConfig profile, string newId)
    {
        newId = newId.Trim();
        if (newId == profile.Id) return RenameOutcome.Unchanged;

        var config = _config();
        if (string.IsNullOrWhiteSpace(newId) || config.Profiles.Any(p => p != profile && string.Equals(p.Id, newId, StringComparison.OrdinalIgnoreCase)))
            return RenameOutcome.Invalid;

        string oldId = profile.Id;
        profile.Id = newId;
        profile.SecretRef = string.Empty;
        profile.WebSearchSecretRef = string.Empty;
        Move(Secrets, oldId, newId);
        Move(WebSecrets, oldId, newId);
        if (config.DefaultProfile == oldId) config.DefaultProfile = newId;
        return RenameOutcome.Renamed;
    }

    private static void Move(Dictionary<string, string> map, string from, string to)
    {
        if (!map.TryGetValue(from, out var value)) return;
        map.Remove(from);
        map[to] = value;
    }

    /// <summary>Removes the profile and its typed keys; the default moves to the first profile left. The last profile stays.</summary>
    public RemoveOutcome Remove(LlmProfileConfig profile)
    {
        var config = _config();
        if (config.Profiles.Count <= 1) return RemoveOutcome.LastProfile;

        bool wasDefault = IsDefault(profile);
        config.Profiles.Remove(profile);
        Secrets.Remove(profile.Id);
        WebSecrets.Remove(profile.Id);
        // The default must always point to an existing profile.
        if (wasDefault) config.DefaultProfile = config.Profiles[0].Id;
        return RemoveOutcome.Removed;
    }
}
