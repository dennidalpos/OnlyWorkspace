using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>How much Outlook data an external profile may send. Ignored for local profiles (nothing leaves the PC).</summary>
[JsonConverter(typeof(ExposureLevelJsonConverter))]
public enum ExposureLevel
{
    /// <summary>The profile is configured but sends nothing: only local profiles work.</summary>
    LocalOnly = 0,
    /// <summary>Only local facts, pseudonymized; no bodies, attachments or web.</summary>
    Minimal = 1,
    /// <summary>Cleaned original bodies, pseudonymized; web only for the security check.</summary>
    Standard = 2
}

/// <summary>Reads the retired level only to migrate existing configurations to the stricter Standard policy.</summary>
public sealed class ExposureLevelJsonConverter : JsonConverter<ExposureLevel>
{
    public override ExposureLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.Equals(value, "Full", StringComparison.OrdinalIgnoreCase) || value == "3") return ExposureLevel.Standard;
            if (Enum.TryParse<ExposureLevel>(value, true, out var level) && Enum.IsDefined(typeof(ExposureLevel), level)) return level;
        }
        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
        {
            if (value == 3) return ExposureLevel.Standard;
            if (value >= 0 && value <= 2) return (ExposureLevel)value;
        }
        throw new JsonException("Livello di esposizione non valido.");
    }

    public override void Write(Utf8JsonWriter writer, ExposureLevel value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(typeof(ExposureLevel), value)) throw new JsonException("Livello di esposizione non valido.");
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>What the archive sends for each email of a multi-email request.</summary>
public enum EvidenceMode
{
    /// <summary>Only literal local facts; an email without usable facts stays out with a note.</summary>
    FactsOnly,
    /// <summary>The whole original body, cleaned before dispatch.</summary>
    Original
}

/// <summary>The rules of one exposure level, shared by the pane and the ConfigTool.</summary>
public sealed class ExposurePolicy
{
    public ExposureLevel Level { get; private set; }
    public bool ModelAllowed { get; private set; }
    public EvidenceMode Evidence { get; private set; }
    public bool Pseudonymize { get; private set; }
    /// <summary>Whole emails, appointments or contacts may be sent; otherwise only local facts.</summary>
    public bool WholeEmails { get; private set; }
    public bool Attachments { get; private set; }
    /// <summary>Estimated tokens of email evidence in one request; 0 = only the model's own limit.</summary>
    public int PackTokens { get; private set; }
    /// <summary>Web search actions the level permits; null = every action the profile enables.</summary>
    public IReadOnlyCollection<string>? WebActions { get; private set; }

    /// <summary>A local profile: nothing leaves the PC, so the level does not restrict it.</summary>
    public static ExposurePolicy Local { get; } = new()
    {
        Level = ExposureLevel.Standard, ModelAllowed = true, Evidence = EvidenceMode.Original, Attachments = true, WholeEmails = true
    };

    private static readonly ExposurePolicy[] Levels =
    {
        new() { Level = ExposureLevel.LocalOnly, Evidence = EvidenceMode.FactsOnly, Pseudonymize = true, WebActions = Array.Empty<string>() },
        new()
        {
            Level = ExposureLevel.Minimal, ModelAllowed = true, Evidence = EvidenceMode.FactsOnly, Pseudonymize = true,
            PackTokens = 16_000, WebActions = Array.Empty<string>()
        },
        new()
        {
            Level = ExposureLevel.Standard, ModelAllowed = true, Evidence = EvidenceMode.Original, Pseudonymize = true, WholeEmails = true,
            PackTokens = 48_000, WebActions = new[] { WebSearchAction.Security }
        }
    };

    public static ExposurePolicy For(ExposureLevel level) =>
        Levels[(int)level >= 0 && (int)level < Levels.Length ? (int)level : 0];

    public static ExposurePolicy Of(LlmProfileConfig profile) => profile.IsExternal ? For(profile.Exposure) : Local;

    public bool AllowsWeb(string action) =>
        WebActions == null || WebActions.Contains(action, StringComparer.OrdinalIgnoreCase);

    public bool AnyWeb => WebActions == null || WebActions.Count > 0;

    public static IReadOnlyList<ExposureLevel> All { get; } =
        new[] { ExposureLevel.LocalOnly, ExposureLevel.Minimal, ExposureLevel.Standard };

    public static string Label(ExposureLevel level) => level switch
    {
        ExposureLevel.LocalOnly => "0 · Solo locale",
        ExposureLevel.Minimal => "1 · Minimo",
        ExposureLevel.Standard => "2 · Standard",
        _ => "Livello non valido"
    };

    public static string Describe(ExposureLevel level) => level switch
    {
        ExposureLevel.LocalOnly => "Il profilo non invia nulla: restano attivi solo i profili locali.",
        ExposureLevel.Minimal => "Escono solo dati estratti in locale, con nomi, indirizzi, telefoni, IBAN e codici sostituiti da segnaposto. Niente testo integrale, allegati o ricerca web.",
        ExposureLevel.Standard => "Escono i fatti locali e il testo dell'email ripulito, sempre con segnaposto. Niente allegati; ricerca web solo per la verifica di sicurezza (soli domini).",
        _ => "Livello non valido: nessun dato viene inviato."
    };
}
