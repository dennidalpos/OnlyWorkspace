using System.Collections.Generic;
using System.Linq;

namespace OnlyWorkspace.Core.Agents;

/// <summary>Local validation losses, separate from provider truncation and intentional action filters.</summary>
public sealed class ExtractionDiagnostics
{
    private readonly Dictionary<string, int> _discarded = new();
    public int DiscardedCount => _discarded.Values.Sum();
    public bool IsPartial => DiscardedCount > 0;
    public void Discard(string reason) => _discarded[reason] = _discarded.TryGetValue(reason, out int count) ? count + 1 : 1;
    public void Merge(ExtractionDiagnostics other)
    {
        foreach (var pair in other._discarded)
            _discarded[pair.Key] = (_discarded.TryGetValue(pair.Key, out int count) ? count : 0) + pair.Value;
    }
    public string Describe() => !IsPartial ? string.Empty :
        "Risultato parziale: " + DiscardedCount + " voci non valide scartate (" +
        string.Join("; ", _discarded.Select(p => p.Key + ": " + p.Value)) + ").";
}

public sealed class ExtractedActionItems : List<ExtractedActionItem>
{
    public ExtractionDiagnostics Diagnostics { get; } = new();
}
