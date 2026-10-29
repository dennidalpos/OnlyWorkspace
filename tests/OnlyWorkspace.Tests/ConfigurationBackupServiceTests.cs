using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Security;
using Xunit;

namespace OnlyWorkspace.Tests;

public sealed class ConfigurationBackupServiceTests : IDisposable
{
    private readonly string _testDir;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectSave_ConfigFailureRecoversPair_AndRetryKeepsEditedVault(bool hadVault)
    {
        string target = Path.Combine(_testDir, "direct");
        Directory.CreateDirectory(target);
        string configPath = Path.Combine(target, "config.json"), vaultPath = Path.Combine(target, "secrets.bin");
        File.WriteAllText(configPath, "previous config");
        var vault = new DpapiSecretVault();
        vault.SetSecret("profile_old_token", "old-test-key");
        if (hadVault) vault.Save(vaultPath);
        byte[]? original = hadVault ? File.ReadAllBytes(vaultPath) : null;
        vault.RemoveSecret("profile_old_token");
        vault.SetSecret("profile_new_token", "new-test-key");
        var config = new WorkspaceConfig { DefaultProfile = "new", Profiles = new() { new() {
            Id = "new", Type = "ollama", BaseUrl = "http://localhost:11434", Model = "test" } } };
        using (var locked = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => ConfigurationBackupService.SaveConfiguration(target, config, vault));
        Assert.Equal("previous config", File.ReadAllText(configPath));
        if (hadVault) Assert.Equal(original, File.ReadAllBytes(vaultPath));
        else Assert.False(File.Exists(vaultPath));
        ConfigurationBackupService.SaveConfiguration(target, config, vault);
        var saved = new DpapiSecretVault();
        saved.Load(vaultPath);
        Assert.Equal("new-test-key", saved.GetSecret("profile_new_token"));
        Assert.False(saved.HasSecret("profile_old_token"));
        Assert.Equal("new", JsonSerializer.Deserialize<WorkspaceConfig>(File.ReadAllText(configPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.DefaultProfile);
        Assert.Empty(Directory.GetFiles(target, "*.restore-*"));
    }

    [Fact]
    public void PairRecoveryFailure_PreservesReadableBackup_AndNamesIt()
    {
        string target = Path.Combine(_testDir, "recovery-failure");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "config.json"), "old config");
        var vault = new DpapiSecretVault();
        vault.SetSecret("key", "old-test-key");
        vault.Save(Path.Combine(target, "secrets.bin"));
        byte[] old = File.ReadAllBytes(Path.Combine(target, "secrets.bin"));
        vault.SetSecret("key", "new-test-key");
        int writes = 0;
        var error = Assert.Throws<IOException>(() => ConfigurationBackupService.ReplacePair(target, new byte[] { 1 }, vault.ExportEncrypted(),
            (source, destination, backup) => { if (++writes > 1) throw new IOException("synthetic failure"); File.Replace(source, destination, backup); }));
        string retained = Assert.Single(Directory.GetFiles(target, "*.bak"));
        Assert.Contains(retained, error.Message);
        Assert.Equal(old, File.ReadAllBytes(retained));
        var recovered = new DpapiSecretVault();
        recovered.Load(retained);
        Assert.Equal("old-test-key", recovered.GetSecret("key"));
        Assert.Equal("old config", File.ReadAllText(Path.Combine(target, "config.json")));
        Assert.DoesNotContain("old-test-key", error.ToString());
        Assert.DoesNotContain("new-test-key", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstConfigWriteFailure_RestoresPreviousVaultPresence(bool hadVault)
    {
        string target = Path.Combine(_testDir, "first-failure");
        Directory.CreateDirectory(Path.Combine(target, "config.json")); // Fail the final move after vault replacement.
        string path = Path.Combine(target, "secrets.bin");
        var vault = new DpapiSecretVault();
        vault.SetSecret("key", "old-test-key");
        if (hadVault) vault.Save(path);
        byte[]? previous = hadVault ? File.ReadAllBytes(path) : null;
        vault.SetSecret("key", "new-test-key");
        Assert.ThrowsAny<IOException>(() => ConfigurationBackupService.ReplacePair(target, new byte[] { 1 }, vault.ExportEncrypted(), File.Replace));
        Assert.False(File.Exists(Path.Combine(target, "config.json")));
        if (hadVault) Assert.Equal(previous, File.ReadAllBytes(path));
        else Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(target, "*.restore-*"));
    }

    [Fact]
    public void DirectSave_FirstSaveMatchesElevatedStagedApply()
    {
        string staged = Path.Combine(_testDir, "staged"), target = Path.Combine(_testDir, "elevated");
        var vault = new DpapiSecretVault();
        vault.SetSecret("key", "synthetic-key");
        ConfigurationBackupService.SaveConfiguration(staged, new WorkspaceConfig { DefaultProfile = "p", Profiles = new() {
            new() { Id = "p", Type = "ollama", BaseUrl = "http://localhost:11434", Model = "test" } } }, vault);
        ConfigurationBackupService.ApplyStaged(staged, target);
        Assert.Equal(File.ReadAllBytes(Path.Combine(staged, "config.json")), File.ReadAllBytes(Path.Combine(target, "config.json")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(staged, "secrets.bin")), File.ReadAllBytes(Path.Combine(target, "secrets.bin")));
    }

    public ConfigurationBackupServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ow_test_backup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void CreateAndRestoreBackup_Roundtrip_Succeeds()
    {
        string sourceDir = Path.Combine(_testDir, "source");
        string restoreDir = Path.Combine(_testDir, "restored");
        string backupFile = Path.Combine(_testDir, "backup.owbak");

        Directory.CreateDirectory(sourceDir);

        var config = new WorkspaceConfig
        {
            DefaultProfile = "test-prof",
            Profiles = new()
            {
                new()
                {
                    Id = "test-prof",
                    DisplayName = "Test Profile",
                    Type = "ollama",
                    BaseUrl = "http://localhost:11434",
                    Model = "llama3"
                }
            }
        };

        File.WriteAllText(Path.Combine(sourceDir, "config.json"), JsonSerializer.Serialize(config));
        var vault = new DpapiSecretVault();
        vault.SetSecret("profile_test-prof_token", "s3cret");
        vault.Save(Path.Combine(sourceDir, "secrets.bin"));

        ConfigurationBackupService.CreateBackup(sourceDir, backupFile);

        Assert.True(File.Exists(backupFile));
        using (var zip = ZipFile.OpenRead(backupFile))
        {
            Assert.NotNull(zip.GetEntry("config.json"));
            Assert.NotNull(zip.GetEntry("secrets.bin"));
        }

        ConfigurationBackupService.RestoreBackup(backupFile, restoreDir);

        Assert.True(File.Exists(Path.Combine(restoreDir, "config.json")));
        Assert.True(File.Exists(Path.Combine(restoreDir, "secrets.bin")));

        string restoredJson = File.ReadAllText(Path.Combine(restoreDir, "config.json"));
        var restoredConfig = JsonSerializer.Deserialize<WorkspaceConfig>(restoredJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(restoredConfig);
        Assert.Equal("test-prof", restoredConfig.DefaultProfile);
        Assert.Single(restoredConfig.Profiles);

        var restoredVault = new DpapiSecretVault();
        restoredVault.Load(Path.Combine(restoreDir, "secrets.bin"));
        Assert.Equal("s3cret", restoredVault.GetSecret("profile_test-prof_token"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreBackup_UnreadableSecrets_ThrowsAndLeavesTargetUntouched(bool empty)
    {
        string sourceDir = Path.Combine(_testDir, "foreign");
        string targetDir = Path.Combine(_testDir, "target");
        string backupFile = Path.Combine(_testDir, "foreign.owbak");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        var config = new WorkspaceConfig
        {
            DefaultProfile = "p",
            Profiles = new() { new() { Id = "p", DisplayName = "P", Type = "ollama", BaseUrl = "http://localhost:11434", Model = "m" } }
        };
        File.WriteAllText(Path.Combine(sourceDir, "config.json"), JsonSerializer.Serialize(config));
        // Not a DPAPI blob: what a vault from another PC looks like here.
        File.WriteAllBytes(Path.Combine(sourceDir, "secrets.bin"), empty ? Array.Empty<byte>() : new byte[] { 1, 2, 3, 4, 5 });
        ConfigurationBackupService.CreateBackup(sourceDir, backupFile);

        File.WriteAllText(Path.Combine(targetDir, "config.json"), "current");
        File.WriteAllText(Path.Combine(targetDir, "secrets.bin"), "current");

        Assert.Throws<InvalidOperationException>(() => ConfigurationBackupService.ValidateBackup(backupFile));
        Assert.Throws<InvalidOperationException>(() => ConfigurationBackupService.RestoreBackup(backupFile, targetDir));
        Assert.Throws<InvalidOperationException>(() => ConfigurationBackupService.ApplyStaged(sourceDir, targetDir));
        Assert.Equal("current", File.ReadAllText(Path.Combine(targetDir, "config.json")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(targetDir, "secrets.bin")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestoreBackup_ConfigReplacementFailure_RollsBackSecrets(bool existingSecrets)
    {
        string sourceDir = Path.Combine(_testDir, "rollback-source");
        string targetDir = Path.Combine(_testDir, "rollback-target");
        string backupFile = Path.Combine(_testDir, "rollback.owbak");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        var config = new WorkspaceConfig
        {
            DefaultProfile = "p",
            Profiles = new() { new() { Id = "p", DisplayName = "P", Type = "ollama", BaseUrl = "http://localhost:11434", Model = "m" } }
        };
        File.WriteAllText(Path.Combine(sourceDir, "config.json"), JsonSerializer.Serialize(config));
        var sourceVault = new DpapiSecretVault();
        sourceVault.SetSecret("token", "new");
        sourceVault.Save(Path.Combine(sourceDir, "secrets.bin"));
        ConfigurationBackupService.CreateBackup(sourceDir, backupFile);

        string targetConfig = Path.Combine(targetDir, "config.json");
        string targetSecrets = Path.Combine(targetDir, "secrets.bin");
        File.WriteAllText(targetConfig, "current");
        byte[]? oldSecrets = null;
        if (existingSecrets)
        {
            var targetVault = new DpapiSecretVault();
            targetVault.SetSecret("token", "old");
            targetVault.Save(targetSecrets);
            oldSecrets = File.ReadAllBytes(targetSecrets);
        }

        // Read sharing permits the snapshot but denies File.Replace on config.json.
        using (var lockedConfig = new FileStream(targetConfig, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => ConfigurationBackupService.RestoreBackup(backupFile, targetDir));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.Equal("current", File.ReadAllText(targetConfig));
        if (existingSecrets) Assert.Equal(oldSecrets, File.ReadAllBytes(targetSecrets));
        else Assert.False(File.Exists(targetSecrets));
        Assert.Empty(Directory.GetFiles(targetDir, "*.restore-*"));

        ConfigurationBackupService.RestoreBackup(backupFile, targetDir);
        Assert.Contains("\"DefaultProfile\":\"p\"", File.ReadAllText(targetConfig));
        var restoredVault = new DpapiSecretVault();
        restoredVault.Load(targetSecrets);
        Assert.Equal("new", restoredVault.GetSecret("token"));
        Assert.Empty(Directory.GetFiles(targetDir, "*.restore-*"));
    }

    [Fact]
    public void RestoreBackup_NotAZip_ThrowsInvalidOperationException()
    {
        string notZip = Path.Combine(_testDir, "bad.owbak");
        File.WriteAllText(notZip, "not a zip");

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBackupService.RestoreBackup(notZip, Path.Combine(_testDir, "target")));
    }

    [Fact]
    public void RestoreBackup_CorruptedOrMissingConfig_ThrowsInvalidOperationException()
    {
        string emptyZip = Path.Combine(_testDir, "empty.zip");
        string targetDir = Path.Combine(_testDir, "target");
        string dummyTemp = Path.Combine(_testDir, "dummy");
        Directory.CreateDirectory(dummyTemp);
        File.WriteAllText(Path.Combine(dummyTemp, "random.txt"), "hello");
        ZipFile.CreateFromDirectory(dummyTemp, emptyZip);

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBackupService.RestoreBackup(emptyZip, targetDir));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("extra.txt")]
    [InlineData("config.json")]
    public void RestoreBackup_UnexpectedOrDuplicateEntry_LeavesTargetUntouched(string entryName)
    {
        string backupFile = Path.Combine(_testDir, "unexpected.owbak");
        string targetDir = Path.Combine(_testDir, "target");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "config.json"), "current");
        using (var zip = ZipFile.Open(backupFile, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("config.json").Open()))
                writer.Write(JsonSerializer.Serialize(new WorkspaceConfig()));
            using (var writer = new StreamWriter(zip.CreateEntry(entryName).Open()))
                writer.Write("unexpected");
        }

        Assert.Throws<InvalidOperationException>(() => ConfigurationBackupService.RestoreBackup(backupFile, targetDir));
        Assert.Equal("current", File.ReadAllText(Path.Combine(targetDir, "config.json")));
        Assert.False(File.Exists(Path.Combine(_testDir, "outside.txt")));
    }

    [Fact]
    public void RestoreBackup_OversizedEntry_LeavesTargetUntouched()
    {
        string backupFile = Path.Combine(_testDir, "oversized.owbak");
        string targetDir = Path.Combine(_testDir, "target");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "config.json"), "current");
        using (var zip = ZipFile.Open(backupFile, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("config.json").Open())
        {
            byte[] block = new byte[1024 * 1024];
            for (int i = 0; i < 5; i++) stream.Write(block, 0, block.Length);
        }

        Assert.Throws<InvalidOperationException>(() => ConfigurationBackupService.RestoreBackup(backupFile, targetDir));
        Assert.Equal("current", File.ReadAllText(Path.Combine(targetDir, "config.json")));
    }

    [Fact]
    public void CreateBackup_MissingSourceOrConfig_DoesNotCreateArchive()
    {
        string sourceDir = Path.Combine(_testDir, "source");
        string backupFile = Path.Combine(_testDir, "missing.owbak");

        Assert.Throws<DirectoryNotFoundException>(() => ConfigurationBackupService.CreateBackup(sourceDir, backupFile));
        Directory.CreateDirectory(sourceDir);
        Assert.Throws<FileNotFoundException>(() => ConfigurationBackupService.CreateBackup(sourceDir, backupFile));
        Assert.False(File.Exists(backupFile));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"profiles\":[]}")]
    public void RestoreBackup_InvalidConfig_LeavesTargetUntouched(string invalidConfig)
    {
        string backupFile = Path.Combine(_testDir, "invalid.owbak");
        string targetDir = Path.Combine(_testDir, "target");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "config.json"), "current");
        using (var zip = ZipFile.Open(backupFile, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("config.json").Open()))
            writer.Write(invalidConfig);

        Assert.Throws<InvalidOperationException>(() => ConfigurationBackupService.RestoreBackup(backupFile, targetDir));
        Assert.Equal("current", File.ReadAllText(Path.Combine(targetDir, "config.json")));
        Assert.Empty(Directory.GetFiles(targetDir, "*.restore-*"));
    }

    [Theory]
    [InlineData("config.json", 4)]
    [InlineData("secrets.bin", 16)]
    public void CreateBackup_OversizedSource_DoesNotCreateArchive(string fileName, int limitMb)
    {
        string sourceDir = Path.Combine(_testDir, "source");
        string backupFile = Path.Combine(_testDir, "oversized-source.owbak");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "config.json"), JsonSerializer.Serialize(new WorkspaceConfig()));
        File.WriteAllBytes(Path.Combine(sourceDir, fileName), new byte[(limitMb + 1) * 1024 * 1024]);

        Assert.Throws<InvalidOperationException>(() => ConfigurationBackupService.CreateBackup(sourceDir, backupFile));
        Assert.False(File.Exists(backupFile));
    }
}
