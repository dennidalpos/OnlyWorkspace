using System;
using System.Diagnostics;
using System.IO;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.Outlook.Services;

public static class ConfigToolLauncher
{
    private const string ExeName = "OnlyWorkspace.ConfigTool.exe";

    /// <summary>Mailboxes = Archivio email → Le mie cassette; the ConfigTool accepts no other page.</summary>
    public const string MailboxesPage = "mailboxes";

    public static bool TryLaunch(bool startMinimized = false, bool mailboxes = false)
    {
        try
        {
            string addinDir = Path.GetDirectoryName(typeof(ConfigToolLauncher).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(addinDir, ExeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OnlyWorkspace", ExeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OnlyWorkspace", ExeName)
            };

            foreach (var configExe in candidates)
            {
                if (!File.Exists(configExe)) continue;

                Process.Start(StartInfo(configExe, startMinimized, mailboxes));
                return true;
            }

            OnlyWorkspaceLog.Logger.Warning("{ExeName} non trovato in {AddinDir}", ExeName, addinDir);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to launch ConfigTool");
        }

        return false;
    }

    /// <summary>
    /// Always a normal window style: Windows applies the launcher's show mode to the first ShowWindow of the
    /// process, so a hidden start would hide the first configuration window the tray opens later. --tray alone
    /// keeps the tray without windows.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string configExe, bool startMinimized, bool mailboxes = false) => new()
    {
        FileName = configExe,
        Arguments = startMinimized ? "--tray" : mailboxes ? "--page " + MailboxesPage : string.Empty,
        WindowStyle = ProcessWindowStyle.Normal,
        WorkingDirectory = Path.GetDirectoryName(configExe) ?? string.Empty,
        UseShellExecute = true
    };
}
