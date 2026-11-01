using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

public sealed class ExposureRetirementTests
{
    [Theory]
    [InlineData("\"Full\"")]
    [InlineData("\"full\"")]
    [InlineData("3")]
    [InlineData("\"3\"")]
    public void RetiredLevelLoadsAsStandardAndCannotBeWrittenBack(string json)
    {
        var level = JsonSerializer.Deserialize<ExposureLevel>(json);
        Assert.Equal(ExposureLevel.Standard, level);
        Assert.Equal("\"Standard\"", JsonSerializer.Serialize(level));
        var policy = ExposurePolicy.For(level);
        Assert.False(policy.Attachments);
        Assert.True(policy.Pseudonymize);
        Assert.False(policy.AllowsWeb(WebSearchAction.Chat));
        Assert.True(policy.AllowsWeb(WebSearchAction.Security));
        Assert.Equal(new[] { ExposureLevel.LocalOnly, ExposureLevel.Minimal, ExposureLevel.Standard, ExposureLevel.Extended }, ExposurePolicy.All);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("-1")]
    [InlineData("\"unknown\"")]
    [InlineData("true")]
    public void InvalidLevelsAreRefusedAndAnUnconvertedValueCannotSend(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ExposureLevel>(json));
        Assert.False(ExposurePolicy.For((ExposureLevel)3).ModelAllowed);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((ExposureLevel)3));
    }

    [Fact]
    public void LegacyConfigurationLoadsWithoutWritingFilesOrChangingProfileIdentity()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ow-exposure-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "config.json");
            const string json = "{\"schemaVersion\":1,\"defaultProfile\":\"local\",\"archive\":{\"enabled\":true,\"cardsEnabled\":true,\"cardsProfile\":\"cloud\",\"cardsDailyTokens\":500000},\"profiles\":[{\"id\":\"local\",\"type\":\"ollama\",\"baseUrl\":\"http://127.0.0.1:11434\",\"model\":\"fixture\"},{\"id\":\"cloud\",\"type\":\"openai\",\"baseUrl\":\"https://provider.invalid/v1\",\"model\":\"fixture\",\"secretRef\":\"profile_cloud_token\",\"exposure\":\"Full\"}]}";
            File.WriteAllText(path, json);
            var config = new FileConfigurationService(directory, directory).LoadConfiguration();
            Assert.Equal(json, File.ReadAllText(path));
            var cloud = config.Profiles.Single(p => p.Id == "cloud");
            Assert.Equal(ExposureLevel.Standard, cloud.Exposure);
            Assert.Equal("profile_cloud_token", cloud.SecretRef);
            Assert.Equal("https://provider.invalid/v1", cloud.BaseUrl);
            Assert.Equal("fixture", cloud.Model);
            string serialized = JsonSerializer.Serialize(config, ConfigJson.Write);
            Assert.DoesNotContain("cardsEnabled", serialized);
            Assert.DoesNotContain("cardsProfile", serialized);
            Assert.DoesNotContain("cardsDailyTokens", serialized);
            Assert.DoesNotContain("\"Full\"", serialized);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PreviousNoticeRequiresRenewalAndItsSignedTextAndHistoryRemain()
    {
        var profile = new LlmProfileConfig { Id = "cloud", Type = "openai", BaseUrl = "https://provider.invalid/v1", Model = "fixture", Exposure = ExposureLevel.Standard };
        var config = TestEgress.Accepted(new WorkspaceConfig { Profiles = new() { profile } });
        var old = config.DataEgress.Consent!;
        old.NoticeVersion = 2;
        old.NoticeText = "Testo firmato della precedente informativa";
        old.NoticeHash = DataEgressNotice.HashOf(old.NoticeText);
        config.DataEgress.History[0].NoticeVersion = 2;
        config.DataEgress.History[0].NoticeHash = old.NoticeHash;
        string priorText = old.NoticeText;
        Assert.False(DataEgress.Current(old));
        Assert.False(DataEgress.AllowsModel(config, profile));
        Assert.Contains(priorText, DataEgressReport.RenderHtml(config, new DataEgressWorkstation()));
        DataEgress.Accept(config, TestEgress.Signer(), TestEgress.Providers(config), "fixture", "fixture", DateTime.UtcNow);
        Assert.True(DataEgress.AllowsModel(config, profile));
        Assert.Equal(4, config.DataEgress.Consent!.NoticeVersion);
        Assert.Equal(priorText, old.NoticeText);
        Assert.Equal(2, config.DataEgress.History.Count);
        Assert.Equal(2, config.DataEgress.History[0].NoticeVersion);
        Assert.Equal(old.NoticeHash, config.DataEgress.History[0].NoticeHash);
    }
}
