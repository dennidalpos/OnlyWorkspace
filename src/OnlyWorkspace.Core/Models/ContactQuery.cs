namespace OnlyWorkspace.Core.Models;

public sealed class ContactQuery
{
    public string? SearchTerm { get; set; }
    public int MaxResults { get; set; } = 50;
    public string? StoreId { get; set; }
}
