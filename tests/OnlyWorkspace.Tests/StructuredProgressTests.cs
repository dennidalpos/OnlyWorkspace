using OnlyWorkspace.Core.Agents;

namespace OnlyWorkspace.Tests;

/// <summary>The pane's line while a structured answer streams in (A150).</summary>
public sealed class StructuredProgressTests
{
    [Fact]
    public void Describe_NamesOnlyTheSectionTitlesAlreadyWhole()
    {
        string partial = "{\"sections\":[{\"title\":\"Scadenze \\\"urgenti\\\"\",\"points\":[{\"text\":\"Pagare 1.200 € entro il 3/11\"}]},{\"title\":\"Contra";

        Assert.Equal(new[] { "Scadenze \"urgenti\"" }, MailAnswerProgress.SectionTitles(partial));
        string line = MailAnswerProgress.Describe(partial, TimeSpan.FromSeconds(42.7));
        Assert.StartsWith("Risposta in arrivo: " + partial.Length.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("it-IT")) + " caratteri · 42 s", line);
        Assert.EndsWith("· Scadenze \"urgenti\"", line);
        // The points are not checked yet: none of their values reaches the line.
        Assert.DoesNotContain("1.200", line.Substring(line.IndexOf(" s ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Report_ReachesOnlyTheObservingCall_AcrossAwaits()
    {
        var seen = new List<string>();
        StructuredProgress.Report("before");
        using (StructuredProgress.Observe(chunk => { lock (seen) seen.Add(chunk); }))
        {
            await Task.Run(() => StructuredProgress.Report("pool"));
            await Task.Yield();
            StructuredProgress.Report("after await");
            StructuredProgress.Report(string.Empty);
        }
        StructuredProgress.Report("outside");

        Assert.Equal(new[] { "pool", "after await" }, seen);
    }
}
