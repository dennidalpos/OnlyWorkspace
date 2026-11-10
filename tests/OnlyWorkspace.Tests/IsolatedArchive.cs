namespace OnlyWorkspace.Tests;

/// <summary>Archive directory under temp so UI tests never open the user's real archive.</summary>
internal static class IsolatedArchive
{
    public static string NewPath() => Path.Combine(Path.GetTempPath(), "ow-archive-test-" + Guid.NewGuid().ToString("N"));
}
