using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>Plain local data only. Never serialized with a model request or audit record.</summary>
public sealed class PrivacyEntity
{
    public long Id { get; set; }
    public string Category { get; set; } = "PERSONA";
    public string Value { get; set; } = string.Empty;
}

public sealed class PseudonymizationContext
{
    public IReadOnlyList<PrivacyIdentity> Entities { get; }
    public bool MaskAmounts { get; }
    public bool MaskDates { get; }
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly TimeSpan _budget;
    private readonly CancellationToken _token;
    private PrivacyEntityIndex? _index;
    internal IEnumerable<PseudonymMatch> MatchEntities(string text) => (_index ??= new PrivacyEntityIndex(Entities, Check)).Detect(text, Check);
    public PseudonymizationContext(IEnumerable<PrivacyEntity>? entities = null, bool maskAmounts = false,
        bool maskDates = false, CancellationToken token = default, TimeSpan? budget = null)
    {
        Entities = Array.AsReadOnly((entities ?? Array.Empty<PrivacyEntity>()).Select(e =>
            new PrivacyIdentity(e.Category, e.Value)).ToArray());
        MaskAmounts = maskAmounts; MaskDates = maskDates; _token = token;
        _budget = budget ?? TimeSpan.FromSeconds(10);
        if (_budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
    }
    public void Check()
    {
        _token.ThrowIfCancellationRequested();
        if (_elapsed.Elapsed > _budget) throw new DataEgressBlockedException("Pseudonimizzazione non completata in tempo: nessun dato inviato.");
    }
}

public sealed class PrivacyIdentity
{
    public string Category { get; }
    public string Value { get; }
    public PrivacyIdentity(string category, string value) { Category = category; Value = value; }
}

public sealed class PseudonymMatch
{
    public int Start { get; }
    public int Length { get; }
    public string Category { get; }
    public PseudonymMatch(int start, int length, string category) { Start = start; Length = length; Category = category; }
}

public interface IPseudonymizationStrategy
{
    string Id { get; }
    string Revision { get; }
    int Priority { get; }
    IEnumerable<PseudonymMatch> Detect(string text, PseudonymizationContext context);
}

public sealed class PseudonymizationAudit
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public string Outcome { get; set; } = "prepared";
    public Dictionary<string, string> Revisions { get; set; } = new();
    public Dictionary<string, int> Counts { get; set; } = new();
}

public interface IPseudonymizationAuditStore
{
    void Write(PseudonymizationAudit record);
    IReadOnlyList<PseudonymizationAudit> Read();
}

/// <summary>Versioned, shipped data. No downloaded rules and no executable extensions.</summary>
public sealed class PseudonymizationCatalog
{
    private static readonly Lazy<string> RevisionValue = new(() => Load().Revision);
    public static string CurrentRevision => RevisionValue.Value;
    public string Revision { get; set; } = string.Empty;
    public string NamePattern { get; set; } = string.Empty;
    public List<string> NameOpeners { get; set; } = new();
    public List<PrivacyPattern> Patterns { get; set; } = new();
    internal static bool ValidCategory(string value) => !string.IsNullOrEmpty(value) && value.Length <= 16 &&
        System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-Z][A-Z0-9]+\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    public static PseudonymizationCatalog Load()
    {
        using var input = typeof(PseudonymizationCatalog).Assembly.GetManifestResourceStream("OnlyWorkspace.Core.Privacy.strategies.json")
            ?? throw new DataEgressBlockedException("Strategie di pseudonimizzazione mancanti: nessun dato inviato.");
        using var reader = new StreamReader(input);
        var catalog = JsonSerializer.Deserialize<PseudonymizationCatalog>(reader.ReadToEnd())
            ?? throw new DataEgressBlockedException("Strategie di pseudonimizzazione non valide: nessun dato inviato.");
        catalog.Validate();
        return catalog;
    }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Revision) || string.IsNullOrWhiteSpace(NamePattern) || Patterns.Count == 0 ||
            Patterns.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != Patterns.Count)
            throw new InvalidDataException("Invalid privacy catalog.");
        var names = Regex(NamePattern);
        if (!names.GetGroupNames().Contains("name") || !names.GetGroupNames().Contains("email")) throw new InvalidDataException("Invalid privacy name pattern.");
        foreach (var p in Patterns)
            if (string.IsNullOrWhiteSpace(p.Id) || !ValidCategory(p.Category) ||
                (p.Option != "" && p.Option != "amounts" && p.Option != "dates") || !Regex(p.Pattern).GetGroupNames().Contains("value"))
                throw new InvalidDataException("Invalid privacy pattern.");
    }
    internal static Regex Regex(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    public IReadOnlyList<IPseudonymizationStrategy> Strategies() => Patterns.Select(p =>
        (IPseudonymizationStrategy)new PatternStrategy(p, Revision)).Concat(new[] { new EntityStrategy(Revision) }).ToArray();

    private sealed class PatternStrategy : IPseudonymizationStrategy
    {
        private readonly PrivacyPattern _rule;
        private readonly Regex _pattern;
        public string Id => _rule.Id;
        public string Revision { get; }
        public int Priority => _rule.Priority;
        public PatternStrategy(PrivacyPattern rule, string revision) { _rule = rule; Revision = revision; _pattern = Regex(rule.Pattern); }
        public IEnumerable<PseudonymMatch> Detect(string text, PseudonymizationContext context)
        {
            if ((_rule.Option == "amounts" && !context.MaskAmounts) || (_rule.Option == "dates" && !context.MaskDates)) yield break;
            foreach (Match match in _pattern.Matches(text))
            {
                context.Check();
                var value = match.Groups["value"];
                yield return new PseudonymMatch(value.Index, value.Length, _rule.Category);
            }
        }
    }
    private sealed class EntityStrategy : IPseudonymizationStrategy
    {
        public string Id => "local-entities";
        public string Revision { get; }
        public int Priority => 50;
        public EntityStrategy(string revision) => Revision = revision;
        public IEnumerable<PseudonymMatch> Detect(string text, PseudonymizationContext context)
        {
            foreach (var match in context.MatchEntities(text)) yield return match;
        }
    }
}

public sealed class PrivacyPattern
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public string Option { get; set; } = string.Empty;
    public int Priority { get; set; }
}
