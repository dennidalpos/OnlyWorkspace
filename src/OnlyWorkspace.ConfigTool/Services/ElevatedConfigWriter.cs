using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>
/// config.json / secrets.bin live in %ProgramData% and are writable only by administrators.
/// When the GUI runs unelevated, changes are staged in the user's temp folder and applied by an
/// elevated instance of this executable (UAC prompt) through the 'apply-staged' CLI command.
/// </summary>
public static class ElevatedConfigWriter
{
    public const string ResultFileName = "result.txt";
    private const int ErrorCancelled = 1223;

    public static bool CanWriteConfig(string configDir)
    {
        return CanWriteFile(Path.Combine(configDir, "config.json"), configDir) &&
               CanWriteFile(Path.Combine(configDir, "secrets.bin"), configDir);
    }

    private static bool CanWriteFile(string path, string dir)
    {
        try
        {
            if (File.Exists(path))
            {
                // Opening for write (without writing) checks the ACL without touching the file.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                return true;
            }

            // A missing folder is created by the elevated writer with the installer's ACL: made here it would
            // inherit ProgramData's create-file right and leave config.json writable by every user.
            if (!Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, ".ow-write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // Locked but not denied: let the real write report the problem.
            return true;
        }
    }

    /// <summary>The installer's ACL (icacls /inheritance:r: Administrators and SYSTEM full control, Users read and execute).</summary>
    public static void EnsureProtectedDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        void Allow(WellKnownSidType sid, FileSystemRights rights) => security.AddAccessRule(
            new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
#if NETFRAMEWORK
        Directory.CreateDirectory(dir, security);
#else
        new DirectoryInfo(dir).Create(security);
#endif
    }

    public static string CreateStagingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A folder made by CreateStagingDirectory. It is checked by shape, not by the elevated %TEMP%: with
    /// over-the-shoulder UAC the elevated process runs as another account, whose temp folder is not the user's.</summary>
    public static bool IsStagingDirectory(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));
        const string prefix = "OnlyWorkspace-staging-";
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
               info.Name.StartsWith(prefix, StringComparison.Ordinal) &&
               Guid.TryParseExact(info.Name.Substring(prefix.Length), "N", out _);
    }

    public static void DeleteStagingDirectory(string? stagingDir)
    {
        if (string.IsNullOrEmpty(stagingDir)) return;
        try
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
        catch
        {
            // Best effort: temp folder is cleaned by Windows anyway.
        }
    }

    public sealed class ElevationResult
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Runs this executable elevated with the given CLI arguments and waits for it.</summary>
    public static Task<ElevationResult> RunElevatedAsync(string arguments, string? resultDir = null)
    {
        string exe = Process.GetCurrentProcess().MainModule?.FileName ?? Application.ExecutablePath;
        return Task.Run(() => RunElevated(exe, arguments, resultDir));
    }

    private static ElevationResult RunElevated(string exe, string arguments, string? resultDir)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return new ElevationResult { Message = "Impossibile avviare il processo con privilegi di amministratore." };
            }

            process.WaitForExit();

            string detail = string.Empty;
            if (resultDir != null)
            {
                var resultFile = Path.Combine(resultDir, ResultFileName);
                if (File.Exists(resultFile))
                {
                    detail = File.ReadAllText(resultFile).Trim();
                }
            }

            return process.ExitCode == 0
                ? new ElevationResult { Success = true, Message = detail }
                : new ElevationResult { Message = string.IsNullOrEmpty(detail) ? $"Operazione non riuscita (codice {process.ExitCode})." : detail };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ElevationResult { Cancelled = true, Message = "Richiesta di autorizzazione amministratore annullata." };
        }
        catch (Exception ex)
        {
            return new ElevationResult { Message = ex.Message };
        }
    }

    public static string Quote(string value) => "\"" + value.TrimEnd('\\') + "\"";

}
