using System;
using System.Collections.Generic;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Core.Models;

/// <summary>Sender, Recipient and Subject accept the wildcards "*" and "?" (see WildcardPattern).</summary>
public sealed class EmailSearchQuery
{
    public List<ArchiveFactFilter> FactFilters { get; set; } = new();
    public string? Keywords { get; set; }
    /// <summary>Freeform question: significant words are alternative local clues, not one required phrase.</summary>
    public bool KeywordsAny { get; set; }
    public string? Sender { get; set; }
    /// <summary>Matched on the To/Cc display names.</summary>
    public string? Recipient { get; set; }
    /// <summary>True: Sender OR Recipient (emails exchanged with a person); false: both must match.</summary>
    public bool EitherPerson { get; set; }
    public string? Subject { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int MaxResults { get; set; } = 50;
    public string? StoreId { get; set; }
}
