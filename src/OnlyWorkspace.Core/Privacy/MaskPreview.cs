using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>One mask of the outgoing text: the placeholder, its category and the original value, which never leaves the PC.</summary>
public sealed class MaskPreviewItem
{
    public string Placeholder { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Original { get; set; } = string.Empty;
}

/// <summary>What the user sees before attachment text goes to an Extended profile (#249): the masked text and the masks.</summary>
public sealed class MaskPreview
{
    /// <summary>The masked parts of the request that carry attachment text.</summary>
    public IReadOnlyList<string> Texts { get; set; } = Array.Empty<string>();
    public IReadOnlyList<MaskPreviewItem> Masks { get; set; } = Array.Empty<MaskPreviewItem>();
}

/// <summary>The user's answer. Without confirmation nothing is sent.</summary>
public sealed class MaskPreviewDecision
{
    public bool Confirmed { get; set; }
    /// <summary>Placeholders to show again as originals.</summary>
    public IReadOnlyCollection<string> Unmask { get; set; } = Array.Empty<string>();
    /// <summary>Literal values to mask in addition.</summary>
    public IReadOnlyCollection<string> AddValues { get; set; } = Array.Empty<string>();

    public static MaskPreviewDecision Cancel() => new();
}

/// <summary>The user declined the preview: a stop, not a model failure.</summary>
public sealed class MaskPreviewCancelledException : OperationCanceledException
{
    public MaskPreviewCancelledException() : base("Invio annullato dall'anteprima del mascheramento.") { }
}

public sealed partial class Pseudonymizer
{
    /// <summary>Marker the pane writes before each attachment's text.</summary>
    public const string AttachmentMarker = "[Allegato: ";
    /// <summary>Audit key counting manual edits; never a value.</summary>
    public const string ManualEditsKey = "MODIFICHE";
    private const string ManualCategory = "MANUALE";
    private const int MaxManualValue = 200, MaxManualEdits = 200;

    public static bool CarriesAttachmentText(IEnumerable<ChatMessage> messages) =>
        messages.Any(m => m.Content.IndexOf(AttachmentMarker, StringComparison.Ordinal) >= 0);

    /// <summary>Masked attachment text and the masks that exist in it, ready to show.</summary>
    public MaskPreview BuildPreview(IEnumerable<ChatMessage> masked)
    {
        var texts = masked.Select(m => m.Content).Where(c => c.IndexOf(AttachmentMarker, StringComparison.Ordinal) >= 0)
            .Select(c => c.Substring(c.IndexOf(AttachmentMarker, StringComparison.Ordinal))).ToList();
        var masks = _byPlaceholder.Where(kv => texts.Any(t => t.Contains(kv.Key))).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new MaskPreviewItem { Placeholder = kv.Key, Category = kv.Key.Substring(1, kv.Key.LastIndexOf('_') - 1), Original = kv.Value }).ToList();
        return new MaskPreview { Texts = texts, Masks = masks };
    }

    /// <summary>
    /// Applies the user's edits to text this map produced: listed placeholders show their original again, listed values
    /// become new masks. The audit gets the number of edits only. The result is accepted by CloudRequestGuard.SealMasked.
    /// </summary>
    public IReadOnlyList<ChatMessage> ApplyManualEdits(IReadOnlyList<ChatMessage> masked, IEnumerable<string>? unmask, IEnumerable<string>? addValues)
    {
        var undo = (unmask ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).Where(p => _byPlaceholder.ContainsKey(p)).ToList();
        var add = (addValues ?? Array.Empty<string>()).Select(v => v?.Trim() ?? string.Empty).Where(v => v.Length >= 2 && v.Length <= MaxManualValue && v.IndexOfAny(new[] { '\r', '\n' }) < 0)
            .Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToList();
        if (undo.Count + add.Count > MaxManualEdits) throw new InvalidOperationException("Too many manual edits.");
        if (undo.Count + add.Count == 0) return masked;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<ChatMessage>();
        foreach (var message in masked)
        {
            string text = message.Content;
            foreach (var placeholder in undo)
                if (text.Contains(placeholder)) { text = text.Replace(placeholder, _byPlaceholder[placeholder]); changed.Add("u\0" + placeholder); }
            foreach (var value in add)
            {
                // Placeholders already in the text are never cut: only the plain parts are searched.
                var parts = new List<string>(); int position = 0;
                foreach (System.Text.RegularExpressions.Match p in Placeholder.Matches(text))
                {
                    parts.Add(text.Substring(position, p.Index - position)); parts.Add(p.Value); position = p.Index + p.Length;
                }
                parts.Add(text.Substring(position));
                for (int i = 0; i < parts.Count; i += 2)
                    if (parts[i].Contains(value)) { parts[i] = parts[i].Replace(value, ManualPlaceholder(value)); changed.Add("a\0" + value); }
                text = string.Concat(parts);
            }
            output.Add(new ChatMessage(message.Role, text));
        }
        // One edit per user choice that changed the text, never the value.
        int edits = changed.Count;
        Audit.Counts.TryGetValue(ManualEditsKey, out int before); Audit.Counts[ManualEditsKey] = checked(before + edits);
        foreach (var message in output) _produced.Add(message);
        return output;
    }

    private string ManualPlaceholder(string value)
    {
        string key = ManualCategory + "\0" + value;
        if (_byValue.TryGetValue(key, out var existing)) return existing;
        _counters.TryGetValue(ManualCategory, out int n); string placeholder;
        do { placeholder = $"[{ManualCategory}_{++n}]"; } while (_reserved.Contains(placeholder));
        _counters[ManualCategory] = n; _byValue[key] = placeholder; _byPlaceholder[placeholder] = value;
        return placeholder;
    }
}
