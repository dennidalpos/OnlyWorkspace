using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OnlyWorkspace.Infrastructure.Configuration;

#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace OnlyWorkspace.Infrastructure.Archive;

/// <summary>Random key of the archive database, kept next to it under CurrentUser DPAPI.</summary>
#if NET5_0_OR_GREATER
[SupportedOSPlatform("windows")]
#endif
public static class ArchiveKey
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OnlyWorkspace-Archive-Key-v1");

    /// <summary>Key as text for the connection string; created on first use. Null when the file exists but cannot be decrypted.</summary>
    public static string? LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                return Convert.ToBase64String(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        var key = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        return Convert.ToBase64String(key);
    }
}
