using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace OnlyWorkspace.Core.Security;

/// <summary>Badge of the security check: only these values reach the pane, never text from the email or the model.</summary>
public enum SecurityOutcome { NoAnomaly, ToReview, Suspicious, Unverifiable }

/// <summary>
/// What an observation means for the verdict. Context explains (forwarding, PEC envelope, declared results) and never
/// changes the badge; Attention is a concrete element to check; Deception is masking or risky behaviour backed by
/// evidence the app read itself.
/// </summary>
public enum ObservationKind { Context, Attention, Deception }

/// <summary>Who says it: the app's own reading, a server's declaration in the headers, or the model's quoted reading.</summary>
public enum ObservationOrigin { Local, Declared, Model }

public enum ObservationSubject { Sender, Server, Link, Attachment, Script, Content }

public sealed class SecurityObservation
{
    public SecurityObservation(string code, ObservationKind kind, ObservationOrigin origin, ObservationSubject subject, string text, string? factKey = null)
    {
        Code = code;
        Kind = kind;
        Origin = origin;
        Subject = subject;
        Text = text;
        FactKey = factKey ?? code + "|" + text;
    }

    public string Code { get; }
    public ObservationKind Kind { get; }
    public ObservationOrigin Origin { get; }
    public ObservationSubject Subject { get; }
    public string Text { get; }
    /// <summary>One fact, however many checks describe it: two descriptions are not two confirmations.</summary>
    public string FactKey { get; }
    /// <summary>Where the evidence is: «Email», «allegato «x»», «allegato «x» › script.ps1, riga 3».</summary>
    public string? Source { get; set; }
    /// <summary>The passage that supports the observation, as written.</summary>
    public string? Quote { get; set; }
}

/// <summary>A check that could not be done or a content that could not be read; Partial marks the check as incomplete.</summary>
public sealed class SecurityLimit
{
    public SecurityLimit(string text, bool partial) { Text = text; Partial = partial; }
    public string Text { get; }
    public bool Partial { get; }
}

/// <summary>Microsoft Defender's own result on one attachment copy, always attributed to Defender.</summary>
public sealed class DefenderEntry
{
    public DefenderEntry(string file, AttachmentScanStatus status, string? detail) { File = file; Status = status; Detail = detail; }
    public string File { get; }
    public AttachmentScanStatus Status { get; }
    public string? Detail { get; }

    public string Text => Status switch
    {
        AttachmentScanStatus.Threat => $"Defender ha segnalato una minaccia in «{File}»" + (Detail != null ? $": {Detail}." : "."),
        AttachmentScanStatus.Clean => $"Defender non ha segnalato minacce in «{File}»: non certifica il file.",
        AttachmentScanStatus.Removed => $"La copia di «{File}» non è più disponibile: forse la protezione in tempo reale, causa non confermata.",
        AttachmentScanStatus.Failed => $"Scansione Defender di «{File}» non riuscita" + (Detail != null ? $" ({Detail})." : "."),
        _ => $"«{File}» non scansionato da Defender" + (Detail != null ? $" ({Detail})." : ".")
    };
}

public sealed class SecurityVerdict
{
    public SecurityOutcome Outcome { get; set; }
    public bool Partial { get; set; }
    /// <summary>At most two, the strongest first; empty when nothing emerged.</summary>
    public IReadOnlyList<string> Reasons { get; set; } = Array.Empty<string>();
    public string Action { get; set; } = string.Empty;

    public string Label => Labels(Outcome);

    public static string Labels(SecurityOutcome outcome) => outcome switch
    {
        SecurityOutcome.Suspicious => "Contenuto sospetto",
        SecurityOutcome.ToReview => "Da verificare",
        SecurityOutcome.Unverifiable => "Non verificabile",
        _ => "Nessuna anomalia rilevata"
    };

    /// <summary>
    /// Local rules on the report: the strongest kind decides, never a sum or a maximum of severities. A model
    /// observation is Attention at most, and only when its quote was found in the source it names.
    /// </summary>
    public static SecurityVerdict Decide(EmailSecurityReport report)
    {
        var facts = report.Observations.GroupBy(o => o.FactKey).Select(g => g.OrderByDescending(o => o.Kind).First()).ToList();
        var decisive = facts.Where(o => o.Kind == ObservationKind.Deception).ToList();
        var outcome = decisive.Count > 0 ? SecurityOutcome.Suspicious
            : (decisive = facts.Where(o => o.Kind == ObservationKind.Attention).ToList()).Count > 0 ? SecurityOutcome.ToReview
            : report.ContentAnalyzable ? SecurityOutcome.NoAnomaly : SecurityOutcome.Unverifiable;
        // Local evidence first: the model's reading never hides what the app saw.
        var ordered = decisive.OrderBy(o => o.Origin == ObservationOrigin.Model ? 1 : 0).ToList();
        return new SecurityVerdict
        {
            Outcome = outcome,
            Partial = report.Limits.Any(l => l.Partial),
            Reasons = ordered.Select(o => o.Text).Distinct().Take(2).ToList(),
            Action = ActionFor(outcome, ordered.FirstOrDefault())
        };
    }

    private static string ActionFor(SecurityOutcome outcome, SecurityObservation? first) => outcome switch
    {
        SecurityOutcome.Suspicious or SecurityOutcome.ToReview => first?.Subject switch
        {
            ObservationSubject.Attachment or ObservationSubject.Script => outcome == SecurityOutcome.Suspicious
                ? "Non aprire l’allegato; chiedi conferma al mittente tramite un recapito già noto."
                : "Apri l’allegato solo se lo aspettavi e dopo aver chiesto conferma al mittente tramite un recapito già noto.",
            ObservationSubject.Link => outcome == SecurityOutcome.Suspicious
                ? "Non aprire il link; raggiungi il sito digitando tu l’indirizzo che conosci."
                : "Prima di aprire il link controlla la destinazione; nel dubbio digita tu l’indirizzo che conosci.",
            ObservationSubject.Content => "Non fornire credenziali né modificare pagamenti senza una conferma tramite un recapito già noto.",
            _ => "Verifica il mittente tramite un recapito già noto prima di rispondere o agire."
        },
        SecurityOutcome.Unverifiable => "I controlli non hanno potuto leggere il contenuto: valutalo con cautela e, se serve, chiedi conferma al mittente.",
        _ => "Nessuna azione suggerita dai controlli eseguiti; resta valido il tuo giudizio su richieste inattese."
    };
}

/// <summary>Stage of the check as the pane shows it: «Verifica in corso» never anticipates a green badge.</summary>
public enum SecurityCheckState { Running, Done, Stopped }

/// <summary>The structured result shared by pane, replay and history. Values are fixed strings; texts are plain text.</summary>
public static class SecurityReportView
{
    public const int SchemaVersion = 1;

    public static SecurityReportData From(EmailSecurityReport report, SecurityCheckState state, string modelStatus)
    {
        var verdict = SecurityVerdict.Decide(report);
        return new SecurityReportData
        {
            V = SchemaVersion,
            State = state switch { SecurityCheckState.Running => "running", SecurityCheckState.Stopped => "stopped", _ => "done" },
            Outcome = verdict.Outcome switch
            {
                SecurityOutcome.Suspicious => "suspicious",
                SecurityOutcome.ToReview => "review",
                SecurityOutcome.Unverifiable => "unverifiable",
                _ => "none"
            },
            Partial = verdict.Partial || state == SecurityCheckState.Stopped,
            Reasons = verdict.Reasons.ToList(),
            Action = verdict.Action,
            Observations = report.Observations
                .GroupBy(o => o.FactKey).Select(g => g.OrderByDescending(o => o.Kind).First())
                .OrderByDescending(o => o.Kind)
                .Select(o => new SecurityObservationData
                {
                    Kind = o.Kind switch { ObservationKind.Deception => "deception", ObservationKind.Attention => "attention", _ => "context" },
                    Origin = o.Origin switch { ObservationOrigin.Declared => "declared", ObservationOrigin.Model => "model", _ => "local" },
                    Text = o.Text,
                    Source = o.Source,
                    Quote = o.Quote
                }).ToList(),
            Defender = report.Defender.Select(d => new SecurityDefenderData
            {
                Status = d.Status switch
                {
                    AttachmentScanStatus.Threat => "threat",
                    AttachmentScanStatus.Clean => "clean",
                    AttachmentScanStatus.Removed => "removed",
                    AttachmentScanStatus.Failed => "failed",
                    _ => "notscanned"
                },
                Text = d.Text
            }).ToList(),
            Limits = report.Limits.Select(l => l.Text).Distinct().ToList(),
            Model = modelStatus
        };
    }

    /// <summary>Plain Markdown of the same report, for history readers and copies.</summary>
    public static string ToMarkdown(SecurityReportData data)
    {
        var sb = new StringBuilder();
        sb.Append("**Esito: ").Append(SecurityReportLabel(data.Outcome)).Append("**");
        if (data.Partial) sb.Append(" · controllo parziale");
        sb.AppendLine().AppendLine();
        foreach (var reason in data.Reasons) sb.Append("- ").AppendLine(SecurityContentAnalysis.MarkdownText(reason));
        if (data.Action.Length > 0) sb.AppendLine().Append("**Cosa fare:** ").AppendLine(SecurityContentAnalysis.MarkdownText(data.Action));
        foreach (var d in data.Defender.Where(d => d.Status == "threat")) sb.AppendLine().Append("**Defender:** ").AppendLine(SecurityContentAnalysis.MarkdownText(d.Text));
        if (data.Observations.Count > 0)
        {
            sb.AppendLine().AppendLine("**Osservazioni**").AppendLine();
            foreach (var o in data.Observations)
            {
                sb.Append("- ").Append(o.Origin == "declared" ? "Dichiarato dal server · " : "")
                    .Append(SecurityContentAnalysis.MarkdownText(o.Text));
                if (o.Source != null) sb.Append(" — ").Append(SecurityContentAnalysis.MarkdownText(o.Source));
                if (o.Quote != null) sb.Append(": «").Append(SecurityContentAnalysis.MarkdownText(o.Quote)).Append('»');
                sb.AppendLine();
            }
        }
        if (data.Defender.Count > 0)
        {
            sb.AppendLine().AppendLine("**Microsoft Defender**").AppendLine();
            foreach (var d in data.Defender) sb.Append("- ").AppendLine(SecurityContentAnalysis.MarkdownText(d.Text));
        }
        if (data.Limits.Count > 0)
        {
            sb.AppendLine().AppendLine("**Limiti dei controlli**").AppendLine();
            foreach (var limit in data.Limits) sb.Append("- ").AppendLine(SecurityContentAnalysis.MarkdownText(limit));
        }
        if (data.Model.Length > 0) sb.AppendLine().AppendLine(SecurityContentAnalysis.MarkdownText(data.Model));
        return sb.ToString();
    }

    public static string SecurityReportLabel(string outcome) => outcome switch
    {
        "suspicious" => SecurityVerdict.Labels(SecurityOutcome.Suspicious),
        "review" => SecurityVerdict.Labels(SecurityOutcome.ToReview),
        "unverifiable" => SecurityVerdict.Labels(SecurityOutcome.Unverifiable),
        _ => SecurityVerdict.Labels(SecurityOutcome.NoAnomaly)
    };
}

public sealed class SecurityReportData
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "done";
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = "none";
    [JsonPropertyName("partial")] public bool Partial { get; set; }
    [JsonPropertyName("reasons")] public List<string> Reasons { get; set; } = new();
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("observations")] public List<SecurityObservationData> Observations { get; set; } = new();
    [JsonPropertyName("defender")] public List<SecurityDefenderData> Defender { get; set; } = new();
    [JsonPropertyName("limits")] public List<string> Limits { get; set; } = new();
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
}

public sealed class SecurityObservationData
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "context";
    [JsonPropertyName("origin")] public string Origin { get; set; } = "local";
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("quote")] public string? Quote { get; set; }
}

public sealed class SecurityDefenderData
{
    [JsonPropertyName("status")] public string Status { get; set; } = "notscanned";
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}
