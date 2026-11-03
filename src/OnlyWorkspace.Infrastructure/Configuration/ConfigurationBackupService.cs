using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Validation;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Infrastructure.Configuration;

public static class ConfigurationBackupService
{
    private const int MaxConfigBytes = 4 * 1024 * 1024;
    private const int MaxSecretsBytes = 16 * 1024 * 1024;

    /// <summary>Prepare both files before replacing the active pair; recover the old vault on a handled config write failure.</summary>
    public static void SaveConfiguration(string targetConfigDir, WorkspaceConfig config, DpapiSecretVault vault)
    {
        ProviderCompatibility.Normalize(config);
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, ConfigJson.Write));
        byte[] secrets = vault.ExportEncrypted();
        ValidateContents(json, secrets, "da salvare");
        ReplacePair(targetConfigDir, json, secrets, File.Replace);
    }

    public static void CreateBackup(string configDir, string destinationZipPath)
    {
        if (string.IsNullOrWhiteSpace(configDir) || !Directory.Exists(configDir))
            throw new DirectoryNotFoundException($"Directory di configurazione non trovata: {configDir}");

        string configPath = Path.Combine(configDir, "config.json");
        string secretsPath = Path.Combine(configDir, "secrets.bin");
        if (!File.Exists(configPath)) throw new FileNotFoundException("La configurazione da salvare non è presente.", configPath);
        byte[] config = ReadFile(configPath, MaxConfigBytes);
        byte[]? secrets = File.Exists(secretsPath) ? ReadFile(secretsPath, MaxSecretsBytes) : null;

        string? destDir = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        using var file = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        WriteEntry(zip, "config.json", config);
        if (secrets != null) WriteEntry(zip, "secrets.bin", secrets);
    }

    public static void ValidateBackup(string backupZipPath) => ReadValidated(backupZipPath);

    public static void RestoreBackup(string backupZipPath, string targetConfigDir)
    {
        var (config, secrets) = ReadValidated(backupZipPath);
        ReplacePair(targetConfigDir, config, secrets, File.Replace);
    }

    /// <summary>
    /// Elevated half of a GUI save: the staged config.json (and secrets.bin, when staged) replace the pair in
    /// <paramref name="targetConfigDir"/> under the same validation and rollback as a restore. Nothing is written
    /// when the staging is missing, unreadable or invalid.
    /// </summary>
    public static void ApplyStaged(string stagingDir, string targetConfigDir)
    {
        string stagedConfig = Path.Combine(stagingDir, "config.json");
        string stagedSecrets = Path.Combine(stagingDir, "secrets.bin");
        if (!File.Exists(stagedConfig)) throw new FileNotFoundException("La configurazione da applicare non è presente.", stagedConfig);
        byte[] config = ReadFile(stagedConfig, MaxConfigBytes);
        byte[]? secrets = File.Exists(stagedSecrets) ? ReadFile(stagedSecrets, MaxSecretsBytes) : null;
        config = ValidateContents(config, secrets, "della configurazione da applicare");
        ReplacePair(targetConfigDir, config, secrets, File.Replace);
    }

    /// <summary>
    /// secrets.bin is swapped first and kept as a backup until config.json is in place; on a handled second-write
    /// failure it is put back, and if that fails too the previous vault stays next to it and the error names it.
    /// </summary>
    internal static void ReplacePair(string targetConfigDir, byte[] config, byte[]? secrets, Action<string, string, string?> replace)
    {
        Directory.CreateDirectory(targetConfigDir);
        string configPath = Path.Combine(targetConfigDir, "config.json");
        string secretsPath = Path.Combine(targetConfigDir, "secrets.bin");
        string suffix = ".restore-" + Guid.NewGuid().ToString("N");
        string configStage = configPath + suffix;
        string secretsStage = secretsPath + suffix;
        string secretsBackup = secretsPath + suffix + ".bak";
        bool hadSecrets = File.Exists(secretsPath);
        bool secretsChanged = false;
        bool keepBackup = false;

        try
        {
            AtomicFile.WriteAllBytes(configStage, config);
            if (secrets != null) AtomicFile.WriteAllBytes(secretsStage, secrets);

            if (secrets != null)
            {
                if (hadSecrets) replace(secretsStage, secretsPath, secretsBackup);
                else File.Move(secretsStage, secretsPath);
                secretsChanged = true;
            }

            if (File.Exists(configPath)) replace(configStage, configPath, null);
            else File.Move(configStage, configPath);
        }
        catch (Exception writeError)
        {
            if (secretsChanged)
            {
                try
                {
                    if (hadSecrets) replace(secretsBackup, secretsPath, null);
                    else File.Delete(secretsPath);
                }
                catch (Exception rollbackError)
                {
                    keepBackup = true;
                    throw new IOException("La scrittura della configurazione è fallita e non è stato possibile riportare secrets.bin alla versione precedente. La copia precedente è in: " + secretsBackup,
                        new AggregateException(writeError, rollbackError));
                }
            }
            throw;
        }
        finally
        {
            TryDelete(configStage);
            TryDelete(secretsStage);
            if (!keepBackup) TryDelete(secretsBackup);
        }
    }

    private static void TryDelete(string path)
    {
        // Cleanup must not turn a committed restore into a reported failure or mask a rollback error.
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static (byte[] Config, byte[]? Secrets) ReadValidated(string backupZipPath)
    {
        if (!File.Exists(backupZipPath))
            throw new FileNotFoundException($"File di backup non trovato: {backupZipPath}");

        byte[]? configBytes = null;
        byte[]? secretsBytes = null;
        try
        {
            using var zip = ZipFile.OpenRead(backupZipPath);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == "config.json" && configBytes == null)
                    configBytes = ReadEntry(entry, MaxConfigBytes);
                else if (entry.FullName == "secrets.bin" && secretsBytes == null)
                    secretsBytes = ReadEntry(entry, MaxSecretsBytes);
                else
                    throw new InvalidOperationException("Il backup contiene file inattesi o duplicati.");
            }
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("Il file scelto non è un archivio di backup di OnlyWorkspace.");
        }

        if (configBytes == null)
            throw new InvalidOperationException("L'archivio di backup non contiene un file 'config.json' valido.");

        configBytes = ValidateContents(configBytes, secretsBytes, "del backup");
        return (configBytes, secretsBytes);
    }

    /// <param name="source">Italian genitive naming the origin in messages, e.g. "del backup".</param>
    private static byte[] ValidateContents(byte[] configBytes, byte[]? secretsBytes, string source)
    {
        WorkspaceConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<WorkspaceConfig>(Encoding.UTF8.GetString(configBytes),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            config = null;
        }
        if (config == null)
            throw new InvalidOperationException($"Impossibile deserializzare il file 'config.json' {source}.");

        bool upgraded = ProviderCompatibility.Normalize(config);
        var validation = ConfigurationValidator.Validate(config);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Validazione {source} fallita: " + string.Join(", ", validation.Errors));

        if (secretsBytes != null)
        {
            try { new DpapiSecretVault().Load(secretsBytes); }
            catch (Exception ex) when (ex is CryptographicException || ex is JsonException)
            {
                throw new InvalidOperationException($"Le credenziali {source} non sono leggibili su questo PC: sono state create su un altro computer oppure sono danneggiate. Nessuna modifica eseguita.");
            }
        }
        return upgraded ? Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, ConfigJson.Write)) : configBytes;
    }

    private static byte[] ReadFile(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        return ReadBounded(stream, stream.Length, limit);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        using var stream = entry.Open();
        return ReadBounded(stream, entry.Length, limit);
    }

    private static byte[] ReadBounded(Stream stream, long expectedLength, int limit)
    {
        if (expectedLength > limit) throw new InvalidOperationException("Un file del backup supera il limite consentito.");
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidOperationException("Un file del backup supera il limite consentito.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] contents)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(contents, 0, contents.Length);
    }
}
