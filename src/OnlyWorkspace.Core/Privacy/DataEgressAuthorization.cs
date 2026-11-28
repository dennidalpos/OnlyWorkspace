using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>
/// One authorized combination of privacy permissions for one service (connector and address). Every profile and model of
/// that service with the same permissions and keys is covered. Fingerprints are salted; resolved credentials are never persisted.
/// </summary>
public sealed class DataEgressServiceGrant
{
    public string Service { get; set; } = string.Empty;
    public string PolicyKey { get; set; } = string.Empty;
    public string CredentialSalt { get; set; } = string.Empty;
    public string CredentialFingerprint { get; set; } = string.Empty;
}

public static partial class DataEgress
{
    /// <summary>Combinations kept per service: the oldest are dropped first.</summary>
    public const int GrantsPerService = 16;

    public static IReadOnlyList<string> ProfileHosts(LlmProfileConfig profile) =>
        new[] { profile.IsExternal && ExposurePolicy.Of(profile).ModelAllowed ? ModelHost(profile) : null }
            .Concat(WebHosts(profile)).Where(h => !string.IsNullOrEmpty(h)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IEnumerable<LlmProfileConfig> EgressProfiles(WorkspaceConfig config) =>
        (config.Profiles ?? new List<LlmProfileConfig>()).Where(p => ProfileHosts(p).Count > 0);

    public static string Endpoint(string value) =>
        Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ? uri.AbsoluteUri : value;

    /// <summary>The service a profile talks to: connector and normalized address. Neither the profile nor the model is part of it.</summary>
    public static string ServiceKey(LlmProfileConfig profile) => profile.Type.ToLowerInvariant() + " " + Endpoint(profile.BaseUrl);

    /// <summary>Only privacy-relevant permissions; profile, display name, model and generation settings do not renew consent.</summary>
    public static string AuthorizationPolicyKey(LlmProfileConfig profile) => Digest(new
    {
        Version = 4,
        Type = profile.Type.ToLowerInvariant(),
        Endpoint = Endpoint(profile.BaseUrl),
        External = profile.IsExternal,
        Exposure = profile.IsExternal ? profile.Exposure : ExposureLevel.LocalOnly,
        Region = profile.IsExternal ? profile.ServiceRegion : ServiceRegion.Unspecified,
        Attachments = profile.IsExternal && profile.SendsAttachmentText,
        profile.UseSystemProxy,
        Auth = profile.AuthType.ToLowerInvariant(),
        profile.SecretRef,
        profile.WebSearchSecretRef,
        Amounts = profile.IsExternal || profile.PseudonymizeAmounts,
        Dates = profile.IsExternal || profile.PseudonymizeDates,
        Revision = PseudonymizationCatalog.CurrentRevision,
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

    private static DataEgressServiceGrant CreateGrant(LlmProfileConfig profile, Func<string, string?>? resolveSecret)
    {
        var bytes = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        string salt = Convert.ToBase64String(bytes);
        return new DataEgressServiceGrant
        {
            Service = ServiceKey(profile),
            PolicyKey = AuthorizationPolicyKey(profile),
            CredentialSalt = salt,
            CredentialFingerprint = CredentialFingerprint(profile, salt, resolveSecret)
                ?? throw new InvalidOperationException("Impossibile autorizzare il profilo: una chiave configurata non è disponibile.")
        };
    }

    private static bool Matches(DataEgressServiceGrant? grant, LlmProfileConfig profile, Func<string, string?>? resolveSecret) =>
        grant != null && grant.Service == ServiceKey(profile) && grant.PolicyKey == AuthorizationPolicyKey(profile) &&
        grant.CredentialSalt?.Length == 44 && grant.CredentialFingerprint?.Length == 44 &&
        CredentialFingerprint(profile, grant.CredentialSalt, resolveSecret) == grant.CredentialFingerprint;

    /// <summary>The grant of the current acceptance that covers the profile's service, permissions and actual keys, or null.</summary>
    private static DataEgressServiceGrant? FindGrant(WorkspaceConfig? config, LlmProfileConfig profile, Func<string, string?>? resolveSecret)
    {
        var consent = config?.DataEgress?.Consent;
        if (!Current(consent) || consent!.Grants == null) return null;
        if (!ProfileHosts(profile).All(h => Covers(config, h, LevelOf(profile)))) return null;
        return consent.Grants.FirstOrDefault(g => Matches(g, profile, resolveSecret));
    }

    public static bool HasProfileGrant(WorkspaceConfig? config, LlmProfileConfig profile, Func<string, string?>? resolveSecret = null) =>
        FindGrant(config, profile, resolveSecret) != null;

    /// <summary>After vault load, acceptance or rollback, bind reusable conversation context to the verified authorization.</summary>
    public static void BindProfiles(WorkspaceConfig config, Func<string, string?>? resolveSecret)
    {
        foreach (var profile in config.Profiles)
            profile.EgressAuthorizationKey = FindGrant(config, profile, resolveSecret) is { } grant
                ? grant.PolicyKey + ":" + grant.CredentialFingerprint : string.Empty;
    }

    /// <summary>
    /// The grants of a new acceptance. A combination already authorized under the current notice keeps its grant (and so its
    /// conversations); the others are created. Earlier combinations stay, the most recent first, up to the limit per service.
    /// </summary>
    private static List<DataEgressServiceGrant> MergeGrants(WorkspaceConfig config, IReadOnlyList<LlmProfileConfig> profiles, Func<string, string?> resolveSecret)
    {
        var previous = config.DataEgress?.Consent;
        var earlier = Current(previous) ? (previous!.Grants ?? new List<DataEgressServiceGrant>()).Where(g => g != null).ToList() : new List<DataEgressServiceGrant>();
        var current = new List<DataEgressServiceGrant>();
        foreach (var profile in profiles)
        {
            if (current.Any(g => Matches(g, profile, resolveSecret))) continue;
            current.Add(earlier.FirstOrDefault(g => Matches(g, profile, resolveSecret)) ?? CreateGrant(profile, resolveSecret));
        }
        // Grants are stored most recent first: the current combinations, then the earlier ones; identical ones are kept once.
        var remembered = new List<DataEgressServiceGrant>(current);
        foreach (var grant in earlier)
            if (!remembered.Any(g => g.Service == grant.Service && g.PolicyKey == grant.PolicyKey && g.CredentialFingerprint == grant.CredentialFingerprint))
                remembered.Add(grant);
        return remembered.GroupBy(g => g.Service, StringComparer.Ordinal).SelectMany(group => group.Take(GrantsPerService)).ToList();
    }

    /// <summary>Profiles grouped by service, in configuration order.</summary>
    public static IEnumerable<IGrouping<string, LlmProfileConfig>> ByService(IEnumerable<LlmProfileConfig> profiles) =>
        profiles.GroupBy(ServiceKey, StringComparer.Ordinal);

    public static string Name(LlmProfileConfig profile) => profile.DisplayName.Length == 0 ? profile.Id : profile.DisplayName;

    /// <summary>What the notice summarizes for one service: connector and address, then each profile with its permissions.</summary>
    public static string ServiceSummary(IReadOnlyList<LlmProfileConfig> profiles)
    {
        var first = profiles[0];
        var lines = new List<string> { $"{ProviderCatalog.Get(first.Type).DisplayName} — {Endpoint(first.BaseUrl)}" };
        foreach (var profile in profiles)
        {
            string data = !profile.IsExternal ? "elaborazione locale con pseudonimizzazione; le query web non verificabili non vengono inviate."
                : ExposurePolicy.Of(profile).MaskedText
                    ? "email e testo libero mascherati con segnaposto, importi e date sempre sostituiti; dati sensibili non riconosciuti non anonimizzati."
                    : "solo fatti strutturati protetti, importi e date sempre sostituiti; testo libero bloccato.";
            string level = profile.IsExternal ? $"«{ExposurePolicy.Label(profile.Exposure)}», area del servizio: {ServiceRegions.Label(profile.ServiceRegion)}; " : string.Empty;
            lines.Add($"• {Name(profile)}: {level}{data}");
        }
        return string.Join("\r\n", lines);
    }
}
