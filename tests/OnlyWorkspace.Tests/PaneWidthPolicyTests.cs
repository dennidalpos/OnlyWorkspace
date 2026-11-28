using System.Runtime.CompilerServices;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Tests;

/// <summary>Pane width in Office units (system-DPI pixels) and the page minimum that holds while the splitter is dragged.</summary>
public sealed class PaneWidthPolicyTests
{
    [Theory]
    [InlineData(96, 380, 360)]
    [InlineData(120, 475, 450)]
    [InlineData(144, 570, 540)]
    [InlineData(192, 760, 720)]
    [InlineData(288, 1140, 1080)]
    public void Widths_ScaleWithSystemDpi(int dpi, int expectedDefault, int expectedMinimum)
    {
        Assert.Equal(expectedDefault, PaneWidthPolicy.DefaultWidth(dpi));
        Assert.Equal(expectedMinimum, PaneWidthPolicy.MinimumWidth(dpi));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(72)]
    public void Widths_NeverScaleBelow96Dpi(int dpi)
    {
        Assert.Equal(380, PaneWidthPolicy.DefaultWidth(dpi));
        Assert.Equal(360, PaneWidthPolicy.MinimumWidth(dpi));
    }

    [Fact]
    public void Minimum_IsBelowDefaultAtEveryScale()
    {
        foreach (int dpi in new[] { 96, 120, 144, 168, 192, 240, 288 })
            Assert.True(PaneWidthPolicy.MinimumWidth(dpi) < PaneWidthPolicy.DefaultWidth(dpi));
    }

    [Fact]
    public void RestoreAfterDrag_IsBoundedToFourAttempts() => Assert.Equal(4, PaneWidthPolicy.MaxAttempts);

    [Fact]
    public void Page_KeepsItsMinimumWidthWithHorizontalScroll()
    {
        string html = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "..", "src", "OnlyWorkspace.Outlook", "UI", "TaskPane.html"));
        Assert.Contains("html { min-width: 340px; overflow-x: auto; }", File.ReadAllText(html));
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
