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
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var referenced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)))
            foreach (var reference in XDocument.Load(project).Descendants("PackageReference"))
                referenced[(string)reference.Attribute("Include")!] = (string)reference.Attribute("Version")!;

        var credited = ThirdPartyComponents.All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var package in referenced)
        {
            Assert.True(credited.TryGetValue(package.Key, out var component), package.Key + " is not credited.");
            Assert.Equal(package.Value, component!.Version);
        }
        var notPackages = new[] { "Public Suffix List", "NSIS", "SQLitePCLRaw.core" };
        Assert.All(ThirdPartyComponents.All.Where(c => !notPackages.Contains(c.Name)), c => Assert.Contains(c.Name, referenced.Keys));
        Assert.All(ThirdPartyComponents.All, c => Assert.StartsWith("https://", c.Url));
    }

    [Fact]
    public void EveryDistributedComponent_HasItsNotice()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));
        Assert.All(ThirdPartyComponents.All.Where(c => c.Distributed), c => Assert.Contains(c.Name, notices));
        Assert.DoesNotContain("Polly", notices);
    }
}
