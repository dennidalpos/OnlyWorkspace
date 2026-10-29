using System;

namespace OnlyWorkspace.Core.Models;

public sealed class EmailSearchResult
{
    public string Subject { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public DateTime ReceivedTime { get; set; }
    public string Body { get; set; } = string.Empty;
    public string InternetMessageId { get; set; } = string.Empty;
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string StoreDisplayName { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public bool HasOnlyHeaders { get; set; }
    public bool IsEncrypted { get; set; }
}
