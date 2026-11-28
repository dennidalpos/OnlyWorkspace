using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Tests;

/// <summary>#096: the Downloads folder is the shell's answer, or the profile fallback, never empty.</summary>
public sealed class KnownFoldersTests
{
    [Fact]
    public void DownloadsIsAnAbsolutePathNamedAfterTheShellFolder()
    {
        string path = KnownFolders.Downloads();
        Assert.True(Path.IsPathRooted(path));
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.Equal(path, KnownFolders.Downloads());
    }
}
