using System.IO;
using System.Threading;
using OnlyWorkspace.Infrastructure.Storage;

namespace OnlyWorkspace.Tests;

public sealed class AppStorageUsageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-storage-" + Guid.NewGuid().ToString("N"));

    public AppStorageUsageTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private void Write(string name, int bytes) => File.WriteAllBytes(Path.Combine(_directory, name), new byte[bytes]);

    [Fact]
    public void Archive_IsSplitByKindOfFile()
    {
        Write("archive-a.engine.db", 3 * 1024 * 1024);
        Write("archive-a.engine.db-wal", 1024);
        Write("archive-a.engine.db-shm", 1024);
        Write("archive-a.engine.db.format-9.rollback.db", 2 * 1024 * 1024);
        Write("archive.key", 100);

        var item = AppStorageUsage.Archive(_directory, CancellationToken.None);

        Assert.Equal(5, item.Files);
        Assert.Equal(5L * 1024 * 1024 + 2048 + 100, item.Bytes);
        Assert.Equal("database 3,0 MB · giornali 2 KB · copie di ripristino 2,0 MB · chiave e blocchi 1 KB", item.Detail);
    }

    [Fact]
    public void Folder_CountsSubfoldersAndAMissingFolderIsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "sub"));
        Write("one.log", 10);
        Write(Path.Combine("sub", "two.log"), 20);

        var item = AppStorageUsage.Folder("Log", _directory, "note", CancellationToken.None);
        Assert.Equal(2, item.Files);
        Assert.Equal(30, item.Bytes);
        Assert.Equal(0, item.Unreadable);

        var missing = AppStorageUsage.Folder("Nulla", Path.Combine(_directory, "missing"), "note", CancellationToken.None);
        Assert.Equal(0, missing.Files);
        Assert.Equal(0, missing.Bytes);
    }

    [Theory]
    [InlineData(0, "0 KB")]
    [InlineData(1, "1 KB")]
    [InlineData(1536 * 1024, "1,5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3,0 GB")]
    public void Sizes_UseItalianNotationOnAnyWindowsLanguage(long bytes, string expected) => Assert.Equal(expected, AppStorageUsage.Text(bytes));

    [Fact]
    public void Measure_ListsEveryGeneratedLocationOnce()
    {
        var report = AppStorageUsage.Measure(CancellationToken.None, _directory);
        Assert.Equal(9, report.Items.Count);
        Assert.Contains(report.Items, i => i.Path == OnlyWorkspace.Core.AppPaths.PseudonymizationAuditDirectory);
        Assert.Equal(report.Items.Count, report.Items.Select(i => i.Name).Distinct().Count());
        Assert.All(report.Items, i => Assert.True(i.Bytes >= 0 && i.Note.Length > 0));
        Assert.Contains(report.Items, i => i.Path == _directory);
    }
}
