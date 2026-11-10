using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>Row cursor over mail headers, owned by the Outlook UI thread: read a small batch, yield, resume.</summary>
public interface IMailHeaderScan : IDisposable
{
    bool Complete { get; }
    int RowsScanned { get; }
    int FoldersFailed { get; }
    IReadOnlyList<EmailSearchResult> ReadNext(int rowsPerBatch);
}

/// <summary>Cursor over sent mail that may still await a reply, with the same batch contract as <see cref="IMailHeaderScan"/>.</summary>
public interface IUnansweredSentScan : IDisposable
{
    bool Complete { get; }
    int Scanned { get; }
    int FoldersFailed { get; }
    IReadOnlyList<FollowUpCandidate> ReadNext(int rowsPerBatch);
}

/// <summary>
/// The Outlook operations the task pane needs, UI thread only. The pane depends on this narrow surface, not on the
/// COM-heavy <see cref="OutlookDataService"/>, so its behavior can be exercised without Outlook.
/// </summary>
public interface IOutlookDataService : IArchiveMailSource
{
    SelectedEmailInfo? GetSelectedEmailHeader(object? contextWindow = null);
    bool CanActOnSelectedEmail(object? contextWindow = null);
    string? GetComposeSubject(object? contextWindow = null);
    Task<SelectedEmailInfo?> GetCurrentSelectedEmailAsync(object? contextWindow = null, CancellationToken ct = default);
    Task<SelectedEmailInfo?> GetEmailByEntryIdAsync(string entryId, string? storeId = null, CancellationToken ct = default);
    EmailSecurityData? GetEmailSecurityData(object? contextWindow, string? entryId = null, string? storeId = null);
    Task<AttachmentCopies?> CopyAttachmentsAsync(string entryId, string? storeId, Func<AttachmentFacts, bool> shouldSave, CancellationToken ct = default);
    Task<AttachmentCopies?> CopyDraftAttachmentsAsync(object? contextWindow, Func<AttachmentFacts, bool> shouldSave, CancellationToken ct = default);
    IReadOnlyList<EmailSearchResult> GetUnreadInbox(int max);
    IReadOnlyList<string> GetCategoryNames();
    void ApplyCategory(string entryId, string? storeId, string category);
    IReadOnlyList<AppointmentInfo> GetAppointments(CalendarQuery query);
    IReadOnlyList<ExtractedActionItem> GetOpenTasks(int max);
    void DisplayActionItemDraft(ExtractedActionItem item);
    void OpenFollowUpDraft(string entryId, string? storeId, string draftMarkdown);
    bool InsertReplyDraft(string draftMarkdown, object? contextWindow = null, bool replyAll = false, string? entryId = null, string? storeId = null);
    IUnansweredSentScan BeginUnansweredSentScan(int minDays, int lookbackDays, ICollection<string>? storeIds = null, EmailSearchQuery? filter = null);
    SelectedEmailInfo? GetCurrentSelectedEmail(object? contextWindow = null);
    SelectedEmailInfo? GetEmailByEntryId(string entryId, string? storeId = null);
    string? GetEmailBody(string entryId, string? storeId);
    string GetEmailMessageId(string entryId, string storeId);
    bool DisplayEmail(string entryId, string? storeId);
    IReadOnlyList<ContactInfo> GetContacts(ContactQuery query);
    IReadOnlyList<MailFolderRef> GetStoreFolders(ICollection<string> storeIds, bool wholeMailbox);
    IReadOnlyList<MailFolderRef> GetChatSearchFolders();
    MailFolderRef? PickFolder();
    MailFolderRef? GetFolderOf(string entryId, string? storeId);
    IMailHeaderScan BeginMailHeaderScan(IReadOnlyList<MailFolderRef> folders, bool includeSubfolders, EmailSearchQuery query);
    Task<IReadOnlyList<PrivacyEntity>> ReadPrivacyEntitiesAsync(CancellationToken ct);
    void SaveActionItem(ExtractedActionItem item);
    ComposeDraft? GetComposeDraft(object? contextWindow = null);
    bool InsertIntoDraft(string markdown, object? contextWindow = null);
    bool ReplaceDraftSelection(string expected, string markdown, object? contextWindow = null);
    bool SetDraftSubject(string subject, object? contextWindow = null);
    void OpenNewMail(string subject, string bodyMarkdown);
    string GetOutlookVersion();
    string GetOutlookBitness();
}
