using System;

namespace OnlyWorkspace.Core.Archive;

/// <summary>A committed deletion; no subject, excerpt or email content is retained.</summary>
public sealed class ArchiveDeletedSource
{
    public long Id { get; set; }
    public string OutlookProfile { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string EntryId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public DateTime DeletedUtc { get; set; }
}
