using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace OnlyWorkspace.Tests;

/// <summary>#204: corpus invariants, recognition floors and the published table equal to the measure.</summary>
public sealed class PseudonymizationBenchTests
{
    private readonly ITestOutputHelper _output;
    public PseudonymizationBenchTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Corpus_IsLargeLabelledAndSynthetic()
    {
        var corpus = PseudonymizationBench.Corpus();
        Assert.True(corpus.Count >= 200, "texts: " + corpus.Count);
        var byCategory = corpus.SelectMany(i => i.Labels).GroupBy(l => l.Category).ToDictionary(g => g.Key, g => g.Count());
        foreach (string category in PseudonymizationBench.Categories) Assert.True(byCategory.GetValueOrDefault(category) >= 10, category);
        Assert.True(corpus.Count(i => i.Labels.Length == 0) >= 30);
        // Every label is in its text, and nothing can belong to a real person or company.
        foreach (var item in corpus)
            foreach (var (category, value) in item.Labels) Assert.Contains(value, item.Text);
        Assert.All(corpus.SelectMany(i => i.Labels).Where(l => l.Category == "EMAIL"), l => Assert.EndsWith(".test", l.Value));
        Assert.All(corpus.SelectMany(i => i.Labels).Where(l => l.Category == "TEL"), l => Assert.Contains("000", l.Value));
        Assert.All(corpus.SelectMany(i => i.Labels).Where(l => l.Category is "IBAN"), l => Assert.Contains("X00000000000000", l.Value.Replace(" ", string.Empty)));
        Assert.Equal(corpus.Select(i => i.Text), PseudonymizationBench.Corpus().Select(i => i.Text));
    }

    [Fact]
    public void Measure_ReportsEveryCategory_AndTheFixedPatternsAreAlwaysMasked()
    {
        var stats = PseudonymizationBench.Measure(PseudonymizationBench.Corpus());
        _output.WriteLine(PseudonymizationBench.Render(PseudonymizationBench.Corpus(), stats));
        foreach (string category in PseudonymizationBench.Categories) Assert.True(stats[category].Values > 0, category);
        // Structured identifiers have a fixed shape: a miss here is a regression of the catalog, not a limit.
        foreach (string category in new[] { "EMAIL", "IBAN", "CF", "PIVA" })
            Assert.True(stats[category].Masked == stats[category].Values, category + ": " + stats[category].Masked + "/" + stats[category].Values);
    }

    [Fact]
    public void PublishedTable_EqualsTheMeasureOfTheCurrentCatalog()
    {
        string guide = File.ReadAllText(Path.Combine(TestBuildPaths.RepositoryRoot, "docs", "tecnico", "pseudonimizzazione.md"));
        var block = Regex.Match(guide, @"<!-- bench:start -->\s*(.*?)\s*<!-- bench:end -->", RegexOptions.Singleline);
        Assert.True(block.Success, "markers bench:start/bench:end missing in pseudonimizzazione.md");
        var corpus = PseudonymizationBench.Corpus();
        string expected = PseudonymizationBench.Render(corpus, PseudonymizationBench.Measure(corpus));
        Assert.Equal(expected, block.Groups[1].Value.Replace("\r\n", "\n"));
    }
}
