using System;
using System.Collections.Generic;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Validation;

public sealed class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new();
    /// <summary>Incoherent but usable settings: shown to the admin, never block loading.</summary>
    public List<string> Warnings { get; } = new();

    public void AddError(string error) => Errors.Add(error);
    public void AddWarning(string warning) => Warnings.Add(warning);
}

public static class ConfigurationValidator
{
    public static ValidationResult Validate(WorkspaceConfig config)
    {
        var result = new ValidationResult();

        if (config == null)
        {
            result.AddError("Configurazione assente.");
            return result;
        }

        if (config.SchemaVersion < 1)
        {
            result.AddError("SchemaVersion deve essere almeno 1.");
        }

        if (config.Limits != null && (config.Limits.FollowUpDays < 1 || config.Limits.FollowUpDays > 30))
        {
            result.AddWarning("Limits.FollowUpDays fuori intervallo (1-30): verrà limitato.");
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
                result.AddError("Un profilo ha l'ID vuoto.");
                continue;
            }

            if (!profileIds.Add(profile.Id))
            {
                result.AddError($"ID profilo duplicato: '{profile.Id}'.");
            }

            // "type": null in the JSON overrides the property default.
            var type = (profile.Type ?? string.Empty).Trim().ToLowerInvariant();

            if (!ProviderCatalog.IsKnown(type))
            {
                result.AddError($"Profilo '{profile.Id}': tipo di provider non supportato '{profile.Type}'.");
            }

            if (!Enum.IsDefined(typeof(Privacy.ExposureLevel), profile.Exposure))
            {
                result.AddError($"Profilo '{profile.Id}': livello di esposizione non valido.");
            }

            // BaseUrl checks
            if (string.IsNullOrWhiteSpace(profile.BaseUrl) || !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri))
            {
                result.AddError($"Profilo '{profile.Id}': l'URL endpoint non è un indirizzo completo valido.");
            }
            else
            {
                if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsLoopback(uri))
                    {
                        result.AddError($"Profilo '{profile.Id}': http:// è ammesso solo su questo PC (localhost/127.0.0.1); per gli altri server usare https://.");
                    }
                }
                else if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                {
                    result.AddError($"Profilo '{profile.Id}': schema '{uri.Scheme}' non supportato; usare https:// (o http:// su questo PC).");
                }
            }

            // Cloud providers manage context and output themselves: the two values are not used there.
            if (ProviderCatalog.Get(type).SupportsNumCtx)
            {
                if (profile.ContextBudgetTokens <= 0)
                {
                    result.AddError($"Profilo '{profile.Id}': il budget token deve essere maggiore di zero.");
                }

                if (profile.MaxOutputTokens <= 0)
                {
                    result.AddError($"Profilo '{profile.Id}': i token di output devono essere maggiori di zero.");
                }
                int maximum = ProviderCatalog.ComputeBudget(profile.NumCtx, profile.MaxOutputTokens);
                if (maximum <= 0 || profile.ContextBudgetTokens > maximum)
                    result.AddError($"Profilo '{profile.Id}': budget ({profile.ContextBudgetTokens}) non compatibile con NumCtx ({profile.NumCtx}), output ({profile.MaxOutputTokens}) e margine. Budget massimo: {maximum}.");
            }

            if (profile.Temperature < 0.0 || profile.Temperature > 2.0)
            {
                result.AddError($"Profilo '{profile.Id}': la temperatura deve essere fra 0,0 e 2,0.");
            }

            if (ProviderCatalog.Get(type).SupportsNumCtx && profile.NumCtx <= 0)
            {
                result.AddError($"Profilo '{profile.Id}': NumCtx deve essere maggiore di zero per Ollama.");
            }

            if (profile.FirstTokenTimeoutSeconds <= 0)
            {
                result.AddError($"Profilo '{profile.Id}': il timeout del primo token deve essere maggiore di zero.");
            }

            if (profile.IdleTimeoutSeconds < 0)
            {
                result.AddError($"Profilo '{profile.Id}': il timeout di inattività non può essere negativo (0 lo disattiva).");
            }

            AddCoherenceWarnings(profile, result);
        }

        if (!string.IsNullOrWhiteSpace(config.DefaultProfile) && !profileIds.Contains(config.DefaultProfile))
        {
            result.AddError($"Il profilo predefinito '{config.DefaultProfile}' non esiste fra i profili.");
        }

        if (config.DataEgress?.Consent is { } consent && Privacy.DataEgress.Problem(consent, DateTime.Now.AddDays(1)) is { } problem)
        {
            result.AddError("Informativa sull'uscita dei dati incompleta: " + problem);
        }

        // The ConfigTool refuses to save it; a file written elsewhere still loads and the pane stays blocked.
        if (Privacy.DataEgress.SelectionProblem(config) is { } selection) result.AddWarning(selection);

        return result;
    }

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback ||
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static void AddCoherenceWarnings(LlmProfileConfig profile, ValidationResult result)
    {
        var caps = ProviderCatalog.Get(profile.Type);

        // A remote Ollama may legitimately be external; a loopback one cannot.
        if (!caps.IsExternal && profile.External
            && Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint) && IsLoopback(endpoint))
        {
            result.AddWarning($"Profilo '{profile.Id}': marcato Cloud/Esterno ma l'endpoint è locale; il riquadro mostra l'avviso cloud e la Verifica non genera la spiegazione IA.");
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            result.AddWarning($"Profilo '{profile.Id}': modello non impostato (usa 'Rileva Modelli').");
        }

        if (caps.RequiresSecret && string.IsNullOrWhiteSpace(profile.SecretRef))
        {
            result.AddWarning($"Profilo '{profile.Id}': API key mancante per il provider {caps.Type}.");
        }

        bool authKnown = false;
        foreach (var a in caps.AuthTypes)
        {
            if (string.Equals(a, profile.AuthType, StringComparison.OrdinalIgnoreCase)) authKnown = true;
        }
        if (!authKnown)
        {
            result.AddWarning($"Profilo '{profile.Id}': AuthType '{profile.AuthType}' non usato dal provider {caps.Type} (ammessi: {string.Join(", ", caps.AuthTypes)}).");
        }
    }
}
