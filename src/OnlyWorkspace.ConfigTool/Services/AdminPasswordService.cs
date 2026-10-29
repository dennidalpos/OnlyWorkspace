using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>Optional application write guard. Windows administrators retain control through UAC.</summary>
public sealed class AdminPasswordService
{
    private const int Iterations = 600000;
    private readonly string _path;

    private sealed class Verifier
    {
        public int Version { get; set; } = 1;
        public int Iterations { get; set; }
        public string Salt { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }

    public AdminPasswordService(string configDir) => _path = Path.Combine(configDir, "admin-password.json");
    public bool Enabled => File.Exists(_path);

    public bool Verify(string password)
    {
        if (!Enabled) return true;
        var record = Read(File.ReadAllBytes(_path));
        byte[] expected = Convert.FromBase64String(record.Hash);
        byte[] actual = Derive(password, Convert.FromBase64String(record.Salt), record.Iterations);
        if (expected.Length != actual.Length) return false;
        int difference = 0;
        for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ actual[i];
        return difference == 0;
    }

    public static byte[] CreateRecord(string password)
    {
        if (password.Length < 8) throw new ArgumentException("La password deve contenere almeno 8 caratteri.");
        var salt = new byte[16];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(salt);
        return JsonSerializer.SerializeToUtf8Bytes(new Verifier
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Derive(password, salt, Iterations))
        });
    }

    public void ApplyRecord(byte[]? record)
    {
        if (record != null) Read(record);
        if (record == null)
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.WriteAllBytes(_path, record);
    }

    private static Verifier Read(byte[] bytes)
    {
        var record = JsonSerializer.Deserialize<Verifier>(bytes) ?? throw new InvalidDataException("Verificatore password non leggibile.");
        if (record.Version != 1 || record.Iterations < 100000 || record.Iterations > 2000000 ||
            Convert.FromBase64String(record.Salt).Length != 16 || Convert.FromBase64String(record.Hash).Length != 32)
            throw new InvalidDataException("Verificatore password non valido.");
        return record;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
#if NET
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
#else
        using var derive = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return derive.GetBytes(32);
#endif
    }
}
