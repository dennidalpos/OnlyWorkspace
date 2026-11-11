using System.Text.RegularExpressions;

namespace OnlyWorkspace.Tests;

/// <summary>#193 and #194: sizes come from Metrics, and every view change goes through Ui.ShowView.</summary>
public sealed class ConfigToolLayoutRulesTests
{
    private static IEnumerable<string> FormSources() =>
        Directory.GetFiles(Path.Combine(TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.ConfigTool", "Forms"), "*.cs");

    // The arguments of a Size(...) assigned to Minimum/MaximumSize, with one level of nested calls.
    private static readonly Regex SizeAssignment = new(
        @"(Minimum|Maximum)Size\s*=\s*new\s+Size\((?<args>(?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)", RegexOptions.Compiled);

    private static readonly Regex TwoDigitNumber = new(@"\b\d{2,}\b", RegexOptions.Compiled);

    private static bool HasLiteralSize(string line)
    {
        var match = SizeAssignment.Match(line);
        return match.Success && TwoDigitNumber.IsMatch(match.Groups["args"].Value);
    }

    [Fact]
    public void Forms_DoNotSetMinimumOrMaximumSizeWithLiterals_OutsideMetrics()
    {
        var offenders = new List<string>();
        foreach (string file in FormSources().Where(f => Path.GetFileName(f) != "Theme.cs"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (HasLiteralSize(lines[i])) offenders.Add(Path.GetFileName(file) + ":" + (i + 1));
        }
        Assert.True(offenders.Count == 0, "Move the numbers to Metrics: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheLiteralRule_CatchesWhatItForbids()
    {
        Assert.True(HasLiteralSize("MaximumSize = new Size(600, 0),"));
        Assert.True(HasLiteralSize("MinimumSize = new Size(Ui.Px(this, 680), 0);"));
        Assert.False(HasLiteralSize("MaximumSize = new Size(Metrics.TextWidthPage, 0), Margin = new Padding(3, 6, 3, 14)"));
        Assert.False(HasLiteralSize("MinimumSize = new Size(0, Metrics.RowMinHeight),"));
        Assert.False(HasLiteralSize("search.MinimumSize = new Size(Metrics.ButtonMinWidth, Metrics.ButtonHeight);"));
    }

    [Fact]
    public void ScrollToTop_IsReachedOnlyThroughShowView()
    {
        var offenders = FormSources()
            .Where(f => Path.GetFileName(f) != "ThemedControls.cs" && File.ReadAllText(f).Contains("ScrollToTop"))
            .Select(Path.GetFileName);
        Assert.Empty(offenders);

        string ui = File.ReadAllText(Path.Combine(TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.ConfigTool", "Forms", "ThemedControls.cs"));
        Assert.Matches(@"private static void ScrollToTop\(", ui);
        Assert.Matches(@"public static void ShowView\(", ui);
    }

    [Theory]
    [InlineData("MainConfigForm.cs", 2)]
    [InlineData("MainConfigForm.Profiles.cs", 1)]
    [InlineData("MainConfigForm.ProfilePage.cs", 1)]
    [InlineData("SetupWizardForm.cs", 1)]
    [InlineData("ArchiveSearchForm.cs", 1)]
    public void EveryPageSectionStepAndTab_ChangesViewThroughShowView(string file, int calls)
    {
        string source = File.ReadAllText(Path.Combine(TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.ConfigTool", "Forms", file));
        Assert.Equal(calls, Regex.Matches(source, @"\bUi\.ShowView\(").Count);
    }
}
