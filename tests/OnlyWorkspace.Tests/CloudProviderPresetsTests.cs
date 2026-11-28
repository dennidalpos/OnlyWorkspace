using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Tests;

/// <summary>EU cloud presets of the «Compatibile OpenAI» connector (#024): addresses only, no new provider type.</summary>
public sealed class CloudProviderPresetsTests
{
    [Fact]
    public void ListsTheFiveSurveyedProvidersWithEuFirst()
    {
        Assert.Equal(new[] { "ionos", "mistral", "ovhcloud", "scaleway", "stackit" }, CloudProviderPresets.All.Select(p => p.Id));
        Assert.All(CloudProviderPresets.All, p => Assert.True(p.EuRegion));
    }

    [Fact]
    public void AddressesAreHttpsVersionedAndUnique()
    {
        Assert.All(CloudProviderPresets.All, p =>
        {
            Assert.StartsWith("https://", p.BaseUrl);
            Assert.EndsWith("/v1", p.BaseUrl);
            Assert.StartsWith("https://", p.DocsUrl);
        });
        Assert.Equal(CloudProviderPresets.All.Count, CloudProviderPresets.All.Select(p => p.BaseUrl).Distinct().Count());
    }

    [Fact]
    public void PresetsAddNoProviderTypeAndRestoreNoRetiredOne()
    {
        Assert.Equal(6, ProviderCatalog.All.Count);
        foreach (var preset in CloudProviderPresets.All)
        {
            Assert.False(ProviderCatalog.IsKnown(preset.Id));
            Assert.Equal(ProviderCatalog.OpenAICompatible, ProviderCatalog.Get(preset.Id).Type == ProviderCatalog.Ollama ? ProviderCatalog.OpenAICompatible : ProviderCatalog.Get(preset.Id).Type);
        }
        Assert.True(ProviderCompatibility.IsRetired("mistral"));
    }

    [Theory]
    [InlineData("https://api.scaleway.ai/v1", "scaleway")]
    [InlineData("https://api.scaleway.ai/v1/", "scaleway")]
    [InlineData(" HTTPS://API.MISTRAL.AI/V1 ", "mistral")]
    public void Match_FindsAPresetByAddress(string address, string id) => Assert.Equal(id, CloudProviderPresets.Match(address)?.Id);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.test/v1")]
    public void Match_ReturnsNullForOtherAddresses(string? address) => Assert.Null(CloudProviderPresets.Match(address));

    [Fact]
    public void Label_StatesTheDeclaredRegionNotACompliance()
    {
        string label = CloudProviderPresets.All[0].ToString();
        Assert.Contains("dati UE", label);
        Assert.DoesNotContain("conform", label, StringComparison.OrdinalIgnoreCase);
    }
}
