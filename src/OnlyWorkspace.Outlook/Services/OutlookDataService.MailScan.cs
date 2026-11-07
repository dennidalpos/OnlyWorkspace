using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Utils;
using OnlyWorkspace.Infrastructure.Logging;
using MSOutlook = Microsoft.Office.Interop.Outlook;

namespace OnlyWorkspace.Outlook.Services;

public sealed partial class OutlookDataService
{
    /// <summary>Cursor owned by the Outlook UI thread. A caller reads a small row batch, yields, then resumes.</summary>
    public sealed class MailHeaderScan : IMailHeaderScan
    {
        private readonly OutlookDataService _owner;
        private readonly Queue<(MailFolderRef Folder, int Depth)> _pending = new();
        private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenMail = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _dasl;
        private readonly EmailRowFilter? _rows;
        private MSOutlook.NameSpace? _session;
        private MSOutlook.MAPIFolder? _folder;
        private MSOutlook.Table? _table;
        private MSOutlook.Columns? _columns;
        private int _depth;
        private string _storeId = string.Empty;
        private string _folderName = string.Empty;
        private bool _hasMessageId;
        private bool _hasSmtp;
        private bool _hasTo;
        private bool _disposed;

        internal MailHeaderScan(OutlookDataService owner, IReadOnlyList<MailFolderRef> folders, bool subfolders, EmailSearchQuery query)
        {
            _owner = owner;
            _dasl = DaslQueryBuilder.BuildEmailSearchFilter(query);
            _rows = EmailRowFilter.From(query);
            if (subfolders)
                foreach (var item in owner.DefaultFoldersOfAllStores(MSOutlook.OlDefaultFolders.olFolderDeletedItems,
                             MSOutlook.OlDefaultFolders.olFolderJunk, MSOutlook.OlDefaultFolders.olFolderDrafts,
                             MSOutlook.OlDefaultFolders.olFolderOutbox)) _skipped.Add(item.EntryId);
            foreach (var item in folders)
            {
                _skipped.Remove(item.EntryId);
                _pending.Enqueue((item, subfolders ? int.MaxValue : 0));
            }
            _session = owner._application.Session;
        }

        public bool Complete => _table == null && _pending.Count == 0;
        public int RowsScanned { get; private set; }
        public int FoldersFailed { get; private set; }

        public IReadOnlyList<EmailSearchResult> ReadNext(int rowsPerBatch)
        {
            _owner.EnsureUiThread();
            if (_disposed) throw new ObjectDisposedException(nameof(MailHeaderScan));
            var found = new List<EmailSearchResult>();
            int remaining = Math.Max(1, rowsPerBatch);
            int consecutiveReadFailures = 0;
            while (remaining > 0)
            {
                if (_table == null && !OpenNextFolder()) break;
                if (_table!.EndOfTable) { CloseFolder(); continue; }
                remaining--;
                RowsScanned++;
                MSOutlook.Row? row = null;
                try
                {
                    row = _table.GetNextRow();
                    consecutiveReadFailures = 0;
                    if (row["EntryID"] is not string entryId) continue;
                    if (!(row["MessageClass"] as string ?? string.Empty).StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase)) continue;
                    string subject = row["Subject"] as string ?? string.Empty;
                    string senderName = row["SenderName"] as string ?? string.Empty;
                    string senderEmail = row["SenderEmailAddress"] as string ?? string.Empty;
                    if (_rows != null && !(_rows.NeedsRecipients && !_hasTo) &&
                        !_rows.Matches(subject, _hasTo ? (row["To"] as string) + "; " + (row["CC"] as string) : null,
                            senderName, senderEmail, _hasSmtp ? row[DaslQueryBuilder.PropSenderSmtpAddress] as string : null)) continue;
                    string messageId = _hasMessageId ? row[DaslQueryBuilder.PropInternetMessageId] as string ?? string.Empty : string.Empty;
                    var received = row["ReceivedTime"] is DateTime date ? date : default;
                    string key = messageId.Length > 0 ? "MID:" + messageId.Trim() : "ENTRY:" + _storeId + "\n" + entryId;
                    if (!_seenMail.Add(key)) continue;
                    found.Add(new EmailSearchResult
                    {
                        Subject = subject, SenderName = senderName, SenderEmail = senderEmail, ReceivedTime = received,
                        InternetMessageId = messageId, EntryId = entryId, StoreId = _storeId, FolderName = _folderName
                    });
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Debug(ex, "Failed to read a mail search row");
                    if (row == null && ++consecutiveReadFailures >= 3)
                    {
                        FoldersFailed++;
                        CloseFolder();
                        consecutiveReadFailures = 0;
                    }
                }
                finally
                {
                    ComObjects.Release(row);
                }
            }
            return found;
        }

        private bool OpenNextFolder()
        {
            while (_pending.Count > 0)
            {
                var (reference, depth) = _pending.Dequeue();
                if (_skipped.Contains(reference.EntryId) || !_visited.Add(reference.StoreId + "\n" + reference.EntryId)) continue;
                try
                {
                    _folder = _session!.GetFolderFromID(reference.EntryId, string.IsNullOrEmpty(reference.StoreId) ? Type.Missing : reference.StoreId);
                    _depth = depth;
                    if (_folder.DefaultItemType != MSOutlook.OlItemType.olMailItem) { CloseFolder(); continue; }
                    _storeId = _folder.StoreID ?? string.Empty;
                    _folderName = _folder.Name ?? string.Empty;
                    _table = _folder.GetTable(_dasl.Length > 0 ? _dasl : Type.Missing, MSOutlook.OlTableContents.olUserItems);
                    _columns = _table.Columns;
                    foreach (var name in new[] { "SenderName", "SenderEmailAddress", "ReceivedTime" })
                        ComObjects.Release(_columns.Add(name));
                    _hasMessageId = TryColumn(DaslQueryBuilder.PropInternetMessageId);
                    _hasSmtp = _rows != null && TryColumn(DaslQueryBuilder.PropSenderSmtpAddress);
                    _hasTo = _rows?.NeedsRecipients == true && TryColumn("To") && TryColumn("CC");
                    _table.Sort("[ReceivedTime]", true);
                    return true;
                }
                catch (Exception ex)
                {
                    FoldersFailed++;
                    OnlyWorkspaceLog.Logger.Warning(ex, "Failed to scan a mail folder");
                    CloseFolder();
                }
            }
            return false;
        }

        private bool TryColumn(string name)
        {
            try { ComObjects.Release(_columns!.Add(name)); return true; }
            catch { return false; }
        }

        private void CloseFolder()
        {
            if (_columns != null) { ComObjects.Release(_columns); _columns = null; }
            if (_table != null) { ComObjects.Release(_table); _table = null; }
            if (_folder == null) return;
            if (_depth > 0)
            {
                MSOutlook.Folders? children = null;
                try
                {
                    children = _folder.Folders;
                    for (int i = 1; i <= children.Count; i++)
                    {
                        MSOutlook.MAPIFolder? child = null;
                        try
                        {
                            child = children[i];
                            _pending.Enqueue((new MailFolderRef { EntryId = child.EntryID ?? string.Empty,
                                StoreId = child.StoreID ?? string.Empty, Path = child.FolderPath ?? string.Empty }, _depth - 1));
                        }
                        catch (Exception ex) { FoldersFailed++; OnlyWorkspaceLog.Logger.Debug(ex, "Failed to list a child mail folder"); }
                        finally { ComObjects.Release(child); }
                    }
                }
                catch (Exception ex) { FoldersFailed++; OnlyWorkspaceLog.Logger.Warning(ex, "Failed to list child mail folders"); }
                finally { ComObjects.Release(children); }
            }
            ComObjects.Release(_folder);
            _folder = null;
        }

        public void Dispose()
        {
            _owner.EnsureUiThread();
            if (_disposed) return;
            _depth = 0;
            CloseFolder();
            if (_session != null) { ComObjects.Release(_session); _session = null; }
            _disposed = true;
        }
    }

    public IMailHeaderScan BeginMailHeaderScan(IReadOnlyList<MailFolderRef> folders, bool includeSubfolders, EmailSearchQuery query)
    {
        EnsureUiThread();
        return new MailHeaderScan(this, folders, includeSubfolders, query);
    }

    public IReadOnlyList<MailFolderRef> GetChatSearchFolders() =>
        DefaultFoldersOfAllStores(MSOutlook.OlDefaultFolders.olFolderInbox, MSOutlook.OlDefaultFolders.olFolderSentMail);

    public sealed class UnansweredSentScan : IUnansweredSentScan
    {
        private readonly OutlookDataService _owner;
        private readonly Queue<MailFolderRef> _folders;
        private readonly HashSet<string> _ownAddresses;
        private readonly EmailRowFilter? _rowFilter;
        private readonly string _dasl;
        private MSOutlook.NameSpace? _session;
        private MSOutlook.MAPIFolder? _folder;
        private MSOutlook.Items? _items;
        private MSOutlook.Items? _restricted;
        private int _position = 1;
        private int _count;
        private bool _disposed;

        internal UnansweredSentScan(OutlookDataService owner, int minDays, int lookbackDays,
            ICollection<string>? storeIds, EmailSearchQuery? filter)
        {
            _owner = owner;
            _folders = new Queue<MailFolderRef>(owner.StoreFolders(storeIds, root: false, MSOutlook.OlDefaultFolders.olFolderSentMail));
            _ownAddresses = owner.GetOwnAddresses();
            _rowFilter = filter == null ? null : EmailRowFilter.From(filter);
            var fromUtc = DateTime.Now.AddDays(-lookbackDays).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            var toUtc = DateTime.Now.AddDays(-minDays).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            _dasl = $"@SQL=\"{PropClientSubmitTime}\" >= '{fromUtc}' AND \"{PropClientSubmitTime}\" <= '{toUtc}'";
            var extra = filter == null ? string.Empty : DaslQueryBuilder.BuildEmailSearchFilter(filter);
            if (extra.Length > 0) _dasl += " AND " + extra.Substring("@SQL=".Length);
            _session = owner._application.Session;
        }

        public bool Complete => _restricted == null && _folders.Count == 0;
        public int Scanned { get; private set; }
        public int FoldersFailed { get; private set; }

        public IReadOnlyList<FollowUpCandidate> ReadNext(int rowsPerBatch)
        {
            _owner.EnsureUiThread();
            if (_disposed) throw new ObjectDisposedException(nameof(UnansweredSentScan));
            var found = new List<FollowUpCandidate>();
            int remaining = Math.Max(1, rowsPerBatch);
            while (remaining > 0)
            {
                if (_restricted == null && !OpenNext()) break;
                if (_position > _count) { CloseFolder(); continue; }
                remaining--;
                Scanned++;
                object? item = null;
                try
                {
                    item = _restricted![_position++];
                    if (item is not MSOutlook.MailItem mail ||
                        !(mail.MessageClass ?? string.Empty).StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase)) continue;
                    if (_rowFilter != null && !_rowFilter.Matches(mail.Subject, (mail.To ?? string.Empty) + "; " + (mail.CC ?? string.Empty))) continue;
                    var recipients = RecipientNames(mail, _ownAddresses);
                    if (recipients.Length == 0 || HasReplyFromOthers(mail, _ownAddresses) != false) continue;
                    found.Add(new FollowUpCandidate
                    {
                        EntryId = mail.EntryID ?? string.Empty, StoreId = _folder!.StoreID ?? string.Empty,
                        Subject = mail.Subject ?? string.Empty, Recipients = recipients, SentOn = mail.SentOn
                    });
                }
                catch (Exception ex) { OnlyWorkspaceLog.Logger.Debug(ex, "Failed to inspect a sent mail item"); }
                finally { ComObjects.Release(item); }
            }
            return found;
        }

        private bool OpenNext()
        {
            while (_folders.Count > 0)
            {
                var reference = _folders.Dequeue();
                try
                {
                    _folder = _session!.GetFolderFromID(reference.EntryId, reference.StoreId);
                    _items = _folder.Items;
                    _restricted = _items.Restrict(_dasl);
                    _restricted.Sort("[SentOn]", true);
                    _count = _restricted.Count;
                    _position = 1;
                    return true;
                }
                catch (Exception ex)
                {
                    FoldersFailed++;
                    OnlyWorkspaceLog.Logger.Warning(ex, "Failed to scan a Sent Items folder");
                    CloseFolder();
                }
            }
            return false;
        }

        private void CloseFolder()
        {
            if (_restricted != null && !ReferenceEquals(_restricted, _items)) ComObjects.Release(_restricted);
            _restricted = null;
            if (_items != null) { ComObjects.Release(_items); _items = null; }
            if (_folder != null) { ComObjects.Release(_folder); _folder = null; }
        }

        public void Dispose()
        {
            _owner.EnsureUiThread();
            if (_disposed) return;
            CloseFolder();
            if (_session != null) { ComObjects.Release(_session); _session = null; }
            _disposed = true;
        }
    }

    public IUnansweredSentScan BeginUnansweredSentScan(int minDays, int lookbackDays,
        ICollection<string>? storeIds = null, EmailSearchQuery? filter = null)
    {
        EnsureUiThread();
        return new UnansweredSentScan(this, minDays, lookbackDays, storeIds, filter);
    }
}
