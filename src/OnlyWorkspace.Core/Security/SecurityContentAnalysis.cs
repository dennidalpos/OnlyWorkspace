using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Prompts;

namespace OnlyWorkspace.Core.Security;

/// <summary>An original the model may quote: the email, one of its links or an attachment's text.</summary>
public sealed class SecuritySource
{
    public SecuritySource(string id, string label, string text) { Id = id; Label = label; Text = text; }
    public string Id { get; }
    public string Label { get; }
    public string Text { get; }
}

/// <summary>A quoted reading the model proposes. It is never a severity: the app decides what the category means.</summary>
public sealed class SecurityContentFinding
{
    public SecurityContentFinding(string category, string reason, SecuritySource source, string quote)
    { Category = category; Reason = reason; Source = source; Quote = quote; }
    public string Category { get; }
    public string Reason { get; }
    public SecuritySource Source { get; }
    public string Quote { get; }

    /// <summary>Requests of credentials or payments, claims of identity and deceptive link texts need checking;
    /// urgency or other remarks alone are context (a legitimate request can be urgent).</summary>
    public ObservationKind Kind => Category is "credentials" or "payment" or "identity" or "link" ? ObservationKind.Attention : ObservationKind.Context;
}

public sealed class SecurityContentAssessment
{
    public List<SecurityContentFinding> Findings { get; } = new();
    public int RejectedFindings { get; internal set; }

    /// <summary>The model's quoted readings as observations of the report; a found quote proves the words, not the reading.</summary>
    public void AddTo(EmailSecurityReport report)
    {
        foreach (var finding in Findings)
            report.Observe("model." + finding.Category, finding.Kind, ObservationOrigin.Model, ObservationSubject.Content,
                "Lettura IA: " + finding.Reason.Trim(), "model:" + finding.Category + ":" + finding.Source.Id + ":" + finding.Quote,
                finding.Source.Label, finding.Quote);
        if (RejectedFindings > 0)
            report.Limit("Lettura IA parziale: scartate osservazioni con categoria, fonte o citazione non valide.", partial: true);
    }
}

public sealed class SecurityReviewResult
{
    public SecurityReviewResult(SecurityContentAssessment assessment, string status, bool completed)
    { Assessment = assessment; Status = status; Completed = completed; }
    public SecurityContentAssessment Assessment { get; }
    public string Status { get; }
    public bool Completed { get; }
}

public static class SecurityContentAnalysis
{
    /// <summary>Citable originals only; the app's own checks travel separately as context.</summary>
    public static List<SecuritySource> CreateSources(string subject, string senderName, string senderAddress, string body, EmailSecurityReport report)
    {
        var sources = new List<SecuritySource>
        {
            new("email", "Email", "Oggetto: " + subject + "\nDa: " + senderName + " <" + senderAddress + ">\n\n" + body)
        };
        for (int i = 0; i < report.Links.Count; i++)
            sources.Add(new("link:" + i, "Link " + (i + 1), "Testo: " + report.Links[i].Text + "\nDestinazione: " + report.Links[i].Destination));
        return sources;
    }

    public static bool AttachmentContentAllowed(LlmProfileConfig profile, AttachmentFacts facts) =>
        profile.SendsAttachmentText && facts.ScanStatus is not AttachmentScanStatus.Threat and not AttachmentScanStatus.Removed;

    public static async Task<SecurityReviewResult> ReviewAsync(
        LlmProfileConfig profile,
        Func<CancellationToken, Task<IReadOnlyList<SecuritySource>>> prepareSources,
        string context,
        Func<ILlmClient> createClient, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sources = await prepareSources(ct);
        var sourceJson = JsonSerializer.Serialize(sources.Select(s => new { id = s.Id, label = s.Label, text = s.Text }));
        var messages = PromptCatalog.CreateSecurityPrompt(sourceJson, context).ToList();
        if (messages.Sum(m => (long)m.Content.Length) > ContextBuilder.MaxChars(ProviderCatalog.PromptBudget(profile)))
            return new SecurityReviewResult(new(), "Lettura IA non eseguita: email, allegati e istruzioni interi superano il contesto del profilo. Nessun testo tagliato.", completed: false);
        ct.ThrowIfCancellationRequested();
        var client = createClient();
        try
        {
            // Native JSON schema where the provider has one (categories fixed by the app).
            var output = await client.CompleteStructuredAsync(messages, PromptCatalog.SecurityJsonSchema, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (client.LastCompletion?.IsTruncated == true) throw new InvalidOperationException("Risposta IA incompleta: limite di output del modello raggiunto.");
            return new SecurityReviewResult(Parse(output, sources),
                "Lettura IA del contenuto completata: le citazioni sono state ritrovate nelle fonti, le interpretazioni restano da valutare.", completed: true);
        }
        finally { (client as IDisposable)?.Dispose(); }
    }

    public static SecurityContentAssessment Parse(string json, IReadOnlyList<SecuritySource> sources)
    {
        json = json.Trim();
        if (json.StartsWith("```json", StringComparison.OrdinalIgnoreCase) && json.EndsWith("```", StringComparison.Ordinal)) json = json.Substring(7, json.Length - 10).Trim();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
            throw new JsonException("Risposta IA non valida: manca l’elenco delle osservazioni.");
        var result = new SecurityContentAssessment();
        var byId = sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in findings.EnumerateArray())
        {
            string? Field(string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var category = Field("category");
            var reason = Field("reason");
            var sourceId = Field("sourceId");
            var quote = Field("quote");
            // Only an original can be quoted: the app's context and unknown ids are refused, as is a quote not in that source.
            if (category == null || Array.IndexOf(PromptCatalog.SecurityCategories, category) < 0 ||
                string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(quote) || sourceId == null ||
                !byId.TryGetValue(sourceId, out var source) || source.Text.IndexOf(quote!, StringComparison.Ordinal) < 0)
            { result.RejectedFindings++; continue; }
            if (seen.Add(sourceId + "\n" + quote + "\n" + category)) result.Findings.Add(new(category, reason!, source, quote!));
        }
        return result;
    }

    // Email and model text must remain evidence, not Markdown links, images or HTML.
    public static string MarkdownText(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ");
        foreach (var character in new[] { "\\", "`", "*", "_", "[", "]", "#", "!", "|", "<", ">" }) text = text.Replace(character, "\\" + character);
        return text;
    }
}
