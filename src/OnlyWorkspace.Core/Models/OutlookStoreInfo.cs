namespace OnlyWorkspace.Core.Models;

public sealed class OutlookStoreInfo
{
    public string StoreId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public bool IsOpen { get; set; }
    public string? ExchangeStoreType { get; set; }
    public bool IsDataFileStore { get; set; }
    /// <summary>An Outlook account delivers to this store.</summary>
    public bool HasAccount { get; set; }
}
