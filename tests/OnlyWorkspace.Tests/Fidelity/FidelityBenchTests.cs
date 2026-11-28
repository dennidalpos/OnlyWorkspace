using System.Text.Json;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Tests.Fidelity;

public sealed class FidelityBenchTests
{
    private static string CorpusPath => Path.Combine(AppContext.BaseDirectory, "fidelity", "corpus.json");

    [Fact]
    public void LanguageCorpusKeepsIndependentReviewExpectationsForEveryBoundary()
    {
        var corpus = FidelityBench.Load(Path.Combine(AppContext.BaseDirectory, "fidelity", "language-corpus.json"));
        Assert.Equal(4, corpus.Cases.Count);
        Assert.All(corpus.Cases, c => { Assert.NotEmpty(c.Review); Assert.All(c.Sources, s => Assert.Empty(s.Expected)); });
        Assert.Empty(corpus.Cases.Single(c => c.Id == "empty-body").Sources.Single(s => s.Id == "").Text);
        Assert.Equal("it", corpus.Cases.Single(c => c.Id == "mixed-body").Language);
    }





    [Fact]
    public void Corpus_HasIndependentSourcesExpectedSpansAndRequiredCoverage()
    {
        var corpus = FidelityBench.Load(CorpusPath);
        Assert.Equal(12, corpus.Cases.Count); Assert.Equal(6, corpus.Cases.Count(c => c.Language == "it"));
        Assert.Equal(6, corpus.Cases.Count(c => c.Language == "en")); Assert.Equal(3, corpus.Scenarios.Count);
        foreach (string tag in new[] { "long", "chain", "forward", "attachment", "automatic", "ambiguous" })
            Assert.Contains(corpus.Cases, c => c.Tags.Contains(tag));
        Assert.Equal(2, corpus.Cases.Count(c => c.Sources.Any(s => s.Text.Length > 10_000)));
        foreach (var c in corpus.Cases)
        {
            var actual = FidelityBench.Extract(c);
            foreach (var s in c.Sources)
                foreach (var fact in s.Expected)
                    Assert.Contains(actual, a => a.Kind == fact.Kind && a.Raw == fact.Raw && a.Value == fact.Value && a.Currency == fact.Currency && a.Start == fact.Start && a.SourceId == fact.SourceId);
        }
    }

    private static FidelityClaim Claim(string value = "1000.01", string source = "", int start = 0) => new()
    { CaseId = "case", SourceId = source, Kind = ArchiveFactKind.Amount, Value = value, Currency = "EUR", Raw = "Importo 1000,01 EUR", Start = start, Length = 19 };

    [Fact]
    public void Score_UsesOneToOneMatchingAndCountsMissingInventedDuplicateAndWrongSource()
    {
        var expected = new[] { Claim(), Claim("42", "attachment") };
        var score = FidelityBench.Score(expected, new[] { Claim(), Claim(), Claim("99"), Claim("42", "wrong") }, true)["Amount"];
        Assert.Equal(1, score.TP); Assert.Equal(3, score.FP); Assert.Equal(1, score.FN);
        Assert.Equal(1, score.Duplicates); Assert.Equal(1, score.WrongSource); Assert.Equal(.25, score.Precision); Assert.Equal(.5, score.Recall);
        var missing = FidelityBench.Score(expected, Array.Empty<FidelityClaim>(), true)["Amount"];
        Assert.Null(missing.Precision); Assert.Equal(0, missing.Recall);
        Assert.Equal(0, FidelityBench.Score(new[] { Claim() }, new[] { Claim(start: 1) }, true)["Amount"].TP);
    }

    [Fact]
    public void Score_EmptyExpectedAndRepeatedOccurrencesDoNotBecomePerfectByAccident()
    {
        var extra = FidelityBench.Score(Array.Empty<FidelityClaim>(), new[] { Claim() }, true)["Amount"];
        Assert.Equal(0, extra.Precision); Assert.Null(extra.Recall);
        var two = FidelityBench.Score(new[] { Claim(), Claim(start: 50) }, new[] { Claim() }, true)["Amount"];
        Assert.Equal(1, two.TP); Assert.Equal(1, two.FN);
    }






    [Fact]
    public void OfflineReport_IsByteReproducibleAndOverBudgetInputIsRejected()
    {
        var corpus = FidelityBench.Load(CorpusPath);
        Assert.Equal(JsonSerializer.Serialize(FidelityBench.Offline(corpus), FidelityBench.Json), JsonSerializer.Serialize(FidelityBench.Offline(corpus), FidelityBench.Json));
        var profile = new LlmProfileConfig { Model = "synthetic", ContextBudgetTokens = 100, NumCtx = 8192 };
        Assert.Throws<ContextTooLongException>(() => ModelRequestContext.Prepare(profile, new[] { ChatMessage.User(new string('x', 10_000)) }));
    }


}
