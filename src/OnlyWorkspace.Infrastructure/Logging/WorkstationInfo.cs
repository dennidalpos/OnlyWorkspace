using System;
using Microsoft.Win32;

namespace OnlyWorkspace.Infrastructure.Logging;

/// <summary>What the registry says about this PC's Windows, Office and WebView2: for diagnostics and the data egress report.</summary>
public static class WorkstationInfo
{
    private const string WindowsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string ClickToRun = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
    private const string ClickToRun32 = @"SOFTWARE\WOW6432Node\Microsoft\Office\ClickToRun\Configuration";

    public static string? WindowsProductName() =>
        ProductName(Read(Registry.LocalMachine, WindowsKey, "ProductName"), Read(Registry.LocalMachine, WindowsKey, "CurrentBuildNumber"));

    /// <summary>
    /// Windows 11 keeps «Windows 10» in ProductName; its builds start at 22000
    /// (https://learn.microsoft.com/windows/release-health/windows11-release-information).
    /// </summary>
    internal static string? ProductName(string? registryName, string? build) =>
        registryName != null && registryName.StartsWith("Windows 10", StringComparison.Ordinal) && int.TryParse(build, out int number) && number >= 22000
            ? "Windows 11" + registryName.Substring("Windows 10".Length)
            : registryName;

    public static string? WindowsDisplayVersion() => Read(Registry.LocalMachine, WindowsKey, "DisplayVersion");

    public static string OutlookVersion() =>
        Read(Registry.LocalMachine, ClickToRun, "VersionToReport") ?? Read(Registry.LocalMachine, ClickToRun32, "VersionToReport") ?? "Unknown";

    public static string OutlookBitness() =>
        Read(Registry.LocalMachine, ClickToRun, "Platform") ?? Read(Registry.LocalMachine, ClickToRun32, "Platform")
        ?? (Environment.Is64BitOperatingSystem ? "x64" : "x86");

    public static string WebView2Version() =>
        Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", "pv")
        ?? Read(Registry.LocalMachine, @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", "pv")
        ?? Read(Registry.CurrentUser, @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", "pv")
        ?? Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-9E47-4764-852F-AF874C73333A}", "pv")
        ?? "Not Detected";

    private static string? Read(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName)?.ToString();
        }
        catch
        {
            return null; // Informative only.
        }
    }
}
