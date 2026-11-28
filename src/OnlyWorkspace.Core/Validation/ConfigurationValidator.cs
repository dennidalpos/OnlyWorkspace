using System;
using System.Collections.Generic;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Validation;

/// <summary>Where an error lives: the ConfigTool selects the profile and focuses the field.</summary>
public sealed class ValidationIssue
{
    public ValidationIssue(string message, string? profileId, string field)
    {
        Message = message; ProfileId = profileId; Field = field;
    }
    public string Message { get; }
    /// <summary>Null for settings outside the profiles.</summary>
    public string? ProfileId { get; }
    /// <summary>One of <see cref="ValidationField"/>; empty when no single field causes the error.</summary>
    public string Field { get; }
}

/// <summary>Fields named by <see cref="ValidationIssue.Field"/>.</summary>
public static class ValidationField
{
    public const string ProfileId = "ProfileId";
    public const string Provider = "Provider";
    public const string Exposure = "Exposure";
    public const string Endpoint = "Endpoint";
    public const string Budget = "Budget";
    public const string MaxOutput = "MaxOutput";
    public const string NumCtx = "NumCtx";
    public const string Temperature = "Temperature";
    public const string FirstTokenTimeout = "FirstTokenTimeout";
    public const string IdleTimeout = "IdleTimeout";
    public const string DefaultProfile = "DefaultProfile";
    public const string Consent = "Consent";
}

public sealed class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new();
    /// <summary>The errors with their profile and field, in the same order as <see cref="Errors"/>.</summary>
    public List<ValidationIssue> Issues { get; } = new();
    /// <summary>Incoherent but usable settings: shown to the admin, never block loading.</summary>
    public List<string> Warnings { get; } = new();

    public void AddError(string error, string? profileId = null, string field = "")
    {
        Errors.Add(error);
        Issues.Add(new ValidationIssue(error, profileId, field));
    }
    public void AddWarning(string warning) => Warnings.Add(warning);
}

/// <summary>Messages use the profile's displayed name and the ConfigTool's on-screen labels, not JSON names.</summary>
public static class ConfigurationValidator
{
    public static ValidationResult Validate(WorkspaceConfig config, Func<string, string?>? resolveSecret = null)
    {
        var result = new ValidationResult();

        if (config == null)
        {
            result.AddError("Configurazione assente.");
            return result;
        }

        if (config.SchemaVersion < 1)
        {
            result.AddError("Il file di configurazione ha una versione non valida: ripristinare una copia da Manutenzione e backup.");
        }

        if (config.Limits != null && (config.Limits.FollowUpDays < 1 || config.Limits.FollowUpDays > 30))
        {
            result.AddWarning("Generale: i giorni per i solleciti sono fuori dall'intervallo 1-30 e verranno limitati.");
        }

        if (config.Profiles == null || config.Profiles.Count == 0)
        {
            result.AddError("Serve almeno un profilo IA.");
            return result;
        }

        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                string unnamed = string.IsNullOrWhiteSpace(profile.DisplayName) ? "Un profilo" : $"Profilo «{profile.DisplayName}»";
                result.AddError(unnamed + ": «ID profilo» è vuoto.", profile.Id, ValidationField.ProfileId);
                continue;
            }

            string name = Name(profile);
            if (!profileIds.Add(profile.Id))
            {
                result.AddError($"{name}: «ID profilo» ({profile.Id}) è già usato da un altro profilo.", profile.Id, ValidationField.ProfileId);
            }

            // "type": null in the JSON overrides the property default.
            var type = (profile.Type ?? string.Empty).Trim().ToLowerInvariant();

            if (!ProviderCatalog.IsKnown(type))
            {
                result.AddError($"{name}: «Servizio IA» non supportato ({profile.Type}).", profile.Id, ValidationField.Provider);
            }

            if (!Enum.IsDefined(typeof(Privacy.ExposureLevel), profile.Exposure))
            {
                result.AddError($"{name}: «Esposizione dati» non valida.", profile.Id, ValidationField.Exposure);
            }

            if (string.IsNullOrWhiteSpace(profile.BaseUrl) || !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri))
            {
                result.AddError($"{name}: «Indirizzo del servizio» non è un indirizzo completo valido.", profile.Id, ValidationField.Endpoint);
            }
            else
            {
                if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsLoopback(uri))
                    {
                        result.AddError($"{name}: «Indirizzo del servizio» con http:// è ammesso solo su questo PC (localhost/127.0.0.1); per gli altri server usare https://.", profile.Id, ValidationField.Endpoint);
                    }
                }
                else if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                {
                    result.AddError($"{name}: «Indirizzo del servizio» con {uri.Scheme}:// non è supportato; usare https:// (o http:// su questo PC).", profile.Id, ValidationField.Endpoint);
                }
            }

            // Cloud providers manage context and output themselves: the two values are not used there.
            if (ProviderCatalog.Get(type).SupportsNumCtx)
            {
                if (profile.ContextBudgetTokens <= 0)
                {
                    result.AddError($"{name}: «Budget token (prompt)» deve essere maggiore di zero.", profile.Id, ValidationField.Budget);
                }

                if (profile.MaxOutputTokens <= 0)
                {
                    result.AddError($"{name}: «Max output token» deve essere maggiore di zero.", profile.Id, ValidationField.MaxOutput);
                }
                int maximum = ProviderCatalog.ComputeBudget(profile.NumCtx, profile.MaxOutputTokens);
                if (maximum <= 0 || profile.ContextBudgetTokens > maximum)
                    result.AddError($"{name}: «Budget token (prompt)» ({profile.ContextBudgetTokens}) non è compatibile con «NumCtx (Ollama)» ({profile.NumCtx}), «Max output token» ({profile.MaxOutputTokens}) e margine. Massimo: {maximum}.", profile.Id, ValidationField.Budget);
            }

            if (profile.Temperature < 0.0 || profile.Temperature > 2.0)
            {
                result.AddError($"{name}: «Temperatura» deve essere fra 0,0 e 2,0.", profile.Id, ValidationField.Temperature);
            }

            if (ProviderCatalog.Get(type).SupportsNumCtx && profile.NumCtx <= 0)
            {
                result.AddError($"{name}: «NumCtx (Ollama)» deve essere maggiore di zero.", profile.Id, ValidationField.NumCtx);
            }

            if (profile.FirstTokenTimeoutSeconds <= 0)
            {
                result.AddError($"{name}: «Timeout primo token (s)» deve essere maggiore di zero.", profile.Id, ValidationField.FirstTokenTimeout);
            }

            if (profile.IdleTimeoutSeconds < 0)
            {
                result.AddError($"{name}: «Timeout inattività (s)» non può essere negativo (0 lo disattiva).", profile.Id, ValidationField.IdleTimeout);
            }

            AddCoherenceWarnings(profile, name, result);
        }

        if (!string.IsNullOrWhiteSpace(config.DefaultProfile) && !profileIds.Contains(config.DefaultProfile))
        {
            result.AddError($"Generale: «Profilo predefinito» ({config.DefaultProfile}) non esiste più fra i profili.", null, ValidationField.DefaultProfile);
        }

        if (config.DataEgress?.Consent is { } consent && Privacy.DataEgress.Problem(consent) is { } problem)
        {
            result.AddError("Privacy e dati: informativa sull'uscita dei dati incompleta: " + problem, null, ValidationField.Consent);
        }

        // Authorization is advisory during validation; dispatch alone requires a verified grant.
        if (Privacy.DataEgress.SelectionProblem(config, resolveSecret) is { } selection) result.AddWarning(selection);

        return result;
    }

    /// <summary>The name the ConfigTool list shows; the ID only when the profile has no displayed name.</summary>
    private static string Name(LlmProfileConfig profile) =>
        "Profilo «" + (string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName.Trim()) + "»";

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback ||
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static void AddCoherenceWarnings(LlmProfileConfig profile, string name, ValidationResult result)
    {
        var caps = ProviderCatalog.Get(profile.Type);
        if (caps.Type == ProviderCatalog.OpenAICompatible && profile.WebSearchEnabled)
            result.AddWarning($"{name}: collegamento Compatibile OpenAI; lettura delle capacità specifiche e ricerca web non disponibili. Indirizzo, modello e autenticazione restano quelli configurati.");

        // A remote Ollama may legitimately be external; a loopback one cannot.
        if (!caps.IsExternal && profile.External
            && Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint) && IsLoopback(endpoint))
        {
            result.AddWarning($"{name}: marcato come servizio esterno ma l'indirizzo è su questo PC; il riquadro mostra l'avviso cloud e la Verifica non genera la spiegazione IA.");
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            result.AddWarning($"{name}: «Modello» non impostato (usa «Scegli modello…»).");
        }

        if (caps.RequiresSecret && string.IsNullOrWhiteSpace(profile.SecretRef))
        {
            result.AddWarning($"{name}: «Chiave di accesso» mancante per {caps.DisplayName}.");
        }

        bool authKnown = false;
        foreach (var a in caps.AuthTypes)
        {
            if (string.Equals(a, profile.AuthType, StringComparison.OrdinalIgnoreCase)) authKnown = true;
        }
        if (!authKnown)
        {
            result.AddWarning($"{name}: «Autenticazione» ({profile.AuthType}) non usata da {caps.DisplayName} (ammesse: {string.Join(", ", caps.AuthTypes)}).");
        }
    }
}
