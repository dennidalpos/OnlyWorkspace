using System.Collections.Generic;

namespace OnlyWorkspace.Core.Models;

public sealed class WorkspaceLimits
{
    /// <summary>Calendar/contact result limit; mail search uses local batches until the scope is exhausted.</summary>
    public int MaxSearchItems { get; set; } = 50;
    /// <summary>Sent emails without reply for at least this many days are proposed for follow-up.</summary>
    public int FollowUpDays { get; set; } = 5;
    /// <summary>"Cerca e analizza" (A124): the search covers the whole scope, the analysis only this many most recent emails; the rest is declared.</summary>
    public int MaxAnalysisEmails { get; set; } = 300;
}

public sealed class WorkspaceHistoryConfig
{
    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = 90;
    public int MaxConversations { get; set; } = 200;
}

/// <summary>Local mail archive (index of headers, words and counterparties). Off until the administrator enables it.</summary>
public sealed class WorkspaceArchiveConfig
{
    public bool Enabled { get; set; } = false;
    /// <summary>Supported attachment text is included in the shared local index.</summary>
    public bool IndexAttachmentText { get; set; } = true;

}

public sealed class WorkspaceLoggingConfig
{
    public string Level { get; set; } = "Information";
}

public sealed class WorkspaceConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string DefaultProfile { get; set; } = "ollama-lan";
    /// <summary>Windows OCR of scanned PDF pages and attached images, in the archive and in the pane (E8).</summary>
    public bool AttachmentOcr { get; set; } = true;
    public WorkspaceLimits Limits { get; set; } = new();
    public WorkspaceHistoryConfig History { get; set; } = new();
    public WorkspaceArchiveConfig Archive { get; set; } = new();
    public WorkspaceLoggingConfig Logging { get; set; } = new();
    /// <summary>Acceptance of the notice on data leaving Outlook: without it cloud profiles and external web search are blocked.</summary>
    public Privacy.WorkspaceDataEgressConfig DataEgress { get; set; } = new();
    public List<LlmProfileConfig> Profiles { get; set; } = new();
}
