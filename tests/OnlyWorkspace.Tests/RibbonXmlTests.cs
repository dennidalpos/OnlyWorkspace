using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OnlyWorkspace.Outlook.Ribbon;
using Xunit;

namespace OnlyWorkspace.Tests;

/// <summary>Office drops a whole ribbon on one malformed attribute: every document parses and every callback exists.</summary>
public class RibbonXmlTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/office/2009/07/customui";

    [Fact]
    public void MainTab_GroupsCommandsAndPreservesEverySearchPreset()
    {
        var doc = XDocument.Parse(RibbonXml.Build("Microsoft.Outlook.Explorer"));
        var tab = doc.Descendants(Ui + "tab").Single(t => (string?)t.Attribute("id") == "tabOnlyWorkspace");
        Assert.Equal(new[] { "Assistente", "Email", "Posta e persone", "Sistema" }, tab.Elements(Ui + "group").Select(g => (string?)g.Attribute("label")));
        Assert.Equal(new[] { "open", "table", "calendar", "followups", "custom" }, tab.Descendants(Ui + "button")
            .Where(b => (string?)b.Attribute("onAction") == "OnAnalysisPresetClicked").Select(b => (string?)b.Attribute("tag")));
        foreach (var menu in doc.Descendants(Ui + "menu").Where(m => ((string?)m.Attribute("id"))?.EndsWith("MenuReply") == true))
            Assert.DoesNotContain(menu.Elements(), b => (string?)b.Attribute("onAction") == "OnReplyCustomClicked");
        Assert.Contains(tab.Descendants(Ui + "button"), b => (string?)b.Attribute("onAction") == "OnArchiveInfoClicked");
    }

    [Theory]
    [InlineData("Microsoft.Outlook.Explorer")]
    [InlineData("Microsoft.Outlook.Mail.Read")]
    [InlineData("Microsoft.Outlook.Mail.Compose")]
    public void Document_ParsesWithUniqueIdsAndKnownCallbacks(string ribbonId)
    {
        var doc = XDocument.Parse(RibbonXml.Build(ribbonId));
        var ids = doc.Descendants().Select(e => (string?)e.Attribute("id")).Where(id => id != null).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var addin = File.ReadAllText(SourceOf("OnlyWorkspaceAddin.cs"));
        var callbacks = doc.Descendants().SelectMany(e => new[] { (string?)e.Attribute("onAction"), (string?)e.Attribute("getEnabled"), (string?)e.Attribute("getVisible") }).Where(c => c != null)
            .Append((string?)doc.Root!.Attribute("onLoad"))
            .Append((string?)doc.Root!.Attribute("loadImage")).Distinct();
        foreach (var callback in callbacks)
            Assert.Matches(new Regex(@"public \S+ " + callback + @"\("), addin);

        // Custom images are only the two the loadImage callback serves.
        Assert.All(doc.Descendants().Select(e => (string?)e.Attribute("image")).Where(i => i != null), i => Assert.Contains(i, new[] { "ow16", "ow32" }));
    }

    [Fact]
    public void EmailActions_RequireASelection_WhileGeneralActionsRemainAvailable()
    {
        var doc = XDocument.Parse(RibbonXml.Build("Microsoft.Outlook.Explorer"));
        foreach (var group in doc.Descendants(Ui + "group").Where(g => (string?)g.Attribute("id") is "grpQuick" or "grpEmail"))
        {
            var hint = Assert.Single(group.Elements(Ui + "labelControl"));
            Assert.Equal("GetNeedsEmailSelection", (string?)hint.Attribute("getVisible"));
            Assert.All(group.Elements().Where(e => e != hint), e => Assert.Equal("GetCanActOnEmail", (string?)e.Attribute("getVisible")));
        }
        foreach (var id in new[] { "btnChat", "btnBriefing", "analysisMenu" })
            Assert.Null(doc.Descendants().Single(e => (string?)e.Attribute("id") == id).Attribute("getVisible"));
    }

    [Fact]
    public void ComposeWindow_OffersTheWritingActions_NotTheReplyToItself()
    {
        var doc = XDocument.Parse(RibbonXml.Build("Microsoft.Outlook.Mail.Compose"));
        var group = doc.Descendants(Ui + "group").Single(g => (string?)g.Attribute("id") == "grpWrite");
        var actions = group.Descendants().Select(e => (string?)e.Attribute("onAction")).Where(a => a != null).Distinct().ToList();

        Assert.Equal(new[] { "OnWriteClicked", "OnImproveClicked", "OnTranslateClicked", "OnSubjectClicked" }, actions);
        Assert.DoesNotContain(doc.Descendants(), e => (string?)e.Attribute("onAction") == "OnReplyCustomClicked");
    }

    private static string SourceOf(string file)
    {
        return Path.Combine(TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.Outlook", file);
    }
}
