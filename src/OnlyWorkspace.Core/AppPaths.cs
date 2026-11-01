using System;
using System.IO;

namespace OnlyWorkspace.Core;

/// <summary>The folders OnlyWorkspace writes to: one place, so the ConfigTool's space figures match what the app writes.</summary>
public static class AppPaths
{
    public const string Folder = "OnlyWorkspace";

    /// <summary>%ProgramData%\OnlyWorkspace: config.json, secrets.bin, admin-password.json (administrators write).</summary>
    public static string ConfigDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Folder);

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
