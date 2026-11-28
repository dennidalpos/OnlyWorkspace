using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Validation;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Infrastructure.Configuration;

public static class ConfigurationBackupService
{
    private const int MaxConfigBytes = 4 * 1024 * 1024;
    private const int MaxSecretsBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Prepare both files before replacing the active pair; recover the old vault on a handled config write failure.
    /// For the standard %ProgramData% folder the per-user vault goes to %LOCALAPPDATA% (written by the caller's own
    /// account, never by an elevated one) and only config.json is written to <paramref name="targetConfigDir"/>, which
    /// may be a staging folder: <paramref name="configDirOfVault"/> then names the folder the files are meant for.
    /// </summary>
    public static void SaveConfiguration(string targetConfigDir, WorkspaceConfig config, DpapiSecretVault vault, string? configDirOfVault = null)
    {
        ProviderCompatibility.Normalize(config);
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, ConfigJson.Write));
        byte[] secrets = vault.ExportEncrypted();
        ValidateContents(json, secrets, "da salvare");
        string home = configDirOfVault ?? targetConfigDir;
        if (AppPaths.IsStandardConfigDirectory(home))
        {
            SaveSecrets(home, vault);
            ReplacePair(targetConfigDir, json, null, File.Replace);
        }
        else ReplacePair(targetConfigDir, json, secrets, File.Replace);
    }

    /// <summary>Writes the vault where <see cref="AppPaths.SecretsDirectoryFor"/> puts it for this configuration folder.</summary>
    public static void SaveSecrets(string configDir, DpapiSecretVault vault)
    {
        string dir = AppPaths.SecretsDirectoryFor(configDir);
        Directory.CreateDirectory(dir);
        AtomicFile.WriteAllBytes(Path.Combine(dir, "secrets.bin"), vault.ExportEncrypted());
    }

    public static void CreateBackup(string configDir, string destinationZipPath)
    {
        if (string.IsNullOrWhiteSpace(configDir) || !Directory.Exists(configDir))
            throw new DirectoryNotFoundException($"Directory di configurazione non trovata: {configDir}");

        string configPath = Path.Combine(configDir, "config.json");
        string secretsPath = Path.Combine(configDir, "secrets.bin");
        if (!File.Exists(configPath)) throw new FileNotFoundException("La configurazione da salvare non è presente.", configPath);
        byte[] config = ReadFile(configPath, MaxConfigBytes);
        // The standard installation keeps the keys per user outside the backup: they are entered again after a restore.
        byte[]? secrets = !AppPaths.IsStandardConfigDirectory(configDir) && File.Exists(secretsPath) ? ReadFile(secretsPath, MaxSecretsBytes) : null;

        string? destDir = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        using var file = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        WriteEntry(zip, "config.json", config);
        if (secrets != null) WriteEntry(zip, "secrets.bin", secrets);
    }

    /// <param name="targetConfigDir">The folder the backup is meant for: for the standard one the vault of the file, if any, is not read.</param>
    public static void ValidateBackup(string backupZipPath, string? targetConfigDir = null) =>
        ReadValidated(backupZipPath, targetConfigDir != null && AppPaths.IsStandardConfigDirectory(targetConfigDir));

    public static void RestoreBackup(string backupZipPath, string targetConfigDir)
    {
        bool standard = AppPaths.IsStandardConfigDirectory(targetConfigDir);
        var (config, secrets) = ReadValidated(backupZipPath, standard);
        ReplacePair(targetConfigDir, config, standard ? null : secrets, File.Replace);
    }

    /// <summary>SHA-256 (hex) of a staged file, or "-" when it is absent: the digest the elevated command receives on its command line.</summary>
    public static string StagedDigest(string path) =>
        File.Exists(path) ? Sha256Hex(File.ReadAllBytes(path)) : "-";

    /// <summary>
    /// Elevated half of a GUI save: the staged config.json (and secrets.bin, when staged) replace the pair in
    /// <paramref name="targetConfigDir"/> under the same validation and rollback as a restore. The staging folder is
    /// writable by the unelevated user, so the bytes read once must match the digests the GUI passed on the elevated
    /// command line: a file replaced after the consent is refused. Nothing is written when the staging is missing,
    /// unreadable, replaced or invalid.
    /// </summary>
    public static void ApplyStaged(string stagingDir, string targetConfigDir, string expectedConfigDigest, string expectedSecretsDigest)
    {
        string stagedConfig = Path.Combine(stagingDir, "config.json");
        string stagedSecrets = Path.Combine(stagingDir, "secrets.bin");
        if (!File.Exists(stagedConfig)) throw new FileNotFoundException("La configurazione da applicare non è presente.", stagedConfig);
        byte[] config = ReadFile(stagedConfig, MaxConfigBytes);
        byte[]? secrets = File.Exists(stagedSecrets) ? ReadFile(stagedSecrets, MaxSecretsBytes) : null;
        // The elevated account may not be the user's: it never writes the per-user vault.
        if (secrets != null && AppPaths.IsStandardConfigDirectory(targetConfigDir))
            throw new InvalidDataException("Le chiavi sono per utente e non si applicano con i diritti di amministratore.");
        if (!DigestMatches(config, expectedConfigDigest) || (secrets == null ? expectedSecretsDigest != "-" : !DigestMatches(secrets, expectedSecretsDigest)))
            throw new InvalidDataException("I file da applicare sono stati modificati dopo l'autorizzazione. Riprovare il salvataggio.");
        config = ValidateContents(config, secrets, "della configurazione da applicare");
        ReplacePair(targetConfigDir, config, secrets, File.Replace);
    }

    private static bool DigestMatches(byte[] content, string expected) =>
        string.Equals(Sha256Hex(content), expected, StringComparison.OrdinalIgnoreCase);

    private static string Sha256Hex(byte[] content)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(content)).Replace("-", "");
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
        string suffix = ".restore-" + AtomicFile.ShortId();
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

    private static (byte[] Config, byte[]? Secrets) ReadValidated(string backupZipPath, bool ignoreSecrets)
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

        if (ignoreSecrets) secretsBytes = null;
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
