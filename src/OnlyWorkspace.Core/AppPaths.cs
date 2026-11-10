using System;
using System.IO;

namespace OnlyWorkspace.Core;

/// <summary>The folders OnlyWorkspace writes to: one place, so the ConfigTool's space figures match what the app writes.</summary>
public static class AppPaths
{
    public const string Folder = "OnlyWorkspace";

    /// <summary>%ProgramData%\OnlyWorkspace: config.json, admin-password.json (administrators write); secrets.bin only of earlier builds.</summary>
    public static string ConfigDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Folder);

    /// <summary>
    /// Where secrets.bin lives for a configuration folder. The standard %ProgramData% folder is written by administrators,
    /// possibly under another account, so the per-user DPAPI vault goes to %LOCALAPPDATA% and is never written elevated.
    /// Any other folder (tests, provisioning to a chosen folder) keeps both files together.
    /// </summary>
    public static string SecretsDirectoryFor(string? configDir) => IsStandardConfigDirectory(configDir) ? LocalDirectory : configDir!;

    public static bool IsStandardConfigDirectory(string? configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir)) return true;
        try
        {
            return string.Equals(Path.GetFullPath(configDir).TrimEnd('\\', '/'), Path.GetFullPath(ConfigDirectory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>The LocalMachine vault of earlier builds, readable by every local user and no longer read: the administrator removes it.</summary>
    public static string LegacySecretsFile => Path.Combine(ConfigDirectory, "secrets.bin");

    /// <summary>%APPDATA%\OnlyWorkspace: preferences.json of the user.</summary>
    public static string PreferencesDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Folder);

    /// <summary>%LOCALAPPDATA%\OnlyWorkspace: per-user data that stays on this PC.</summary>
    public static string LocalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Folder);

    public static string ArchiveDirectory => Path.Combine(LocalDirectory, "archive");
    public static string LogDirectory => Path.Combine(LocalDirectory, "logs");
    public static string HistoryDirectory => Path.Combine(LocalDirectory, "history");
    public static string PseudonymizationAuditDirectory => Path.Combine(LocalDirectory, "privacy-audit");
    public static string WebViewDirectory => Path.Combine(LocalDirectory, "WebView2");
    /// <summary>Copies of a config.json or secrets.bin the ConfigTool could not read, kept for the administrator.</summary>
    public static string UnreadableConfigDirectory => Path.Combine(LocalDirectory, "config-non-leggibile");

    /// <summary>%TEMP%\OnlyWorkspace: attachment copies being read, removed when done.</summary>
    public static string TempDirectory => Path.Combine(Path.GetTempPath(), Folder);
    public static string ScanDirectory => Path.Combine(TempDirectory, "scan");
}
