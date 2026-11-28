using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Tests;

/// <summary>#091: the task pane depends on a narrow interface, and every COM release goes through one helper.</summary>
public sealed class OutlookDataServiceContractTests
{
    private static string Source(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.Outlook" }.Concat(relative).ToArray()));

    [Theory]
    [InlineData("UI", "TaskPaneCoordinator.cs")]
    [InlineData("Services", "TaskPaneManager.cs")]
    public void TaskPane_DependsOnTheInterface_NotOnTheConcreteService(string folder, string file)
    {
        string source = Source(folder, file);
        Assert.Matches(@"\bIOutlookDataService\s+_dataService\b", source);
        Assert.Matches(@"\bIOutlookDataService\s+dataService\b", source);
        Assert.DoesNotMatch(@"(?<!I)\bOutlookDataService\s+_?dataService\b", source);
    }

    [Fact]
    public void TaskPaneParts_DoNotNameTheConcreteService()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.Outlook", "UI"), "TaskPaneCoordinator*.cs"))
            Assert.DoesNotMatch(@"(?<!I)\bOutlookDataService\b", File.ReadAllText(file));
    }

    [Fact]
    public void ConcreteService_ImplementsTheInterface()
    {
        Assert.Matches(@"class OutlookDataService\s*:[^{\r\n]*\bIOutlookDataService\b", Source("Services", "OutlookDataService.Archive.cs"));
    }

    [Fact]
    public void ComReleases_AreNotWrittenByHandInTheDataService()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.Outlook", "Services"), "OutlookDataService*.cs"))
            Assert.DoesNotContain("ReleaseComObject", File.ReadAllText(file));
        Assert.Contains("Marshal.ReleaseComObject", Source("Services", "ComObjects.cs"));
    }

    [Fact]
    public void ComReleases_AreWrittenOnlyInTheHelper_AcrossTheOutlookProject()
    {
        string root = Path.Combine(TestBuildPaths.RepositoryRoot, "src", "OnlyWorkspace.Outlook");
        var offenders = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => Path.GetFileName(f) != "ComObjects.cs" && File.ReadAllText(f).Contains("ReleaseComObject"))
            .Select(f => f.Substring(root.Length + 1)).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void ScanCursors_AreExposedAsInterfaces_AndImplementedByTheService()
    {
        string contract = Source("Services", "IOutlookDataService.cs");
        Assert.Matches(@"\bIMailHeaderScan\s+BeginMailHeaderScan\b", contract);
        Assert.Matches(@"\bIUnansweredSentScan\s+BeginUnansweredSentScan\b", contract);
        Assert.DoesNotContain("OutlookDataService.MailHeaderScan", contract);
        Assert.DoesNotContain("OutlookDataService.UnansweredSentScan", contract);
        string impl = Source("Services", "OutlookDataService.MailScan.cs");
        Assert.Matches(@"class MailHeaderScan\s*:\s*IMailHeaderScan\b", impl);
        Assert.Matches(@"class UnansweredSentScan\s*:\s*IUnansweredSentScan\b", impl);
        foreach (string file in new[] { Source("UI", "TaskPaneCoordinator.cs"), Source("UI", "TaskPaneCoordinator.Analysis.cs") })
            Assert.DoesNotMatch(@"(?<![A-Za-z])(MailHeaderScan|UnansweredSentScan)\b", file);
    }

    [Fact]
    public void ComRelease_IgnoresNullAndPlainObjects_AndReleasesComObjects()
    {
        ComObjects.Release(null);
        ComObjects.Release(new object());

        var type = Type.GetTypeFromProgID("Scripting.FileSystemObject");
        Assert.NotNull(type);
        object com = Activator.CreateInstance(type!)!;
        Assert.True(Marshal.IsComObject(com));
        ComObjects.Release(com);
        Assert.Throws<InvalidComObjectException>(() => type!.InvokeMember("GetDriveName", BindingFlags.InvokeMethod, null, com, new object[] { @"C:\" }));
    }
}
