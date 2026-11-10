using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace OnlyWorkspace.ConfigTool.Services;

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AppRegistrationName = "OnlyWorkspaceTray";

    public static bool IsRunAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(AppRegistrationName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enable)
            {
                string exePath = GetExecutablePath();
                string command = $"\"{exePath}\" --tray";
                key.SetValue(AppRegistrationName, command);
            }
            else
            {
                key.DeleteValue(AppRegistrationName, false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string GetExecutablePath()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            return exePath!;
        }

        string asmLocation = typeof(StartupManager).Assembly.Location ?? string.Empty;
        if (!string.IsNullOrEmpty(asmLocation) && asmLocation.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            string candidateExe = Path.ChangeExtension(asmLocation, ".exe");
            if (File.Exists(candidateExe)) return candidateExe;
        }

        return !string.IsNullOrEmpty(asmLocation) ? asmLocation : AppDomain.CurrentDomain.BaseDirectory;
    }
}
