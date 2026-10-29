using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>
/// Replaces personal data in a request with stable placeholders ([PERSONA_1], [EMAIL_1], [TEL_1], [IBAN_1],
/// [PIVA_1], [CF_1]) and puts the values back in the answer, on this PC only. One instance per request; the map
/// lives in memory. It lowers what reaches the provider but does not anonymize: the data stays personal (GDPR,
/// EDPB draft guidelines 02/2026). Detection is pattern based: names are recognized next to an email address
/// («Mario Rossi &lt;m.rossi@…&gt;») and then everywhere in the request; a name written alone elsewhere is not.
/// </summary>
public sealed class Pseudonymizer
{
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", Options, Timeout);
    private static readonly Regex NamedAddress = new(
        @"(?<name>\p{Lu}[\p{L}'’\-]+(?:[ \t]+(?:d[aeiu]|de|del|della|van|von|di|la|le|\p{Lu}[\p{L}'’\-]+)){1,3})[ \t]*[<(\[]\s*(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})",
        Options, Timeout);
    private static readonly Regex Iban = new(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]){11,30}\b", Options, Timeout);
    private static readonly Regex TaxCode = new(@"\b[A-Za-z]{6}\d{2}[A-Za-z]\d{2}[A-Za-z]\d{3}[A-Za-z]\b", Options, Timeout);
    private static readonly Regex Vat = new(@"\b(?:IT)?\d{11}\b", Options, Timeout);
    // Italian mobile (3xx) and landline (0x) numbers, with an optional country code; not dates or amounts (no / and no digit after . or ,).
    private static readonly Regex Phone = new(
        @"(?<![\w/]|\d[.,])(?:(?:\+|00)\d{1,3}[ .\-]?)?(?:3\d{2}|0\d{1,3})[ .\-]?\d{3,4}[ .\-]?\d{3,4}(?![\w/]|[.,]\d)", Options, Timeout);
    /// <summary>Capitalized words that open a sentence or a header before a name, never part of it.</summary>
    private static readonly HashSet<string> Openers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Da", "A", "Cc", "Ccn", "Per", "From", "To", "Gentile", "Gentili", "Caro", "Cara", "Cari", "Egregio", "Egregia", "Spett", "Spettabile",
        "Dear", "Hi", "Hello", "Ciao", "Salve", "Buongiorno", "Buonasera", "Dott", "Dott.ssa", "Ing", "Avv", "Sig", "Sig.ra", "Mittente", "Destinatario"
    };

    private static readonly Regex Placeholder = new(@"\[(?:PERSONA|EMAIL|TEL|IBAN|PIVA|CF)_\d+\]", Options, Timeout);

    private static readonly JsonSerializerOptions JsonText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Dictionary<string, string> _byValue = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byPlaceholder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    /// <summary>Values replaced so far.</summary>
    public int Count => _byPlaceholder.Count;

    /// <summary>
    /// Masks every message of one request with the same map. A pattern that times out stops the request:
    /// nothing is sent unmasked.
    /// </summary>
    public IReadOnlyList<ChatMessage> Mask(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        try
        {
            // Names first, from every message: the same person is masked everywhere in the request.
            foreach (var message in list)
                foreach (Match m in NamedAddress.Matches(message.Content))
                {
                    var words = m.Groups["name"].Value.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    while (words.Count > 2 && Openers.Contains(words[0].TrimEnd('.', ':'))) words.RemoveAt(0);
                    if (words.Count >= 2) PlaceholderFor("PERSONA", string.Join(" ", words));
                    PlaceholderFor("EMAIL", m.Groups["email"].Value);
                }
            return list.Select(m => new ChatMessage(m.Role, Mask(m.Content))).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            throw new DataEgressBlockedException("Dati personali non sostituiti in tempo con i segnaposto: la richiesta non è stata inviata.");
        }
    }

    private string Mask(string text)
    {
        // Known names, longest first, as whole words.
        foreach (var name in _byValue.Where(p => p.Value.StartsWith("[PERSONA_", StringComparison.Ordinal)).Select(p => p.Key).OrderByDescending(n => n.Length))
            text = Regex.Replace(text, @"(?<![\p{L}\p{Nd}])" + Regex.Escape(name) + @"(?![\p{L}\p{Nd}])", _byValue[name], RegexOptions.IgnoreCase | Options, Timeout);
        text = Email.Replace(text, m => PlaceholderFor("EMAIL", m.Value));
        text = Iban.Replace(text, m => PlaceholderFor("IBAN", m.Value));
        text = TaxCode.Replace(text, m => PlaceholderFor("CF", m.Value));
        text = Vat.Replace(text, m => PlaceholderFor("PIVA", m.Value));
        text = Phone.Replace(text, m => PlaceholderFor("TEL", m.Value));
        return text;
    }

    private string PlaceholderFor(string kind, string value)
    {
        if (_byValue.TryGetValue(value, out var existing)) return existing;
        _counters.TryGetValue(kind, out int n);
        _counters[kind] = ++n;
        string placeholder = $"[{kind}_{n}]";
        _byValue[value] = placeholder;
        _byPlaceholder[placeholder] = value;
        return placeholder;
    }

    /// <summary>The answer with the original values; a placeholder the request did not contain stays as written.</summary>
    public string Restore(string text) =>
        _byPlaceholder.Count == 0 ? text : Placeholder.Replace(text, m => _byPlaceholder.TryGetValue(m.Value, out var v) ? v : m.Value);

    /// <summary>A JSON answer with the original values escaped as JSON string content.</summary>
    public string RestoreJson(string json) =>
        _byPlaceholder.Count == 0 ? json : Placeholder.Replace(json, m =>
            _byPlaceholder.TryGetValue(m.Value, out var v) ? JsonSerializer.Serialize(v, JsonText).Trim('"') : m.Value);

    /// <summary>Restores a streamed answer whose placeholders may be split between chunks.</summary>
    public sealed class StreamRestorer
    {
        private const int Longest = 16; // "[PERSONA_99999]"
        private readonly Pseudonymizer _owner;
        private readonly StringBuilder _pending = new();

        public StreamRestorer(Pseudonymizer owner) => _owner = owner;

        /// <summary>The text that can be shown now; an opening bracket that may start a placeholder waits for the next chunk.</summary>
        public string Push(string chunk)
        {
            _pending.Append(chunk);
            string text = _pending.ToString();
            int open = text.LastIndexOf('[');
            int keep = open >= 0 && text.IndexOf(']', open) < 0 && text.Length - open < Longest ? text.Length - open : 0;
            _pending.Clear();
            if (keep > 0) _pending.Append(text, text.Length - keep, keep);
            return _owner.Restore(text.Substring(0, text.Length - keep));
        }

        public string Flush()
        {
            string rest = _owner.Restore(_pending.ToString());
            _pending.Clear();
            return rest;
        }
    }
}
