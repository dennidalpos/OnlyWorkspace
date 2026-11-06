using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Utils;
using OnlyWorkspace.Infrastructure.Logging;
using MSOutlook = Microsoft.Office.Interop.Outlook;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>
/// Rows of one folder, newest first, a batch at a time: the table stays open between batches so a large
/// folder is read in short slices on the UI thread. Dispose on the UI thread.
/// </summary>
public sealed class ArchiveHeaderReader : IArchiveHeaderReader
{
    private const string PropReferences = "http://schemas.microsoft.com/mapi/proptag/0x1039001F";
    private const string PropSent = "http://schemas.microsoft.com/mapi/proptag/0x00390040";
    private const string PropInReplyTo = "http://schemas.microsoft.com/mapi/proptag/0x1042001F";
    private const string PropConversationId = "http://schemas.microsoft.com/mapi/proptag/0x30130102";
    private const string PropHasAttach = "http://schemas.microsoft.com/mapi/proptag/0x0E1B000B";
    private const string PropMessageSize = "http://schemas.microsoft.com/mapi/proptag/0x0E080003";

    private MSOutlook.Table? _table;
    private readonly bool _refreshBodies;
    public bool CanDeleteAbsent { get; }
    private readonly int _references = -1, _sent = -1, _messageId = -1, _smtp = -1, _inReplyTo = -1, _conversation = -1, _hasAttach = -1, _size = -1, _modified = -1;

    /// <param name="extended">False: the six basic columns only, for a store whose rows fail with the others.</param>
    internal ArchiveHeaderReader(MSOutlook.MAPIFolder folder, string filter, bool extended, bool refreshBodies = false)
    {
        _refreshBodies = refreshBodies;
        MSOutlook.Columns? columns = null;
        try
        {
            _table = folder.GetTable(filter.Length > 0 ? filter : Type.Missing, MSOutlook.OlTableContents.olUserItems);
            CanDeleteAbsent = OutlookDataService.CanVerifyArchiveAbsence(folder);
            columns = _table.Columns;
            columns.RemoveAll();
            // 0..5: always available.
            foreach (var name in new[] { "EntryID", "Subject", "MessageClass", "SenderName", "SenderEmailAddress", "ReceivedTime" }) columns.Add(name);
            if (extended)
            {
                int next = 6;
                // Not every store exposes these: a missing column is -1.
                // No "Body" column: on IMAP stores it is accepted, then every row read fails (checked on 2026-10-11).
                _messageId = TryAdd(columns, DaslQueryBuilder.PropInternetMessageId, ref next);
                _smtp = TryAdd(columns, DaslQueryBuilder.PropSenderSmtpAddress, ref next);
                _inReplyTo = TryAdd(columns, PropInReplyTo, ref next);
                _references = TryAdd(columns, PropReferences, ref next);
                _sent = TryAdd(columns, PropSent, ref next);
                _conversation = TryAdd(columns, PropConversationId, ref next);
                _hasAttach = TryAdd(columns, PropHasAttach, ref next);
                _size = TryAdd(columns, PropMessageSize, ref next);
                _modified = TryAdd(columns, "LastModificationTime", ref next);
            }
            _table.Sort("[ReceivedTime]", true);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            ComObjects.Release(columns);
        }
    }

    private static int TryAdd(MSOutlook.Columns columns, string name, ref int next)
    {
        MSOutlook.Column? column = null;
        try
        {
            column = columns.Add(name);
            return next++;
        }
        catch
        {
            return -1;
        }
        finally
        {
            ComObjects.Release(column);
        }
    }

    public bool EndOfTable => _table == null || _table.EndOfTable;

    /// <summary>Next rows; meeting requests and receipts come flagged NotEmail (date only). Empty at the end of the folder.</summary>
    public List<ArchiveHeader> ReadBatch(int max)
    {
        var result = new List<ArchiveHeader>();
        if (_table == null || _table.EndOfTable) return result;
        if (_table.GetArray(max) is not object[,] rows)
        {
            if (_table.EndOfTable) return result;
            throw new InvalidOperationException("The folder table did not return rows or a verified end.");
        }

        for (int i = rows.GetLowerBound(0); i <= rows.GetUpperBound(0); i++)
        {
            if (rows[i, 0] is not string entryId || string.IsNullOrWhiteSpace(entryId) || rows[i, 2] is not string messageClass || messageClass.Length == 0)
                throw new InvalidOperationException("The folder table returned a row without EntryID or MessageClass.");
            // Built-in column names return local time.
            var receivedUtc = rows[i, 5] is DateTime received ? received.ToUniversalTime() : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (!messageClass.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ArchiveHeader { EntryId = entryId, ReceivedUtc = receivedUtc, NotEmail = true, RefreshBody = _refreshBodies });
                continue;
            }

            string address = rows[i, 4] as string ?? string.Empty;
            // Exchange rows carry a DN as sender address.
            if (address.IndexOf('@') < 0 && Text(rows, i, _smtp) is { Length: > 0 } smtp) address = smtp;
            result.Add(new ArchiveHeader
            {
                EntryId = entryId,
                Subject = rows[i, 1] as string ?? string.Empty,
                SenderName = rows[i, 3] as string ?? string.Empty,
                SenderAddress = address,
                ReceivedUtc = receivedUtc,
                RefreshBody = _refreshBodies,
                ModifiedUtc = _modified >= 0 && rows[i, _modified] is DateTime modified ? modified.ToUniversalTime() : null,
                MessageId = Text(rows, i, _messageId),
                InReplyTo = Text(rows, i, _inReplyTo),
                References = Text(rows, i, _references),
                // DASL PT_SYSTIME columns are UTC; named ReceivedTime above is local.
                SentUtc = _sent >= 0 && rows[i, _sent] is DateTime sent && sent.Year >= 1980 && sent.Year < 4500 ? DateTime.SpecifyKind(sent, DateTimeKind.Utc) : null,
                ThreadMetadataComplete = _messageId >= 0 && _inReplyTo >= 0 && _conversation >= 0 && _references >= 0 && _sent >= 0,
                ConversationId = _conversation >= 0 && rows[i, _conversation] is byte[] id ? Hex(id) : string.Empty,
                HasAttachments = _hasAttach >= 0 && rows[i, _hasAttach] is bool has && has,
                Size = _size >= 0 && rows[i, _size] is int size ? size : 0
            });
        }
        return result;
    }

    private static string Text(object[,] rows, int row, int column) => column >= 0 ? rows[row, column] as string ?? string.Empty : string.Empty;

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    public void Dispose()
    {
        ComObjects.Release(_table);
        _table = null;
    }
}

public sealed partial class OutlookDataService : IArchiveMailSource, IOutlookDataService
{
    private const int MaxArchiveFolderDepth = 12;
    private const int MaxArchiveFolders = 3000;
    private const int MaxArchiveRecipients = 50;
    private const int MaxArchiveAttachments = 30;

    private readonly Dictionary<string, bool> _archiveExtendedColumns = new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="ArchiveStoreClosedException">The folder's store is gone (a PST closed while it was read).</exception>
    internal static bool CanVerifyArchiveAbsence(MSOutlook.MAPIFolder folder)
    {
        MSOutlook.Store? store = null;
        try
        {
            store = folder.Store ?? throw new ArchiveStoreClosedException();
            // OST and server-backed inventories describe a cache, not authoritative deletion.
            return store.IsDataFileStore && store.ExchangeStoreType == MSOutlook.OlExchangeStoreType.olNotExchange &&
                   (store.FilePath ?? string.Empty).EndsWith(".pst", StringComparison.OrdinalIgnoreCase);
        }
        finally { ComObjects.Release(store); }
    }

    /// <summary>Name of the Outlook profile in use: each profile has its own archive.</summary>
    public string GetProfileName()
    {
        EnsureUiThread();
        MSOutlook.NameSpace? session = null;
        try
        {
            session = _application.Session;
            return session.CurrentProfileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            ComObjects.Release(session);
        }
    }

    /// <summary>SMTP addresses of the accounts plus the Exchange DN of the current user.</summary>
    public IReadOnlyCollection<string> GetOwnAddressList()
    {
        EnsureUiThread();
        return GetOwnAddresses();
    }

    /// <summary>The PSTs' own addresses and names: from now on they count as the user's, like the accounts' ones.</summary>
    public void SetStoreOwnAddresses(IReadOnlyCollection<string> addresses, IReadOnlyCollection<string> names)
    {
        EnsureUiThread();
        _storeOwnAddresses = addresses.Where(a => a.IndexOf('@') > 0).ToList();
        _storeOwnNames = names.ToList();
        _ownDomains = null;
    }

    private const string PropIpmSentMailEntryId = "http://schemas.microsoft.com/mapi/proptag/0x35E40102"; // PR_IPM_SENTMAIL_ENTRYID
    private static readonly string[] SentFolderNames = { "Posta inviata", "Elementi inviati", "Sent Items", "Sent Mail", "Sent", "Gesendete Elemente", "Éléments envoyés", "Elementos enviados" };

    /// <summary>
    /// Senders of the last <see cref="StoreOwnAddresses.SentRows"/> emails of the store's Sent Items, as a guess of the
    /// user's addresses; null when the store or its Sent Items cannot be found. One bounded table read: the indexer
    /// calls it for one store per step.
    /// </summary>
    public StoreOwnEntry? GuessStoreOwnAddresses(string storeId)
    {
        EnsureUiThread();
        MSOutlook.NameSpace? session = null;
        MSOutlook.Store? store = null;
        MSOutlook.MAPIFolder? sent = null;
        MSOutlook.Table? table = null;
        MSOutlook.Columns? columns = null;
        try
        {
            session = _application.Session;
            store = session.GetStoreFromID(storeId);
            sent = SentFolderOf(session, store, storeId);
            if (sent == null) return null;
            table = sent.GetTable(Type.Missing, MSOutlook.OlTableContents.olUserItems);
            columns = table.Columns;
            ComObjects.Release(columns.Add("SenderName"));
            ComObjects.Release(columns.Add("SenderEmailAddress"));
            bool hasSmtp = true;
            try { ComObjects.Release(columns.Add(DaslQueryBuilder.PropSenderSmtpAddress)); }
            catch { hasSmtp = false; }
            try { table.Sort("[SentOn]", true); }
            catch { table.Sort("[ReceivedTime]", true); }
            var rows = new List<SentRow>();
            for (int read = 0; read < StoreOwnAddresses.SentRows && !table.EndOfTable; read++)
            {
                MSOutlook.Row? row = null;
                try
                {
                    row = table.GetNextRow();
                    rows.Add(new SentRow(hasSmtp ? row[DaslQueryBuilder.PropSenderSmtpAddress] as string : null, row["SenderEmailAddress"] as string, row["SenderName"] as string));
                }
                catch { } // unreadable row
                finally { ComObjects.Release(row); }
            }
            return StoreOwnAddresses.Guess(rows);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Archive: failed to read a store's Sent Items");
            return null;
        }
        finally
        {
            ComObjects.Release(columns);
            ComObjects.Release(table);
            ComObjects.Release(sent);
            ComObjects.Release(store);
            ComObjects.Release(session);
        }
    }

    /// <summary>Sent Items: the default folder, then PR_IPM_SENTMAIL_ENTRYID of the store, then a top folder with a known name.</summary>
    private static MSOutlook.MAPIFolder? SentFolderOf(MSOutlook.NameSpace session, MSOutlook.Store store, string storeId)
    {
        try { return store.GetDefaultFolder(MSOutlook.OlDefaultFolders.olFolderSentMail); }
        catch { } // PST without default folders
        MSOutlook.PropertyAccessor? accessor = null;
        try
        {
            accessor = store.PropertyAccessor;
            if (accessor.GetProperty(PropIpmSentMailEntryId) is byte[] id && id.Length > 0)
                return session.GetFolderFromID(accessor.BinaryToString(id), storeId);
        }
        catch { } // property absent
        finally { ComObjects.Release(accessor); }

        MSOutlook.MAPIFolder? root = null;
        MSOutlook.Folders? children = null;
        try
        {
            root = store.GetRootFolder();
            children = root.Folders;
            // Top level only, a bounded list: names come from the Outlook UI languages.
            for (int i = 1; i <= Math.Min(children.Count, 100); i++)
            {
                MSOutlook.MAPIFolder? child = null;
                try
                {
                    child = children[i];
                    if (SentFolderNames.Contains(child.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    {
                        var found = child;
                        child = null;
                        return found;
                    }
                }
                catch { } // unreadable folder
                finally { ComObjects.Release(child); }
            }
        }
        catch { } // store without a readable root
        finally
        {
            ComObjects.Release(children);
            ComObjects.Release(root);
        }
        return null;
    }

    /// <summary>
    /// Every mail folder of the chosen stores (all open stores when none is given), without trash, junk,
    /// drafts, outbox, sync issues and RSS feeds; at most MaxArchiveFolders per store. Listed a few folders per
    /// <see cref="IArchiveFolderWalk.Step"/>: a store with thousands of folders read in one call held Outlook's UI
    /// thread long enough for Outlook to disable the add-in.
    /// </summary>
    public IArchiveFolderWalk WalkArchiveFolders(ICollection<string>? storeIds)
    {
        EnsureUiThread();
        return new ArchiveFolderWalk(this, storeIds, StoreFolders(storeIds, root: true));
    }

    private static readonly MSOutlook.OlDefaultFolders[] SkippedArchiveFolders =
    {
        MSOutlook.OlDefaultFolders.olFolderJunk, MSOutlook.OlDefaultFolders.olFolderDrafts,
        MSOutlook.OlDefaultFolders.olFolderOutbox, MSOutlook.OlDefaultFolders.olFolderSyncIssues, MSOutlook.OlDefaultFolders.olFolderConflicts,
        MSOutlook.OlDefaultFolders.olFolderLocalFailures, MSOutlook.OlDefaultFolders.olFolderServerFailures, MSOutlook.OlDefaultFolders.olFolderRssFeeds
    };

    /// <summary>EntryIDs of the default folders the archive leaves out, for one store: read when the walk reaches it.</summary>
    private static HashSet<string> SkippedFoldersOf(MSOutlook.NameSpace session, string storeId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MSOutlook.Store? store = null;
        try
        {
            store = session.GetStoreFromID(storeId);
            foreach (var kind in SkippedArchiveFolders)
            {
                MSOutlook.MAPIFolder? folder = null;
                try
                {
                    folder = store.GetDefaultFolder(kind);
                    if (!string.IsNullOrEmpty(folder?.EntryID)) result.Add(folder!.EntryID);
                }
                catch { } // store without that folder (archive, public folders)
                finally { ComObjects.Release(folder); }
            }
        }
        catch (Exception ex) { OnlyWorkspaceLog.Logger.Debug(ex, "Archive: default folders of a store not readable"); }
        finally { ComObjects.Release(store); }
        return result;
    }

    private sealed class ArchiveFolderWalk : IArchiveFolderWalk
    {
        private sealed class Frame
        {
            public Frame(MSOutlook.MAPIFolder folder, int depth) { Folder = folder; Depth = depth; }
            public MSOutlook.MAPIFolder Folder { get; }
            public int Depth { get; }
            public MSOutlook.Folders? Children { get; set; }
            public int Next { get; set; } = 1;
        }

        private readonly OutlookDataService _owner;
        private readonly ICollection<string>? _storeIds;
        private readonly IReadOnlyList<MailFolderRef> _roots;
        private HashSet<string> _skipped = new();
        private readonly List<MailFolderRef> _result = new();
        private readonly List<string> _complete = new();
        private readonly List<string> _authoritative = new();
        private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);
        // Depth-first position inside the current store, kept between steps.
        private readonly Stack<Frame> _stack = new();
        private MSOutlook.NameSpace? _session;
        private int _root;
        private string? _store;
        private List<MailFolderRef> _storeFolders = new();
        private bool _storeComplete;
        // Limit reached: the rest of the store is left out, its frames released one per loop.
        private bool _storeFull;
        private bool _finished;

        public ArchiveFolderWalk(OutlookDataService owner, ICollection<string>? storeIds, IReadOnlyList<MailFolderRef> roots)
        {
            _owner = owner;
            _storeIds = storeIds;
            _roots = roots;
            _session = owner._application.Session;
        }

        public IReadOnlyList<MailFolderRef> Folders => _result;
        public IReadOnlyCollection<string> AuthoritativeStores => _authoritative;
        public IReadOnlyDictionary<string, string> Issues => _failures;

        public bool Step(int max)
        {
            _owner.EnsureUiThread();
            for (int visited = 0; !_finished && visited < Math.Max(1, max);)
            {
                if (_stack.Count == 0)
                {
                    if (_root >= _roots.Count) Finish();
                    else OpenStore(_roots[_root++]);
                    continue;
                }
                if (_storeFull)
                {
                    Pop();
                    continue;
                }
                var frame = _stack.Peek();
                if (frame.Children == null)
                {
                    visited++;
                    if (!Visit(frame)) Pop();
                    continue;
                }
                if (frame.Next > frame.Children.Count)
                {
                    Pop();
                    continue;
                }
                MSOutlook.MAPIFolder? child = null;
                try
                {
                    child = frame.Children[frame.Next++];
                    _stack.Push(new Frame(child, frame.Depth - 1));
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Debug(ex, "Archive: failed to read subfolder {Index}", frame.Next - 1);
                    ComObjects.Release(child);
                    _storeComplete = false;
                }
            }
            return _finished;
        }

        private void OpenStore(MailFolderRef root)
        {
            _store = root.StoreId;
            _storeFolders = new List<MailFolderRef>();
            _storeComplete = true;
            _storeFull = false;
            try
            {
                _skipped = SkippedFoldersOf(_session!, root.StoreId);
                _stack.Push(new Frame(_session!.GetFolderFromID(root.EntryId, root.StoreId), MaxArchiveFolderDepth));
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Warning(ex, "Archive: failed to list the folders of a store");
                _failures[root.StoreId] = "Cassetta non disponibile per l'elenco cartelle.";
            }
        }

        /// <summary>The folder itself and its subfolder list; false when its subfolders are not to be read.</summary>
        private bool Visit(Frame frame)
        {
            try
            {
                if (_skipped.Contains(frame.Folder.EntryID ?? string.Empty)) return false;
                if (_storeFolders.Count >= MaxArchiveFolders)
                {
                    // The limit applies to each mailbox: a large one must not hide the mailboxes listed after it.
                    _storeComplete = false;
                    _storeFull = true;
                    return false;
                }
                if (frame.Folder.DefaultItemType == MSOutlook.OlItemType.olMailItem) _storeFolders.Add(FolderRefOf(frame.Folder));
                frame.Children = frame.Folder.Folders;
                if (frame.Depth <= 0)
                {
                    if (frame.Children.Count != 0) _storeComplete = false;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Debug(ex, "Archive: failed to read a folder");
                _storeComplete = false;
                return false;
            }
        }

        private void Pop()
        {
            var frame = _stack.Pop();
            if (_stack.Count == 0) CloseStore(frame.Folder);
            ComObjects.Release(frame.Children);
            ComObjects.Release(frame.Folder);
        }

        private void CloseStore(MSOutlook.MAPIFolder root)
        {
            _result.AddRange(_storeFolders);
            if (_storeComplete)
            {
                _complete.Add(_store!);
                try { if (CanVerifyArchiveAbsence(root)) _authoritative.Add(_store!); }
                catch (Exception ex) { OnlyWorkspaceLog.Logger.Debug(ex, "Archive: store type not readable"); }
            }
            else _failures[_store!] = _storeFolders.Count >= MaxArchiveFolders
                ? $"Elenco cartelle incompleto: superato il limite di {MaxArchiveFolders} cartelle per cassetta."
                : $"Elenco cartelle incompleto: cartelle non leggibili o oltre {MaxArchiveFolderDepth} livelli.";
            _store = null;
        }

        private void Finish()
        {
            if (_storeIds != null)
                foreach (var store in _storeIds)
                    if (!_complete.Contains(store, StringComparer.OrdinalIgnoreCase) && !_failures.ContainsKey(store))
                        _failures[store] = "Cassetta selezionata non raggiungibile per l'inventario.";
            _finished = true;
            Release();
        }

        private void Release()
        {
            while (_stack.Count > 0)
            {
                var frame = _stack.Pop();
                ComObjects.Release(frame.Children);
                ComObjects.Release(frame.Folder);
            }
            ComObjects.Release(_session);
            _session = null;
        }

        public void Dispose() => Release();
    }

    /// <summary>Opens the rows of a folder between the two dates (either may be null); null when the folder is gone.</summary>
    public IArchiveHeaderReader? OpenArchiveReader(string folderEntryId, string storeId, DateTime? newerThanUtc, DateTime? olderThanUtc, DateTime? modifiedSinceUtc = null, DateTime? modifiedUntilUtc = null)
    {
        EnsureUiThread();
        MSOutlook.NameSpace? session = null;
        MSOutlook.MAPIFolder? folder = null;
        try
        {
            session = _application.Session;
            folder = session.GetFolderFromID(folderEntryId, storeId);
            // A column can be accepted and still break every row: try the extended set once per store.
            if (!_archiveExtendedColumns.TryGetValue(storeId, out bool extended))
            {
                var verdict = ArchiveColumnProbe.Probe(() => ProbeColumns(folder, true), () => ProbeColumns(folder, false), out var failure);
                // Basic columns failing too: the store or the folder, not the columns; nothing is remembered.
                if (verdict == null) throw failure!;
                extended = verdict.Value;
                if (!extended) OnlyWorkspaceLog.Logger.Warning(failure, "Archive: extended table columns not usable on this store, basic columns only");
                _archiveExtendedColumns[storeId] = extended;
            }
            string filter = modifiedSinceUtc.HasValue && modifiedUntilUtc.HasValue
                ? DaslQueryBuilder.BuildArchiveRecentFilter(newerThanUtc ?? DateTime.UtcNow.AddDays(-1), modifiedSinceUtc.Value, modifiedUntilUtc.Value)
                : DaslQueryBuilder.BuildEmailSearchFilter(new EmailSearchQuery { StartDate = newerThanUtc, EndDate = olderThanUtc });
            return new ArchiveHeaderReader(folder, filter, extended, refreshBodies: modifiedSinceUtc.HasValue);
        }
        catch (ArchiveStoreClosedException)
        {
            // The indexer then sees the mailbox closed and drops its queued folders.
            OnlyWorkspaceLog.Logger.Information("Archive: folder table not opened, its mailbox is no longer available");
            return null;
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Archive: failed to open a folder table");
            return null;
        }
        finally
        {
            ComObjects.Release(folder);
            ComObjects.Release(session);
        }
    }

    private static void ProbeColumns(MSOutlook.MAPIFolder folder, bool extended)
    {
        using var probe = new ArchiveHeaderReader(folder, string.Empty, extended);
        probe.ReadBatch(1);
    }

    /// <summary>
    /// What the index needs from an opened email: plain body, recipients with their SMTP address and the
    /// attachment names. Null when the email is gone. Never triggers a download.
    /// </summary>
    // MAPI_E_NOT_FOUND: what GetItemFromID gives for an item no longer in the store (Gmail IMAP, 19/10/2026).
    private const int MapiNotFound = unchecked((int)0x8004010F);

    public bool? ArchiveItemExists(string entryId, string storeId)
    {
        EnsureUiThread();
        MSOutlook.NameSpace? session = null;
        object? item = null;
        try
        {
            session = _application.Session;
            item = session.GetItemFromID(entryId, storeId);
            return true;
        }
        catch (COMException ex)
        {
            if (ex.ErrorCode == MapiNotFound) return false;
            OnlyWorkspaceLog.Logger.Warning(ex, "Archive: cannot tell whether {EntryId} is still in Outlook", entryId);
            return null;
        }
        finally
        {
            ComObjects.Release(item);
            ComObjects.Release(session);
        }
    }


    public ArchiveBody? ReadForArchive(string entryId, string storeId)
    {
        EnsureUiThread();
        return WithMailById(entryId, storeId, mail =>
        {
            var body = new ArchiveBody();
            body.ModifiedUtc = mail.LastModificationTime.ToUniversalTime();
            string messageClass = mail.MessageClass ?? string.Empty;
            if (mail.DownloadState == MSOutlook.OlDownloadState.olHeaderOnly ||
                messageClass.StartsWith("IPM.Note.SMIME", StringComparison.OrdinalIgnoreCase) ||
                messageClass.StartsWith("IPM.Note.Secure", StringComparison.OrdinalIgnoreCase))
            {
                body.Unavailable = true;
                body.Retryable = mail.DownloadState == MSOutlook.OlDownloadState.olHeaderOnly;
            }
            else
            {
                body.Text = mail.Body ?? string.Empty;
            }

            MSOutlook.Recipients? recipients = null;
            try
            {
                recipients = mail.Recipients;
                body.RecipientsLimited = recipients.Count > MaxArchiveRecipients;
                for (int i = 1; i <= recipients.Count && body.Recipients.Count < MaxArchiveRecipients; i++)
                {
                    MSOutlook.Recipient? r = null;
                    try
                    {
                        r = recipients[i];
                        if (r.Type == (int)MSOutlook.OlMailRecipientType.olBCC) continue;
                        body.Recipients.Add(new ArchiveRecipient { Address = SmtpOf(r), Name = r.Name ?? string.Empty, IsCc = r.Type == (int)MSOutlook.OlMailRecipientType.olCC });
                    }
                    catch (Exception ex)
                    {
                        OnlyWorkspaceLog.Logger.Debug(ex, "Archive: failed to read recipient {Index}", i);
                        body.RecipientsLimited = true;
                    }
                    finally
                    {
                        ComObjects.Release(r);
                    }
                }
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Debug(ex, "Archive: failed to read recipients");
                body.RecipientsLimited = true;
            }
            finally
            {
                ComObjects.Release(recipients);
            }

            MSOutlook.Attachments? attachments = null;
            try
            {
                attachments = mail.Attachments;
                body.AttachmentsLimited = attachments.Count > MaxArchiveAttachments;
                for (int i = 1; i <= attachments.Count && body.AttachmentNames.Count < MaxArchiveAttachments; i++)
                {
                    MSOutlook.Attachment? attachment = null;
                    try
                    {
                        attachment = attachments[i];
                        string? name;
                        try { name = attachment.FileName; } catch { name = attachment.DisplayName; }
                        if (!string.IsNullOrWhiteSpace(name) && !IsInlineImage(attachment, name!)) body.AttachmentNames.Add(name!);
                    }
                    catch (Exception ex)
                    {
                        OnlyWorkspaceLog.Logger.Debug(ex, "Archive: failed to read attachment {Index}", i);
                        body.AttachmentsLimited = true;
                    }
                    finally
                    {
                        ComObjects.Release(attachment);
                    }
                }
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Debug(ex, "Archive: failed to read attachments");
                body.AttachmentsLimited = true;
            }
            finally
            {
                ComObjects.Release(attachments);
            }
            return body;
        });
    }
}
