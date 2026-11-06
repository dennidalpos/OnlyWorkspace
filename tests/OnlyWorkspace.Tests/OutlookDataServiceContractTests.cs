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
