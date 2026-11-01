using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>A conversation belongs to one destination and data policy. Unknown history is for reading only.</summary>
public static class ConversationPolicy
{
    public const string ChangedMessage = "La destinazione o i permessi dei dati sono cambiati: il prossimo messaggio apre una nuova conversazione. Lo storico precedente resta consultabile.";

    public static string Key(LlmProfileConfig profile)
    {
        using var hash = SHA256.Create();
        string endpoint = Uri.TryCreate(profile.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            ? uri.AbsoluteUri : profile.BaseUrl;
        return Convert.ToBase64String(hash.ComputeHash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 5, profile.Id, Type = profile.Type.Trim().ToLowerInvariant(), profile.Model,
            Endpoint = endpoint, Auth = profile.AuthType.ToLowerInvariant(), profile.SecretRef,
            profile.UseSystemProxy, External = profile.IsExternal, profile.Exposure, profile.AllowAttachmentUpload,
            profile.PseudonymizeAmounts, profile.PseudonymizeDates, PseudonymizationRevision = PseudonymizationCatalog.CurrentRevision,
            profile.WebSearchEnabled, profile.WebSearchSecretRef,
            WebActions = (profile.WebSearchActions ?? new()).Select(a => a.ToLowerInvariant()).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToArray(),
            WebHosts = DataEgress.WebHosts(profile)
        })));
    }

    public static bool Matches(ConversationHistory conversation, LlmProfileConfig profile) =>
        conversation.ProfileId == profile.Id && conversation.PolicyKey == Key(profile);

    public static ConversationHistory Create(LlmProfileConfig profile, string title = "Nuova conversazione") =>
        new() { ProfileId = profile.Id, PolicyKey = Key(profile), Title = title };
}
