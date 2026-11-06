using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Validation;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Infrastructure.Security;

using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.ConfigTool.Tray;

namespace OnlyWorkspace.ConfigTool;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    public static int Main(string[] args)
    {
        bool isTrayOnly = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-tray", StringComparison.OrdinalIgnoreCase));
        // --page mailboxes: open on Archivio email → Le mie cassette (from the pane).
        string? page = args.Length == 2 && string.Equals(args[0], "--page", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], SingleInstanceManager.MailboxesPage, StringComparison.OrdinalIgnoreCase) ? SingleInstanceManager.MailboxesPage : null;
        bool isGuiOnly = args.Length == 0 || page != null || (args.Length == 1 && string.Equals(args[0], "--gui", StringComparison.OrdinalIgnoreCase));

        if (isTrayOnly || isGuiOnly)
        {
            if (!SingleInstanceManager.TryStartPrimary(out var singleInstance))
            {
                if (!isTrayOnly)
                {
                    int attempt = 0;
                    while (!SingleInstanceManager.SignalPrimaryInstance(page: page))
                    {
                        if (++attempt == 40)
                        {
                            OnlyWorkspaceLog.Logger.Warning("ConfigTool: the running instance did not receive the request to show its window");
                            break;
                        }
                        System.Threading.Thread.Sleep(50);
                    }
                }
                return 0;
            }

            using (singleInstance)
            {
                OnlyWorkspaceLog.Initialize(configTool: true);
#if NET
                // net48 reads DpiAwareness from App.config.
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) =>
                {
                    OnlyWorkspaceLog.Logger.Error(e.Exception, "Unhandled UI thread exception in ConfigTool");
                    CopyableMessageBox.Show("Si è verificato un errore imprevisto: " + ErrorText.For(e.Exception) + "\n\nCartella dei log: " + OnlyWorkspace.Core.AppPaths.LogDirectory,
                        "OnlyWorkspace", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    if (e.ExceptionObject is Exception ex)
                    {
                        OnlyWorkspaceLog.Logger.Fatal(ex, "Fatal unhandled domain exception in ConfigTool");
                    }
                };

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    using var trayContext = new TrayApplicationContext(startMinimized: isTrayOnly, singleInstance: singleInstance, startPage: page);
                    Application.Run(trayContext);
                }
                finally { OnlyWorkspaceLog.CloseAndFlush(); }
                return 0;
            }
        }

        AttachConsole(ATTACH_PARENT_PROCESS);

        Console.WriteLine("==================================================");
        Console.WriteLine(" OnlyWorkspace Administrative Configuration Tool  ");
        Console.WriteLine("==================================================");

        string command = args[0].ToLowerInvariant();
        try
        {
            switch (command)
            {
                case "validate":
                    return HandleValidate(args);

                case "import":
                    return HandleImport(args).GetAwaiter().GetResult();

                case "show":
                    return HandleShow(args);

                case "test":
                    return HandleTest(args).GetAwaiter().GetResult();

                case "backup":
                    return HandleBackup(args);

                case "restore":
                    return HandleRestore(args);

                case "restore-authorized":
                    return HandleRestoreAuthorized(args);

                case "admin-password-apply":
                    return HandlePasswordApply(args);

                case "apply-staged":
                    return HandleApplyStaged(args);

                default:
                    Console.Error.WriteLine($"[ERROR] Unknown command: '{command}'");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] Error executing '{command}': {ex.Message}");
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  OnlyWorkspace.ConfigTool                         (Avvia tray icon e apre Configurazione GUI)");
        Console.WriteLine("  OnlyWorkspace.ConfigTool --tray                  (Avvia minimizzato nell'area di notifica / System Tray)");
        Console.WriteLine("  OnlyWorkspace.ConfigTool --gui                   (Apre Configurazione GUI)");
        Console.WriteLine("  OnlyWorkspace.ConfigTool validate <config.json>");
        Console.WriteLine("  OnlyWorkspace.ConfigTool import <source_config.json> [target_dir] [--skip-test]");
        Console.WriteLine("  OnlyWorkspace.ConfigTool show [config_path]");
        Console.WriteLine("  OnlyWorkspace.ConfigTool test [config_path]");
        Console.WriteLine("  OnlyWorkspace.ConfigTool backup [dest.owbak] [config_dir]");
        Console.WriteLine("  OnlyWorkspace.ConfigTool restore <source.owbak> [config_dir]");
    }

    private static int HandleBackup(string[] args)
    {
        string targetFile = args.Length >= 2 ? args[1] : $"OnlyWorkspace_Backup_{DateTime.Now:yyyyMMdd_HHmm}.owbak";
        string configDir = args.Length >= 3 ? args[2] : OnlyWorkspace.Core.AppPaths.ConfigDirectory;
        try
        {
            ConfigurationBackupService.CreateBackup(configDir, targetFile);
            Console.WriteLine($"[BACKUP OK] Backup completato in: {targetFile}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Backup fallito: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Internal command run elevated by the GUI (UAC) to copy staged config.json / secrets.bin
    /// into the admin-protected configuration folder: apply-staged &lt;staging_dir&gt; &lt;config_dir&gt; &lt;config_sha256&gt; &lt;secrets_sha256|-&gt;.
    /// </summary>
    private static int HandleApplyStaged(string[] args) => IsElevated() ? ApplyStaged(args) : 1;

    /// <summary>apply-staged without the elevation check: tests run it on isolated local copies.</summary>
    internal static int ApplyStaged(string[] args)
    {
        if (args.Length != 5)
        {
            Console.Error.WriteLine("[ERROR] Usage: apply-staged <staging_dir> <config_dir> <config_sha256> <secrets_sha256|->");
            return 1;
        }

        // Same gate as restore-authorized: only a folder made by CreateStagingDirectory receives the result.
        string stagingDir = Path.GetFullPath(args[1]);
        using var hold = ElevatedConfigWriter.HoldStagingDirectory(stagingDir);
        if (hold == null) return 1;
        string configDir = args[2];

        try
        {
            ElevatedConfigWriter.EnsureProtectedDirectory(configDir);
            ConfigurationBackupService.ApplyStaged(stagingDir, configDir, args[3], args[4]);
            ElevatedConfigWriter.WriteResult(stagingDir, ElevatedConfigWriter.ResultFileName, "OK");
            return 0;
        }
        catch (Exception ex)
        {
            // The GUI receives Italian instructions; the copyable detail retains any recovery path.
            try { ElevatedConfigWriter.WriteFailure(stagingDir, ex); } catch { }
            return 1;
        }
    }

    private static int HandleRestore(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("[ERROR] Percorso file di backup mancante.");
            return 1;
        }
        string backupFile = args[1];
        string configDir = args.Length >= 3 ? args[2] : OnlyWorkspace.Core.AppPaths.ConfigDirectory;
        if (!AuthenticateWrite(configDir)) return 1;
        try
        {
            ElevatedConfigWriter.EnsureProtectedDirectory(configDir);
            ConfigurationBackupService.RestoreBackup(backupFile, configDir);
            Console.WriteLine($"[RESTORE OK] Ripristino completato con successo in: {configDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Ripristino fallito: {ex.Message}");
            return 1;
        }
    }

    /// <summary>restore-authorized &lt;backup&gt; &lt;config_dir&gt; &lt;staging_dir&gt;: run elevated by the GUI; the cause goes to the staging folder.</summary>
    private static int HandleRestoreAuthorized(string[] args) => IsElevated() ? RestoreAuthorized(args) : 1;

    /// <summary>restore-authorized without the elevation check: tests run it on isolated local copies.</summary>
    internal static int RestoreAuthorized(string[] args)
    {
        if (args.Length != 4) return 1;
        string staging = Path.GetFullPath(args[3]);
        using var hold = ElevatedConfigWriter.HoldStagingDirectory(staging);
        if (hold == null) return 1;
        try
        {
            ElevatedConfigWriter.EnsureProtectedDirectory(args[2]);
            ConfigurationBackupService.RestoreBackup(args[1], args[2]);
            ElevatedConfigWriter.WriteResult(staging, ElevatedConfigWriter.ResultFileName, "OK");
            return 0;
        }
        catch (Exception ex)
        {
            try { ElevatedConfigWriter.WriteFailure(staging, ex); } catch { }
            return 1;
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool AuthenticateWrite(string configDir)
    {
        var guard = new AdminPasswordService(configDir);
        if (!guard.Enabled) return true;
        try
        {
            Console.Error.Write("Password amministrativa: ");
            string entered;
            if (Console.IsInputRedirected)
            {
                entered = Console.ReadLine() ?? string.Empty;
            }
            else
            {
                var password = new System.Text.StringBuilder();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) break;
                    if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; continue; }
                    if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
                }
                entered = password.ToString();
            }
            Console.Error.WriteLine();
            if (guard.Verify(entered)) return true;
        }
        catch (Exception) { }
        Console.Error.WriteLine("[ERROR] Password amministrativa mancante o non corretta.");
        return false;
    }

    private static int HandlePasswordApply(string[] args)
    {
        if (!IsElevated() || args.Length != 3) return 1;
        string staging = Path.GetFullPath(args[1]);
        using var hold = ElevatedConfigWriter.HoldStagingDirectory(staging);
        if (hold == null) return 1;
        try
        {
            ElevatedConfigWriter.EnsureProtectedDirectory(args[2]);
            string source = Path.Combine(staging, "verifier.json");
            new AdminPasswordService(args[2]).ApplyRecord(File.Exists(source) ? File.ReadAllBytes(source) : null);
            ElevatedConfigWriter.WriteResult(staging, ElevatedConfigWriter.ResultFileName, "OK");
            return 0;
        }
        catch (Exception ex)
        {
            ElevatedConfigWriter.WriteFailure(staging, ex);
            return 1;
        }
    }

    private static int HandleValidate(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("[ERROR] Missing config file path for 'validate'.");
            return 1;
        }

        string path = args[1];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[ERROR] File not found: {path}");
            return 1;
        }

        string json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<WorkspaceConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (config == null)
        {
            Console.Error.WriteLine("[ERROR] Failed to deserialize JSON configuration.");
            return 1;
        }

        ProviderCompatibility.Normalize(config);
        var result = ConfigurationValidator.Validate(config);
        if (!result.IsValid)
        {
            Console.Error.WriteLine("[VALIDATION FAILED] The following errors were found:");
            foreach (var err in result.Errors)
            {
                Console.Error.WriteLine($"  - {err}");
            }
            return 1;
        }

        foreach (var warn in result.Warnings)
        {
            Console.WriteLine($"  [WARN] {warn}");
        }
        Console.WriteLine($"[VALIDATION OK] Configuration '{path}' is valid.");
        return 0;
    }

    private static async Task<int> HandleImport(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("[ERROR] Missing source config file path for 'import'.");
            return 1;
        }

        string srcPath = args[1];
        if (!File.Exists(srcPath))
        {
            Console.Error.WriteLine($"[ERROR] Source file not found: {srcPath}");
            return 1;
        }

        bool skipTest = args.Any(a => string.Equals(a, "--skip-test", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(a, "--offline", StringComparison.OrdinalIgnoreCase));

        string targetDir = (args.Length >= 3 && !args[2].StartsWith("--"))
            ? args[2]
            : OnlyWorkspace.Core.AppPaths.ConfigDirectory;

        if (!AuthenticateWrite(targetDir)) return 1;

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string rawJson = File.ReadAllText(srcPath);
        using var doc = JsonDocument.Parse(rawJson);
        var inlineSecrets = ReadImportSecrets(doc.RootElement);
        var config = JsonSerializer.Deserialize<WorkspaceConfig>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (config == null)
        {
            Console.Error.WriteLine("[ERROR] Failed to parse configuration.");
            return 1;
        }

        ProviderCompatibility.Normalize(config);
        var validation = ConfigurationValidator.Validate(config);
        if (!validation.IsValid)
        {
            Console.Error.WriteLine("[ERROR] Configuration validation failed before import:");
            foreach (var err in validation.Errors)
            {
                Console.Error.WriteLine($"  - {err}");
            }
            return 1;
        }

        string secretsFile = Path.Combine(targetDir, "secrets.bin");
        var vault = new DpapiSecretVault();
        if (File.Exists(secretsFile))
        {
            vault.Load(secretsFile);
        }

        // Both readers use the same verified array and ordinal case-insensitive names.
        if (inlineSecrets.Count != config.Profiles.Count)
            throw new InvalidOperationException("L'elenco dei profili da importare non è coerente.");
        for (int idx = 0; idx < inlineSecrets.Count; idx++)
        {
            string? secretVal = inlineSecrets[idx];
            if (!string.IsNullOrEmpty(secretVal))
            {
                var profile = config.Profiles[idx];
                string secretKey = $"profile_{profile.Id}_token";
                vault.SetSecret(secretKey, secretVal!);
                profile.SecretRef = secretKey;
            }
        }

        // Pre-import health check
        if (!skipTest)
        {
            Console.WriteLine("[INFO] Verifying profile connectivity before import...");
            foreach (var profile in config.Profiles)
            {
                string? token = !string.IsNullOrEmpty(profile.SecretRef) ? vault.GetSecret(profile.SecretRef) : null;
                try
                {
                    var client = LlmClientFactory.CreateClient(profile, secret: token);
                    var health = await client.CheckHealthAsync().ConfigureAwait(false);
                    if (client is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }

                    if (!health.IsHealthy)
                    {
                        Console.Error.WriteLine($"[ERROR] Connectivity check failed for profile '{profile.Id}' ({profile.Type}) at {profile.BaseUrl}: {health.ErrorMessage}");
                        return 1;
                    }
                    Console.WriteLine($"[OK] Connectivity verified for profile '{profile.Id}' ({profile.Type}).");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ERROR] Connection attempt failed for profile '{profile.Id}': {ex.Message}");
                    return 1;
                }
            }
        }
        else
        {
            Console.WriteLine("[WARN] Connectivity check skipped by --skip-test flag.");
        }

        ConfigurationBackupService.SaveConfiguration(targetDir, config, vault);
        string destConfig = Path.Combine(targetDir, "config.json");

        Console.WriteLine($"[IMPORT SUCCESS] Configuration imported cleanly to: {destConfig}");
        Console.WriteLine($"[IMPORT SUCCESS] Encrypted secrets stored at: {secretsFile}");
        return 0;
    }

    private static List<string?> ReadImportSecrets(JsonElement root)
    {
        var secrets = new List<string?>();
        if (!TryGetImportProperty(root, "profiles", out var profiles)) return secrets;
        if (profiles.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("La proprietà 'profiles' deve essere un array.");
        foreach (var profile in profiles.EnumerateArray())
        {
            string? secret = null;
            if (TryGetImportProperty(profile, "auth", out var auth) && auth.ValueKind != JsonValueKind.Null)
            {
                if (TryGetImportProperty(auth, "secret", out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    if (value.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException("La proprietà 'secret' deve essere una stringa o null.");
                    secret = value.GetString();
                }
            }
            secrets.Add(secret);
        }
        return secrets;
    }

    private static bool TryGetImportProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("La struttura dell'import deve contenere oggetti JSON.");
        value = default;
        bool found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) throw new InvalidOperationException($"La proprietà '{name}' è duplicata nell'import.");
            value = property.Value;
            found = true;
        }
        return found;
    }

    private static int HandleShow(string[] args)
    {
        string targetDir = args.Length >= 2
            ? (Directory.Exists(args[1]) ? args[1] : Path.GetDirectoryName(args[1]) ?? ".")
            : OnlyWorkspace.Core.AppPaths.ConfigDirectory;

        var svc = new FileConfigurationService(configDir: targetDir);
        WorkspaceConfig config;
        try
        {
            config = svc.LoadConfiguration();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Could not load configuration: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Schema Version       : {config.SchemaVersion}");
        Console.WriteLine($"Default Profile      : {config.DefaultProfile}");
        Console.WriteLine($"Profiles ({config.Profiles.Count}):");

        foreach (var p in config.Profiles)
        {
            bool hasSecret = !string.IsNullOrEmpty(p.SecretRef) && svc.ResolveSecret(p.SecretRef) != null;
            string secStatus = hasSecret ? "*** (Configured in Vault)" : "[None]";
            Console.WriteLine($"  - ID: {p.Id} [{p.Type}] - '{p.DisplayName}'");
            int budget = ProviderCatalog.PromptBudget(p);
            Console.WriteLine($"    Endpoint: {p.BaseUrl} | Model: {p.Model} | CtxBudget: {(budget > 0 ? budget.ToString() : "provider")}");
            Console.WriteLine($"    Auth: {p.AuthType} | Secret: {secStatus} | External: {p.IsExternal}");
        }

        return 0;
    }

    private static async Task<int> HandleTest(string[] args)
    {
        string targetDir = args.Length >= 2
            ? (Directory.Exists(args[1]) ? args[1] : Path.GetDirectoryName(args[1]) ?? ".")
            : OnlyWorkspace.Core.AppPaths.ConfigDirectory;

        var svc = new FileConfigurationService(configDir: targetDir);
        WorkspaceConfig config;
        try
        {
            config = svc.LoadConfiguration();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Could not load configuration: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Testing connectivity for {config.Profiles.Count} configured profile(s)...");
        bool allOk = true;

        foreach (var profile in config.Profiles)
        {
            Console.Write($"Testing profile '{profile.Id}' ({profile.Type}) at {profile.BaseUrl}... ");
            string? token = svc.ResolveSecret(profile.SecretRef);
            try
            {
                var client = LlmClientFactory.CreateClient(profile, secret: token);
                var health = await client.CheckHealthAsync().ConfigureAwait(false);
                if (client is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                if (health.IsHealthy)
                {
                    Console.WriteLine($"[OK] ({health.AvailableModels.Count} models found)");
                }
                else
                {
                    allOk = false;
                    Console.WriteLine($"[FAILED] {health.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                Console.WriteLine($"[FAILED] {ex.Message}");
            }
        }

        return allOk ? 0 : 1;
    }

}
