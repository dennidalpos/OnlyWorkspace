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
        finally { if (File.Exists(path)) File.Delete(path); }
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
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
