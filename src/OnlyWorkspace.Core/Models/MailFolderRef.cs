namespace OnlyWorkspace.Core.Models;

/// <summary>A folder chosen as analysis scope.</summary>
public sealed class MailFolderRef
{
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    /// <summary>"Mailbox\Folder\Subfolder" for display.</summary>
    public string Path { get; set; } = string.Empty;
    public bool IsMailFolder { get; set; } = true;
}
