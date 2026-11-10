using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OnlyWorkspace.Outlook.Services;

internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>User's Downloads folder (honours redirection), %USERPROFILE%\Downloads as fallback.</summary>
    public static string Downloads()
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out ptr) == 0)
            {
                var path = Marshal.PtrToStringUni(ptr);
                if (!string.IsNullOrEmpty(path)) return path!;
            }
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
