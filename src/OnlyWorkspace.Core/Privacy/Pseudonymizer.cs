using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>One reversible map per request. The map never enters the audit record.</summary>
public sealed class Pseudonymizer
{
    private static readonly Regex Placeholder = new(@"\[[A-Z][A-Z0-9]{1,15}_\d+\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex ProtectedToken = new(@"\[[A-Z][A-Z0-9_]*\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly JsonSerializerOptions JsonText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Dictionary<string, string> _byValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byPlaceholder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);
    private readonly PseudonymizationCatalog _catalog;
    private readonly IReadOnlyList<IPseudonymizationStrategy> _strategies;
    private readonly PrivacyEntity[] _entities;
    private readonly bool _amounts, _dates;
    private readonly TimeSpan? _budget;
    private bool _used;
    public int Count => _byPlaceholder.Count;
    public PseudonymizationAudit Audit { get; } = new();

    public Pseudonymizer(IEnumerable<PrivacyEntity>? entities = null, bool maskAmounts = false, bool maskDates = false,
        PseudonymizationCatalog? catalog = null, IEnumerable<IPseudonymizationStrategy>? strategies = null, TimeSpan? budget = null)
    {
        _catalog = catalog ?? PseudonymizationCatalog.Load(); _catalog.Validate();
        _strategies = (strategies ?? _catalog.Strategies()).OrderByDescending(s => s.Priority).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        if (_strategies.Count == 0 || _strategies.Any(s => string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Revision)) ||
            _strategies.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != _strategies.Count)
            throw new DataEgressBlockedException("Strategie di pseudonimizzazione non valide: nessun dato inviato.");
        _entities = (entities ?? Array.Empty<PrivacyEntity>()).Select(e => new PrivacyEntity { Id = e.Id, Category = e.Category, Value = e.Value }).ToArray();
        _amounts = maskAmounts; _dates = maskDates; _budget = budget;
        Audit.Revisions["catalog"] = _catalog.Revision;
        foreach (var strategy in _strategies) Audit.Revisions[strategy.Id] = strategy.Revision;
    }

    public IReadOnlyList<ChatMessage> Mask(IEnumerable<ChatMessage> messages) => Mask(messages, default);
    public IReadOnlyList<ChatMessage> Mask(IEnumerable<ChatMessage> messages, CancellationToken token)
    {
        if (_used) throw new InvalidOperationException("Use a new map for every request.");
        _used = true;
        try
        {
            var context = new PseudonymizationContext(_entities, _amounts, _dates, token, _budget); context.Check();
            var list = messages.ToArray(); var entities = new List<PrivacyEntity>(_entities);
            var names = PseudonymizationCatalog.Regex(_catalog.NamePattern);
            foreach (var message in list)
            {
                context.Check();
                foreach (Match p in ProtectedToken.Matches(message.Content)) _reserved.Add(p.Value);
                foreach (Match match in names.Matches(message.Content))
                {
                    context.Check();
                    var words = match.Groups["name"].Value.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    while (words.Count > 2 && _catalog.NameOpeners.Contains(words[0].TrimEnd('.', ':'), StringComparer.OrdinalIgnoreCase)) words.RemoveAt(0);
                    if (words.Count >= 2) entities.Add(new PrivacyEntity { Value = string.Join(" ", words) });
                }
            }
            // Keep the same deadline for discovery and detection.
            var detected = new PseudonymizationContext(entities.GroupBy(e => e.Category + "\0" + e.Value, StringComparer.OrdinalIgnoreCase).Select(g => g.First()), _amounts, _dates, token, _budget);
            var output = new List<ChatMessage>();
            foreach (var message in list) { context.Check(); output.Add(new ChatMessage(message.Role, MaskText(message.Content, detected, context))); }
            context.Check(); return output;
        }
        catch (OperationCanceledException) { Audit.Outcome = "cancelled"; throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Audit.Outcome = "blocked"; throw new DataEgressBlockedException("Pseudonimizzazione non completata: la richiesta non è stata inviata."); }
    }

    private string MaskText(string text, PseudonymizationContext detected, PseudonymizationContext deadline)
    {
        var protectedSpans = ProtectedToken.Matches(text).Cast<Match>().Select(m => (Start: m.Index, End: m.Index + m.Length)).ToArray();
        var candidates = new List<(PseudonymMatch Match, int Priority, string Strategy)>();
        foreach (var strategy in _strategies)
        {
            deadline.Check();
            foreach (var match in strategy.Detect(text, detected))
            {
                deadline.Check();
                if (match.Start < 0 || match.Length <= 0 || match.Start > text.Length - match.Length || !PseudonymizationCatalog.ValidCategory(match.Category)) throw new InvalidOperationException("Invalid privacy match.");
                if (!protectedSpans.Any(p => match.Start < p.End && match.Start + match.Length > p.Start)) candidates.Add((match, strategy.Priority, strategy.Id));
            }
        }
        var chosen = new List<PseudonymMatch>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Priority).ThenByDescending(c => c.Match.Length).ThenBy(c => c.Match.Start).ThenBy(c => c.Strategy, StringComparer.Ordinal))
        {
            deadline.Check(); var m = candidate.Match;
            if (!chosen.Any(p => m.Start < p.Start + p.Length && m.Start + m.Length > p.Start)) chosen.Add(m);
        }
        var output = new StringBuilder(); int position = 0;
        foreach (var match in chosen.OrderBy(m => m.Start))
        {
            deadline.Check(); output.Append(text, position, match.Start - position);
            string value = text.Substring(match.Start, match.Length), key = match.Category + "\0" + value;
            if (!_byValue.TryGetValue(key, out var placeholder))
            {
                _counters.TryGetValue(match.Category, out int n);
                do { placeholder = $"[{match.Category}_{++n}]"; } while (_reserved.Contains(placeholder));
                _counters[match.Category] = n; _byValue[key] = placeholder; _byPlaceholder[placeholder] = value;
            }
            Audit.Counts.TryGetValue(match.Category, out int count); Audit.Counts[match.Category] = checked(count + 1);
            output.Append(placeholder); position = match.Start + match.Length;
        }
        output.Append(text, position, text.Length - position); return output.ToString();
    }

    public string Restore(string text) => Placeholder.Replace(text, m => _byPlaceholder.TryGetValue(m.Value, out var v) ? v : m.Value);
    public string RestoreJson(string json) => Placeholder.Replace(json, m =>
    {
        if (!_byPlaceholder.TryGetValue(m.Value, out var value)) return m.Value;
        string quoted = JsonSerializer.Serialize(value, JsonText);
        return quoted.Substring(1, quoted.Length - 2);
    });
    public sealed class StreamRestorer
    {
        private readonly Pseudonymizer _owner;
        private readonly StringBuilder _pending = new();
        public StreamRestorer(Pseudonymizer owner) => _owner = owner;
        public string Push(string chunk)
        {
            _pending.Append(chunk); string text = _pending.ToString(); int open = text.LastIndexOf('[');
            int keep = open >= 0 && text.IndexOf(']', open) < 0 && text.Length - open <= 40 ? text.Length - open : 0;
            _pending.Clear(); if (keep > 0) _pending.Append(text, text.Length - keep, keep);
            return _owner.Restore(text.Substring(0, text.Length - keep));
        }
        public string Flush() { string text = _owner.Restore(_pending.ToString()); _pending.Clear(); return text; }
    }
}
