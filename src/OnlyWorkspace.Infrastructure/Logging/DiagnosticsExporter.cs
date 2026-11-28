using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using OnlyWorkspace.Core;

namespace OnlyWorkspace.Infrastructure.Logging;

public static class DiagnosticsExporter
{
    public static string CreateDiagnosticsZip(
        string? targetZipPath = null,
        string? outlookVersion = null,
        string? outlookBitness = null)
    {
        var localAppData = AppPaths.LocalDirectory;

        var destination = targetZipPath;
        if (string.IsNullOrWhiteSpace(destination))
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!Directory.Exists(desktop))
            {
                desktop = Path.GetTempPath();
            }
            destination = Path.Combine(desktop, $"OnlyWorkspace-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Diag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // System diagnostics metadata
            var metadata = new
            {
                GeneratedAtUtc = DateTime.UtcNow,
                ProductVersion = ProductInfo.Version,
                MachineName = Environment.MachineName,
                Windows = new
                {
                    OsVersion = Environment.OSVersion.ToString(),
                    ProductName = WorkstationInfo.WindowsProductName(),
                    DisplayVersion = WorkstationInfo.WindowsDisplayVersion(),
                    Is64BitOperatingSystem = Environment.Is64BitOperatingSystem
                },
                Outlook = new
                {
                    Version = !string.IsNullOrWhiteSpace(outlookVersion) ? outlookVersion : WorkstationInfo.OutlookVersion(),
                    Bitness = !string.IsNullOrWhiteSpace(outlookBitness) ? outlookBitness : WorkstationInfo.OutlookBitness()
                },
                DotNetFramework = new
                {
                    Release = GetRegistryDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release"),
                    ClrVersion = Environment.Version.ToString()
                },
                WebView2Runtime = new
                {
                    Version = WorkstationInfo.WebView2Version()
                },
                Process = new
                {
                    Is64BitProcess = Environment.Is64BitProcess,
                    LocalAppDir = localAppData
                },
                Archive = DescribeArchive(AppPaths.ArchiveDirectory),
                Storage = DescribeStorage(),
                OutlookResiliency = DescribeResiliency()
            };

            var metaJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(tempDir, "diagnostics_system.json"), metaJson);

            var destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            ZipFile.CreateFromDirectory(tempDir, destination);
            return destination!;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch { } // temp folder cleanup: Windows removes %TEMP% leftovers
        }
    }

    /// <summary>Mail archive of this Windows user as seen from outside: files and sizes only, the database is not opened.</summary>
    /// <summary>Space per generated folder, as the ConfigTool's Privacy e dati page shows it.</summary>
    private static object? DescribeStorage()
    {
        try
        {
            var report = Storage.AppStorageUsage.Measure();
            return new { report.TotalBytes, report.FreeBytes, Items = report.Items.Select(i => new { i.Name, i.Path, i.Bytes, i.Files, i.Unreadable }).ToList() };
        }
        catch
        {
            return null; // Informative only.
        }
    }

    private const string AddinProgId = "OnlyWorkspace.Addin";
    private const string OutlookKey = @"Software\Microsoft\Office\16.0\Outlook";

    /// <summary>
    /// What Outlook recorded about this add-in's speed (HKCU, this Windows user): startup times, whether and why it was
    /// disabled, the reminder of a slow add-in shown to the user. Values for the other add-ins are left out.
    /// Reference: https://learn.microsoft.com/en-us/office/vba/outlook/concepts/getting-started/support-for-keeping-add-ins-enabled
    /// </summary>
    private static object? DescribeResiliency()
    {
        try
        {
            object? loadTimes = null;
            using (var key = Registry.CurrentUser.OpenSubKey(OutlookKey + @"\AddInLoadTimes"))
                if (key?.GetValue(AddinProgId) is byte[] times)
                    loadTimes = Enumerable.Range(0, times.Length / 4).Select(i => BitConverter.ToInt32(times, i * 4)).ToList();

            var disabled = new System.Collections.Generic.List<object>();
            using (var key = Registry.CurrentUser.OpenSubKey(OutlookKey + @"\Resiliency\DisabledItems"))
                foreach (var name in key?.GetValueNames() ?? Array.Empty<string>())
                    if (key!.GetValue(name) is byte[] item && item.Length >= 4 &&
                        System.Text.Encoding.Unicode.GetString(item).IndexOf("OnlyWorkspace", StringComparison.OrdinalIgnoreCase) >= 0)
                        disabled.Add(new { Reason = BitConverter.ToInt32(item, 0) });

            int? doNotDisable = null;
            using (var key = Registry.CurrentUser.OpenSubKey(OutlookKey + @"\Resiliency\DoNotDisableAddinList"))
                if (key?.GetValue(AddinProgId) is int value) doNotDisable = value;

            object? reminder = null;
            using (var key = Registry.CurrentUser.OpenSubKey(OutlookKey + @"\Resiliency\NotificationReminderAddinData"))
                if (key?.GetValue(AddinProgId) is int next)
                    reminder = new { NextReminderUtc = DateTimeOffset.FromUnixTimeSeconds(next).UtcDateTime, Type = key.GetValue(AddinProgId + @"\dtype") as int? };

            // DisabledItems reason: 1 startup, 2 demand load, 3 crash, 4 FolderSwitch, 5 BeforeFolderSwitch, 6 item open, 7 iteration count, 8 shutdown.
            return new { LoadTimesMs = loadTimes, Disabled = disabled, DoNotDisable = doNotDisable, SlowAddinReminder = reminder };
        }
        catch
        {
            return null; // Informative only.
        }
    }

    private static object DescribeArchive(string directory)
    {
        var databases = new System.Collections.Generic.List<object>();
        var rollbackCopies = new System.Collections.Generic.List<object>();
        bool key = false;
        try
        {
            if (Directory.Exists(directory))
            {
                key = File.Exists(Path.Combine(directory, "archive.key"));
                // The file name is a hash of the Outlook profile: the profile itself is read only by opening the database.
                foreach (var file in Directory.GetFiles(directory, "*.engine.db"))
                {
                    var db = new FileInfo(file);
                    var wal = new FileInfo(file + "-wal");
                    databases.Add(new { File = db.Name, Bytes = db.Length, WalBytes = wal.Exists ? wal.Length : 0, LastWriteUtc = db.LastWriteTimeUtc });
                }
                foreach (var file in Directory.GetFiles(directory, "*.rollback.db"))
                {
                    var copy = new FileInfo(file);
                    rollbackCopies.Add(new { File = copy.Name, Bytes = copy.Length, LastWriteUtc = copy.LastWriteTimeUtc });
                }
            }
        }
        catch
        {
            // Informative only.
        }
        return new { KeyPresent = key, Databases = databases, RollbackCopies = rollbackCopies };
    }

    private static int? GetRegistryDword(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            var val = key?.GetValue(valueName);
            if (val is int intVal) return intVal;
            if (int.TryParse(val?.ToString(), out var parsed)) return parsed;
            return null;
        }
        catch
        {
            return null;
        }
    }
}
