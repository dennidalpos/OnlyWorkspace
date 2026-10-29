using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Core.Utils;
using OnlyWorkspace.Infrastructure.Logging;
using MSOutlook = Microsoft.Office.Interop.Outlook;
using MSWord = Microsoft.Office.Interop.Word;

namespace OnlyWorkspace.Outlook.Services;

public sealed class SelectedEmailInfo
{
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public DateTime ReceivedTime { get; set; }
    public string EntryId { get; set; } = string.Empty;
    /// <summary>Store of the parent folder: GetItemFromID needs it outside the default store.</summary>
    public string StoreId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string InReplyTo { get; set; } = string.Empty;
    public string References { get; set; } = string.Empty;
    public DateTime? SentUtc { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    /// <summary>1-based position of the message inside its conversation (from PR_CONVERSATION_INDEX).</summary>
    public int ThreadPosition { get; set; } = 1;
    public IReadOnlyList<SelectedEmailInfo>? ThreadMessages { get; set; }
    public bool ConversationLimited { get; set; }
    /// <summary>Emails in the Outlook conversation; more than ThreadMessages when only the most recent were read.</summary>
    public int ConversationTotal { get; set; }
}

public sealed class EmailSecurityData
{
    public EmailSecurityData(SelectedEmailInfo email, EmailSecurityInput input, string scanDirectory, IReadOnlyList<KeyValuePair<AttachmentFacts, string>> savedAttachments)
    {
        Email = email;
        Input = input;
        ScanDirectory = scanDirectory;
        SavedAttachments = savedAttachments;
    }

    public SelectedEmailInfo Email { get; }
    public EmailSecurityInput Input { get; }
    /// <summary>Temp folder with the attachment copies; the caller deletes it.</summary>
    public string ScanDirectory { get; }
    /// <summary>Attachments saved for the antivirus scan, with their temp path.</summary>
    public IReadOnlyList<KeyValuePair<AttachmentFacts, string>> SavedAttachments { get; }
}

public sealed partial class OutlookDataService
{
    private const int MaxThreadMessages = 20;
    private const string PropTransportHeaders = "http://schemas.microsoft.com/mapi/proptag/0x007D001F"; // PR_TRANSPORT_MESSAGE_HEADERS (Unicode)

    private IReadOnlyList<string>? _ownDomains;
    // The PSTs' own addresses and names (archive meta store_own_addresses), set by the archive indexer.
    private IReadOnlyCollection<string> _storeOwnAddresses = Array.Empty<string>();
    private IReadOnlyCollection<string> _storeOwnNames = Array.Empty<string>();

    private readonly MSOutlook.Application _application;
    private readonly int _uiThreadId;

    public OutlookDataService(MSOutlook.Application application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _uiThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    /// <summary>
    /// The Outlook Object Model is single-threaded (STA). Calling it from a pool thread can hang or crash Outlook,
    /// so fail fast instead.
    /// </summary>
    private void EnsureUiThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _uiThreadId)
        {
            throw new InvalidOperationException("Outlook Object Model accessed outside the Outlook UI thread.");
        }
    }

    /// <summary>
    /// Window RCWs (Explorer/Inspector) are shared with the TaskPaneManager and are intentionally never released here.
    /// </summary>
    private object? ResolveWindow(object? contextWindow)
    {
        return contextWindow ?? _application.ActiveWindow();
    }

    /// <summary>
    /// Lightweight read used on every selection change: no Body, no conversation walk.
    /// </summary>
    public SelectedEmailInfo? GetSelectedEmailHeader(object? contextWindow = null)
    {
        EnsureUiThread();
        return WithSelectedMail(ResolveWindow(contextWindow), ExtractHeaderInfo);
    }

    /// <summary>Ribbon visibility and selection-based actions: no body, conversation or pane creation.</summary>
    public bool CanActOnSelectedEmail(object? contextWindow = null)
    {
        EnsureUiThread();
        try
        {
            var window = ResolveWindow(contextWindow);
            return GetComposeSubject(window) == null && WithSelectedMail(window, _ => string.Empty) != null;
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Selected email action context unavailable");
            return false;
        }
    }

    public SelectedEmailInfo? GetCurrentSelectedEmail(object? contextWindow = null)
    {
        EnsureUiThread();
        return WithSelectedMail(ResolveWindow(contextWindow), ExtractEmailInfo);
    }

    /// <summary>Ids of the first email of a ribbon context-menu Selection (the caller owns the Selection).</summary>
    public bool TryGetFirstMailId(MSOutlook.Selection selection, out string entryId, out string storeId)
    {
        EnsureUiThread();
        entryId = storeId = string.Empty;
        object? item = null;
        try
        {
            if (selection.Count == 0) return false;
            item = selection[1];
            if (item is not MSOutlook.MailItem mail) return false;
            entryId = mail.EntryID ?? string.Empty;
            storeId = SafeGetStoreId(mail);
            return entryId.Length > 0;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    private static T? WithSelectedMail<T>(object? window, Func<MSOutlook.MailItem, T> extractor) where T : class
    {
        if (window is MSOutlook.Inspector inspector)
        {
            object? currentItem = null;
            try
            {
                currentItem = inspector.CurrentItem;
                // An email being written is not an email to act on: the writing actions read it (GetComposeDraft).
                return currentItem is MSOutlook.MailItem { Sent: true } mail ? extractor(mail) : null;
            }
            finally
            {
                if (currentItem != null) Marshal.ReleaseComObject(currentItem);
            }
        }

        if (window is MSOutlook.Explorer explorer)
        {
            MSOutlook.Selection? selection = null;
            object? item = null;
            try
            {
                selection = explorer.Selection;
                if (selection != null && selection.Count > 0)
                {
                    item = selection[1];
                    if (item is MSOutlook.MailItem mail)
                    {
                        return extractor(mail);
                    }
                }
                return null;
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
                if (selection != null) Marshal.ReleaseComObject(selection);
            }
        }

        return null;
    }

    /// <summary>Most recent unread email headers of the default Inbox; Briefing reopens their whole originals.</summary>
    public IReadOnlyList<EmailSearchResult> GetUnreadInbox(int max)
    {
        EnsureUiThread();
        var result = new List<EmailSearchResult>();
        MSOutlook.NameSpace? session = null;
        MSOutlook.MAPIFolder? inbox = null;
        MSOutlook.Table? table = null;
        try
        {
            session = _application.Session;
            inbox = session.GetDefaultFolder(MSOutlook.OlDefaultFolders.olFolderInbox);
            string storeId = inbox.StoreID ?? string.Empty;
            // Table rows, not Items.Restrict + Sort + index: on a large inbox that froze Outlook for minutes.
            table = inbox.GetTable("[UnRead] = True", MSOutlook.OlTableContents.olUserItems);
            table.Sort("[ReceivedTime]", true);

            // The result count is bounded; non-mail rows do not consume the limit.
            for (int scanned = 0; result.Count < max && !table.EndOfTable; scanned++)
            {
                MSOutlook.Row? row = null;
                object? obj = null;
                try
                {
                    row = table.GetNextRow();
                    if (row["EntryID"] is not string entryId) continue;
                    obj = session.GetItemFromID(entryId, storeId);
                    if (obj is not MSOutlook.MailItem mail) continue;
                    result.Add(new EmailSearchResult
                    {
                        Subject = mail.Subject ?? string.Empty,
                        SenderName = mail.SenderName ?? string.Empty,
                        SenderEmail = mail.SenderEmailAddress ?? string.Empty,
                        ReceivedTime = mail.ReceivedTime,
                        EntryId = entryId,
                        StoreId = storeId,
                        FolderName = inbox.Name ?? string.Empty
                    });
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read unread row {Index}", scanned);
                }
                finally
                {
                    if (obj != null) Marshal.ReleaseComObject(obj);
                    if (row != null) Marshal.ReleaseComObject(row);
                }
            }
        }
        finally
        {
            if (table != null) Marshal.ReleaseComObject(table);
            if (inbox != null) Marshal.ReleaseComObject(inbox);
            if (session != null) Marshal.ReleaseComObject(session);
        }
        return result;
    }

    /// <summary>Names from the profile's master category list.</summary>
    public IReadOnlyList<string> GetCategoryNames()
    {
        EnsureUiThread();
        var names = new List<string>();
        MSOutlook.NameSpace? session = null;
        MSOutlook.Categories? categories = null;
        try
        {
            session = _application.Session;
            categories = session.Categories;
            for (int i = 1; i <= categories.Count; i++)
            {
                MSOutlook.Category? category = null;
                try
                {
                    category = categories[i];
                    if (!string.IsNullOrWhiteSpace(category.Name)) names.Add(category.Name);
                }
                finally
                {
                    if (category != null) Marshal.ReleaseComObject(category);
                }
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read categories");
        }
        finally
        {
            if (categories != null) Marshal.ReleaseComObject(categories);
            if (session != null) Marshal.ReleaseComObject(session);
        }
        return names;
    }

    /// <summary>Adds an existing category to the email (user-confirmed action from the briefing).</summary>
    public void ApplyCategory(string entryId, string? storeId, string category)
    {
        EnsureUiThread();
        var applied = WithMailById(entryId, storeId, mail =>
        {
            // Outlook joins categories with the regional list separator.
            var separator = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator;
            var current = mail.Categories ?? string.Empty;
            var existing = current.Split(new[] { separator, ",", ";" }, StringSplitOptions.RemoveEmptyEntries);
            if (Array.Exists(existing, c => c.Trim().Equals(category, StringComparison.OrdinalIgnoreCase))) return Done;
            mail.Categories = current.Trim().Length == 0 ? category : current.TrimEnd() + separator + " " + category;
            mail.Save();
            return Done;
        });
        if (applied == null) throw new InvalidOperationException("L'email non è più disponibile (spostata o eliminata).");
    }

    private const string PropSmtpAddress ="http://schemas.microsoft.com/mapi/proptag/0x39FE001F"; // PR_SMTP_ADDRESS
    private const string PropClientSubmitTime = "http://schemas.microsoft.com/mapi/proptag/0x00390040"; // PR_CLIENT_SUBMIT_TIME (SentOn)
    /// <summary>true/false from the conversation table; null when the conversation is unavailable (no verdict).</summary>
    private static bool? HasReplyFromOthers(MSOutlook.MailItem sentMail, HashSet<string> me)
    {
        MSOutlook.Conversation? conversation = null;
        MSOutlook.Table? table = null;
        MSOutlook.Columns? columns = null;
        try
        {
            conversation = sentMail.GetConversation();
            if (conversation == null) return null;
            table = conversation.GetTable();
            columns = table.Columns;
            Marshal.ReleaseComObject(columns.Add("SenderEmailAddress"));
            Marshal.ReleaseComObject(columns.Add("ReceivedTime"));

            var sentOn = sentMail.SentOn;
            while (!table.EndOfTable)
            {
                var row = table.GetNextRow();
                try
                {
                    var sender = row["SenderEmailAddress"] as string ?? string.Empty;
                    // Built-in column names return local time, like SentOn.
                    if (row["ReceivedTime"] is DateTime received && received > sentOn.AddMinutes(1) && !me.Contains(sender))
                    {
                        return true;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(row);
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Conversation table unavailable");
            return null;
        }
        finally
        {
            if (columns != null) Marshal.ReleaseComObject(columns);
            if (table != null) Marshal.ReleaseComObject(table);
            if (conversation != null) Marshal.ReleaseComObject(conversation);
        }
    }

    /// <summary>To/Cc display names, excluding the user.</summary>
    private static string RecipientNames(MSOutlook.MailItem mail, HashSet<string> me)
    {
        var names = new List<string>();
        MSOutlook.Recipients? recipients = null;
        try
        {
            recipients = mail.Recipients;
            for (int i = 1; i <= recipients.Count; i++)
            {
                MSOutlook.Recipient? r = null;
                try
                {
                    r = recipients[i];
                    if (r.Type == (int)MSOutlook.OlMailRecipientType.olBCC || me.Contains(r.Address ?? string.Empty)) continue;
                    names.Add(string.IsNullOrWhiteSpace(r.Name) ? r.Address ?? string.Empty : r.Name);
                }
                finally
                {
                    if (r != null) Marshal.ReleaseComObject(r);
                }
            }
        }
        finally
        {
            if (recipients != null) Marshal.ReleaseComObject(recipients);
        }
        return names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.GetRange(0, 3)) + $" e altri {names.Count - 3}";
    }

    /// <summary>SMTP addresses of the accounts plus the Exchange DN of the current user.</summary>
    private HashSet<string> GetOwnAddresses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MSOutlook.NameSpace? session = null;
        MSOutlook.Accounts? accounts = null;
        MSOutlook.Recipient? current = null;
        try
        {
            session = _application.Session;
            current = session.CurrentUser;
            if (!string.IsNullOrEmpty(current?.Address)) set.Add(current!.Address);
            accounts = session.Accounts;
            for (int i = 1; i <= accounts.Count; i++)
            {
                MSOutlook.Account? account = null;
                try
                {
                    account = accounts[i];
                    if (!string.IsNullOrEmpty(account.SmtpAddress)) set.Add(account.SmtpAddress);
                }
                catch
                {
                    // Account without SMTP address
                }
                finally
                {
                    if (account != null) Marshal.ReleaseComObject(account);
                }
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read own addresses");
        }
        finally
        {
            if (accounts != null) Marshal.ReleaseComObject(accounts);
            if (current != null) Marshal.ReleaseComObject(current);
            if (session != null) Marshal.ReleaseComObject(session);
        }
        set.UnionWith(_storeOwnAddresses);
        return set;
    }

    /// <summary>Names the accounts send as (profile user and each account's "Your name").</summary>
    private IReadOnlyList<string> GetOwnNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MSOutlook.NameSpace? session = null;
        MSOutlook.Accounts? accounts = null;
        MSOutlook.Recipient? current = null;
        try
        {
            session = _application.Session;
            current = session.CurrentUser;
            if (!string.IsNullOrWhiteSpace(current?.Name)) names.Add(current!.Name);
            accounts = session.Accounts;
            for (int i = 1; i <= accounts.Count; i++)
            {
                MSOutlook.Account? account = null;
                try
                {
                    account = accounts[i];
                    if (!string.IsNullOrWhiteSpace(account.UserName)) names.Add(account.UserName);
                }
                catch
                {
                    // Account without a user name
                }
                finally
                {
                    if (account != null) Marshal.ReleaseComObject(account);
                }
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read own names");
        }
        finally
        {
            if (accounts != null) Marshal.ReleaseComObject(accounts);
            if (current != null) Marshal.ReleaseComObject(current);
            if (session != null) Marshal.ReleaseComObject(session);
        }
        names.UnionWith(_storeOwnNames);
        return names.ToList();
    }

    /// <summary>Follow-up on a sent email: new reply addressed to its original To/Cc (Reply on a sent item would target the user).</summary>
    public void OpenFollowUpDraft(string entryId, string? storeId, string draftMarkdown)
    {
        EnsureUiThread();
        if (WithMailById(entryId, storeId, sentMail => OpenFollowUp(sentMail, draftMarkdown)) == null)
            throw new InvalidOperationException("L'email inviata non è più disponibile (spostata o eliminata).");
    }

    private static object OpenFollowUp(MSOutlook.MailItem sentMail, string draftMarkdown)
    {
        MSOutlook.MailItem? reply = null;
        MSOutlook.Inspector? inspector = null;
        MSOutlook.Recipients? target = null;
        MSOutlook.Recipients? original = null;
        try
        {
            reply = sentMail.ReplyAll();
            inspector = reply.GetInspector; // adds the default signature
            target = reply.Recipients;
            while (target.Count > 0) target.Remove(1);

            original = sentMail.Recipients;
            for (int i = 1; i <= original.Count; i++)
            {
                MSOutlook.Recipient? source = null;
                MSOutlook.Recipient? added = null;
                try
                {
                    source = original[i];
                    if (source.Type == (int)MSOutlook.OlMailRecipientType.olBCC) continue;
                    added = target.Add(SmtpOf(source));
                    added.Type = source.Type;
                }
                finally
                {
                    if (added != null) Marshal.ReleaseComObject(added);
                    if (source != null) Marshal.ReleaseComObject(source);
                }
            }
            target.ResolveAll();
            PrependDraft(reply, draftMarkdown);
            reply.Display(false);
            return Done;
        }
        finally
        {
            if (original != null) Marshal.ReleaseComObject(original);
            if (target != null) Marshal.ReleaseComObject(target);
            if (inspector != null) Marshal.ReleaseComObject(inspector);
            if (reply != null) Marshal.ReleaseComObject(reply);
        }
    }

    /// <summary>Exchange recipients carry an X.500 address: prefer PR_SMTP_ADDRESS so Add() resolves reliably.</summary>
    private static string SmtpOf(MSOutlook.Recipient recipient)
    {
        MSOutlook.PropertyAccessor? accessor = null;
        try
        {
            accessor = recipient.PropertyAccessor;
            if (accessor.GetProperty(PropSmtpAddress) is string smtp && smtp.Length > 0) return smtp;
        }
        catch
        {
            // Property not available
        }
        finally
        {
            if (accessor != null) Marshal.ReleaseComObject(accessor);
        }
        return recipient.Address ?? recipient.Name ?? string.Empty;
    }

    /// <summary>Opens an unsaved Task/Appointment window prefilled from the item: the user reviews and saves it.</summary>
    public void DisplayActionItemDraft(ExtractedActionItem item) => CreateActionItem(item, save: false);

    /// <summary>Category on items saved by the mailbox scan, to find or remove them later.</summary>
    public const string SavedItemCategory = "OnlyWorkspace";

    /// <summary>Saves the item straight into the default Calendar/Tasks folder (confirmed by the user in the pane).</summary>
    public void SaveActionItem(ExtractedActionItem item) => CreateActionItem(item, save: true);

    private void CreateActionItem(ExtractedActionItem item, bool save)
    {
        EnsureUiThread();
        object? created = null;
        try
        {
            if (item.IsAppointment && item.Start.HasValue)
            {
                created = _application.CreateItem(MSOutlook.OlItemType.olAppointmentItem);
                var appointment = (MSOutlook.AppointmentItem)created;
                appointment.Subject = item.Title;
                appointment.AllDayEvent = item.AllDay;
                appointment.Start = item.Start.Value;
                appointment.End = item.End ?? item.Start.Value.AddHours(1);
                if (item.Location.Length > 0) appointment.Location = item.Location;
                appointment.Body = item.Notes;
                if (save)
                {
                    appointment.Categories = SavedItemCategory;
                    appointment.Save();
                }
                else appointment.Display(false);
            }
            else
            {
                created = _application.CreateItem(MSOutlook.OlItemType.olTaskItem);
                var task = (MSOutlook.TaskItem)created;
                task.Subject = item.Title;
                if (item.Due.HasValue) task.DueDate = item.Due.Value.Date;
                task.Body = item.Notes;
                if (save)
                {
                    task.Categories = SavedItemCategory;
                    task.Save();
                }
                else task.Display(false);
            }
        }
        finally
        {
            // The open Inspector keeps its own reference.
            if (created != null) Marshal.ReleaseComObject(created);
        }
    }

    /// <summary>Inbox, or the mailbox root, of the chosen stores (all open stores when none is given).</summary>
    public IReadOnlyList<MailFolderRef> GetStoreFolders(ICollection<string> storeIds, bool wholeMailbox) =>
        StoreFolders(storeIds, wholeMailbox, MSOutlook.OlDefaultFolders.olFolderInbox);

    private IReadOnlyList<MailFolderRef> DefaultFoldersOfAllStores(params MSOutlook.OlDefaultFolders[] kinds) => StoreFolders(null, root: false, kinds);

    /// <summary>Default folders (or the root) of the open stores, limited to <paramref name="storeIds"/> when given.</summary>
    private IReadOnlyList<MailFolderRef> StoreFolders(ICollection<string>? storeIds, bool root, params MSOutlook.OlDefaultFolders[] kinds)
    {
        EnsureUiThread();
        var result = new List<MailFolderRef>();
        var only = storeIds != null && storeIds.Count > 0 ? new HashSet<string>(storeIds, StringComparer.OrdinalIgnoreCase) : null;
        MSOutlook.NameSpace? session = null;
        MSOutlook.Stores? stores = null;
        try
        {
            session = _application.Session;
            stores = session.Stores;
            for (int s = 1; s <= stores.Count; s++)
            {
                MSOutlook.Store? store = null;
                try
                {
                    store = stores[s];
                    if (store == null || !store.IsOpen) continue;
                    if (only != null && !only.Contains(store.StoreID ?? string.Empty)) continue;
                    if (root)
                    {
                        MSOutlook.MAPIFolder? rootFolder = null;
                        try
                        {
                            rootFolder = store.GetRootFolder();
                            result.Add(FolderRefOf(rootFolder));
                        }
                        finally
                        {
                            if (rootFolder != null) Marshal.ReleaseComObject(rootFolder);
                        }
                        continue;
                    }
                    foreach (var kind in kinds)
                    {
                        MSOutlook.MAPIFolder? folder = null;
                        try
                        {
                            folder = store.GetDefaultFolder(kind);
                            result.Add(FolderRefOf(folder));
                        }
                        catch { } // store without that folder (archive, public folders)
                        finally
                        {
                            if (folder != null) Marshal.ReleaseComObject(folder);
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read default folders of store at index {Index}", s);
                }
                finally
                {
                    if (store != null) Marshal.ReleaseComObject(store);
                }
            }
        }
        finally
        {
            if (stores != null) Marshal.ReleaseComObject(stores);
            if (session != null) Marshal.ReleaseComObject(session);
        }
        return result;
    }

    private static MailFolderRef FolderRefOf(MSOutlook.MAPIFolder folder) => new()
    {
        EntryId = folder.EntryID ?? string.Empty,
        StoreId = folder.StoreID ?? string.Empty,
        Path = (folder.FolderPath ?? folder.Name ?? string.Empty).TrimStart('\\'),
        IsMailFolder = folder.DefaultItemType == MSOutlook.OlItemType.olMailItem
    };

    /// <summary>Outlook's own folder dialog; null when the user cancels.</summary>
    public MailFolderRef? PickFolder()
    {
        EnsureUiThread();
        MSOutlook.NameSpace? session = null;
        MSOutlook.MAPIFolder? folder = null;
        try
        {
            session = _application.Session;
            folder = session.PickFolder();
            return folder == null ? null : FolderRefOf(folder);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "PickFolder failed");
            return null;
        }
        finally
        {
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (session != null) Marshal.ReleaseComObject(session);
        }
    }

    /// <summary>Folder that contains the email; null when the item is gone.</summary>
    public MailFolderRef? GetFolderOf(string entryId, string? storeId)
    {
        if (string.IsNullOrEmpty(entryId)) return null;
        EnsureUiThread();
        return WithMailById(entryId, storeId, mail =>
        {
            object? parent = null;
            try
            {
                parent = mail.Parent;
                return parent is MSOutlook.MAPIFolder folder ? FolderRefOf(folder) : null!;
            }
            finally
            {
                if (parent != null) Marshal.ReleaseComObject(parent);
            }
        });
    }

    /// <summary>Plain-text body of one email, whole; null when the item is gone.</summary>
    public string? GetEmailBody(string entryId, string? storeId)
    {
        EnsureUiThread();
        return WithMailById(entryId, storeId, SafeGetBody);
    }

    public string GetEmailMessageId(string entryId, string storeId)
    {
        EnsureUiThread();
        return WithMailById(entryId, storeId, mail =>
        {
            MSOutlook.PropertyAccessor? accessor = null;
            try { accessor = mail.PropertyAccessor; return accessor.GetProperty(DaslQueryBuilder.PropInternetMessageId) as string ?? string.Empty; }
            catch { return string.Empty; }
            finally { if (accessor != null) Marshal.ReleaseComObject(accessor); }
        }) ?? string.Empty;
    }

    /// <summary>Open tasks (title + due date) of the default Tasks folder, to avoid proposing them again.</summary>
    public IReadOnlyList<ExtractedActionItem> GetOpenTasks(int max)
    {
        EnsureUiThread();
        var result = new List<ExtractedActionItem>();
        MSOutlook.NameSpace? session = null;
        MSOutlook.MAPIFolder? folder = null;
        MSOutlook.Table? table = null;
        MSOutlook.Columns? columns = null;
        try
        {
            session = _application.Session;
            folder = session.GetDefaultFolder(MSOutlook.OlDefaultFolders.olFolderTasks);
            table = folder.GetTable("[Complete] = False", MSOutlook.OlTableContents.olUserItems);
            columns = table.Columns;
            Marshal.ReleaseComObject(columns.Add("DueDate"));
            while (result.Count < max && !table.EndOfTable)
            {
                MSOutlook.Row? row = null;
                try
                {
                    row = table.GetNextRow();
                    // Outlook stores "no date" as 1/1/4501.
                    DateTime? due = row["DueDate"] is DateTime d && d.Year < 4000 ? d.Date : null;
                    result.Add(new ExtractedActionItem { Title = row["Subject"] as string ?? string.Empty, Due = due });
                }
                finally
                {
                    if (row != null) Marshal.ReleaseComObject(row);
                }
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read open tasks");
        }
        finally
        {
            if (columns != null) Marshal.ReleaseComObject(columns);
            if (table != null) Marshal.ReleaseComObject(table);
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (session != null) Marshal.ReleaseComObject(session);
        }
        return result;
    }

    /// <summary>Headers, HTML, attachment names and own domains for the local phishing check.</summary>
    /// <param name="entryId">Bound email (Rigenera); null = current selection.</param>
    public EmailSecurityData? GetEmailSecurityData(object? contextWindow, string? entryId = null, string? storeId = null)
    {
        EnsureUiThread();
        return string.IsNullOrEmpty(entryId)
            ? WithSelectedMail(ResolveWindow(contextWindow), ExtractSecurityData)
            : WithMailById(entryId!, storeId, ExtractSecurityData);
    }

    private EmailSecurityData ExtractSecurityData(MSOutlook.MailItem mail)
    {
        var info = ExtractHeaderInfo(mail);
        info.Body = SafeGetBody(mail);
        info.StoreId = SafeGetStoreId(mail);

        var readLimitations = new List<string>();
        string html;
        try
        {
            html = mail.HTMLBody ?? string.Empty;
        }
        catch
        {
            html = string.Empty;
            readLimitations.Add("HTML non disponibile: controllati solo i link nel corpo di testo leggibile.");
        }

        var scanDirectory = NewScanDirectory();
        var saved = new List<KeyValuePair<AttachmentFacts, string>>();
        int priorLimitations = readLimitations.Count;
        // Track this read separately from unrelated HTML/body limitations.
        var attachments = ReadAttachments(mail, scanDirectory, saved, _ => true, readLimitations, maxCopies: 0);
        var input = new EmailSecurityInput
        {
            Subject = info.Subject,
            FromName = info.SenderName,
            FromAddress = info.SenderEmail,
            TransportHeaders = GetStringProperty(mail, PropTransportHeaders),
            HtmlBody = html,
            Body = info.Body,
            // Names and sizes only: the files are copied by CopyAttachmentsAsync, a couple per call.
            Attachments = attachments,
            AttachmentListComplete = readLimitations.Count == priorLimitations,
            HasAttachmentFlag = HasAttachFlag(mail),
            OwnDomains = GetOwnDomains(),
            OwnAddresses = GetOwnAddresses().Where(a => a.Contains("@")).ToList(),
            OwnNames = GetOwnNames()
        };
        input.ReadLimitations.AddRange(readLimitations);
        if (string.IsNullOrWhiteSpace(info.Body))
            input.ReadLimitations.Add("Corpo di testo vuoto o non disponibile: analisi IA limitata agli altri dati leggibili.");
        return new EmailSecurityData(info, input, scanDirectory, saved);
    }

    private static bool HasAttachFlag(MSOutlook.MailItem mail)
    {
        MSOutlook.PropertyAccessor? accessor = null;
        try
        {
            accessor = mail.PropertyAccessor;
            return accessor.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x0E1B000B") is true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (accessor != null) Marshal.ReleaseComObject(accessor);
        }
    }

    private static string GetStringProperty(MSOutlook.MailItem mail, string dasl)
    {
        MSOutlook.PropertyAccessor? accessor = null;
        try
        {
            accessor = mail.PropertyAccessor;
            return accessor.GetProperty(dasl) as string ?? string.Empty;
        }
        catch
        {
            // Property absent (internal / locally created item)
            return string.Empty;
        }
        finally
        {
            if (accessor != null) Marshal.ReleaseComObject(accessor);
        }
    }

    /// <summary>
    /// Copies the attachments accepted by <paramref name="shouldSave"/> (e.g. extractable formats) to a temp folder.
    /// With <paramref name="maxCopies"/> it stops after that many copies and NextIndex says where to resume, so a long
    /// list never holds the UI thread in one call.
    /// </summary>
    public AttachmentCopies? CopyAttachments(string entryId, string? storeId, Func<AttachmentFacts, bool> shouldSave, int fromIndex = 1, int maxCopies = int.MaxValue) =>
        CopyAttachmentsInto(NewScanDirectory(), entryId, storeId, shouldSave, fromIndex, maxCopies);

    private AttachmentCopies? CopyAttachmentsInto(string directory, string entryId, string? storeId, Func<AttachmentFacts, bool> shouldSave, int fromIndex, int maxCopies)
    {
        if (string.IsNullOrEmpty(entryId)) return null;
        EnsureUiThread();
        return WithMailById(entryId, storeId, mail =>
        {
            var saved = new List<KeyValuePair<AttachmentFacts, string>>();
            int next = 0;
            var all = ReadAttachments(mail, directory, saved, shouldSave, null, fromIndex, maxCopies, i => next = i);
            return new AttachmentCopies(directory, all, saved) { NextIndex = next };
        });
    }

    // Copies per Outlook call in CopyAttachmentsAsync: a copy can be 30 MB, and Outlook disables an add-in that holds its UI thread.
    private const int CopiesPerSlice = 2;

    /// <summary>Every accepted attachment copied into one folder, a couple per Outlook call (<see cref="AttachmentCopies.CollectAsync"/>).</summary>
    public Task<AttachmentCopies?> CopyAttachmentsAsync(string entryId, string? storeId, Func<AttachmentFacts, bool> shouldSave, CancellationToken ct = default) =>
        AttachmentCopies.CollectAsync(
            () => CopyAttachments(entryId, storeId, shouldSave, 1, CopiesPerSlice),
            (directory, next) => CopyAttachmentsInto(directory, entryId, storeId, shouldSave, next, CopiesPerSlice), ct);

    private static string ScanRoot => OnlyWorkspace.Core.AppPaths.ScanDirectory;

    private static string NewScanDirectory() => Path.Combine(ScanRoot, Guid.NewGuid().ToString("N"));

    /// <summary>Removes attachment copies left by a crash during a scan/extraction (older than one hour).</summary>
    public static void CleanupStaleScanDirectories()
    {
        try
        {
            if (!Directory.Exists(ScanRoot)) return;
            foreach (var dir in Directory.GetDirectories(ScanRoot))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddHours(-1)) Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Debug(ex, "Stale scan folder not removed");
                }
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Scan folder cleanup failed");
        }
    }

    /// <summary>
    /// Metadata of every attachment; real files (by value / embedded items, within size and count limits)
    /// are also copied to <paramref name="scanDirectory"/> for the antivirus scan and signature check.
    /// </summary>
    private static IReadOnlyList<AttachmentFacts> ReadAttachments(MSOutlook.MailItem mail, string scanDirectory, List<KeyValuePair<AttachmentFacts, string>> saved, Func<AttachmentFacts, bool> shouldSave,
        List<string>? readLimitations = null, int fromIndex = 1, int maxCopies = int.MaxValue, Action<int>? stoppedAt = null)
    {
        var result = new List<AttachmentFacts>();
        int copies = 0;
        bool stopped = false;
        MSOutlook.Attachments? attachments = null;
        try
        {
            attachments = mail.Attachments;
            for (int i = 1; i <= attachments.Count; i++)
            {
                MSOutlook.Attachment? attachment = null;
                AttachmentFacts? facts = null;
                try
                {
                    attachment = attachments[i];
                    // Embedded items have no FileName.
                    string? name;
                    try { name = attachment.FileName; } catch { name = attachment.DisplayName; }
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        readLimitations?.Add("Allegato " + i + ": nome non disponibile, controlli non eseguiti.");
                        continue;
                    }

                    facts = new AttachmentFacts(name!, attachment.Size) { IsInline = IsInlineImage(attachment, name!) };
                    result.Add(facts);

                    var type = attachment.Type;
                    bool savable = type == MSOutlook.OlAttachmentType.olByValue || type == MSOutlook.OlAttachmentType.olEmbeddeditem;
                    if (!savable || facts.Size > AttachmentFacts.MaxCopyBytes || !shouldSave(facts))
                    {
                        facts.ScanDetail = !savable ? "tipo di allegato non copiabile" : facts.Size > AttachmentFacts.MaxCopyBytes ? "oltre il limite di 30 MB" : "contenuto non richiesto";
                        continue;
                    }
                    // Copied in an earlier slice, or left to the next one.
                    if (i < fromIndex || stopped) continue;
                    if (copies >= maxCopies)
                    {
                        stopped = true;
                        stoppedAt?.Invoke(i);
                        continue;
                    }
                    copies++;

                    Directory.CreateDirectory(scanDirectory);
                    var path = Path.Combine(scanDirectory, i + "_" + AttachmentFacts.SafeFileName(name!));
                    attachment.SaveAsFile(path);
                    facts.DetectedType = ReadSignature(path);
                    if (File.Exists(path))
                    {
                        saved.Add(new KeyValuePair<AttachmentFacts, string>(facts, path));
                    }
                    else
                    {
                        facts.ScanStatus = AttachmentScanStatus.Removed;
                    }
                }
                catch (Exception ex)
                {
                    if (facts != null)
                    {
                        facts.ScanStatus = AttachmentScanStatus.Failed;
                        facts.ScanDetail = "lettura o copia locale non riuscita";
                    }
                    else readLimitations?.Add("Allegato " + i + ": metadati non leggibili, controlli non eseguiti.");
                    OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read attachment {Index}", i);
                }
                finally
                {
                    if (attachment != null) Marshal.ReleaseComObject(attachment);
                }
            }
        }
        catch (Exception ex)
        {
            readLimitations?.Add("Elenco degli allegati non letto integralmente: controlli limitati agli elementi disponibili.");
            OnlyWorkspaceLog.Logger.Debug(ex, "Failed to enumerate attachments");
        }
        finally
        {
            if (attachments != null) Marshal.ReleaseComObject(attachments);
        }
        return result;
    }

    private const string PropAttachContentId = "http://schemas.microsoft.com/mapi/proptag/0x3712001F";

    /// <summary>Image referenced by the HTML body (logo, signature): it has a Content-ID.</summary>
    private static bool IsInlineImage(MSOutlook.Attachment attachment, string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (ext is not ("png" or "jpg" or "jpeg" or "gif" or "bmp" or "svg" or "emz" or "wmz")) return false;

        MSOutlook.PropertyAccessor? accessor = null;
        try
        {
            accessor = attachment.PropertyAccessor;
            return !string.IsNullOrEmpty(accessor.GetProperty(PropAttachContentId) as string);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (accessor != null) Marshal.ReleaseComObject(accessor);
        }
    }

    private static string? ReadSignature(string path)
    {
        try
        {
            var header = new byte[FileSignature.HeaderLength];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int read = stream.Read(header, 0, header.Length);
            return FileSignature.Detect(header, read);
        }
        catch
        {
            // Deleted or locked by real-time protection
            return null;
        }
    }

    /// <summary>Company domains of the user's accounts and of the PSTs' own addresses (mailbox providers excluded).</summary>
    public IReadOnlyList<string> GetOwnDomainList()
    {
        EnsureUiThread();
        return GetOwnDomains();
    }

    /// <summary>
    /// SMTP domains of the profile's accounts (cached: accounts don't change while Outlook runs) and of the PSTs' own
    /// addresses, mailbox providers excepted.
    /// </summary>
    private IReadOnlyList<string> GetOwnDomains()
    {
        if (_ownDomains != null) return _ownDomains;

        var domains = new List<string>();
        MSOutlook.NameSpace? session = null;
        MSOutlook.Accounts? accounts = null;
        try
        {
            session = _application.Session;
            accounts = session.Accounts;
            for (int i = 1; i <= accounts.Count; i++)
            {
                MSOutlook.Account? account = null;
                try
                {
                    account = accounts[i];
                    var smtp = account.SmtpAddress ?? string.Empty;
                    int at = smtp.LastIndexOf('@');
                    if (at > 0)
                    {
                        var domain = smtp.Substring(at + 1).ToLowerInvariant();
                        if (!domains.Contains(domain)) domains.Add(domain);
                    }
                }
                catch
                {
                    // Account without SMTP address (e.g. some IMAP/EAS setups)
                }
                finally
                {
                    if (account != null) Marshal.ReleaseComObject(account);
                }
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read account domains");
        }
        finally
        {
            if (accounts != null) Marshal.ReleaseComObject(accounts);
            if (session != null) Marshal.ReleaseComObject(session);
        }
        foreach (var address in _storeOwnAddresses)
        {
            var domain = address.Substring(address.LastIndexOf('@') + 1).ToLowerInvariant();
            if (domain.Length > 0 && !OnlyWorkspace.Core.Archive.CounterpartyResolver.IsMailboxProvider(domain) && !domains.Contains(domain)) domains.Add(domain);
        }
        return _ownDomains = domains;
    }

    /// <param name="draftMarkdown">Model output (markdown), converted to the reply's body format.</param>
    /// <param name="entryId">Email the draft was generated for; null = current selection.</param>
    /// <returns>True when inserted at the cursor of a message being written (no reply opened).</returns>
    public bool InsertReplyDraft(string draftMarkdown, object? contextWindow = null, bool replyAll = false, string? entryId = null, string? storeId = null)
    {
        if (string.IsNullOrWhiteSpace(draftMarkdown)) return false;
        EnsureUiThread();

        var window = ResolveWindow(contextWindow);

        // Open compose window: the user is writing, insert at the cursor.
        if (window is MSOutlook.Inspector inspector && TryInsertIntoCompose(inspector, draftMarkdown)) return true;

        // Inline reply/draft being edited in the reading pane: same rule (and Reply() fails on such an item).
        if (window is MSOutlook.Explorer explorer && TryInsertIntoInlineResponse(explorer, draftMarkdown)) return true;

        if (!string.IsNullOrEmpty(entryId))
        {
            if (WithMailById(entryId!, storeId, mail => OpenReply(mail, draftMarkdown, replyAll)) == null)
                throw new InvalidOperationException("L'email originale non è più disponibile (spostata o eliminata).");
            return false;
        }

        if (WithSelectedMail(window, mail => OpenReply(mail, draftMarkdown, replyAll)) == null)
            throw new InvalidOperationException("Seleziona un messaggio email per inserire la bozza di risposta.");
        return false;
    }

    private static bool TryInsertIntoCompose(MSOutlook.Inspector inspector, string draftMarkdown)
    {
        object? currentItem = null;
        try
        {
            currentItem = inspector.CurrentItem;
            if (currentItem is not MSOutlook.MailItem mail || mail.Sent) return false;

            if (inspector.EditorType == MSOutlook.OlEditorType.olEditorWord && inspector.WordEditor is MSWord.Document wordDoc)
            {
                MSWord.Application? wordApp = null;
                MSWord.Selection? wordSel = null;
                try
                {
                    wordApp = wordDoc.Application;
                    wordSel = wordApp?.Selection;
                    if (wordSel != null)
                    {
                        wordSel.TypeText(DraftFormatter.ToPlainText(draftMarkdown));
                        return true;
                    }
                }
                finally
                {
                    if (wordSel != null) Marshal.ReleaseComObject(wordSel);
                    if (wordApp != null) Marshal.ReleaseComObject(wordApp);
                    Marshal.ReleaseComObject(wordDoc);
                }
            }

            PrependDraft(mail, draftMarkdown);
            return true;
        }
        finally
        {
            if (currentItem != null) Marshal.ReleaseComObject(currentItem);
        }
    }

    private static bool TryInsertIntoInlineResponse(MSOutlook.Explorer explorer, string draftMarkdown)
    {
        object? editor = null;
        MSWord.Application? wordApp = null;
        MSWord.Selection? wordSel = null;
        try
        {
            // Null when no inline response is active.
            editor = explorer.ActiveInlineResponseWordEditor;
            if (editor is not MSWord.Document wordDoc) return false;

            // Document.ActiveWindow is null for the inline editor: the selection comes from the application.
            wordApp = wordDoc.Application;
            wordSel = wordApp?.Selection;
            if (wordSel == null) return false;

            wordSel.TypeText(DraftFormatter.ToPlainText(draftMarkdown));
            return true;
        }
        catch (COMException ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Inline response editor not available");
            return false;
        }
        finally
        {
            if (wordSel != null) Marshal.ReleaseComObject(wordSel);
            if (wordApp != null) Marshal.ReleaseComObject(wordApp);
            if (editor != null) Marshal.ReleaseComObject(editor);
        }
    }

    // Non-null marker for WithSelectedMail/WithMailById callers that only need "found".
    private static readonly object Done = new();

    private static object OpenReply(MSOutlook.MailItem mail, string draftMarkdown, bool replyAll)
    {
        MSOutlook.MailItem? reply = null;
        MSOutlook.Inspector? replyInspector = null;
        try
        {
            reply = replyAll ? mail.ReplyAll() : mail.Reply();
            // Touching GetInspector makes Outlook add the default signature before we edit the body.
            replyInspector = reply.GetInspector;
            PrependDraft(reply, draftMarkdown);
            reply.Display(false);
            return Done;
        }
        finally
        {
            if (replyInspector != null) Marshal.ReleaseComObject(replyInspector);
            if (reply != null) Marshal.ReleaseComObject(reply);
        }
    }

    private static readonly Regex BodyTagRegex = new(@"<body[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Puts the draft on top keeping format, signature and quoted original (RTF becomes HTML).</summary>
    private static void PrependDraft(MSOutlook.MailItem item, string draftMarkdown)
    {
        if (item.BodyFormat == MSOutlook.OlBodyFormat.olFormatPlain)
        {
            item.Body = DraftFormatter.ToPlainText(draftMarkdown) + "\r\n\r\n" + item.Body;
            return;
        }

        var html = item.HTMLBody ?? string.Empty;
        var fragment = DraftFormatter.ToGeneratedHtml(draftMarkdown);
        var body = BodyTagRegex.Match(html);
        item.HTMLBody = body.Success ? html.Insert(body.Index + body.Length, fragment) : fragment + html;
    }

    private T? WithMailById<T>(string entryId, string? storeId, Func<MSOutlook.MailItem, T> action) where T : class
    {
        MSOutlook.NameSpace? session = null;
        object? item = null;
        try
        {
            session = _application.Session;
            if (session == null) return null;
            try
            {
                item = string.IsNullOrWhiteSpace(storeId) ? session.GetItemFromID(entryId) : session.GetItemFromID(entryId, storeId);
            }
            catch (COMException ex)
            {
                // Moved, deleted or store unavailable.
                OnlyWorkspaceLog.Logger.Warning(ex, "Failed to retrieve item by EntryId {EntryId}", entryId);
                return null;
            }
            return item is MSOutlook.MailItem mail ? action(mail) : null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            if (session != null) Marshal.ReleaseComObject(session);
        }
    }

    private static SelectedEmailInfo ExtractHeaderInfo(MSOutlook.MailItem mail)
    {
        var info = new SelectedEmailInfo
        {
            Subject = mail.Subject ?? string.Empty,
            SenderName = mail.SenderName ?? string.Empty,
            SenderEmail = mail.SenderEmailAddress ?? string.Empty,
            ReceivedTime = mail.ReceivedTime,
            EntryId = mail.EntryID ?? string.Empty,
            StoreId = SafeGetStoreId(mail)
        };

        try
        {
            // PR_CONVERSATION_INDEX: 22-byte header + 5 bytes per reply level (hex encoded).
            var convIndex = mail.ConversationIndex;
            if (!string.IsNullOrEmpty(convIndex) && convIndex.Length > 44)
            {
                info.ThreadPosition = 1 + (convIndex.Length - 44) / 10;
            }
        }
        catch
        {
            // Optional property
        }

        return info;
    }

    private SelectedEmailInfo ExtractEmailInfo(MSOutlook.MailItem mail)
    {
        var thread = StartThread(mail);
        if (thread.Read == null) return thread.Info;
        var messages = new List<SelectedEmailInfo>();
        foreach (var entryId in thread.Read) if (ReadThreadMessage(entryId, thread.Info.StoreId) is { } message) messages.Add(message);
        return FinishThread(thread, messages);
    }

    /// <summary>The selected email and its conversation, one conversation email per UI-thread slice: up to 20 emails opened in one call held Outlook long enough to be noticed.</summary>
    public async Task<SelectedEmailInfo?> GetCurrentSelectedEmailAsync(object? contextWindow = null, CancellationToken ct = default)
    {
        EnsureUiThread();
        var thread = WithSelectedMail(ResolveWindow(contextWindow), StartThread);
        return thread == null ? null : await ReadThreadAsync(thread, ct);
    }

    /// <summary><see cref="GetEmailByEntryId"/>, reading the conversation one email per UI-thread slice.</summary>
    public async Task<SelectedEmailInfo?> GetEmailByEntryIdAsync(string entryId, string? storeId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entryId)) return null;
        EnsureUiThread();
        var thread = WithMailById(entryId, storeId, StartThread);
        return thread == null ? null : await ReadThreadAsync(thread, ct);
    }

    private async Task<SelectedEmailInfo> ReadThreadAsync(ThreadRead thread, CancellationToken ct)
    {
        if (thread.Read == null) return thread.Info;
        var messages = new List<SelectedEmailInfo>();
        await UiSlice.ForEachAsync(thread.Read, "conversation read", entryId =>
        {
            if (ReadThreadMessage(entryId, thread.Info.StoreId) is { } message) messages.Add(message);
        }, ct);
        return FinishThread(thread, messages);
    }

    /// <summary>The email and its conversation's table; <see cref="Read"/> lists the conversation emails still to open (null: nothing left).</summary>
    private sealed class ThreadRead
    {
        public ThreadRead(SelectedEmailInfo info) => Info = info;
        public SelectedEmailInfo Info { get; }
        public List<string>? Read { get; set; }
        public int Total { get; set; }
    }

    private ThreadRead StartThread(MSOutlook.MailItem mail)
    {
        var info = ExtractHeaderInfo(mail);
        info.Body = SafeGetBody(mail);
        info.StoreId = SafeGetStoreId(mail);
        FillConversationKeys(mail, info);
        var thread = new ThreadRead(info);

        MSOutlook.Conversation? conv = null;
        MSOutlook.SimpleItems? rootItems = null;
        try
        {
            conv = mail.GetConversation();
            if (conv != null)
            {
                // The most recent emails with the conversation's size; the tree walk only if the table is not readable.
                var recent = RecentConversationIds(conv, out int total);
                if (recent != null)
                {
                    thread.Read = recent;
                    thread.Total = total;
                    return thread;
                }
                var threadList = new List<SelectedEmailInfo>();
                rootItems = conv.GetRootItems();
                if (rootItems != null) CollectConversation(conv, rootItems, threadList, depth: 0);
                SetThread(info, threadList, threadList.Count, limited: threadList.Count >= MaxThreadMessages);
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Conversation walk failed; using single message");
        }
        finally
        {
            if (rootItems != null) Marshal.ReleaseComObject(rootItems);
            if (conv != null) Marshal.ReleaseComObject(conv);
        }
        return thread;
    }

    private static SelectedEmailInfo FinishThread(ThreadRead thread, List<SelectedEmailInfo> messages)
    {
        SetThread(thread.Info, messages, thread.Total, limited: false);
        return thread.Info;
    }

    private static void SetThread(SelectedEmailInfo info, List<SelectedEmailInfo> threadList, int total, bool limited)
    {
        if (threadList.Count <= 1) return;
        threadList.Sort((a, b) => a.ReceivedTime.CompareTo(b.ReceivedTime));
        info.ThreadMessages = threadList;
        info.ConversationTotal = Math.Max(total, threadList.Count);
        info.ConversationLimited = total > threadList.Count || limited;
    }

    private SelectedEmailInfo? ReadThreadMessage(string entryId, string storeId)
    {
        MSOutlook.NameSpace? session = null;
        object? item = null;
        try
        {
            session = _application.Session;
            item = storeId.Length > 0 ? session.GetItemFromID(entryId, storeId) : session.GetItemFromID(entryId);
            return item is MSOutlook.MailItem threadMail ? ThreadMessageInfo(threadMail) : null;
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Conversation email not opened");
            return null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            if (session != null) Marshal.ReleaseComObject(session);
        }
    }

    private static string SafeGetStoreId(MSOutlook.MailItem mail)
    {
        object? parent = null;
        try
        {
            parent = mail.Parent;
            return parent is MSOutlook.MAPIFolder folder ? folder.StoreID ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (parent != null) Marshal.ReleaseComObject(parent);
        }
    }

    /// <summary>
    /// EntryIDs of the conversation's most recent emails (at most <see cref="MaxThreadMessages"/>) and how many it has, from
    /// Conversation.GetTable: rows cost no item opening. Null when the table fails.
    /// </summary>
    private static List<string>? RecentConversationIds(MSOutlook.Conversation conv, out int total)
    {
        total = 0;
        MSOutlook.Table? table = null;
        MSOutlook.Columns? columns = null;
        try
        {
            table = conv.GetTable();
            columns = table.Columns;
            Marshal.ReleaseComObject(columns.Add("ReceivedTime"));
            var rows = new List<(string EntryId, DateTime Received)>();
            while (!table.EndOfTable)
            {
                var row = table.GetNextRow();
                try
                {
                    // Emails only, as the tree walk (MailItem): meeting items and reports stay out.
                    if ((row["MessageClass"] as string ?? string.Empty).StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase) &&
                        row["EntryID"] is string id && id.Length > 0)
                        rows.Add((id, row["ReceivedTime"] is DateTime received ? received : DateTime.MinValue));
                }
                finally { Marshal.ReleaseComObject(row); }
            }
            total = rows.Count;
            return rows.OrderByDescending(r => r.Received).Take(MaxThreadMessages).Select(r => r.EntryId).ToList();
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Conversation table not readable; walking the conversation tree");
            return null;
        }
        finally
        {
            if (columns != null) Marshal.ReleaseComObject(columns);
            if (table != null) Marshal.ReleaseComObject(table);
        }
    }

    private static SelectedEmailInfo ThreadMessageInfo(MSOutlook.MailItem threadMail)
    {
        var message = new SelectedEmailInfo
        {
            Subject = threadMail.Subject ?? string.Empty,
            Body = SafeGetBody(threadMail),
            SenderName = threadMail.SenderName ?? string.Empty,
            SenderEmail = threadMail.SenderEmailAddress ?? string.Empty,
            ReceivedTime = threadMail.ReceivedTime,
            EntryId = threadMail.EntryID ?? string.Empty,
            StoreId = SafeGetStoreId(threadMail)
        };
        FillConversationKeys(threadMail, message);
        return message;
    }

    private static void CollectConversation(MSOutlook.Conversation conv, MSOutlook.SimpleItems items, List<SelectedEmailInfo> acc, int depth)
    {
        int count = items.Count;
        for (int i = 1; i <= count && acc.Count < MaxThreadMessages; i++)
        {
            object? obj = null;
            MSOutlook.SimpleItems? children = null;
            try
            {
                obj = items[i];
                if (obj is MSOutlook.MailItem threadMail) acc.Add(ThreadMessageInfo(threadMail));

                if (obj != null && depth < MaxThreadMessages && acc.Count < MaxThreadMessages)
                {
                    children = conv.GetChildren(obj);
                    if (children != null && children.Count > 0)
                    {
                        CollectConversation(conv, children, acc, depth + 1);
                    }
                }
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read conversation item at index {Index}", i);
            }
            finally
            {
                if (children != null) Marshal.ReleaseComObject(children);
                if (obj != null) Marshal.ReleaseComObject(obj);
            }
        }
    }

    private static void FillConversationKeys(MSOutlook.MailItem mail, SelectedEmailInfo info)
    {
        MSOutlook.PropertyAccessor? accessor = null;
        try
        {
            accessor = mail.PropertyAccessor;
            try { info.MessageId = accessor.GetProperty(DaslQueryBuilder.PropInternetMessageId) as string ?? string.Empty; } catch { }
            try { info.References = accessor.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x1039001F") as string ?? string.Empty; } catch { }
            try { var sent = mail.SentOn; if (sent.Year >= 1980 && sent.Year < 4500) info.SentUtc = sent.ToUniversalTime(); } catch { }
            try { info.InReplyTo = accessor.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x1042001F") as string ?? string.Empty; } catch { }
            try
            {
                if (accessor.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x30130102") is byte[] bytes)
                    info.ConversationId = BitConverter.ToString(bytes).Replace("-", string.Empty);
            }
            catch { }
        }
        catch { }
        finally { if (accessor != null) Marshal.ReleaseComObject(accessor); }
    }

    private static string SafeGetBody(MSOutlook.MailItem mail)
    {
        try
        {
            return mail.Body ?? string.Empty;
        }
        catch
        {
            // Protected / encrypted item
            return string.Empty;
        }
    }

    private readonly StoreListCache _storeList = new(TimeSpan.FromSeconds(30));
    private MSOutlook.Stores? _storeEvents;

    /// <summary>Open stores, from <see cref="StoreListCache"/>.</summary>
    public IReadOnlyList<OutlookStoreInfo> EnumerateStores()
    {
        EnsureUiThread();
        return _storeList.Get(() =>
        {
            WatchStores();
            return ReadStores();
        });
    }

    private void WatchStores()
    {
        if (_storeEvents != null) return;
        MSOutlook.NameSpace? session = null;
        try
        {
            session = _application.Session;
            // Kept for the add-in's lifetime: the events stop when the RCW is released.
            _storeEvents = session?.Stores;
            if (_storeEvents is MSOutlook.StoresEvents_12_Event events)
            {
                events.StoreAdd += _ => _storeList.Invalidate();
                events.BeforeStoreRemove += (MSOutlook.Store _, ref bool cancel) => _storeList.Invalidate();
            }
        }
        catch (Exception ex)
        {
            // Without events the list is still read again every 30 seconds.
            OnlyWorkspaceLog.Logger.Debug(ex, "Store events unavailable");
        }
        finally
        {
            if (session != null) Marshal.ReleaseComObject(session);
        }
    }

    private List<OutlookStoreInfo> ReadStores()
    {
        var result = new List<OutlookStoreInfo>();
        MSOutlook.NameSpace? session = null;
        MSOutlook.Stores? stores = null;

        try
        {
            session = _application.Session;
            if (session == null) return result;

            stores = session.Stores;
            if (stores == null) return result;

            var delivery = AccountDeliveryStores(session);
            int count = stores.Count;
            for (int i = 1; i <= count; i++)
            {
                MSOutlook.Store? store = null;
                try
                {
                    store = stores[i];
                    if (store == null) continue;

                    var id = store.StoreID ?? string.Empty;
                    bool dataFile = false;
                    try { dataFile = store.IsDataFileStore; }
                    catch { } // store that does not say (public folders)
                    result.Add(new OutlookStoreInfo
                    {
                        StoreId = id,
                        DisplayName = store.DisplayName ?? string.Empty,
                        FilePath = store.FilePath,
                        IsOpen = store.IsOpen,
                        ExchangeStoreType = store.ExchangeStoreType.ToString(),
                        IsDataFileStore = dataFile,
                        HasAccount = delivery.Contains(id)
                    });
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Warning(ex, "Failed to read store at index {Index}", i);
                }
                finally
                {
                    if (store != null) Marshal.ReleaseComObject(store);
                }
            }
        }
        finally
        {
            if (stores != null) Marshal.ReleaseComObject(stores);
            if (session != null) Marshal.ReleaseComObject(session);
        }

        return result;
    }

    /// <summary>StoreIDs the profile's accounts deliver to.</summary>
    private static HashSet<string> AccountDeliveryStores(MSOutlook.NameSpace session)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MSOutlook.Accounts? accounts = null;
        try
        {
            accounts = session.Accounts;
            for (int i = 1; i <= accounts.Count; i++)
            {
                MSOutlook.Account? account = null;
                MSOutlook.Store? store = null;
                try
                {
                    account = accounts[i];
                    store = account.DeliveryStore;
                    if (!string.IsNullOrEmpty(store?.StoreID)) result.Add(store!.StoreID);
                }
                catch { } // DeliveryStore throws for some account types
                finally
                {
                    if (store != null) Marshal.ReleaseComObject(store);
                    if (account != null) Marshal.ReleaseComObject(account);
                }
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read the accounts' delivery stores");
        }
        finally
        {
            if (accounts != null) Marshal.ReleaseComObject(accounts);
        }
        return result;
    }

    public IReadOnlyList<AppointmentInfo> GetAppointments(CalendarQuery query)
    {
        if (query == null) throw new ArgumentNullException(nameof(query));
        EnsureUiThread();

        var result = new List<AppointmentInfo>();
        int maxResults = Math.Max(1, Math.Min(query.MaxResults, 200));
        string filter = CalendarQueryBuilder.BuildDateRangeFilter(query.StartDate, query.EndDate);

        MSOutlook.NameSpace? session = null;
        MSOutlook.Stores? stores = null;

        try
        {
            session = _application.Session;
            if (session == null) return result;

            stores = session.Stores;
            if (stores == null) return result;

            int storeCount = stores.Count;
            for (int s = 1; s <= storeCount && result.Count < maxResults; s++)
            {
                MSOutlook.Store? store = null;
                try
                {
                    store = stores[s];
                    if (store == null || !store.IsOpen) continue;

                    if (!string.IsNullOrEmpty(query.StoreId) &&
                        !string.Equals(store.StoreID, query.StoreId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string storeDisplayName = store.DisplayName ?? string.Empty;
                    FetchAppointmentsFromStore(store, filter, storeDisplayName, maxResults, result);
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Warning(ex, "Failed to query appointments from store at index {Index}", s);
                }
                finally
                {
                    if (store != null) Marshal.ReleaseComObject(store);
                }
            }
        }
        finally
        {
            if (stores != null) Marshal.ReleaseComObject(stores);
            if (session != null) Marshal.ReleaseComObject(session);
        }

        return result;
    }

    // Safety cap on occurrences read per calendar.
    private const int MaxCalendarScan = 500;

    private static void FetchAppointmentsFromStore(
        MSOutlook.Store store,
        string filter,
        string storeDisplayName,
        int maxResults,
        List<AppointmentInfo> appointments)
    {
        MSOutlook.MAPIFolder? folder = null;
        MSOutlook.Items? items = null;
        MSOutlook.Items? restrictedItems = null;

        try
        {
            try
            {
                folder = store.GetDefaultFolder(MSOutlook.OlDefaultFolders.olFolderCalendar);
            }
            catch
            {
                return;
            }

            if (folder == null) return;

            items = folder.Items;
            if (items == null) return;

            items.IncludeRecurrences = true;
            items.Sort("[Start]", false);

            restrictedItems = items.Restrict(filter);

            // With IncludeRecurrences Count is undefined (Int32.MaxValue on endless series) and indexing
            // can loop forever: Microsoft documents enumeration (GetFirst/GetNext) as the only safe way.
            object? item = restrictedItems.GetFirst();
            for (int i = 1; item != null && appointments.Count < maxResults && i <= MaxCalendarScan; i++)
            {
                try
                {
                    if (item is MSOutlook.AppointmentItem appt)
                    {
                        appointments.Add(new AppointmentInfo
                        {
                            Subject = appt.Subject ?? string.Empty,
                            Start = appt.Start,
                            End = appt.End,
                            Location = appt.Location ?? string.Empty,
                            Organizer = appt.Organizer ?? string.Empty,
                            IsAllDayEvent = appt.AllDayEvent,
                            IsRecurring = appt.IsRecurring,
                            Body = appt.Body ?? string.Empty,
                            EntryId = appt.EntryID ?? string.Empty,
                            StoreDisplayName = storeDisplayName
                        });
                    }
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Warning(ex, "Failed to read appointment item at index {Index}", i);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
                item = restrictedItems.GetNext();
            }
            if (item != null) Marshal.ReleaseComObject(item);
        }
        finally
        {
            if (restrictedItems != null) Marshal.ReleaseComObject(restrictedItems);
            if (items != null) Marshal.ReleaseComObject(items);
            if (folder != null) Marshal.ReleaseComObject(folder);
        }
    }

    public IReadOnlyList<ContactInfo> GetContacts(ContactQuery query)
    {
        if (query == null) throw new ArgumentNullException(nameof(query));
        EnsureUiThread();

        var result = new List<ContactInfo>();
        int maxResults = Math.Max(1, Math.Min(query.MaxResults, 200));
        string? term = query.SearchTerm?.Trim();

        MSOutlook.NameSpace? session = null;
        MSOutlook.Stores? stores = null;

        try
        {
            session = _application.Session;
            if (session == null) return result;

            stores = session.Stores;
            if (stores == null) return result;

            int storeCount = stores.Count;
            for (int s = 1; s <= storeCount && result.Count < maxResults; s++)
            {
                MSOutlook.Store? store = null;
                try
                {
                    store = stores[s];
                    if (store == null || !store.IsOpen) continue;

                    if (!string.IsNullOrEmpty(query.StoreId) &&
                        !string.Equals(store.StoreID, query.StoreId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string storeDisplayName = store.DisplayName ?? string.Empty;
                    FetchContactsFromStore(store, term, storeDisplayName, maxResults, result);
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Warning(ex, "Failed to query contacts from store at index {Index}", s);
                }
                finally
                {
                    if (store != null) Marshal.ReleaseComObject(store);
                }
            }
        }
        finally
        {
            if (stores != null) Marshal.ReleaseComObject(stores);
            if (session != null) Marshal.ReleaseComObject(session);
        }

        return result;
    }

    private static void FetchContactsFromStore(
        MSOutlook.Store store,
        string? term,
        string storeDisplayName,
        int maxResults,
        List<ContactInfo> contacts)
    {
        MSOutlook.MAPIFolder? folder = null;
        MSOutlook.Items? items = null;
        MSOutlook.Items? targetItems = null;
        string? localTerm = null;

        try
        {
            try
            {
                folder = store.GetDefaultFolder(MSOutlook.OlDefaultFolders.olFolderContacts);
            }
            catch
            {
                return;
            }

            if (folder == null) return;

            items = folder.Items;
            if (items == null) return;

            if (!string.IsNullOrWhiteSpace(term))
            {
                var termSafe = DaslQueryBuilder.EscapeString(term!);
                string daslFilter = $"@SQL=(\"urn:schemas:contacts:cn\" LIKE '%{termSafe}%' OR " +
                                    $"\"urn:schemas:contacts:email1\" LIKE '%{termSafe}%' OR " +
                                    $"\"urn:schemas:contacts:o\" LIKE '%{termSafe}%')";
                try
                {
                    targetItems = items.Restrict(daslFilter);
                }
                catch (Exception ex)
                {
                    // Store without the contacts schema: match here, never return unfiltered contacts as hits.
                    OnlyWorkspaceLog.Logger.Warning(ex, "Contact filter rejected by store; matching locally");
                    targetItems = items;
                    localTerm = term!;
                }
            }
            else
            {
                targetItems = items;
            }

            int count = targetItems.Count;
            for (int i = 1; i <= count && contacts.Count < maxResults; i++)
            {
                object? item = null;
                try
                {
                    item = targetItems[i];
                    if (item is MSOutlook.ContactItem contact && (localTerm == null || ContactMatches(localTerm, contact.FullName, contact.Email1Address, contact.CompanyName)))
                    {
                        contacts.Add(new ContactInfo
                        {
                            FullName = contact.FullName ?? string.Empty,
                            Email = contact.Email1Address ?? string.Empty,
                            MobilePhone = contact.MobileTelephoneNumber ?? string.Empty,
                            BusinessPhone = contact.BusinessTelephoneNumber ?? string.Empty,
                            CompanyName = contact.CompanyName ?? string.Empty,
                            JobTitle = contact.JobTitle ?? string.Empty,
                            EntryId = contact.EntryID ?? string.Empty,
                            StoreDisplayName = storeDisplayName
                        });
                    }
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Warning(ex, "Failed to read contact item at index {Index}", i);
                }
                finally
                {
                    if (item != null) Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            if (targetItems != null && !ReferenceEquals(targetItems, items))
            {
                Marshal.ReleaseComObject(targetItems);
            }
            if (items != null) Marshal.ReleaseComObject(items);
            if (folder != null) Marshal.ReleaseComObject(folder);
        }
    }

    private static bool ContactMatches(string term, params string?[] fields) =>
        fields.Any(f => f != null && f.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>Opens the email in its own window; false when it is no longer there.</summary>
    public bool DisplayEmail(string entryId, string? storeId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return false;
        EnsureUiThread();
        return WithMailById(entryId, storeId, mail =>
        {
            mail.Display(false);
            return Done;
        }) != null;
    }

    public SelectedEmailInfo? GetEmailByEntryId(string entryId, string? storeId = null)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return null;
        EnsureUiThread();
        return WithMailById(entryId, storeId, ExtractEmailInfo);
    }

    public string GetOutlookVersion()
    {
        try
        {
            return _application.Version ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    public string GetOutlookBitness()
    {
        return Environment.Is64BitProcess ? "x64" : "x86";
    }
}
