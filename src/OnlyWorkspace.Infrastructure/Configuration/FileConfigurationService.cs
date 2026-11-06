using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Validation;

namespace OnlyWorkspace.Infrastructure.Configuration;

public sealed class FileConfigurationService : IConfigurationService
{
    private readonly string _configPath;
    private readonly string _secretsPath;
    private readonly string _preferencesPath;
    private readonly ISecretVault _vault;
    private WorkspaceConfig? _cachedConfig;
    private (DateTime, DateTime) _loadedStamp;

    public FileConfigurationService(
        string? configDir = null,
        string? preferencesDir = null,
        ISecretVault? vault = null)
    {
        var programData = configDir ?? OnlyWorkspace.Core.AppPaths.ConfigDirectory;
        var appData = preferencesDir ?? OnlyWorkspace.Core.AppPaths.PreferencesDirectory;

        _configPath = Path.Combine(programData, "config.json");
        _secretsPath = Path.Combine(programData, "secrets.bin");
        _preferencesPath = Path.Combine(appData, "preferences.json");

        _vault = vault ?? new Security.DpapiSecretVault();
    }

    /// <summary>
    /// One load on a throwaway instance, run off Outlook's UI thread at startup: the first load in a process costs
    /// 265-392 ms (JIT, serializer metadata, validator, DPAPI), later ones about 1 ms, and the indexer's first tick
    /// paid it on the UI thread (A149, 27/10/2026). Reads only; a failure is left to the real load.
    /// Also the profile checks of the pane's creation: cold, the policy key and the notice coverage cost about 60 ms
    /// inside the ribbon callback (A157, 28/10/2026).
    /// </summary>
    public static void WarmUp()
    {
        try
        {
            var service = new FileConfigurationService();
            var config = service.LoadConfiguration();
            foreach (var profile in config.Profiles)
            {
                DataEgress.AllowsModel(config, profile, service.ResolveSecret);
                ConversationPolicy.Key(profile);
            }
        }
        catch (Exception) { }
    }


    // Missing files report a constant time, so no existence check is needed.
    private (DateTime, DateTime) DiskStamp() => (File.GetLastWriteTimeUtc(_configPath), File.GetLastWriteTimeUtc(_secretsPath));

    public bool HasConfigurationChanged() => DiskStamp() != _loadedStamp;

    public WorkspaceConfig LoadConfiguration()
    {
        // Set first: a failing load is not retried until the files change again,
        // except a read that collided with a save in progress.
        _loadedStamp = DiskStamp();
        try
        {
            return Load();
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            _loadedStamp = default;
            throw;
        }
    }

    private WorkspaceConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            throw new FileNotFoundException($"config.json non trovato: {_configPath}");
        }

        var json = File.ReadAllText(_configPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<WorkspaceConfig>(json, options)
                     ?? throw new InvalidOperationException("config.json vuoto o non leggibile.");

        ProviderCompatibility.Normalize(config);
        foreach (var profile in config.Profiles)
        {
            if (ProviderCatalog.Get(profile.Type).IsExternal)
            {
                profile.External = true;
            }
        }

        var validation = ConfigurationValidator.Validate(config);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Configurazione non valida: " + string.Join("; ", validation.Errors));
        }

        // Absence revokes the loaded keys; an unreadable file still throws without replacing them.
        _vault.Load(_secretsPath);
        DataEgress.BindProfiles(config, ResolveSecret);

        _cachedConfig = config;
        return config;
    }

    /// <summary>config.json as written, without validation or vault: shown read-only when the normal load fails. Null if not parseable.</summary>
    public WorkspaceConfig? ReadForDisplay()
    {
        try
        {
            var config = JsonSerializer.Deserialize<WorkspaceConfig>(File.ReadAllText(_configPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null) return null;
            config.Profiles ??= new();
            config.Limits ??= new();
            config.History ??= new();
            config.Archive ??= new();
            config.Logging ??= new();
            return config;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void SaveConfiguration(WorkspaceConfig config)
    {
        ProviderCompatibility.Normalize(config);
        DataEgress.ScrubHistory(config);
        var validation = ConfigurationValidator.Validate(config);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Configurazione non salvata perché non valida: " + string.Join("; ", validation.Errors));
        }

        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(config, ConfigJson.Write);
        AtomicFile.WriteAllText(_configPath, json);
        _cachedConfig = config;
    }

    public string? ResolveSecret(string secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef)) return null;
        return _vault.GetSecret(secretRef);
    }

    public void StoreSecret(string secretRef, string secretValue)
    {
        _vault.SetSecret(secretRef, secretValue);
        _vault.Save(_secretsPath);
    }

    /// <summary>False only when absent or explicitly false. Unreadable/invalid preferences throw.</summary>
    public bool GetUserFlag(string name)
    {
        var preferences = ReadPreferencesForUpdate();
        if (!preferences.TryGetValue(name, out var value)) return false;
        if (value is JsonElement { ValueKind: JsonValueKind.True }) return true;
        if (value is JsonElement { ValueKind: JsonValueKind.False }) return false;
        throw new InvalidDataException("La preferenza «" + name + "» non è un valore booleano.");
    }

    public void SetUserFlag(string name, bool value) => UpdatePreferences(p => p[name] = value);

    /// <summary>Only absence is an empty preference set; errors never authorize a destructive replacement.</summary>
    private Dictionary<string, object?> ReadPreferencesForUpdate()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        string text;
        try { text = File.ReadAllText(_preferencesPath); }
        catch (FileNotFoundException) { return result; }
        catch (DirectoryNotFoundException) { return result; }
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("preferences.json deve contenere un oggetto JSON.");
        foreach (var property in doc.RootElement.EnumerateObject()) result[property.Name] = property.Value.Clone();
        return result;
    }

    /// <summary>Read, change one key, write: other preferences survive.</summary>
    private void UpdatePreferences(Action<Dictionary<string, object?>> change)
    {
        using var sha = SHA256.Create();
        string digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(_preferencesPath).ToUpperInvariant()))).Replace("-", "");
        using var gate = new Mutex(false, @"Global\OnlyWorkspace_Preferences_" + digest);
        bool held;
        try { held = gate.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new TimeoutException("Le preferenze sono occupate: riprovare.");
        try
        {
            var preferences = ReadPreferencesForUpdate();
            change(preferences);
            preferences["updatedAt"] = DateTime.UtcNow;
            var dir = Path.GetDirectoryName(_preferencesPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(_preferencesPath, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { gate.ReleaseMutex(); }
    }
}
