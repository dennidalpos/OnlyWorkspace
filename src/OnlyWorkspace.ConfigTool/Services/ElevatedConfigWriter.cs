using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using System.Threading.Tasks;
using OnlyWorkspace.ConfigTool.Forms;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>
/// config.json lives in %ProgramData% and is writable only by administrators (the per-user secrets.bin does not).
/// When the GUI runs unelevated, changes are staged in the user's temp folder and applied by an
/// elevated instance of this executable (UAC prompt) through the 'apply-staged' CLI command.
/// </summary>
public static class ElevatedConfigWriter
{
    public const string ResultFileName = "result.txt";
    public const string DetailFileName = "result-detail.txt";
    private const int ErrorCancelled = 1223;

    public static bool CanWriteConfig(string configDir)
    {
        // In the standard folder the vault is per user in %LOCALAPPDATA% and never part of the administrators' write.
        return CanWriteFile(Path.Combine(configDir, "config.json"), configDir) &&
               (OnlyWorkspace.Core.AppPaths.IsStandardConfigDirectory(configDir) || CanWriteFile(Path.Combine(configDir, "secrets.bin"), configDir));
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>Checks the folder and keeps it open without delete sharing: the user can no longer rename or replace it
    /// (or a parent) with a junction between this check and the result write. Null if it is not a staging folder.</summary>
    internal static IDisposable? HoldStagingDirectory(string path)
    {
        const uint ListDirectory = 0x1, ShareRead = 0x1, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        var handle = CreateFile(Path.GetFullPath(path), ListDirectory, ShareRead, IntPtr.Zero, OpenExisting,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        if (!IsStagingDirectory(path)) { handle.Dispose(); return null; }
        return handle;
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
        public string TechnicalDetail { get; set; } = string.Empty;
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

            string detail = string.Empty, technicalDetail = string.Empty;
            if (resultDir != null)
            {
                var resultFile = Path.Combine(resultDir, ResultFileName);
                if (File.Exists(resultFile))
                {
                    detail = File.ReadAllText(resultFile).Trim();
                }
                var detailFile = Path.Combine(resultDir, DetailFileName);
                if (File.Exists(detailFile)) technicalDetail = File.ReadAllText(detailFile);
            }

            return process.ExitCode == 0
                ? new ElevationResult { Success = true, Message = detail }
                : new ElevationResult { Message = string.IsNullOrEmpty(detail) ? $"Operazione non riuscita (codice {process.ExitCode})." : detail, TechnicalDetail = technicalDetail };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ElevationResult { Cancelled = true, Message = "Richiesta di autorizzazione amministratore annullata." };
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    internal static ElevationResult Failure(Exception ex) => new()
    {
        Message = ErrorText.For(ex), TechnicalDetail = ex.ToString()
    };

    /// <summary>The GUI reads the Italian outcome separately from the original, copyable diagnostic.</summary>
    internal static void WriteFailure(string stagingDir, Exception ex)
    {
        var result = Failure(ex);
        WriteResult(stagingDir, ResultFileName, result.Message);
        WriteResult(stagingDir, DetailFileName, result.TechnicalDetail);
    }

    /// <summary>The staging folder belongs to the unelevated user, so the elevated writer never opens an existing entry:
    /// a link or hardlink left at the name is removed (File.Delete drops the entry, not its target) and the file is
    /// created new. If the user re-plants a link in between, CreateNew fails and nothing is written.</summary>
    internal static void WriteResult(string stagingDir, string fileName, string text)
    {
        string path = Path.Combine(stagingDir, fileName);
        File.Delete(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    public static string Quote(string value) => "\"" + value.TrimEnd('\\') + "\"";

}
