using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Interfaces;

#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace OnlyWorkspace.Infrastructure.Security;

#if NET5_0_OR_GREATER
[SupportedOSPlatform("windows")]
#endif
public sealed class DpapiSecretVault : ISecretVault
{
    // v2: per-user vault. A v1 (LocalMachine) file fails to open instead of being converted.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OnlyWorkspace-UserVault-Entropy-v2");
    private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);
    private readonly DataProtectionScope _scope;

    public DpapiSecretVault(DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        _scope = scope;
    }

    public void SetSecret(string key, string secret)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        _secrets[key] = secret ?? string.Empty;
    }

    public string? GetSecret(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _secrets.TryGetValue(key, out var val) ? val : null;
    }

    public bool HasSecret(string key) => !string.IsNullOrWhiteSpace(key) && _secrets.ContainsKey(key);

    public IReadOnlyCollection<string> Keys => _secrets.Keys.ToList();

    public void Clear() => _secrets.Clear();

    public void RemoveSecret(string key)
    {
        if (!string.IsNullOrWhiteSpace(key)) _secrets.Remove(key);
    }

    public void Save(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Configuration.AtomicFile.WriteAllBytes(filePath, ExportEncrypted());
    }

    internal byte[] ExportEncrypted() => ProtectedData.Protect(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_secrets)), Entropy, _scope);

    public void Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _secrets.Clear();
            return;
        }

        Load(File.ReadAllBytes(filePath));
    }

    internal void Load(byte[] cipherBytes)
    {
        // Decrypt first: an unreadable vault leaves the secrets already loaded in place.
        if (cipherBytes.Length == 0) throw new CryptographicException("Il file delle credenziali è vuoto o danneggiato.");
        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(ProtectedData.Unprotect(cipherBytes, Entropy, _scope)));
        _secrets.Clear();
        if (loaded != null)
        {
            foreach (var kvp in loaded)
            {
                _secrets[kvp.Key] = kvp.Value;
            }
        }
    }
}
