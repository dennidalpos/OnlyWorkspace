using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>
/// Aligns the vault with the profiles before they are validated and saved: a typed key gets its reference, an emptied
/// field removes the key, and keys of removed or renamed profiles leave the vault (and every later backup).
/// </summary>
public static class ProfileSecretSync
{
    public static void Apply(WorkspaceConfig config, IReadOnlyDictionary<string, string> secrets,
        IReadOnlyDictionary<string, string> webSecrets, ISecretVault vault, Func<IReadOnlyCollection<string>> vaultKeys)
    {
        // Before validation: it warns on cloud profiles without a SecretRef. An emptied field removes the key.
        foreach (var profile in config.Profiles)
        {
            if (secrets.TryGetValue(profile.Id, out var secret) && !string.IsNullOrWhiteSpace(secret))
            {
                profile.SecretRef = $"profile_{profile.Id}_token";
                vault.SetSecret(profile.SecretRef, secret);
            }
            else
            {
                profile.SecretRef = string.Empty;
            }
        }
        foreach (var kvp in webSecrets)
        {
            var profile = config.Profiles.FirstOrDefault(p => p.Id == kvp.Key);
            if (profile == null) continue;
            if (string.IsNullOrWhiteSpace(kvp.Value))
            {
                if (!string.IsNullOrEmpty(profile.WebSearchSecretRef)) vault.RemoveSecret(profile.WebSearchSecretRef);
                profile.WebSearchSecretRef = string.Empty;
                profile.WebSearchEnabled = false;
                continue;
            }
            var key = $"profile_{profile.Id}_web_search";
            vault.SetSecret(key, kvp.Value);
            profile.WebSearchSecretRef = key;
        }
        // Keys of removed or renamed profiles would otherwise stay in secrets.bin and in every backup.
        var referenced = new HashSet<string>(config.Profiles.SelectMany(p => new[] { p.SecretRef, p.WebSearchSecretRef }), StringComparer.OrdinalIgnoreCase);
        foreach (var key in vaultKeys())
            if (key.StartsWith("profile_", StringComparison.OrdinalIgnoreCase) && !referenced.Contains(key)) vault.RemoveSecret(key);
    }
}
