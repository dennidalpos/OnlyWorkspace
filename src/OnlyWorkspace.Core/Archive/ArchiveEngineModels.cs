using System;

namespace OnlyWorkspace.Core.Archive;

public sealed class ArchiveProfileStatus
{
    public string Profile { get; set; } = string.Empty;
    public string Phase { get; set; } = "waiting";
    public string? Error { get; set; }
    public bool Paused { get; set; }
    public bool OutlookConnected { get; set; }
    public DateTime? LastOutlookUtc { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public ArchiveStats Stats { get; set; } = new();
}

public sealed class ArchiveHeaderCommit
{
    public int Added { get; set; }
    public ArchiveFolder Folder { get; set; } = new();
}

public sealed class ArchiveOriginalRequest
{
    public string Id { get; set; } = string.Empty;
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
}

public sealed class ArchiveOriginalResult
{
    public bool Ready { get; set; }
    public string? Text { get; set; }
    public string? Error { get; set; }
}
