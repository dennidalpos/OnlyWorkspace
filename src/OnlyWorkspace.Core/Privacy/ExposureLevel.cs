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
    Standard = 2,
    /// <summary>Standard plus explicitly authorized, locally extracted attachment text. Value 3 stays retired.</summary>
    Extended = 4
}

/// <summary>Reads and writes only supported exposure levels.</summary>
public sealed class ExposureLevelJsonConverter : JsonConverter<ExposureLevel>
{
    public override ExposureLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<ExposureLevel>(value, true, out var level) && Enum.IsDefined(typeof(ExposureLevel), level)) return level;
        }
        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
        {
            if (Enum.IsDefined(typeof(ExposureLevel), value)) return (ExposureLevel)value;
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
    /// <summary>Free text may leave after the mechanical masking; otherwise only typed local facts.</summary>
    public bool MaskedText => ModelAllowed && Level >= ExposureLevel.Standard;
    /// <summary>A cloud profile that sends typed local facts only (Minimo).</summary>
    public static bool SendsFactsOnly(LlmProfileConfig profile) => profile.IsExternal && !Of(profile).MaskedText;
    /// <summary>Estimated tokens of email evidence in one request; 0 = only the model's own limit.</summary>
    public int PackTokens { get; private set; }
    /// <summary>Web search actions the level permits; null = every action the profile enables.</summary>
    public IReadOnlyCollection<string>? WebActions { get; private set; }

    /// <summary>A local profile: nothing leaves the PC, so the level does not restrict it.</summary>
    public static ExposurePolicy Local { get; } = new()
    {
        Level = ExposureLevel.Standard, ModelAllowed = true, Evidence = EvidenceMode.Original, Attachments = true, WholeEmails = true, Pseudonymize = true
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
        },
        new()
        {
            Level = ExposureLevel.Extended, ModelAllowed = true, Evidence = EvidenceMode.Original, Pseudonymize = true, WholeEmails = true,
            Attachments = true, PackTokens = 48_000, WebActions = new[] { WebSearchAction.Security }
        }
    };

    public static ExposurePolicy For(ExposureLevel level) =>
        Levels.FirstOrDefault(policy => policy.Level == level) ?? Levels[0];

    public static ExposurePolicy Of(LlmProfileConfig profile) => profile.IsExternal ? For(profile.Exposure) : Local;

    public bool AllowsWeb(string action) =>
        WebActions == null || WebActions.Contains(action, StringComparer.OrdinalIgnoreCase);

    public bool AnyWeb => WebActions == null || WebActions.Count > 0;

    public static IReadOnlyList<ExposureLevel> All { get; } =
        new[] { ExposureLevel.LocalOnly, ExposureLevel.Minimal, ExposureLevel.Standard, ExposureLevel.Extended };

    public static string Label(ExposureLevel level) => level switch
    {
        ExposureLevel.LocalOnly => "0 · Solo locale",
        ExposureLevel.Minimal => "1 · Minimo",
        ExposureLevel.Standard => "2 · Standard",
        ExposureLevel.Extended => "3 · Esteso",
        _ => "Livello non valido"
    };

    public static string Describe(ExposureLevel level) => level switch
    {
        ExposureLevel.LocalOnly => "Il profilo non invia nulla: restano attivi solo i profili locali.",
        ExposureLevel.Minimal => "Escono solo dati estratti in locale, con nomi, indirizzi, telefoni, IBAN e codici sostituiti da segnaposto. Niente testo integrale, allegati o ricerca web.",
        ExposureLevel.Standard => "Escono email e testo libero con nomi, indirizzi, recapiti, IBAN, codici, importi e date sostituiti da segnaposto. Niente allegati né file binari; query web non verificabili bloccate.",
        ExposureLevel.Extended => "Come Standard; con il permesso allegati escono anche i testi estratti in locale, mascherati allo stesso modo. Nessun file binario viene inviato.",
        _ => "Livello non valido: nessun dato viene inviato."
    };
}
