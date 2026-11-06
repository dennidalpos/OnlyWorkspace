using System.Text.Json;
using System.Xml.Linq;
using OnlyWorkspace.Core;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Tests;

/// <summary>Traffic lights of the archive and the credits list shown in the guide and the ConfigTool.</summary>
public sealed class StatusAndCreditsTests
{
    [Theory]
    [InlineData(false, true, false, "idle", null, 0, 0, 0, StatusLight.Off)]
    [InlineData(true, false, false, "idle", null, 0, 0, 0, StatusLight.Off)]
    [InlineData(true, true, true, "paused", null, 0, 0, 0, StatusLight.Paused)]
    [InlineData(true, true, false, "idle", null, 0, 0, 0, StatusLight.Ok)]
    // Yellow only while a step runs: a queue held by user activity or the next cycle is grey.
    [InlineData(true, true, false, "waiting", null, 0, 5, 0, StatusLight.Waiting)]
    [InlineData(true, true, false, "waiting", null, 0, 0, 0, StatusLight.Waiting)]
    [InlineData(true, true, false, "headers", null, 0, 0, 0, StatusLight.Working)]
    [InlineData(true, true, false, "cards", null, 0, 0, 0, StatusLight.Working)]
    [InlineData(true, true, false, "idle", null, 0, 0, 3, StatusLight.Waiting)]
    [InlineData(true, true, false, "idle", null, 1, 0, 0, StatusLight.Error)]
    [InlineData(true, true, false, "limit", null, 0, 0, 0, StatusLight.Error)]
    [InlineData(true, true, true, "paused", "Motore non disponibile", 0, 0, 0, StatusLight.Error)]
    public void ArchiveLight_GreenSyncedYellowWorkingRedErrorsGreyIdle(bool available, bool active, bool paused, string phase, string? error,
        int foldersFailed, int bodiesPending, int foldersPending, StatusLight expected) =>
        Assert.Equal(expected, StatusLights.Archive(available, active, paused, phase, error, foldersFailed, bodiesPending, foldersPending));

    // Every red light has a reason in words; the other lights have none.
    [Theory]
    [InlineData("waiting", null, 1, "1 cartella non leggibile")]
    [InlineData("idle", null, 3, "3 cartelle non leggibili")]
    [InlineData("limit", null, 0, "limite di spazio raggiunto")]
    [InlineData("error", null, 0, "errore dell'archivio")]
    [InlineData("waiting", "Motore non disponibile", 2, "Motore non disponibile")]
    [InlineData("waiting", null, 0, null)]
    [InlineData("headers", " ", 0, null)]
    public void ArchiveReason_ExplainsEveryRedLight(string phase, string? error, int foldersFailed, string? expected)
    {
        Assert.Equal(expected, StatusLights.ArchiveReason(phase, error, foldersFailed));
        Assert.Equal(expected != null, StatusLights.Archive(true, true, false, phase, error, foldersFailed, 0, 0) == StatusLight.Error);
    }

    [Fact]
    public void ArchiveLights_WorstFirstWithTheirLabels()
    {
        Assert.Equal(StatusLight.Error, StatusLights.Worst(new[] { StatusLight.Ok, StatusLight.Error, StatusLight.Working }));
        Assert.Equal(StatusLight.Working, StatusLights.Worst(new[] { StatusLight.Waiting, StatusLight.Working }));
        Assert.Equal(StatusLight.Waiting, StatusLights.Worst(new[] { StatusLight.Ok, StatusLight.Waiting, StatusLight.Paused }));
        Assert.Equal(StatusLight.Paused, StatusLights.Worst(new[] { StatusLight.Ok, StatusLight.Paused }));
        Assert.Equal(StatusLight.Off, StatusLights.Worst(Array.Empty<StatusLight>()));
        Assert.Equal(
            new[] { "spento", "sincronizzato", "in lavorazione", "errori", "in pausa", "in attesa" },
            Enum.GetValues(typeof(StatusLight)).Cast<StatusLight>().Select(StatusLights.Label));
    }

    /// <summary>Every package a project references is credited with the same version; nothing stale is listed.</summary>
    [Fact]
    public void Credits_MatchThePackageReferencesOfTheProjects()
    {
        string root = TestBuildPaths.RepositoryRoot;
        // Versions live only in Directory.Packages.props; a PackageReference never carries its own.
        var central = XDocument.Load(Path.Combine(root, "Directory.Packages.props")).Descendants("PackageVersion")
            .ToDictionary(v => (string)v.Attribute("Include")!, v => (string)v.Attribute("Version")!, StringComparer.OrdinalIgnoreCase);
        var referenced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)))
            foreach (var reference in XDocument.Load(project).Descendants("PackageReference"))
            {
                string id = (string)reference.Attribute("Include")!;
                Assert.Null(reference.Attribute("Version"));
                Assert.True(central.TryGetValue(id, out var version), id + " has no PackageVersion in Directory.Packages.props.");
                referenced[id] = version!;
            }
        Assert.All(central.Keys, id => Assert.True(referenced.ContainsKey(id), id + " is in Directory.Packages.props but no project references it."));

        var credited = ThirdPartyComponents.All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var package in referenced)
        {
            Assert.True(credited.TryGetValue(package.Key, out var component), package.Key + " is not credited.");
            Assert.Equal(package.Value, component!.Version);
        }
        var notPackages = new[] { "Public Suffix List", "NSIS", "SQLitePCLRaw.core" };
        var shipped = ShippedPackages();
        Assert.All(ThirdPartyComponents.All.Where(c => !notPackages.Contains(c.Name)), c => Assert.True(referenced.ContainsKey(c.Name) || shipped.ContainsKey(c.Name), c.Name + " is neither referenced nor shipped."));
        Assert.All(ThirdPartyComponents.All, c => Assert.StartsWith("https://", c.Url));
    }

    /// <summary>The restore is reproducible: SDK pinned, versions central, and every project has the lock file that pins the transitive packages.</summary>
    [Fact]
    public void Restore_IsPinned_BySdkCentralVersionsAndLockFiles()
    {
        string root = TestBuildPaths.RepositoryRoot;
        using (var global = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json"))))
        {
            var sdk = global.RootElement.GetProperty("sdk");
            Assert.Matches(@"^\d+\.\d+\.\d+$", sdk.GetProperty("version").GetString()!);
            Assert.Equal("latestPatch", sdk.GetProperty("rollForward").GetString());
        }
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", File.ReadAllText(Path.Combine(root, "Directory.Build.props")));
        foreach (var project in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)))
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")), project + " has no packages.lock.json.");
    }

    /// <summary>Packages whose assemblies reach the installation, direct or transitive, from the restore graphs of the add-in and the extractor.</summary>
    private static readonly string[] ShippedGraphs = { "OutlookAssetsFile", "ExtractorAssetsFile" };

    private static Dictionary<string, HashSet<string>> ShippedPackages(params string[] graphs)
    {
        var shipped = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in graphs.Length == 0 ? ShippedGraphs : graphs)
        {
            using var assets = JsonDocument.Parse(File.ReadAllText(TestBuildPaths.Get(file)));
            foreach (var framework in assets.RootElement.GetProperty("targets").EnumerateObject().Where(t => t.Name.StartsWith("net4", StringComparison.Ordinal) || t.Name.StartsWith(".NETFramework", StringComparison.Ordinal)))
                foreach (var library in framework.Value.EnumerateObject())
                {
                    if (library.Value.GetProperty("type").GetString() != "package" || !library.Value.TryGetProperty("runtime", out var runtime)) continue;
                    if (runtime.EnumerateObject().All(r => r.Name.EndsWith("_._", StringComparison.Ordinal))) continue;
                    string[] idAndVersion = library.Name.Split('/');
                    if (!shipped.TryGetValue(idAndVersion[0], out var versions)) shipped[idAndVersion[0]] = versions = new HashSet<string>();
                    versions.Add(idAndVersion[1]);
                }
        }
        return shipped;
    }

    /// <summary>The credits also cover the packages that arrive only as dependencies and whose assemblies are installed.</summary>
    [Fact]
    public void Credits_CoverEveryShippedAssemblyPackage_IncludingTransitiveOnes()
    {
        var credited = ThirdPartyComponents.All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        // Per shipped component: one credit line cannot cover two versions, so each graph must ship exactly the credited one.
        foreach (var graph in ShippedGraphs)
        {
            var shipped = ShippedPackages(graph);
            Assert.NotEmpty(shipped);
            foreach (var package in shipped)
            {
                Assert.True(credited.TryGetValue(package.Key, out var component), package.Key + " ships but is not credited (" + graph + ").");
                Assert.True(component!.Distributed, package.Key + " ships but is credited as not distributed.");
                Assert.True(package.Value.All(v => v == component.Version), package.Key + " is credited as " + component.Version + " but " + graph + " ships " + string.Join("/", package.Value) + ".");
            }
        }
    }

    [Fact]
    public void EveryDistributedComponent_HasItsNotice()
    {
        string root = TestBuildPaths.RepositoryRoot;
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));
        Assert.All(ThirdPartyComponents.All.Where(c => c.Distributed), c => Assert.Contains(c.Name, notices));
        Assert.DoesNotContain("Polly", notices);
    }
}
