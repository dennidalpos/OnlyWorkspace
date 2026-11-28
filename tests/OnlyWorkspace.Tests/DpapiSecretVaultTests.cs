using System.IO;
using System.Security.Cryptography;
using OnlyWorkspace.Infrastructure.Security;
using Xunit;

namespace OnlyWorkspace.Tests;

public class DpapiSecretVaultTests
{
    [Fact]
    public void PresentEmptyVault_PreservesKeys_ButEncryptedEmptyAndAbsenceClearThem()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vault_empty_{System.Guid.NewGuid():N}.bin");
        try
        {
            var vault = new DpapiSecretVault();
            vault.SetSecret("synthetic", "old");
            File.WriteAllBytes(path, System.Array.Empty<byte>());
            for (int i = 0; i < 2; i++)
            {
                Assert.Throws<CryptographicException>(() => vault.Load(path));
                Assert.Equal("old", vault.GetSecret("synthetic"));
            }
            new DpapiSecretVault().Save(path);
            vault.Load(path);
            Assert.Empty(vault.Keys);
            vault.SetSecret("synthetic", "old");
            File.Delete(path);
            vault.Load(path);
            Assert.Empty(vault.Keys);
        }
        finally { if (File.Exists(path)) TestArtifacts.DeleteFile(path); }
    }

    /// <summary>CRYPTPROTECT_LOCAL_MACHINE (0x4) sits in the flags of the DPAPI blob header (offset 40): with it any local
    /// user can decrypt. The default vault must not set it, and a vault of the previous format must be refused.</summary>
    [Fact]
    public void DefaultVault_IsPerUser_AndRefusesTheLocalMachineVaultOfEarlierBuilds()
    {
        const int flagsOffset = 40;
        var vault = new DpapiSecretVault();
        vault.SetSecret("synthetic", "value");
        Assert.Equal(0, vault.ExportEncrypted()[flagsOffset] & 0x4);
        var machine = new DpapiSecretVault(DataProtectionScope.LocalMachine);
        machine.SetSecret("synthetic", "value");
        Assert.NotEqual(0, machine.ExportEncrypted()[flagsOffset] & 0x4);

        byte[] previous = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes("{\"synthetic\":\"value\"}"),
            System.Text.Encoding.UTF8.GetBytes("OnlyWorkspace-MachineVault-Entropy-v1"), DataProtectionScope.LocalMachine);
        Assert.ThrowsAny<CryptographicException>(() => new DpapiSecretVault().Load(previous));
    }

    [Fact]
    public void SecretsFolder_IsPerUserForTheStandardFolderAndTogetherForAChosenOne()
    {
        Assert.Equal(OnlyWorkspace.Core.AppPaths.LocalDirectory, OnlyWorkspace.Core.AppPaths.SecretsDirectoryFor(null));
        Assert.Equal(OnlyWorkspace.Core.AppPaths.LocalDirectory, OnlyWorkspace.Core.AppPaths.SecretsDirectoryFor(OnlyWorkspace.Core.AppPaths.ConfigDirectory + "\\"));
        string chosen = Path.Combine(Path.GetTempPath(), "ow_chosen");
        Assert.Equal(chosen, OnlyWorkspace.Core.AppPaths.SecretsDirectoryFor(chosen));
        Assert.False(OnlyWorkspace.Core.AppPaths.IsStandardConfigDirectory(chosen));
    }

    [Fact]
    public void SaveAndLoad_RoundtripsEncryptedSecrets()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"vault_test_{System.Guid.NewGuid():N}.bin");
        try
        {
            var vault = new DpapiSecretVault(DataProtectionScope.CurrentUser);
            vault.SetSecret("profile_lan_token", "secret-token-12345");
            vault.SetSecret("api_key", "sk-live-67890");
            vault.Save(tempFile);

            // Verify ciphertext
            var rawBytes = File.ReadAllBytes(tempFile);
            var rawString = System.Text.Encoding.UTF8.GetString(rawBytes);
            Assert.DoesNotContain("secret-token-12345", rawString);
            Assert.DoesNotContain("sk-live-67890", rawString);

            // Verify reload
            var loader = new DpapiSecretVault(DataProtectionScope.CurrentUser);
            loader.Load(tempFile);

            Assert.True(loader.HasSecret("profile_lan_token"));
            Assert.Equal("secret-token-12345", loader.GetSecret("profile_lan_token"));
            Assert.Equal("sk-live-67890", loader.GetSecret("api_key"));
        }
        finally
        {
            if (File.Exists(tempFile)) TestArtifacts.DeleteFile(tempFile);
        }
    }
}
