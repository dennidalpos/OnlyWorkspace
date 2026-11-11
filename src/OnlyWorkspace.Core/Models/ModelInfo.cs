using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace OnlyWorkspace.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapabilityState { Unknown, Supported, Unsupported }

/// <summary>Model capabilities as declared by the provider; Unknown when it does not publish them.</summary>
public sealed class ModelCapabilities
{
    public CapabilityState Tools { get; set; }
    public CapabilityState Reasoning { get; set; }
    /// <summary>Effort or thinking levels the model accepts, when declared.</summary>
    public List<string> ReasoningLevels { get; set; } = new();
    public CapabilityState WebSearch { get; set; }
    public CapabilityState Vision { get; set; }
    public CapabilityState StructuredOutput { get; set; }
    /// <summary>Endpoint the values were read from (shown as tooltip).</summary>
    public string Source { get; set; } = string.Empty;

    public bool AnyKnown => new[] { Tools, Reasoning, WebSearch, Vision, StructuredOutput }.Any(s => s != CapabilityState.Unknown);

    public static CapabilityState From(bool? value) => value switch
    {
        true => CapabilityState.Supported,
        false => CapabilityState.Unsupported,
        _ => CapabilityState.Unknown
    };
}

/// <summary>Limits (0 = not declared) and capabilities a provider declares for one model.</summary>
public sealed class ModelInfo
{
    public int ContextWindow { get; set; }
    public int MaxOutputTokens { get; set; }
    public ModelCapabilities Capabilities { get; set; } = new();
}

/// <summary>The functions the app can try on a model with synthetic data; Vision and Tools are only declared.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProbeKind { Text, Json, Reasoning, Web }

/// <summary>A timeout or a refused key is Inconclusive, never Unsupported: it says nothing about the model.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProbeOutcome { Supported, Unsupported, Inconclusive }

/// <summary>Outcome of one explicit probe, with what was observed (no secrets, no Outlook data).</summary>
public sealed class CapabilityProbeResult
{
    public ProbeKind Kind { get; set; }
    public ProbeOutcome Outcome { get; set; }
    public string Detail { get; set; } = string.Empty;
    public DateTime CheckedUtc { get; set; }
}

/// <summary>
/// Capabilities saved in the profile, valid only for the service, authentication, model and endpoint they were
/// read or probed with. Saved with the administrative save; a changed key clears the probes in the ConfigTool.
/// </summary>
public sealed class ModelCapabilitiesCache
{
    public string Type { get; set; } = string.Empty;
    public string AuthType { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>When the service's declaration was read; null when it publishes nothing.</summary>
    public DateTime? CheckedUtc { get; set; }
    public int ContextWindow { get; set; }
    public ModelCapabilities Capabilities { get; set; } = new();
    public List<CapabilityProbeResult> Probes { get; set; } = new();

    public static ModelCapabilitiesCache For(LlmProfileConfig profile) => new()
    {
        Type = profile.Type ?? string.Empty,
        AuthType = profile.AuthType ?? string.Empty,
        Model = profile.Model?.Trim() ?? string.Empty,
        BaseUrl = profile.BaseUrl?.Trim() ?? string.Empty
    };

    public bool Matches(LlmProfileConfig profile) =>
        string.Equals(Type, profile.Type, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(AuthType, profile.AuthType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Model, profile.Model?.Trim(), StringComparison.Ordinal) &&
        string.Equals(BaseUrl.TrimEnd('/'), profile.BaseUrl?.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public CapabilityProbeResult? Probe(ProbeKind kind) => Probes.LastOrDefault(p => p.Kind == kind);

    /// <summary>A newer probe of the same kind replaces the older one.</summary>
    public void Record(CapabilityProbeResult result)
    {
        Probes.RemoveAll(p => p.Kind == result.Kind);
        Probes.Add(result);
    }
}

/// <summary>Where a capability row's outcome comes from.</summary>
public enum CapabilityOrigin { NotDeclared, Declared, Verified, NotVerifiable, NotAvailable }

/// <summary>One row of «Avanzate» (capabilities): outcome, provenance and date, never a guess.</summary>
public sealed class CapabilityRow
{
    public CapabilityRow(string name, CapabilityState state, CapabilityOrigin origin, string detail, DateTime? checkedUtc)
    {
        Name = name;
        State = state;
        Origin = origin;
        Detail = detail;
        CheckedUtc = checkedUtc;
    }

    public string Name { get; }
    public CapabilityState State { get; }
    public CapabilityOrigin Origin { get; }
    public string Detail { get; }
    public DateTime? CheckedUtc { get; }

    public string OutcomeText => Origin switch
    {
        CapabilityOrigin.NotAvailable => "non disponibile",
        CapabilityOrigin.NotVerifiable => "non verificabile",
        CapabilityOrigin.NotDeclared => "non dichiarata",
        _ => State == CapabilityState.Supported ? "sì" : State == CapabilityState.Unsupported ? "no" : "non verificabile"
    };

    public string OriginText => Origin switch
    {
        CapabilityOrigin.Verified => "verificata dall'app",
        CapabilityOrigin.Declared => "dichiarata dal servizio",
        CapabilityOrigin.NotVerifiable => "prova inconcludente",
        CapabilityOrigin.NotAvailable => "connettore",
        _ => "nessuna dichiarazione né prova"
    };
}

public static class CapabilityReport
{
    /// <summary>
    /// Rows for the probed functions (a probe wins over the declaration), then Strumenti and Visione, only declared:
    /// the app has no general tool calling or image transport to try them with.
    /// </summary>
    public static IReadOnlyList<CapabilityRow> Rows(ModelCapabilitiesCache? cache, ModelCapabilities? declared, bool webAvailable)
    {
        declared ??= cache?.Capabilities;
        DateTime? declaredUtc = cache?.CheckedUtc;
        string source = declared is { AnyKnown: true } && !string.IsNullOrEmpty(declared.Source) ? "Fonte: " + declared.Source : string.Empty;

        CapabilityRow Probed(string name, ProbeKind kind, CapabilityState declaredState, string untested)
        {
            var probe = cache?.Probe(kind);
            // An inconclusive probe does not hide what the service declares.
            if (probe is { Outcome: ProbeOutcome.Inconclusive } && declaredState != CapabilityState.Unknown)
                return new CapabilityRow(name, declaredState, CapabilityOrigin.Declared,
                    (source.Length > 0 ? source + ". " : string.Empty) + "Prova inconcludente: " + probe.Detail, declaredUtc);
            if (probe != null)
            {
                return probe.Outcome == ProbeOutcome.Inconclusive
                    ? new CapabilityRow(name, CapabilityState.Unknown, CapabilityOrigin.NotVerifiable, probe.Detail, probe.CheckedUtc)
                    : new CapabilityRow(name, probe.Outcome == ProbeOutcome.Supported ? CapabilityState.Supported : CapabilityState.Unsupported,
                        CapabilityOrigin.Verified, probe.Detail, probe.CheckedUtc);
            }
            return declaredState != CapabilityState.Unknown
                ? new CapabilityRow(name, declaredState, CapabilityOrigin.Declared, source, declaredUtc)
                : new CapabilityRow(name, CapabilityState.Unknown, CapabilityOrigin.NotDeclared, untested, null);
        }

        CapabilityRow Declared(string name, CapabilityState state) =>
            state != CapabilityState.Unknown
                ? new CapabilityRow(name, state, CapabilityOrigin.Declared, source, declaredUtc)
                : new CapabilityRow(name, CapabilityState.Unknown, CapabilityOrigin.NotDeclared, "Il servizio non la dichiara e l'app non la prova.", null);

        const string Untested = "Da provare con «Diagnosi».";
        var rows = new List<CapabilityRow>
        {
            Probed("Risposta testuale", ProbeKind.Text, CapabilityState.Unknown, Untested),
            Probed("JSON strutturato", ProbeKind.Json, declared?.StructuredOutput ?? CapabilityState.Unknown, Untested),
            Probed("Ragionamento", ProbeKind.Reasoning, declared?.Reasoning ?? CapabilityState.Unknown, Untested),
            webAvailable
                ? Probed("Ricerca web", ProbeKind.Web, declared?.WebSearch ?? CapabilityState.Unknown, "Da provare con «Diagnosi».")
                : new CapabilityRow("Ricerca web", CapabilityState.Unsupported, CapabilityOrigin.NotAvailable, "Il connettore non offre Ricerca web.", null),
            Declared("Strumenti", declared?.Tools ?? CapabilityState.Unknown),
            Declared("Visione", declared?.Vision ?? CapabilityState.Unknown)
        };
        return rows;
    }
}
