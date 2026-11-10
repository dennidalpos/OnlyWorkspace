using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Validation;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

public sealed class ProviderCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ow-provider-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExactlySixPresetsOnFourProtocols_GenericRequiresAnAddress()
    {
        Assert.Equal(new[] { "ollama", "gemini", "anthropic", "openai", "qwen", "openai-compatible" }, ProviderCatalog.All.Select(p => p.Type));
        Assert.Equal(4, ProviderCatalog.All.Select(p => p.Family).Distinct().Count());
        var generic = ProviderCatalog.Get(ProviderCatalog.OpenAICompatible);
        Assert.Equal(string.Empty, generic.DefaultBaseUrl);
        Assert.Equal(new[] { "none", "bearer" }, generic.AuthTypes);
        Assert.Equal(WebSearchMode.None, generic.WebSearch);
        Assert.Equal(CapabilitySource.None, generic.Capabilities);
        Assert.Equal("chat/completions", generic.ChatPath);
        Assert.False(ConfigurationValidator.Validate(Config(new() { Id = "p", Type = generic.Type, BaseUrl = "" })).IsValid);
    }

    [Theory]
    [InlineData("lmstudio", false)]
    [InlineData("llamacpp", false)]
    [InlineData("vllm", false)]
    [InlineData("localai", false)]
    [InlineData("jan", false)]
    [InlineData("mistral", true)]
    [InlineData("xai", true)]
    [InlineData("deepseek", true)]
    [InlineData("openrouter", true)]
    [InlineData("groq", true)]
    [InlineData("together", true)]
    [InlineData("fireworks", true)]
    public void RetiredPresets_NormalizeOnLoadWithoutWriting_AndRoundTripVault(string type, bool external)
    {
        Directory.CreateDirectory(_directory);
        var profile = Profile(type);
        profile.ModelCapabilities = ModelCapabilitiesCache.For(profile);
        var config = Config(profile);
        string oldPolicy = ConversationPolicy.Key(profile);
        string path = Path.Combine(_directory, "config.json");
        string original = JsonSerializer.Serialize(config);
        File.WriteAllText(path, original);
        var vault = new DpapiSecretVault();
        vault.SetSecret("preserved-token", "synthetic-provider-token");
        vault.SetSecret("preserved-web", "synthetic-web-token");
        vault.Save(Path.Combine(_directory, "secrets.bin"));
        byte[] originalVault = File.ReadAllBytes(Path.Combine(_directory, "secrets.bin"));
        var service = new FileConfigurationService(_directory, _directory);
        var loaded = service.LoadConfiguration();
        var migrated = Assert.Single(loaded.Profiles);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(originalVault, File.ReadAllBytes(Path.Combine(_directory, "secrets.bin")));
        Assert.Equal(ProviderCatalog.OpenAICompatible, migrated.Type);
        Assert.Equal(external, migrated.External);
        Assert.Null(migrated.ModelCapabilities);
        Assert.NotEqual(oldPolicy, ConversationPolicy.Key(migrated));
        Assert.Equal(profile.Id, migrated.Id);
        Assert.Equal(profile.DisplayName, migrated.DisplayName);
        Assert.Equal(profile.BaseUrl, migrated.BaseUrl);
        Assert.Equal(profile.Model, migrated.Model);
        Assert.Equal(profile.AuthType, migrated.AuthType);
        Assert.Equal(profile.SecretRef, migrated.SecretRef);
        Assert.Equal(profile.WebSearchSecretRef, migrated.WebSearchSecretRef);
        Assert.True(migrated.WebSearchEnabled);
        Assert.Equal(profile.WebSearchActions, migrated.WebSearchActions);
        Assert.True(migrated.PseudonymizeAmounts);
        Assert.True(migrated.AllowAttachmentUpload);
        Assert.False(ProviderCompatibility.Normalize(loaded));
        Assert.Contains(ConfigurationValidator.Validate(loaded).Warnings, w => w.Contains("ricerca web non disponibili"));
        ConfigurationBackupService.SaveConfiguration(_directory, loaded, vault);
        var reopened = new FileConfigurationService(_directory, _directory);
        Assert.Equal(ProviderCatalog.OpenAICompatible, reopened.LoadConfiguration().Profiles.Single().Type);
        Assert.Equal("synthetic-provider-token", reopened.ResolveSecret("preserved-token"));
        Assert.Equal("synthetic-web-token", reopened.ResolveSecret("preserved-web"));
        Assert.DoesNotContain("synthetic-provider-token", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("azure-openai")]
    [InlineData("perplexity")]
    [InlineData("ollama-cloud")]
    public void LegacyProtocolsRemainRecognizedButAreNotNewPresets(string type)
    {
        var profile = Profile(type);
        Assert.False(ProviderCompatibility.Normalize(profile));
        Assert.Equal(type, profile.Type);
        Assert.True(ProviderCatalog.IsLegacy(type));
        Assert.True(ProviderCatalog.IsKnown(type));
        Assert.DoesNotContain(ProviderCatalog.All, p => p.Type == type);
    }

    [Fact]
    public void MigratingProviderInvalidatesPriorAuthorizationAndConversationPolicy()
    {
        var profile = Profile("xai");
        profile.BaseUrl = "https://synthetic.example.test/v1";
        var config = TestEgress.Accepted(Config(profile));
        Assert.True(DataEgress.AllowsModel(config, profile, TestEgress.ResolveSecret));
        var conversation = ConversationPolicy.Create(profile, "synthetic");
        Assert.True(ProviderCompatibility.Normalize(config));
        Assert.False(DataEgress.AllowsModel(config, profile, TestEgress.ResolveSecret));
        Assert.False(ConversationPolicy.Matches(conversation, profile));
    }

    [Fact]
    public void BackupAndStagedRestoreNormalizeOnlyTheDestination_AndKeepCredentials()
    {
        Directory.CreateDirectory(_directory);
        string source = Path.Combine(_directory, "source"), restored = Path.Combine(_directory, "restored"), staged = Path.Combine(_directory, "staged");
        Directory.CreateDirectory(source);
        string original = JsonSerializer.Serialize(Config(Profile("OpenRouter")));
        File.WriteAllText(Path.Combine(source, "config.json"), original);
        var vault = new DpapiSecretVault();
        vault.SetSecret("preserved-token", "synthetic-provider-token");
        vault.Save(Path.Combine(source, "secrets.bin"));
        string backup = Path.Combine(_directory, "source.owbak");
        ConfigurationBackupService.CreateBackup(source, backup);
        byte[] backupBytes = File.ReadAllBytes(backup);
        ConfigurationBackupService.ValidateBackup(backup);
        ConfigurationBackupService.RestoreBackup(backup, restored);
        ConfigurationBackupService.ApplyStaged(source, staged, ConfigurationBackupService.StagedDigest(Path.Combine(source, "config.json")), ConfigurationBackupService.StagedDigest(Path.Combine(source, "secrets.bin")));
        foreach (string target in new[] { restored, staged })
        {
            var service = new FileConfigurationService(target, target);
            Assert.Equal(ProviderCatalog.OpenAICompatible, service.LoadConfiguration().Profiles.Single().Type);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "config.json")));
            Assert.Equal(ProviderCatalog.OpenAICompatible, json.RootElement.GetProperty("profiles")[0].GetProperty("type").GetString());
            Assert.Equal("synthetic-provider-token", service.ResolveSecret("preserved-token"));
        }
        Assert.Equal(original, File.ReadAllText(Path.Combine(source, "config.json")));
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
    }

    private static LlmProfileConfig Profile(string type) => new()
    {
        Id = "preserved",
        DisplayName = "Profilo sintetico",
        Type = type,
        BaseUrl = "http://127.0.0.1:1234/v1",
        Model = "synthetic/model",
        AuthType = "bearer",
        SecretRef = "preserved-token",
        WebSearchSecretRef = "preserved-web",
        WebSearchEnabled = true,
        PseudonymizeAmounts = true,
        AllowAttachmentUpload = true
    };

    private static WorkspaceConfig Config(LlmProfileConfig profile) => new() { DefaultProfile = profile.Id, Profiles = new() { profile } };

    public void Dispose() { if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true); }
}
