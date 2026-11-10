using System.Collections.Generic;
using System.Threading.Tasks;
using OnlyWorkspace.Infrastructure.Archive;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>The part of <see cref="ArchiveIndexer"/> the pane uses, so the coordinator does not read the process-wide instance.</summary>
public interface IArchiveIndexer
{
    ArchiveClient? Db { get; }
    Task<ArchiveClient?> OpenForSearchAsync();
    Task<List<string>?> GetAllowedSearchStoresAsync();
    Task<ArchiveStatus> GetStatusAsync();
    Task ReportMissingAsync();
    Task SetPausedAsync(bool paused);
    Task IndexNowAsync();
    Task DeleteArchiveAsync();
}

/// <summary>The one place that resolves the running indexer for the pane.</summary>
public static class ArchiveIndexerAccess
{
    public static IArchiveIndexer? Current() => ArchiveIndexer.Current;
}
