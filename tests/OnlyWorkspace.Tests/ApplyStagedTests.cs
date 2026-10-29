using System.Text.Json;
using OnlyWorkspace.ConfigTool;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

/// <summary>
/// The elevated apply-staged command on isolated local copies: config.json and secrets.bin change together or
/// not at all, and every failure exits non-zero with its reason in the result file the GUI reads.
/// </summary>
public sealed class ApplyStagedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ow_test_apply_staged_" + Guid.NewGuid().ToString("N"));
    private readonly string _staging;
    private readonly string _target;

    public ApplyStagedTests()
    {
        _staging = Path.Combine(_root, "staging");
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_staging);
        Directory.CreateDirectory(_target);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private string TargetConfig => Path.Combine(_target, "config.json");
    private string TargetSecrets => Path.Combine(_target, "secrets.bin");
    private string ResultText => File.ReadAllText(Path.Combine(_staging, ElevatedConfigWriter.ResultFileName));

    private static string ConfigJson(string profileId) => JsonSerializer.Serialize(new WorkspaceConfig
    {
        DefaultProfile = profileId,
        Profiles = new() { new() { Id = profileId, DisplayName = "P", Type = "ollama", BaseUrl = "http://localhost:11434", Model = "m" } }
    });

    private static byte[] Vault(string token)
    {
        var vault = new DpapiSecretVault();
        vault.SetSecret("token", token);
        string path = Path.GetTempFileName();
        try
        {
            vault.Save(path);
            return File.ReadAllBytes(path);
        }
        finally { File.Delete(path); }
    }

    private static string TokenIn(string path)
    {
        var vault = new DpapiSecretVault();
        vault.Load(path);
        return vault.GetSecret("token")!;
    }

    private (string Config, byte[] Secrets) SeedTarget()
    {
        File.WriteAllText(TargetConfig, ConfigJson("old"));
        File.WriteAllBytes(TargetSecrets, Vault("old"));
        return (File.ReadAllText(TargetConfig), File.ReadAllBytes(TargetSecrets));
    }

    private void Stage(string? config, byte[]? secrets)
    {
        if (config != null) File.WriteAllText(Path.Combine(_staging, "config.json"), config);
        if (secrets != null) File.WriteAllBytes(Path.Combine(_staging, "secrets.bin"), secrets);
    }

    private int Run() => Program.ApplyStaged(new[] { "apply-staged", _staging, _target });

    private void AssertUntouched((string Config, byte[] Secrets) previous)
    {
        Assert.Equal(previous.Config, File.ReadAllText(TargetConfig));
        Assert.Equal(previous.Secrets, File.ReadAllBytes(TargetSecrets));
        Assert.DoesNotContain(Directory.GetFiles(_target), f => !f.EndsWith("config.json") && !f.EndsWith("secrets.bin"));
    }

    [Fact]
    public void ValidStaging_ReplacesThePair()
    {
        SeedTarget();
        Stage(ConfigJson("new"), Vault("new"));

        Assert.Equal(0, Run());
        Assert.Equal("OK", ResultText);
        Assert.Contains("\"DefaultProfile\":\"new\"", File.ReadAllText(TargetConfig));
        Assert.Equal("new", TokenIn(TargetSecrets));
        Assert.Equal(2, Directory.GetFiles(_target).Length);
    }

    [Fact]
    public void StagingWithoutSecrets_KeepsTheCurrentVault()
    {
        var previous = SeedTarget();
        Stage(ConfigJson("new"), null);

        Assert.Equal(0, Run());
        Assert.Contains("\"DefaultProfile\":\"new\"", File.ReadAllText(TargetConfig));
        Assert.Equal(previous.Secrets, File.ReadAllBytes(TargetSecrets));
    }

    [Fact]
    public void MissingStagedConfig_FailsWithoutWriting()
    {
        var previous = SeedTarget();
        Stage(null, Vault("new"));

        Assert.NotEqual(0, Run());
        Assert.Contains("configurazione da applicare", ResultText);
        AssertUntouched(previous);
    }

    [Fact]
    public void MissingStagingDirectoryOrArguments_FailNonZero()
    {
        var previous = SeedTarget();
        Directory.Delete(_staging);

        Assert.NotEqual(0, Run());
        Assert.NotEqual(0, Program.ApplyStaged(new[] { "apply-staged", _staging }));
        AssertUntouched(previous);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"profiles\":[]}")]
    public void CorruptOrInvalidStagedConfig_FailsWithoutWriting(string config)
    {
        var previous = SeedTarget();
        Stage(config, Vault("new"));

        Assert.NotEqual(0, Run());
        Assert.NotEqual("OK", ResultText);
        AssertUntouched(previous);
    }

    [Fact]
    public void UnreadableStagedSecrets_FailWithoutWriting()
    {
        var previous = SeedTarget();
        // Not a DPAPI blob: a damaged staging file must not replace a working vault.
        Stage(ConfigJson("new"), new byte[] { 1, 2, 3, 4, 5 });

        Assert.NotEqual(0, Run());
        AssertUntouched(previous);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfigWriteFailure_RestoresThePreviousPair(bool existingSecrets)
    {
        File.WriteAllText(TargetConfig, ConfigJson("old"));
        byte[]? oldSecrets = existingSecrets ? Vault("old") : null;
        if (oldSecrets != null) File.WriteAllBytes(TargetSecrets, oldSecrets);
        string oldConfig = File.ReadAllText(TargetConfig);
        Stage(ConfigJson("new"), Vault("new"));

        // Read sharing lets the staging be validated but denies File.Replace on config.json.
        using (new FileStream(TargetConfig, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotEqual(0, Run());
        }

        Assert.Equal(oldConfig, File.ReadAllText(TargetConfig));
        if (oldSecrets != null) Assert.Equal(oldSecrets, File.ReadAllBytes(TargetSecrets));
        else Assert.False(File.Exists(TargetSecrets));
        Assert.Empty(Directory.GetFiles(_target, "*.restore-*"));

        Assert.Equal(0, Run());
        Assert.Equal("new", TokenIn(TargetSecrets));
    }

    [Fact]
    public void FailedRollback_KeepsThePreviousVaultAndNamesIt()
    {
        var previous = SeedTarget();
        byte[] newSecrets = Vault("new");
        int replaces = 0;
        // First replace (vault) succeeds, then config.json and the vault rollback both fail.
        void Replace(string source, string destination, string? backup)
        {
            if (++replaces > 1) throw new IOException("simulated write failure");
            File.Replace(source, destination, backup);
        }

        var error = Assert.Throws<IOException>(() =>
            ConfigurationBackupService.ReplacePair(_target, System.Text.Encoding.UTF8.GetBytes(ConfigJson("new")), newSecrets, Replace));

        string kept = Assert.Single(Directory.GetFiles(_target, "secrets.bin.restore-*.bak"));
        Assert.Contains(kept, error.Message);
        Assert.Equal(previous.Secrets, File.ReadAllBytes(kept));
        Assert.Equal(previous.Config, File.ReadAllText(TargetConfig));
        Assert.DoesNotContain(Directory.GetFiles(_target, "*.restore-*"), f => f != kept);
    }

    [Fact]
    public void StagingDirectory_IsRecognizedByShape_InAnyUsersTempFolder()
    {
        // Over-the-shoulder UAC: the folder sits in the standard user's %TEMP%, not in the elevated account's.
        string otherUsersTemp = Path.Combine(_root, "Users", "standard", "AppData", "Local", "Temp");
        string staging = Path.Combine(otherUsersTemp, "OnlyWorkspace-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        Assert.True(ElevatedConfigWriter.IsStagingDirectory(staging));
        Assert.False(ElevatedConfigWriter.IsStagingDirectory(_staging));
        Assert.False(ElevatedConfigWriter.IsStagingDirectory(Path.Combine(otherUsersTemp, "OnlyWorkspace-staging-x")));
        Assert.False(ElevatedConfigWriter.IsStagingDirectory(Path.Combine(otherUsersTemp, "OnlyWorkspace-staging-" + Guid.NewGuid().ToString("N"))));
    }

    /// <summary>A failed restore through UAC must hand its real cause back to the GUI, not just an exit code.</summary>
    [Fact]
    public void RestoreAuthorized_WritesTheCause_ToTheStagingFolder()
    {
        string staging = ElevatedConfigWriter.CreateStagingDirectory();
        try
        {
            string backup = Path.Combine(_root, "broken.owbak");
            File.WriteAllText(backup, "not a zip");
            File.WriteAllText(TargetConfig, ConfigJson("kept"));

            Assert.Equal(1, Program.RestoreAuthorized(new[] { "restore-authorized", backup, _target, staging }));
            string cause = File.ReadAllText(Path.Combine(staging, ElevatedConfigWriter.ResultFileName));
            Assert.False(string.IsNullOrWhiteSpace(cause));
            Assert.NotEqual("OK", cause);
            Assert.Equal(ConfigJson("kept"), File.ReadAllText(TargetConfig));

            // Only a folder made by CreateStagingDirectory receives the result.
            Assert.Equal(1, Program.RestoreAuthorized(new[] { "restore-authorized", backup, _target, _staging }));
            Assert.False(File.Exists(Path.Combine(_staging, ElevatedConfigWriter.ResultFileName)));
        }
        finally { ElevatedConfigWriter.DeleteStagingDirectory(staging); }
    }

    [Fact]
    public void MissingConfigFolder_NeedsElevation_AndIsCreatedWithTheInstallerAcl()
    {
        string dir = Path.Combine(_root, "ProgramData", "OnlyWorkspace");
        // %ProgramData% always exists: only the leaf gets the protected ACL, so the test also runs without elevation.
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        Assert.False(ElevatedConfigWriter.CanWriteConfig(dir));
        Assert.False(Directory.Exists(dir));

        ElevatedConfigWriter.EnsureProtectedDirectory(dir);
#if NETFRAMEWORK
        var acl = Directory.GetAccessControl(dir);
#else
        var acl = new DirectoryInfo(dir).GetAccessControl();
#endif
        Assert.True(acl.AreAccessRulesProtected);
        var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .ToDictionary(r => r.IdentityReference.Value, r => r.FileSystemRights);
        Assert.Equal(3, rules.Count);
        Assert.Equal(System.Security.AccessControl.FileSystemRights.FullControl, rules["S-1-5-32-544"]);
        Assert.Equal(System.Security.AccessControl.FileSystemRights.FullControl, rules["S-1-5-18"]);
        Assert.Equal(0, (int)(rules["S-1-5-32-545"] & (System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData)));
    }
}
