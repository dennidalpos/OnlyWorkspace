using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Logging;
using MSOutlook = Microsoft.Office.Interop.Outlook;

namespace OnlyWorkspace.Outlook.Services;

public sealed class ContactFolderInfo
{
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

/// <summary>One contact of an address book, as the comparison needs it.</summary>
public sealed class ContactIndexEntry
{
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Emails { get; } = new();
    public DateTime LastModifiedUtc { get; set; }
}

/// <summary>The text of an email a candidate wrote, or why it cannot be used.</summary>
public sealed class ContactMailText
{
    public string Body { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderAddress { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; }
    /// <summary>Empty when usable.</summary>
    public string Problem { get; set; } = string.Empty;
}

public enum ContactWrite { Saved, ChangedMeanwhile, Missing, Denied, Full }

/// <summary>
/// Contacts table of one folder, read a batch at a time on the UI thread (address books of thousands of contacts).
/// Dispose on the UI thread.
/// </summary>
public sealed class ContactTableReader : IDisposable
{
    private const string Address = "http://schemas.microsoft.com/mapi/id/{00062004-0000-0000-C000-000000000046}/";
    // Email1..3: address, type, original display name (holds the SMTP address of an "EX" entry).
    private static readonly string[] EmailProps =
    {
        Address + "8083001F", Address + "8082001F", Address + "8084001F",
        Address + "8093001F", Address + "8092001F", Address + "8094001F",
        Address + "80A3001F", Address + "80A2001F", Address + "80A4001F"
    };
    private static readonly Regex Smtp = new(@"[^\s<>()""']+@[^\s<>()""']+\.[^\s<>()""']+", RegexOptions.CultureInvariant);

    private MSOutlook.Table? _table;
    private readonly string _storeId;
    private readonly int[] _email = new int[9];

    internal ContactTableReader(MSOutlook.MAPIFolder folder, string storeId)
    {
        _storeId = storeId;
        MSOutlook.Columns? columns = null;
        try
        {
            _table = folder.GetTable(Type.Missing, MSOutlook.OlTableContents.olUserItems);
            columns = _table.Columns;
            columns.RemoveAll();
            foreach (var name in new[] { "EntryID", "MessageClass", "Subject", "LastModificationTime" }) columns.Add(name);
            int next = 4;
            for (int i = 0; i < EmailProps.Length; i++)
            {
                MSOutlook.Column? column = null;
                try { column = columns.Add(EmailProps[i]); _email[i] = next++; }
                catch { _email[i] = -1; }
                finally { if (column != null) Marshal.ReleaseComObject(column); }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (columns != null) Marshal.ReleaseComObject(columns);
        }
    }

    public bool EndOfTable => _table == null || _table.EndOfTable;

    public List<ContactIndexEntry> ReadBatch(int max)
    {
        var result = new List<ContactIndexEntry>();
        if (_table == null || _table.EndOfTable || _table.GetArray(max) is not object[,] rows) return result;
        for (int i = rows.GetLowerBound(0); i <= rows.GetUpperBound(0); i++)
        {
            // Distribution lists and other items share the folder.
            if (rows[i, 0] is not string entryId || !(rows[i, 1] as string ?? string.Empty).StartsWith("IPM.Contact", StringComparison.OrdinalIgnoreCase)) continue;
            var entry = new ContactIndexEntry
            {
                EntryId = entryId, StoreId = _storeId, Name = rows[i, 2] as string ?? string.Empty,
                // Built-in column names return local time.
                LastModifiedUtc = rows[i, 3] is DateTime modified ? modified.ToUniversalTime() : DateTime.MinValue
            };
            for (int n = 0; n < 3; n++)
            {
                var address = Cell(rows, i, _email[n * 3]);
                if (string.Equals(Cell(rows, i, _email[n * 3 + 1]), "EX", StringComparison.OrdinalIgnoreCase) || address.IndexOf('@') < 0)
                    address = Smtp.Match(Cell(rows, i, _email[n * 3 + 2])).Value;
                address = CounterpartyResolver.Normalize(address);
                if (address.IndexOf('@') > 0 && !entry.Emails.Contains(address)) entry.Emails.Add(address);
            }
            result.Add(entry);
        }
        return result;
    }

    private static string Cell(object[,] rows, int row, int column) => column >= 0 ? rows[row, column] as string ?? string.Empty : string.Empty;

    public void Dispose()
    {
        if (_table != null) Marshal.ReleaseComObject(_table);
        _table = null;
    }
}

public sealed partial class OutlookDataService
{
    private const int MaxContactFolders = 200;

    private sealed class ContactFolderFrame
    {
        public ContactFolderFrame(MSOutlook.MAPIFolder folder, int depth) { Folder = folder; Depth = depth; }
        public MSOutlook.MAPIFolder Folder { get; }
        public int Depth { get; }
        public MSOutlook.Folders? Children { get; set; }
        public int Next { get; set; } = 1;
    }

    /// <summary>
    /// Contact folders of an open store (subfolders and the IMAP "this computer only" one included); the default first.
    /// Every folder of the mailbox is looked at, so the walk yields Outlook's UI thread every few tens of milliseconds.
    /// </summary>
    public async Task<IReadOnlyList<ContactFolderInfo>> ContactFoldersAsync(string storeId, CancellationToken ct = default)
    {
        EnsureUiThread();
        var result = new List<ContactFolderInfo>();
        string defaultId = string.Empty;
        MSOutlook.MAPIFolder? root = null;
        WithStore(storeId, store =>
        {
            MSOutlook.MAPIFolder? def = null;
            try { def = store.GetDefaultFolder(MSOutlook.OlDefaultFolders.olFolderContacts); defaultId = def?.EntryID ?? string.Empty; }
            catch { } // PST without a Contacts folder
            finally { if (def != null) Marshal.ReleaseComObject(def); }
            root = store.GetRootFolder();
        });

        var stack = new Stack<ContactFolderFrame>();
        stack.Push(new ContactFolderFrame(root!, 0));
        var slice = new UiSlice();
        try
        {
            while (stack.Count > 0 && result.Count < MaxContactFolders)
            {
                await slice.YieldIfDueAsync(ct);
                var frame = stack.Peek();
                try
                {
                    if (frame.Children == null)
                    {
                        if (frame.Depth > 0 && frame.Folder.DefaultItemType == MSOutlook.OlItemType.olContactItem)
                        {
                            var id = frame.Folder.EntryID ?? string.Empty;
                            result.Add(new ContactFolderInfo { EntryId = id, StoreId = storeId, Path = frame.Folder.FolderPath ?? frame.Folder.Name ?? string.Empty, IsDefault = id.Length > 0 && id == defaultId });
                        }
                        if (frame.Depth > 6) Release(stack.Pop());
                        else frame.Children = frame.Folder.Folders;
                    }
                    else if (frame.Next > frame.Children.Count) Release(stack.Pop());
                    else stack.Push(new ContactFolderFrame(frame.Children[frame.Next++], frame.Depth + 1));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    OnlyWorkspaceLog.Logger.Debug(ex, "Contact folder skipped");
                    if (ReferenceEquals(stack.Peek(), frame)) Release(stack.Pop());
                }
            }
        }
        finally
        {
            while (stack.Count > 0) Release(stack.Pop());
        }
        result.Sort((a, b) => a.IsDefault != b.IsDefault ? (a.IsDefault ? -1 : 1) : string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase));
        return result;

        static void Release(ContactFolderFrame frame)
        {
            if (frame.Children != null) Marshal.ReleaseComObject(frame.Children);
            Marshal.ReleaseComObject(frame.Folder);
        }
    }

    /// <summary>A "Contatti" folder at the root of the store, for a PST without one. Returns its EntryID.</summary>
    public string CreateContactsFolder(string storeId)
    {
        EnsureUiThread();
        string id = string.Empty;
        WithStore(storeId, store =>
        {
            MSOutlook.MAPIFolder? root = null;
            MSOutlook.Folders? folders = null;
            MSOutlook.MAPIFolder? created = null;
            try
            {
                root = store.GetRootFolder();
                folders = root.Folders;
                created = folders.Add("Contatti", MSOutlook.OlDefaultFolders.olFolderContacts);
                id = created.EntryID ?? string.Empty;
            }
            finally
            {
                if (created != null) Marshal.ReleaseComObject(created);
                if (folders != null) Marshal.ReleaseComObject(folders);
                if (root != null) Marshal.ReleaseComObject(root);
            }
        });
        return id;
    }

    /// <summary>Reader of a contact folder; null when the folder is no longer there.</summary>
    public ContactTableReader? OpenContactTable(string storeId, string folderEntryId)
    {
        EnsureUiThread();
        MSOutlook.NameSpace? session = null;
        MSOutlook.MAPIFolder? folder = null;
        try
        {
            session = _application.Session;
            folder = session.GetFolderFromID(folderEntryId, storeId);
            return new ContactTableReader(folder, storeId);
        }
        catch (COMException ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Contact folder not available");
            return null;
        }
        finally
        {
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (session != null) Marshal.ReleaseComObject(session);
        }
    }

    /// <summary>Default Contacts folder of every open store but <paramref name="exceptStoreId"/>: where else a contact may already be.</summary>
    public IReadOnlyList<ContactFolderInfo> OtherDefaultContactFolders(string exceptStoreId) =>
        DefaultFoldersOfAllStores(MSOutlook.OlDefaultFolders.olFolderContacts)
            .Where(f => !string.Equals(f.StoreId, exceptStoreId, StringComparison.OrdinalIgnoreCase))
            .Select(f => new ContactFolderInfo { EntryId = f.EntryId, StoreId = f.StoreId, Path = f.Path, IsDefault = true }).ToList();

    /// <summary>The fields of a contact the flow may change; null when it is no longer there.</summary>
    public Dictionary<string, string>? ReadContactFields(string entryId, string storeId, out DateTime lastModifiedUtc)
    {
        EnsureUiThread();
        DateTime modified = DateTime.MinValue;
        var fields = WithContact(entryId, storeId, contact =>
        {
            modified = contact.LastModificationTime.ToUniversalTime();
            return FieldsOf(contact);
        });
        lastModifiedUtc = modified;
        return fields;
    }

    private static Dictionary<string, string> FieldsOf(MSOutlook.ContactItem c) => new()
    {
        [ContactFields.FullName] = c.FullName ?? string.Empty,
        [ContactFields.Company] = c.CompanyName ?? string.Empty,
        [ContactFields.JobTitle] = c.JobTitle ?? string.Empty,
        [ContactFields.BusinessPhone] = c.BusinessTelephoneNumber ?? string.Empty,
        [ContactFields.MobilePhone] = c.MobileTelephoneNumber ?? string.Empty,
        [ContactFields.BusinessAddress] = c.BusinessAddress ?? string.Empty,
        [ContactFields.WebPage] = c.WebPage ?? string.Empty
    };

    private static void Apply(MSOutlook.ContactItem c, IReadOnlyDictionary<string, string> fields)
    {
        foreach (var pair in fields)
        {
            switch (pair.Key)
            {
                case ContactFields.FullName: c.FullName = pair.Value; break;
                case ContactFields.Company: c.CompanyName = pair.Value; break;
                case ContactFields.JobTitle: c.JobTitle = pair.Value; break;
                case ContactFields.BusinessPhone: c.BusinessTelephoneNumber = pair.Value; break;
                case ContactFields.MobilePhone: c.MobileTelephoneNumber = pair.Value; break;
                case ContactFields.BusinessAddress: c.BusinessAddress = pair.Value; break;
                case ContactFields.WebPage: c.WebPage = pair.Value; break;
            }
        }
    }

    /// <summary>
    /// Writes the chosen fields, or one more address (<paramref name="addEmail"/>), unless the contact changed after
    /// <paramref name="expectedModifiedUtc"/>: the user's own edit is never overwritten unseen.
    /// </summary>
    public ContactWrite UpdateContact(string entryId, string storeId, DateTime expectedModifiedUtc, IReadOnlyDictionary<string, string> fields, string? addEmail = null)
    {
        EnsureUiThread();
        var outcome = ContactWrite.Missing;
        try
        {
            WithContact(entryId, storeId, contact =>
            {
                if (Math.Abs((contact.LastModificationTime.ToUniversalTime() - expectedModifiedUtc).TotalSeconds) > 2) { outcome = ContactWrite.ChangedMeanwhile; return Done; }
                if (!string.IsNullOrEmpty(addEmail))
                {
                    if (string.IsNullOrEmpty(contact.Email2Address)) contact.Email2Address = addEmail;
                    else if (string.IsNullOrEmpty(contact.Email3Address)) contact.Email3Address = addEmail;
                    else { outcome = ContactWrite.Full; return Done; }
                }
                Apply(contact, fields);
                contact.Save();
                outcome = ContactWrite.Saved;
                return Done;
            });
        }
        catch (COMException ex)
        {
            // Shared or read-only address book.
            OnlyWorkspaceLog.Logger.Warning(ex, "Contact update refused");
            return ContactWrite.Denied;
        }
        return outcome;
    }

    /// <summary>New contact in the chosen folder; its EntryID, or empty when the folder refuses it.</summary>
    public string CreateContact(string storeId, string folderEntryId, string address, IReadOnlyDictionary<string, string> fields)
    {
        EnsureUiThread();
        MSOutlook.NameSpace? session = null;
        MSOutlook.MAPIFolder? folder = null;
        MSOutlook.Items? items = null;
        MSOutlook.ContactItem? contact = null;
        try
        {
            session = _application.Session;
            folder = session.GetFolderFromID(folderEntryId, storeId);
            items = folder.Items;
            contact = (MSOutlook.ContactItem)items.Add(MSOutlook.OlItemType.olContactItem);
            contact.Email1Address = address;
            Apply(contact, fields);
            contact.Save();
            return contact.EntryID ?? string.Empty;
        }
        catch (COMException ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Contact creation refused");
            return string.Empty;
        }
        finally
        {
            if (contact != null) Marshal.ReleaseComObject(contact);
            if (items != null) Marshal.ReleaseComObject(items);
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (session != null) Marshal.ReleaseComObject(session);
        }
    }

    public bool DisplayContact(string entryId, string storeId)
    {
        EnsureUiThread();
        return WithContact(entryId, storeId, contact => { contact.Display(false); return Done; }) != null;
    }

    /// <summary>Text of an email written by a candidate; Problem says why it cannot be used (moved, protected, automatic).</summary>
    public ContactMailText ReadContactMail(string entryId, string storeId)
    {
        EnsureUiThread();
        string? messageClass = null;
        var text = WithMailById(entryId, storeId, mail =>
        {
            messageClass = mail.MessageClass;
            var result = new ContactMailText
            {
                Subject = mail.Subject ?? string.Empty, SenderName = mail.SenderName ?? string.Empty,
                SenderAddress = mail.SenderEmailAddress ?? string.Empty, ReceivedUtc = mail.ReceivedTime.ToUniversalTime()
            };
            if (ContactExtraction.IsAutomatic(messageClass, result.Subject)) result.Problem = "risposta automatica o messaggio protetto";
            else if (IsHeaderOnly(mail)) result.Problem = "solo intestazione scaricata";
            else
            {
                result.Body = SafeGetBody(mail);
                if (result.Body.Trim().Length == 0) result.Problem = "testo non leggibile";
            }
            return result;
        });
        return text ?? new ContactMailText { Problem = "email non più dove l'archivio l'aveva trovata" };
    }

    private static bool IsHeaderOnly(MSOutlook.MailItem mail)
    {
        try { return mail.DownloadState == MSOutlook.OlDownloadState.olHeaderOnly; }
        catch { return false; }
    }

    private T? WithContact<T>(string entryId, string storeId, Func<MSOutlook.ContactItem, T> action) where T : class
    {
        MSOutlook.NameSpace? session = null;
        object? item = null;
        try
        {
            session = _application.Session;
            try { item = session.GetItemFromID(entryId, storeId); }
            catch (COMException ex)
            {
                OnlyWorkspaceLog.Logger.Warning(ex, "Contact not found");
                return null;
            }
            return item is MSOutlook.ContactItem contact ? action(contact) : null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            if (session != null) Marshal.ReleaseComObject(session);
        }
    }

    private void WithStore(string storeId, Action<MSOutlook.Store> action)
    {
        MSOutlook.NameSpace? session = null;
        MSOutlook.Stores? stores = null;
        try
        {
            session = _application.Session;
            stores = session.Stores;
            for (int i = 1; i <= stores.Count; i++)
            {
                MSOutlook.Store? store = null;
                try
                {
                    store = stores[i];
                    if (store == null || !store.IsOpen || !string.Equals(store.StoreID, storeId, StringComparison.OrdinalIgnoreCase)) continue;
                    action(store);
                    return;
                }
                finally { if (store != null) Marshal.ReleaseComObject(store); }
            }
            throw new InvalidOperationException("La cassetta non è aperta in Outlook.");
        }
        finally
        {
            if (stores != null) Marshal.ReleaseComObject(stores);
            if (session != null) Marshal.ReleaseComObject(session);
        }
    }
}
