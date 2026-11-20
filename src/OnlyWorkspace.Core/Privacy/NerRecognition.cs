using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>One entity found by the local recognizer: offsets inside the block it was given, plus the model's own label.</summary>
public sealed class NerSpan
{
    public int Start { get; set; }
    public int Length { get; set; }
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// The second masking layer (#247). Any failure must surface as an exception: the caller turns it into
/// <see cref="DataEgressBlockedException"/> and nothing is sent.
/// </summary>
public interface INerRecognizer
{
    /// <summary>Identity of the verified model; reading it fails when the model is missing or its hash differs.</summary>
    string ModelRevision { get; }
    IReadOnlyList<NerSpan> Recognize(string block, TimeSpan budget, CancellationToken token);
}

/// <summary>
/// Adds entities the rules cannot see (people and companies in free text, addresses without a keyword). It only adds:
/// <see cref="Pseudonymizer"/> keeps every rule match that overlaps an entity because rules have a higher priority.
/// </summary>
internal sealed class NerStrategy : IPseudonymizationStrategy
{
    internal const int BlockChars = 2000;
    internal const int OverlapChars = 200;
    private static readonly Regex AddressGap = PseudonymizationCatalog.Regex(@"\A[ \t,/\-]{0,3}\z");
    private readonly INerRecognizer _recognizer;
    private readonly PseudonymizationCatalog _catalog;

    public NerStrategy(INerRecognizer recognizer, PseudonymizationCatalog catalog)
    {
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _catalog = catalog;
        Revision = _recognizer.ModelRevision;
    }

    public string Id => "ner";
    public string Revision { get; }
    // Below every rule (the lowest is 50, the local entities): a rule mask is never replaced by a model one.
    public int Priority => 10;

    /// <summary>Time the recognizer may use for a block: proportional to its length, never beyond the request deadline.</summary>
    internal static TimeSpan BudgetFor(int length) => TimeSpan.FromMilliseconds(1000 + 2 * length);

    internal static IEnumerable<(int Start, int Length)> Blocks(string text)
    {
        if (text.Length == 0) yield break;
        for (int start = 0; ; start += BlockChars - OverlapChars)
        {
            int length = Math.Min(BlockChars, text.Length - start);
            yield return (start, length);
            if (start + length >= text.Length) yield break;
        }
    }

    public IEnumerable<PseudonymMatch> Detect(string text, PseudonymizationContext context)
    {
        var found = new List<(int Start, int End, string Category)>();
        foreach (var (start, length) in Blocks(text))
        {
            context.Check();
            var budget = BudgetFor(length);
            var remaining = context.Remaining;
            if (remaining < budget) budget = remaining;
            if (budget <= TimeSpan.Zero) throw new DataEgressBlockedException("Riconoscimento locale non completato in tempo: nessun dato inviato.");
            IReadOnlyList<NerSpan> spans;
            try { spans = _recognizer.Recognize(text.Substring(start, length), budget, context.Token); }
            catch (OperationCanceledException) { throw; }
            catch (DataEgressBlockedException) { throw; }
            catch (Exception) { throw new DataEgressBlockedException("Riconoscimento locale non disponibile: nessun dato inviato."); }
            context.Check();
            foreach (var span in spans ?? Array.Empty<NerSpan>())
            {
                if (span == null || span.Start < 0 || span.Length <= 0 || span.Start > length - span.Length)
                    throw new DataEgressBlockedException("Risposta del riconoscimento locale non valida: nessun dato inviato.");
                var category = _catalog.CategoryOfNerLabel(span.Label);
                if (category != null) found.Add((start + span.Start, start + span.Start + span.Length, category));
            }
        }
        foreach (var match in Fuse(Merge(found), text)) yield return match;
    }

    // Blocks overlap, so one entity can come back twice: the same category becomes one span, a different one is dropped.
    private static List<(int Start, int End, string Category)> Merge(List<(int Start, int End, string Category)> found)
    {
        var merged = new List<(int Start, int End, string Category)>();
        foreach (var span in found.OrderBy(s => s.Start).ThenByDescending(s => s.End))
        {
            if (merged.Count > 0 && span.Start < merged[merged.Count - 1].End)
            {
                var last = merged[merged.Count - 1];
                if (last.Category == span.Category && span.End > last.End) merged[merged.Count - 1] = (last.Start, span.End, last.Category);
                continue;
            }
            merged.Add(span);
        }
        return merged;
    }

    // Street, number, postcode and city arrive as separate labels: one address is one mask.
    private static IEnumerable<PseudonymMatch> Fuse(List<(int Start, int End, string Category)> spans, string text)
    {
        for (int i = 0; i < spans.Count; i++)
        {
            var current = spans[i];
            while (current.Category == "INDIRIZZO" && i + 1 < spans.Count && spans[i + 1].Category == "INDIRIZZO" &&
                spans[i + 1].Start >= current.End && AddressGap.IsMatch(text.Substring(current.End, spans[i + 1].Start - current.End)))
            {
                current = (current.Start, spans[i + 1].End, current.Category);
                i++;
            }
            yield return new PseudonymMatch(current.Start, current.End - current.Start, current.Category);
        }
    }
}

/// <summary>
/// Remembers what the recognizer found for a block of text, keyed by the hash of its content (no text is kept),
/// so the pre-analysis done when a mail opens makes the later masking instant (#247).
/// </summary>
public sealed class CachingNerRecognizer : INerRecognizer
{
    private readonly INerRecognizer _inner;
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Dictionary<string, NerSpan[]> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public CachingNerRecognizer(INerRecognizer inner, int capacity = 256)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    public string ModelRevision => _inner.ModelRevision;
    public int Count { get { lock (_lock) return _cache.Count; } }

    public IReadOnlyList<NerSpan> Recognize(string block, TimeSpan budget, CancellationToken token)
    {
        string key = Key(ModelRevision, block);
        lock (_lock) if (_cache.TryGetValue(key, out var hit)) return Copy(hit);
        var spans = _inner.Recognize(block, budget, token).ToArray();
        lock (_lock)
        {
            if (!_cache.ContainsKey(key))
            {
                _cache[key] = Copy(spans);
                _order.Enqueue(key);
                while (_order.Count > _capacity) _cache.Remove(_order.Dequeue());
            }
        }
        return spans;
    }

    /// <summary>Analyses the whole text now, block by block; a failure leaves the cache as it is.</summary>
    public void PreAnalyze(string text, CancellationToken token)
    {
        foreach (var (start, length) in NerStrategy.Blocks(text))
            Recognize(text.Substring(start, length), NerStrategy.BudgetFor(length), token);
    }

    private static NerSpan[] Copy(IEnumerable<NerSpan> spans) =>
        spans.Select(s => new NerSpan { Start = s.Start, Length = s.Length, Label = s.Label }).ToArray();

    private static string Key(string revision, string block)
    {
        using var sha = SHA256.Create();
        return revision + ":" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(block))).Replace("-", "");
    }
}
