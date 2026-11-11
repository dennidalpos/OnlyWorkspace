using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Tests;

/// <summary>#177: vault alignment of the ConfigTool profiles, without a form or DPAPI.</summary>
public sealed class ProfileSecretSyncTests
{
    private sealed class MemoryVault : ISecretVault
    {
        public Dictionary<string, string> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void SetSecret(string key, string secret) => Items[key] = secret;
        public string? GetSecret(string key) => Items.TryGetValue(key, out var v) ? v : null;
        public bool HasSecret(string key) => Items.ContainsKey(key);
        public void RemoveSecret(string key) => Items.Remove(key);
        public void Save(string filePath) { }
        public void Load(string filePath) { }
    }

    private static WorkspaceConfig Config() => new()
    {
        DefaultProfile = "a",
        Profiles = { new LlmProfileConfig { Id = "a", SecretRef = "old", WebSearchEnabled = true, WebSearchSecretRef = "profile_a_web_search" }, new LlmProfileConfig { Id = "b" } }
    };

    [Fact]
    public void TypedKey_GetsItsReference_AndAnEmptiedOneIsRemoved()
    {
        var vault = new MemoryVault();
        vault.Items["profile_b_token"] = "stale";
        var config = Config();

        ProfileSecretSync.Apply(config, new Dictionary<string, string> { ["a"] = "key-a", ["b"] = "  " },
            new Dictionary<string, string>(), vault, () => vault.Items.Keys.ToList());

        Assert.Equal("profile_a_token", config.Profiles[0].SecretRef);
        Assert.Equal("key-a", vault.GetSecret("profile_a_token"));
        Assert.Equal(string.Empty, config.Profiles[1].SecretRef);
        Assert.False(vault.HasSecret("profile_b_token"));
    }

    [Fact]
    public void WebKey_IsStoredOrClearedTogetherWithTheSearchFlag()
    {
        var vault = new MemoryVault { Items = { ["profile_a_web_search"] = "old-web" } };
        var config = Config();
        config.Profiles[1].WebSearchEnabled = true;

        ProfileSecretSync.Apply(config, new Dictionary<string, string>(),
            new Dictionary<string, string> { ["a"] = "", ["b"] = "web-b", ["gone"] = "x" }, vault, () => vault.Items.Keys.ToList());

        Assert.Equal(string.Empty, config.Profiles[0].WebSearchSecretRef);
        Assert.False(config.Profiles[0].WebSearchEnabled);
        Assert.False(vault.HasSecret("profile_a_web_search"));
        Assert.Equal("profile_b_web_search", config.Profiles[1].WebSearchSecretRef);
        Assert.Equal("web-b", vault.GetSecret("profile_b_web_search"));
        Assert.True(config.Profiles[1].WebSearchEnabled);
    }

    [Fact]
    public void KeysOfRemovedProfiles_LeaveTheVault_OtherKeysStay()
    {
        var vault = new MemoryVault { Items = { ["profile_gone_token"] = "x", ["profile_gone_web_search"] = "y", ["unrelated"] = "z" } };
        var config = Config();

        ProfileSecretSync.Apply(config, new Dictionary<string, string> { ["a"] = "k" }, new Dictionary<string, string>(), vault, () => vault.Items.Keys.ToList());

        Assert.False(vault.HasSecret("profile_gone_token"));
        Assert.False(vault.HasSecret("profile_gone_web_search"));
        Assert.True(vault.HasSecret("unrelated"));
        Assert.True(vault.HasSecret("profile_a_token"));
    }
}
