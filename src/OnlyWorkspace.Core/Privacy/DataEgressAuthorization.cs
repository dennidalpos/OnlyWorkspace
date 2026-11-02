using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>One explicit profile authorization. Fingerprints are salted; resolved credentials are never persisted.</summary>
public sealed class DataEgressProfileGrant
{
    public string ProfileId { get; set; } = string.Empty;
    public string PolicyKey { get; set; } = string.Empty;
    public string CredentialSalt { get; set; } = string.Empty;
    public string CredentialFingerprint { get; set; } = string.Empty;
}

public static partial class DataEgress
{
    public static IReadOnlyList<string> ProfileHosts(LlmProfileConfig profile) =>
        new[] { profile.IsExternal && ExposurePolicy.Of(profile).ModelAllowed ? ModelHost(profile) : null }
            .Concat(WebHosts(profile)).Where(h => !string.IsNullOrEmpty(h)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IEnumerable<LlmProfileConfig> EgressProfiles(WorkspaceConfig config) =>
        (config.Profiles ?? new List<LlmProfileConfig>()).Where(p => ProfileHosts(p).Count > 0);

    public static string Endpoint(string value) =>
        Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ? uri.AbsoluteUri : value;

    /// <summary>Only privacy-relevant permissions; display name, model and generation settings do not renew consent.</summary>
    public static string AuthorizationPolicyKey(LlmProfileConfig profile) => Digest(new
    {
        Version = 2, profile.Id, Type = profile.Type.ToLowerInvariant(), Endpoint = Endpoint(profile.BaseUrl),
        External = profile.IsExternal, Exposure = profile.IsExternal ? profile.Exposure : ExposureLevel.LocalOnly,
        Attachments = profile.IsExternal && profile.SendsAttachmentText,
        profile.UseSystemProxy, Auth = profile.AuthType.ToLowerInvariant(), profile.SecretRef, profile.WebSearchSecretRef,
        Amounts = profile.IsExternal || profile.PseudonymizeAmounts, Dates = profile.IsExternal || profile.PseudonymizeDates, Revision = PseudonymizationCatalog.CurrentRevision,
        WebHosts = WebHosts(profile),
        WebActions = WebSearchAction.All.Where(a => WebSearchAction.Uses(profile, a.Key)).Select(a => a.Key).OrderBy(a => a, StringComparer.Ordinal).ToArray()
    });

    private static string Digest<T>(T value)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private static string? CredentialFingerprint(LlmProfileConfig profile, string salt, Func<string, string?>? resolveSecret)
    {
        string? Resolve(string reference) => string.IsNullOrEmpty(reference) ? string.Empty : resolveSecret?.Invoke(reference);
        var model = Resolve(profile.SecretRef);
        var web = Resolve(profile.WebSearchSecretRef);
        // A named credential that cannot be read never means an anonymous authorization.
        return model == null || web == null ? null : Digest(new { Version = 1, Salt = salt, Model = model, Web = web });
    }

    private static DataEgressProfileGrant CreateGrant(LlmProfileConfig profile, Func<string, string?>? resolveSecret)
    {
        var bytes = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        string salt = Convert.ToBase64String(bytes);
        return new DataEgressProfileGrant
        {
            ProfileId = profile.Id, PolicyKey = AuthorizationPolicyKey(profile), CredentialSalt = salt,
            CredentialFingerprint = CredentialFingerprint(profile, salt, resolveSecret)
                ?? throw new InvalidOperationException("Impossibile autorizzare il profilo: una chiave configurata non è disponibile.")
        };
    }

    public static bool HasProfileGrant(WorkspaceConfig? config, LlmProfileConfig profile, Func<string, string?>? resolveSecret = null)
    {
        var consent = config?.DataEgress?.Consent;
        if (!Current(consent)) return false;
        var grants = consent!.Profiles?.Where(p => string.Equals(p.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (grants?.Count != 1) return false;
        var grant = grants[0];
        if (grant.PolicyKey != AuthorizationPolicyKey(profile) || grant.CredentialSalt?.Length != 44 || grant.CredentialFingerprint?.Length != 44)
            return false;
        if (!ProfileHosts(profile).All(h => Covers(config, h, LevelOf(profile)))) return false;
        return CredentialFingerprint(profile, grant.CredentialSalt, resolveSecret) == grant.CredentialFingerprint;
    }

    /// <summary>After vault load, acceptance or rollback, bind reusable conversation context to the verified authorization.</summary>
    public static void BindProfiles(WorkspaceConfig config, Func<string, string?>? resolveSecret)
    {
        foreach (var profile in config.Profiles)
        {
            var grant = config.DataEgress?.Consent?.Profiles?.FirstOrDefault(p => string.Equals(p.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase));
            profile.EgressAuthorizationKey = HasProfileGrant(config, profile, resolveSecret)
                ? grant!.PolicyKey + ":" + grant.CredentialFingerprint : string.Empty;
        }
    }

    public static string ProfileSummary(LlmProfileConfig profile)
    {
        string data = profile.IsExternal ? "Solo fatti strutturati protetti; importi e date sempre sostituiti. Testo libero non verificabile bloccato."
            : "Elaborazione locale. Le query web non verificabili non vengono inviate.";
        return $"{(profile.DisplayName.Length == 0 ? profile.Id : profile.DisplayName)} — {Endpoint(profile.BaseUrl)}\r\n" + data;
    }
}
