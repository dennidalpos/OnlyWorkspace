using System;
using System.Collections.Generic;

namespace OnlyWorkspace.Core.Archive;

/// <summary>An archived mail folder the user can include in the contact search.</summary>
public sealed class ArchiveScopeFolder
{
    public long Id { get; set; }
    public string StoreId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Emails { get; set; }
}

/// <summary>One email written by a candidate: where to open it in Outlook.</summary>
public sealed class ContactSourceEmail
{
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; }
}

/// <summary>An external address of the chosen folders, with its newest emails as sender.</summary>
public sealed class ContactCandidate
{
    public string Address { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long CounterpartyId { get; set; }
    /// <summary>Company domain, or the address itself on mailbox providers.</summary>
    public string GroupKey { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public bool TwoWay { get; set; }
    public int Emails { get; set; }
    public DateTime? LastFromUtc { get; set; }
    /// <summary>Newest first, one per Message-ID.</summary>
    public List<ContactSourceEmail> Sources { get; } = new();
}

public sealed class ContactRejection
{
    public string Address { get; set; } = string.Empty;
    public DateTime RejectedUtc { get; set; }
}
