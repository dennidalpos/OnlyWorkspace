using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Prompts;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>Only enum categories and generated references leave the PC. All values stay in this local map.</summary>
public sealed class CloudFactsRequest
{
    public const string BlockedMessage = "Invio cloud bloccato: il testo libero non ha una rappresentazione protetta verificabile. Usa un profilo locale per questa azione.";
    public const string Schema = "{\"type\":\"object\",\"properties\":{\"facts\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"facts\"],\"additionalProperties\":false}";
    private readonly Dictionary<string, (int Source, ArchiveFact Fact)> _facts = new(StringComparer.Ordinal);
    public IReadOnlyList<ChatMessage> Messages { get; }
    public PseudonymizationAudit Audit { get; } = new();

    public static bool IsFactRequest(string instruction)
    {
        string text = instruction.Trim().TrimEnd('.', '?').ToLowerInvariant();
        foreach (string suffix in new[] { " delle email", " delle mail", " della posta", " di questa email", " dell'email selezionata" })
            if (text.EndsWith(suffix, StringComparison.Ordinal)) { text = text.Substring(0, text.Length - suffix.Length); break; }
        return new[] { "elenca i fatti", "mostra i fatti", "elenca le scadenze", "mostra le scadenze", "elenca gli importi", "mostra gli importi" }
            .Contains(text, StringComparer.Ordinal);
    }

    public static ArchiveFactKind? RequestedKind(string instruction) =>
        instruction.IndexOf("scadenze", StringComparison.OrdinalIgnoreCase) >= 0 ? ArchiveFactKind.Deadline
        : instruction.IndexOf("importi", StringComparison.OrdinalIgnoreCase) >= 0 ? ArchiveFactKind.Amount : null;

    public CloudFactsRequest(MailEvidencePack pack, bool attachments = false, ArchiveFactKind? kind = null)
    {
        var rows = new List<object>();
        for (int i = 0; i < pack.Count; i++)
        {
            foreach (var fact in pack.Items[i].Mail.Facts.Where(f => f.Resolved && (!kind.HasValue || f.Kind == kind) && (attachments || f.SourceId.Length == 0) && Enum.IsDefined(typeof(ArchiveFactKind), f.Kind)))
            {
                string reference = "[FATTO_" + (_facts.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                // Snapshot: neither caller mutations nor a provider answer can change restored local values.
                var copy = JsonSerializer.Deserialize<ArchiveFact>(JsonSerializer.Serialize(fact))!;
                _facts.Add(reference, (i + 1, copy));
                rows.Add(new { source = i + 1, kind = fact.Kind.ToString(), value = reference });
                string category = fact.Kind.ToString().ToUpperInvariant();
                Audit.Counts.TryGetValue(category, out int count); Audit.Counts[category] = count + 1;
            }
        }
        if (_facts.Count == 0) throw new DataEgressBlockedException("Nessun fatto strutturato verificabile: nessun dato inviato. Usa un profilo locale.");
        Audit.Revisions["catalog"] = PseudonymizationCatalog.CurrentRevision;
        Audit.Revisions["cloud-contract"] = "1";
        var proof = new CloudRequestProof(Schema, Audit);
        Messages = Array.AsReadOnly(PromptCatalog.CreateCloudFactsPrompt(JsonSerializer.Serialize(rows))
            .Select(m => new ChatMessage(m.Role, m.Content, proof)).ToArray());
    }

    public MailAnswer ReadAnswer(string json)
    {
        var refs = ReadReferences(json);
        var answer = new MailAnswer();
        var section = new MailAnswerSection { Title = "Fatti verificati sul PC" };
        foreach (string reference in refs)
        {
            var local = _facts[reference];
            var point = new MailAnswerPoint { Text = local.Fact.Kind + ": " + local.Fact.Raw };
            point.Sources.Add(local.Source); section.Points.Add(point);
        }
        answer.Sections.Add(section);
        return answer;
    }

    public ExtractedActionItems ReadActionItems(string json)
    {
        var items = new ExtractedActionItems();
        foreach (var reference in ReadReferences(json))
        {
            var local = _facts[reference];
            var f = local.Fact;
            bool appointment = f.Kind == ArchiveFactKind.Calendar && f.Label == "DTSTART";
            if (!appointment && f.Kind != ArchiveFactKind.Deadline) continue;
            var date = ActionItemParser.ParseDate(appointment ? f.Value ?? f.Date ?? "" : f.Date ?? f.Value ?? "", out bool allDay);
            if (!date.HasValue) continue;
            items.Add(new ExtractedActionItem
            {
                Source = local.Source,
                Kind = appointment ? "appointment" : "task",
                Title = string.IsNullOrWhiteSpace(f.Raw) ? (appointment ? "Appuntamento dalla mail" : "Scadenza dalla mail") : f.Raw,
                DateText = f.Raw,
                Start = appointment ? date : null,
                Due = appointment ? null : date,
                AllDay = allDay,
                End = appointment ? allDay ? date.Value.AddDays(1) : date.Value.AddHours(1) : null
            });
        }
        return items;
    }

    private List<string> ReadReferences(string json)
    {
        using var doc = ModelOutput.ParseObject(json, "facts");
        var values = doc.RootElement.GetProperty("facts");
        if (values.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Risposta protetta non valida.");
        var refs = values.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : "").ToList();
        if (refs.Count != _facts.Count || refs.Distinct(StringComparer.Ordinal).Count() != refs.Count || refs.Any(r => !_facts.ContainsKey(r)))
            throw new InvalidOperationException("Risposta protetta incompleta o con riferimenti sconosciuti: riprova.");
        return refs;
    }
}

internal sealed class CloudRequestProof
{
    internal string? Schema { get; }
    internal IReadOnlyDictionary<string, int> Counts { get; }
    /// <summary>The text went through the mechanical masking; valid only for levels that send masked free text.</summary>
    internal bool Masked { get; }
    internal CloudRequestProof(string? schema, PseudonymizationAudit? audit = null, bool masked = false)
    {
        Schema = schema;
        Masked = masked;
        Counts = new Dictionary<string, int>(audit?.Counts ?? new Dictionary<string, int>(), StringComparer.Ordinal);
    }
}

public static class CloudRequestGuard
{
    public const string MaskedBlockedMessage = "Invio cloud bloccato: il testo non ha superato la pseudonimizzazione meccanica. Nessun dato è stato inviato.";

    /// <summary>True when the messages are an approved typed-facts request (they must reach the transport unchanged).</summary>
    public static bool HasProof(IEnumerable<ChatMessage> messages) => messages.Any(m => m.CloudProof != null);

    public static IReadOnlyDictionary<string, int> Counts(IEnumerable<ChatMessage> messages) =>
        messages.FirstOrDefault()?.CloudProof?.Counts ?? new Dictionary<string, int>();

    /// <summary>Transport check: a cloud request carries the typed-facts proof, or the masking proof at a level that sends masked text.</summary>
    public static List<ChatMessage> Validate(LlmProfileConfig profile, IEnumerable<ChatMessage> messages, string? schema) =>
        Validate(profile, messages, schema, beforeMasking: false);

    /// <summary>
    /// With beforeMasking the dispatcher, which masks next, also accepts free text at Standard or Extended;
    /// every client revalidates without it, so unmasked text can never reach a transport.
    /// </summary>
    public static List<ChatMessage> Validate(LlmProfileConfig profile, IEnumerable<ChatMessage> messages, string? schema, bool beforeMasking)
    {
        var list = messages.ToList();
        if (!profile.IsExternal) return list;
        bool maskedText = ExposurePolicy.Of(profile).MaskedText;
        var proof = list.FirstOrDefault()?.CloudProof;
        if (proof == null)
        {
            if (beforeMasking && maskedText && list.All(m => m.CloudProof == null)) return list;
            throw new DataEgressBlockedException(CloudFactsRequest.BlockedMessage);
        }
        if (!SameSchema(proof.Schema, schema) || list.Any(m => !ReferenceEquals(m.CloudProof, proof)) || (proof.Masked && !maskedText))
            throw new DataEgressBlockedException(CloudFactsRequest.BlockedMessage);
        return list;
    }

    // A client may re-serialize the schema (spacing, escaping); the structure and key order must stay the same.
    private static bool SameSchema(string? sealedSchema, string? sent)
    {
        if (string.Equals(sealedSchema, sent, StringComparison.Ordinal)) return true;
        if (sealedSchema == null || sent == null) return false;
        try
        {
            using var a = JsonDocument.Parse(sealedSchema);
            using var b = JsonDocument.Parse(sent);
            return JsonSerializer.Serialize(a.RootElement) == JsonSerializer.Serialize(b.RootElement);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Called by the dispatcher with the output of Pseudonymizer.Mask: marks exactly those messages as sendable.</summary>
    public static IReadOnlyList<ChatMessage> SealMasked(LlmProfileConfig profile, IReadOnlyList<ChatMessage> masked, string? schema, Pseudonymizer mask)
    {
        if (!profile.IsExternal || !ExposurePolicy.Of(profile).MaskedText) throw new DataEgressBlockedException(CloudFactsRequest.BlockedMessage);
        // Only the output of this very map counts as masked: raw or foreign messages cannot be sealed.
        if (mask == null || masked.Count == 0 || masked.Any(m => !mask.Produced(m))) throw new DataEgressBlockedException(MaskedBlockedMessage);
        var proof = new CloudRequestProof(schema, mask.Audit, masked: true);
        return Array.AsReadOnly(masked.Select(m => new ChatMessage(m.Role, m.Content, proof)).ToArray());
    }

    /// <summary>Fixed synthetic inputs only. No caller-provided text can become a probe.</summary>
    public static IReadOnlyList<ChatMessage> Probe(bool structured = false, bool reasoning = false)
    {
        var proof = new CloudRequestProof(structured ? ProbeSchema : null);
        string text = structured ? "Il semaforo della prova è verde e il numero della prova è 7. Riporta colore e numero."
            : reasoning ? "Un treno parte alle 9:40 e arriva alle 11:15. Quanti minuti dura il viaggio? Rispondi solo con il numero."
            : "Rispondi soltanto con OK.";
        return new[] { new ChatMessage("user", text, proof) };
    }
    public const string ProbeSchema = "{\"type\":\"object\",\"properties\":{\"colore\":{\"type\":\"string\",\"enum\":[\"rosso\",\"giallo\",\"verde\"]},\"numero\":{\"type\":\"integer\"}},\"required\":[\"colore\",\"numero\"],\"additionalProperties\":false}";
}
