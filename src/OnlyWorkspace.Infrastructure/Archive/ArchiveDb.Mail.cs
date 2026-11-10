using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
    private const int PreviewChars = 255;

    private readonly HashSet<string> _ownAddresses = new(StringComparer.Ordinal);

    /// <summary>
    /// The user's own addresses (accounts and PSTs): never a counterparty. The whole set each time: an address no longer
    /// listed goes back to its counterparty. An empty set changes nothing (Outlook could not say).
    /// </summary>
    public void SetOwnAddresses(IEnumerable<string> addresses)
    {
        var own = new HashSet<string>(addresses.Select(CounterpartyResolver.Normalize).Where(a => a.Length > 0), StringComparer.Ordinal);
        if (own.Count == 0) return;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var touched = new HashSet<long>();
            var former = new List<(long Id, string Addr, string? Name)>();
            using (var cmd = Command("select id, addr, name from addresses where is_me=1", tx))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) former.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            }
            foreach (var (id, addr, name) in former)
            {
                if (own.Contains(addr)) continue;
                long target = CounterpartyFor(CounterpartyResolver.KeyOf(addr), name, tx);
                Exec("update addresses set is_me=0, cp_id=$c where id=$a", tx, ("$c", target), ("$a", id));
                touched.Add(target);
            }
            foreach (var addr in own)
            {
                if (Scalar<long?>("select cp_id from addresses where addr=$a", tx, ("$a", addr)) is long previous) touched.Add(previous);
                Exec("insert into addresses(addr, is_me) values($a, 1) on conflict(addr) do update set is_me=1, cp_id=null", tx, ("$a", addr));
            }
            RefreshTwoWay(tx, touched.ToArray());
            tx.Commit();
            _ownAddresses.Clear();
            _ownAddresses.UnionWith(own);
        }
    }

    public ArchiveFolder GetOrAddFolder(string storeId, string entryId, string path)
    {
        lock (_gate)
        {
            Exec("insert into folders(store_id, entry_id, path) values($s,$e,$p) on conflict(store_id, entry_id) do update set path=case when length(excluded.path)>0 then excluded.path else folders.path end",
                null, ("$s", storeId), ("$e", entryId), ("$p", path));
            using var cmd = Command("select id, oldest, newest, done, error, verified_utc, path from folders where store_id=$s and entry_id=$e", null, ("$s", storeId), ("$e", entryId));
            using var r = cmd.ExecuteReader();
            r.Read();
            return new ArchiveFolder
            {
                Id = r.GetInt64(0),
                StoreId = storeId,
                EntryId = entryId,
                OldestUtc = r.IsDBNull(1) ? null : FromUnix(r.GetInt64(1)),
                NewestUtc = r.IsDBNull(2) ? null : FromUnix(r.GetInt64(2)),
                Done = r.GetInt64(3) != 0,
                Error = r.IsDBNull(4) ? null : r.GetString(4),
                VerifiedUtc = r.IsDBNull(5) ? null : FromUnix(r.GetInt64(5)),
                Path = r.IsDBNull(6) ? string.Empty : r.GetString(6)
            };
        }
    }

    /// <summary>
    /// The folders Outlook lists now. The others of the mailboxes listed whole were deleted there: they leave the
    /// folder count and its errors, and the next full check gives up their emails (in a cache store, those Outlook
    /// no longer finds; see <see cref="UnseenCacheCopies"/>). A mailbox with no folder listed is
    /// left as it is: an unreachable store lists nothing. The folders of a mailbox no longer open in Outlook become
    /// closed (listed=2): out of the folder count, their emails kept for offline search, listed again once it reopens.
    /// </summary>
    public void SetListedFolders(IReadOnlyCollection<long> folderIds, IReadOnlyCollection<string>? completedStoreIds = null, IReadOnlyCollection<string>? openStoreIds = null)
    {
        lock (_gate)
        {
            // No open mailbox at all: Outlook is going away, not every mailbox closed.
            if (openStoreIds is { Count: > 0 })
            {
                var open = openStoreIds.Select((s, i) => ("$o" + i, (object?)s)).ToArray();
                Exec("update folders set listed=2 where listed=1 and store_id not in (" + string.Join(",", open.Select(a => a.Item1)) + ")", null, open);
            }
            if (folderIds.Count == 0 && (completedStoreIds == null || completedStoreIds.Count == 0)) return;
            // Row ids read from this database: safe to inline.
            string listed = "(id in (" + InlineIds(folderIds) + "))";
            Exec("update folders set listed=1 where " + listed);
            if (completedStoreIds == null || completedStoreIds.Count == 0) return;
            var args = completedStoreIds.Select((s, i) => ("$s" + i, (object?)s)).ToArray();
            Exec("update folders set listed=0 where not " + listed + " and store_id in (" + string.Join(",", args.Select(a => a.Item1)) + ")", null, args);
        }
    }

    /// <summary>
    /// Adds one batch and its bookmark atomically; Message-ID correlates copies without merging them.
    /// </summary>
    public int AddHeaders(ArchiveFolder folder, IReadOnlyList<ArchiveHeader> rows, bool folderDone, bool verified = false, bool canDeleteAbsent = true, string? requestId = null)
    {
        lock (_gate)
        {
            string digest = RequestDigest(new { folder.Id, folder.StoreId, folder.EntryId, rows, folderDone, verified, canDeleteAbsent });
            if (ReadReceipt(requestId, nameof(AddHeaders), digest) is { } receipt)
            {
                var current = GetOrAddFolder(folder.StoreId, folder.EntryId, string.Empty);
                folder.OldestUtc = current.OldestUtc;
                folder.NewestUtc = current.NewestUtc;
                folder.Done = current.Done;
                return int.Parse(receipt, System.Globalization.CultureInfo.InvariantCulture);
            }
            RequireIndexCapacity();
            if (Scalar<long>("select count(*) from folders where id=$id and store_id=$s and entry_id=$e", null, ("$id", folder.Id), ("$s", folder.StoreId), ("$e", folder.EntryId)) != 1)
                throw new ArgumentException("The batch folder does not belong to this archive.");
            int added = 0;
            // Recent and historical readers may hold different snapshots of the same folder.
            var currentFolder = GetOrAddFolder(folder.StoreId, folder.EntryId, string.Empty);
            var oldest = currentFolder.OldestUtc;
            var newest = currentFolder.NewestUtc;
            bool done = currentFolder.Done || folderDone;
            using var tx = _db.BeginTransaction();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.EntryId)) throw new ArgumentException("The batch contains a row without an EntryID.");
                if (!row.NotEmail && AddHeader(folder.Id, row, tx)) added++;
                if (row.RefreshBody && !row.NotEmail && NeedsOriginalRefresh(folder.Id, row, tx))
                {
                    long emailId = Scalar<long>("select id from emails where folder_id=$f and entry_id=$e", tx, ("$f", folder.Id), ("$e", row.EntryId));
                    Exec("update emails set body_state=0,retry_utc=$utc,retry_count=0 where id=$id", tx, ("$id", emailId), ("$utc", ToUnix(DateTime.UtcNow)));
                    Exec("delete from body_fts where rowid=$id", tx, ("$id", emailId));
                    Exec("insert into body_fts(rowid,subject,body) values($id,$s,$b)", tx, ("$id", emailId), ("$s", row.Subject), ("$b", row.Preview));
                }
                // A row without a date must not move the bookmark: the backfill would resume from 1970.
                if (row.ReceivedUtc.Year < 1980) continue;
                if (!row.RefreshBody && (oldest == null || row.ReceivedUtc < oldest)) oldest = row.ReceivedUtc;
                if (newest == null || row.ReceivedUtc > newest) newest = row.ReceivedUtc;
            }
            Exec("update folders set oldest=$o, newest=$n, done=$d, error=null, pass=case when $end=1 then $p else pass end, " +
                "verified=case when $end=1 then $v else verified end, absence_verified=case when $end=1 then $a else absence_verified end, cache_store=$cache, verified_utc=case when $v=1 then $utc else verified_utc end where id=$id", tx,
                ("$o", oldest.HasValue ? ToUnix(oldest.Value) : null), ("$n", newest.HasValue ? ToUnix(newest.Value) : null),
                ("$d", done ? 1 : 0), ("$end", folderDone ? 1 : 0), ("$v", verified && folderDone ? 1 : 0), ("$a", verified && folderDone && canDeleteAbsent ? 1 : 0), ("$cache", canDeleteAbsent ? 0 : 1), ("$utc", ToUnix(DateTime.UtcNow)), ("$p", _pass), ("$id", folder.Id));
            SaveReceipt(requestId, nameof(AddHeaders), digest, added.ToString(System.Globalization.CultureInfo.InvariantCulture), tx);
            tx.Commit();
            folder.OldestUtc = oldest;
            folder.NewestUtc = newest;
            folder.Done = done;
            folder.Error = null;
            return added;
        }
    }


    private bool NeedsOriginalRefresh(long folderId, ArchiveHeader row, SqliteTransaction tx) =>
        !row.ModifiedUtc.HasValue || Scalar<long?>("select modified_utc from emails where folder_id=$f and entry_id=$e", tx,
            ("$f", folderId), ("$e", row.EntryId)) != row.ModifiedUtc.Value.Ticks;

    private bool AddHeader(long folderId, ArchiveHeader row, SqliteTransaction tx)
    {
        string messageId = row.MessageId.Trim();
        // Still where it was: confirmed for this check, and back in the archive if it had been given up as gone.
        var here = Scalar<long?>("select id from emails where folder_id=$f and entry_id=$e", tx, ("$f", folderId), ("$e", row.EntryId));
        if (here != null)
        {
            bool changed = Scalar<long>("select count(*) from emails e left join addresses a on a.id=e.sender_id where e.id=$id and " +
                "(coalesce(e.subject,'')<>$s or e.received<>$d or coalesce(e.size,0)<>$z or coalesce(a.addr,'')<>$a or coalesce(e.sender_name,'')<>$an or e.has_attach<>$h)", tx,
                ("$id", here), ("$s", row.Subject), ("$d", ToUnix(row.ReceivedUtc)), ("$z", row.Size), ("$a", CounterpartyResolver.Normalize(row.SenderAddress)), ("$an", row.SenderName), ("$h", row.HasAttachments ? 1 : 0)) > 0;
            if (changed)
            {
                var sender = AddressId(row.SenderAddress, row.SenderName, tx);
                Exec("delete from email_addr where email_id=$id and role=0", tx, ("$id", here));
                if (sender != null) Exec("insert into email_addr(email_id,addr_id,role) values($id,$a,0)", tx, ("$id", here), ("$a", sender));
                Exec("update emails set subject=$s,subject_norm=$sn,sender_id=$a,sender_name=$an,received=$d,size=$z,has_attach=$h,body_state=0 where id=$id", tx,
                    ("$id", here), ("$s", row.Subject), ("$sn", ArchiveText.NormalizeSubject(row.Subject)), ("$a", sender), ("$an", row.SenderName), ("$d", ToUnix(row.ReceivedUtc)), ("$z", row.Size), ("$h", row.HasAttachments ? 1 : 0));
                Exec("delete from body_fts where rowid=$id", tx, ("$id", here));
                Exec("insert into body_fts(rowid,subject,body) values($id,$s,$b)", tx, ("$id", here), ("$s", row.Subject), ("$b", row.Preview));
                Exec("delete from email_addr where email_id=$id and role<>0", tx, ("$id", here));
            }
            Exec("update emails set seen=$p,present=1, body_state=case when body_state in (3,4) or ($refresh=1 and body_state=1) then 0 else body_state end where id=$id", tx, ("$p", _pass), ("$refresh", row.RefreshBody && NeedsOriginalRefresh(folderId, row, tx) ? 1 : 0), ("$id", here));
            UpdateThreadMetadata(here.Value, row, tx);
            return false;
        }

        string inReplyTo = row.InReplyTo.Trim();
        long? thread = messageId.Length > 0 ? Scalar<long?>("select thread_id from emails where message_id=$m limit 1", tx, ("$m", messageId)) : null;
        // Another copy of this message already has the thread; its parent may not be archived at all.
        if (thread == null && inReplyTo.Length > 0) thread = Scalar<long?>("select thread_id from emails where message_id=$m limit 1", tx, ("$m", inReplyTo));
        if (thread == null && row.ConversationId.Length > 0) thread = Scalar<long?>("select thread_id from emails where conv_id=$c limit 1", tx, ("$c", row.ConversationId));

        long? senderId = AddressId(row.SenderAddress, row.SenderName, tx);
        long id = Scalar<long>("insert into meta(key,value) values('email_sequence',(select coalesce(max(id),0)+1 from emails)) " +
            "on conflict(key) do update set value=max(cast(meta.value as integer),(select coalesce(max(id),0) from emails))+1 returning cast(value as integer)", tx);
        Exec(
            "insert into emails(id,folder_id, entry_id, message_id, conv_id, in_reply_to, thread_id, subject, subject_norm, sender_id, sender_name, received, size, has_attach, preview, seen) " +
            "values($id,$f,$e,$m,$c,$r,$t,$s,$sn,$a,$an,$d,$z,$h,$p,$pass)", tx, ("$id", id), ("$pass", _pass),
            ("$f", folderId), ("$e", row.EntryId), ("$m", messageId.Length > 0 ? messageId : null), ("$c", row.ConversationId.Length > 0 ? row.ConversationId : null),
            ("$r", inReplyTo.Length > 0 ? inReplyTo : null), ("$t", thread ?? 0), ("$s", row.Subject), ("$sn", ArchiveText.NormalizeSubject(row.Subject)),
            ("$a", senderId), ("$an", row.SenderName), ("$d", ToUnix(row.ReceivedUtc)), ("$z", row.Size), ("$h", row.HasAttachments ? 1 : 0), ("$p", row.Preview));

        if (thread == null)
        {
            thread = id;
            Exec("update emails set thread_id=$t where id=$t", tx, ("$t", id));
        }
        // Newest first: the replies to this message may already be there, each in a thread of its own.
        if (messageId.Length > 0)
        {
            Exec("update emails set thread_id=$t where thread_id in (select thread_id from emails where in_reply_to=$m and thread_id<>$t)", tx, ("$t", thread), ("$m", messageId));
        }

        if (senderId != null) Exec("insert or ignore into email_addr(email_id, addr_id, role) values($e,$a,0)", tx, ("$e", id), ("$a", senderId));
        UpdateThreadMetadata(id, row, tx);
        // Searchable at once by subject and preview; the body replaces the preview when the email is opened.
        Exec("insert into body_fts(rowid, subject, body) values($id,$s,$b)", tx, ("$id", id), ("$s", row.Subject), ("$b", row.Preview));
        return true;
    }

    /// <summary>
    /// No full check is running and the last one (or the creation of the archive) is older than <paramref name="every"/>,
    /// or than <see cref="MinCheckGap"/> when one was asked for.
    /// </summary>
    public bool RescanDue(TimeSpan every)
    {
        lock (_gate)
        {
            if (GetMeta(MetaCheckOpen) == "1") return false;
            if (GetMeta(MetaCheckAsked) == "1" && MinCheckGap < every) every = MinCheckGap;
            return !long.TryParse(GetMeta(MetaCheckTime), out var last) || DateTime.UtcNow - FromUnix(last) >= every;
        }
    }

    /// <summary>
    /// An email was not found where the archive has it (moved or deleted): the next full check comes early
    /// instead of leaving it out of reach for up to a week.
    /// </summary>
    public void RequestRescan()
    {
        lock (_gate)
        {
            if (GetMeta(MetaCheckOpen) != "1") SetMeta(MetaCheckAsked, "1");
        }
    }

    /// <summary>
    /// Starts a full check: these folders are read again from the newest email, so that what was moved,
    /// deleted or filed with an old date since the first reading is noticed. Call when all of them are done.
    /// </summary>
    public void BeginRescan(IEnumerable<long> folderIds)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            long pass = _pass + 1;
            Exec("insert or replace into meta(key, value) values($k, $v)", tx, ("$k", MetaPass), ("$v", pass.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Exec("insert or replace into meta(key, value) values($k, '1')", tx, ("$k", MetaCheckOpen));
            Exec("delete from meta where key=$k", tx, ("$k", MetaCheckAsked));
            // Row ids read from this database: safe to inline.
            Exec("update folders set done=0, oldest=null, verified=0, absence_verified=0 where id in (" + InlineIds(folderIds) + ")", tx);
            tx.Commit();
            _pass = pass;
        }
    }

    // Emails of a cache folder (IMAP, OST) whose inventory this check completed without them: it cannot prove them absent.
    private const string UnseenCacheSql = "from emails e join folders f on f.id=e.folder_id where e.seen<$p and e.body_state<>3 " +
        "and ((f.listed=1 and f.done=1 and f.pass=$p and f.verified=1 and f.absence_verified=0) or (f.listed=0 and f.cache_store=1))";

    private bool CheckCanClose() =>
        GetMeta(MetaCheckOpen) == "1" && GetMeta("folder_issues") == null &&
        Scalar<long>("select count(*) from folders where listed=1 and (done=0 or error is not null)") == 0;

    /// <summary>
    /// The emails Outlook must be asked about, one by one, before the check closes: those of cache folders it did not see.
    /// Empty until every folder has been read.
    /// </summary>
    public IReadOnlyList<ArchiveEmailRef> UnseenCacheCopies(int max)
    {
        lock (_gate)
        {
            var result = new List<ArchiveEmailRef>();
            if (!CheckCanClose()) return result;
            using var cmd = Command("select e.id, e.entry_id, f.store_id " + UnseenCacheSql + " order by e.id limit $n", null, ("$p", _pass), ("$n", Math.Max(1, Math.Min(500, max))));
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new ArchiveEmailRef { Id = r.GetInt64(0), EntryId = r.GetString(1), StoreId = r.GetString(2) });
            r.Close();
            AddUnseenSourceChecks(result, max);
            return result;
        }
    }

    /// <summary>
    /// What Outlook said of <see cref="UnseenCacheCopies"/>: <paramref name="gone"/> were not found and leave the archive
    /// when the check closes; <paramref name="kept"/> opened, or could not be asked, and stay.
    /// </summary>
    public void ConfirmCacheCopies(IReadOnlyCollection<long> kept, IReadOnlyCollection<long> gone)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            // Row ids read from this database: safe to inline.
            Exec("update emails set seen=$p where id in (" + InlineIds(kept) + ")", tx, ("$p", _pass));
            Exec("update emails set body_state=3 where id in (select e.id " + UnseenCacheSql + " and e.id in (" + InlineIds(gone) + "))", tx, ("$p", _pass));
            ConfirmSourceChecks(kept, gone, tx);
            tx.Commit();
        }
    }

    /// <summary>
    /// Closes a completed check. Only a verified folder inventory can establish absence, including an empty folder;
    /// in a cache folder only Outlook failing to find the email (<see cref="ConfirmCacheCopies"/>) can.
    /// </summary>
    /// <returns>Emails given up; null while the check cannot close or none is open.</returns>
    public int? FinishRescan()
    {
        lock (_gate)
        {
            if (!CheckCanClose()) return null;
            if (Scalar<long>("select count(*) " + UnseenCacheSql, null, ("$p", _pass)) > 0) return null;
            if (HasUnseenSourceChecks()) return null;
            using var tx = _db.BeginTransaction();
            Exec(
                "update emails set body_state=3 where seen<$p and body_state<>3 and folder_id in (select id from folders where done=1 and pass=$p) " +
                "and folder_id in (select id from folders where absence_verified=1)", tx, ("$p", _pass));
            Exec("update emails set body_state=3 where seen<$p and body_state<>3 and folder_id in (select id from folders where listed=0)", tx, ("$p", _pass));
            int gone = (int)Scalar<long>("select count(*) from emails where body_state=3 and present=1", tx);
            Exec("update emails set present=0 where body_state=3", tx);
            const string absent = "select e.id from emails e join folders f on f.id=e.folder_id where e.present=0 and not exists(select 1 from emails other join folders of on of.id=other.folder_id where other.present=1 and " + CopyIdentitySql + ")";
            QueueDeletedSources(absent, tx);
            Exec("delete from body_fts where rowid in (" + absent + ")", tx);
            Exec("delete from attachments where email_id in (" + absent + ")", tx);
            Exec("delete from email_addr where email_id in (" + absent + ")", tx);
            Exec("delete from emails where id in (" + absent + ")", tx);
            PruneAttachmentContents(tx);
            PruneUnusedAddresses(tx);
            Exec("delete from meta where key=$k", tx, ("$k", MetaCheckOpen));
            Exec("insert or replace into meta(key, value) values($k, $v)", tx, ("$k", MetaCheckTime), ("$v", UnixText(DateTime.UtcNow)));
            Exec("insert or replace into meta(key,value) select 'last_verified',$utc where not exists(select 1 from folders where listed=1 and verified=0)", tx, ("$utc", UnixText(DateTime.UtcNow)));
            tx.Commit();
            return gone;
        }
    }

    public void SetFolderError(long folderId, string error)
    {
        lock (_gate) Exec("update folders set error=$e, verified=0 where id=$id", null, ("$id", folderId), ("$e", error));
    }

    public void ConfirmInitialInventory()
    {
        lock (_gate)
        {
            if (GetMeta("last_verified") != null || GetMeta("folder_issues") != null ||
                Scalar<long>("select count(*) from folders where listed=1") == 0 ||
                Scalar<long>("select count(*) from folders where listed=1 and (done=0 or verified=0 or error is not null)") != 0) return;
            using var tx = _db.BeginTransaction();
            Exec("insert or replace into meta(key,value) values('last_verified',$utc)", tx, ("$utc", UnixText(DateTime.UtcNow)));
            tx.Commit();
        }
    }

    public string? GetBodyVersion(long emailId)
    {
        lock (_gate) return Scalar<string?>("select body_version from emails where id=$id and body_state=1", null, ("$id", emailId));
    }

    /// <summary>
    /// Removes what was indexed from the mailboxes no longer chosen, a slice at a time: up to
    /// <paramref name="maxEmails"/> emails, then their folders and the addresses left without emails.
    /// The folders that stay are read again from the top: an email also present there (same Message-ID)
    /// was recorded once, possibly under a mailbox removed. An empty choice means every mailbox.
    /// </summary>
    /// <returns>Emails and folders removed by this call; 0 when nothing is left to remove.</returns>
    public int RemoveOtherStores(IReadOnlyCollection<string> keepStoreIds, int maxEmails)
    {
        if (keepStoreIds.Count == 0) return 0;
        lock (_gate)
        {
            var args = keepStoreIds.Select((s, i) => ("$s" + i, (object?)s)).ToArray();
            string filter = "store_id collate nocase not in (" + string.Join(",", args.Select(a => a.Item1)) + ")";
            return RemoveStoresSlice(filter, args, maxEmails, tx => RemoveCheckedStoreSources(filter, args, tx));
        }
    }

    /// <summary>
    /// Removes a mailbox no longer open in Outlook (a PST taken out of the profile), a slice at a time like
    /// <see cref="RemoveOtherStores"/>, also when every mailbox is archived. With its folders go the choices about it:
    /// own addresses and contacts refused for its address book. A mailbox Outlook lists again is refused.
    /// </summary>
    /// <returns>Emails and folders removed by this call; 0 when nothing is left to remove.</returns>
    public int RemoveClosedStore(string storeId, int maxEmails)
    {
        lock (_gate)
        {
            var arg = ("$s", (object?)storeId);
            if (Scalar<long>("select count(*) from folders where store_id collate nocase = $s and listed=1", null, arg) > 0)
                throw new InvalidOperationException("La cassetta è aperta in Outlook: chiudila prima di toglierla dall'archivio.");
            return RemoveStoresSlice("store_id collate nocase = $s", new[] { arg }, maxEmails, tx => ForgetStore(storeId, tx));
        }
    }

    /// <summary>One slice: emails of the matching folders, then (with none left) the folders and what only they used.</summary>
    private int RemoveStoresSlice(string folderFilter, (string Name, object? Value)[] args, int maxEmails, Action<SqliteTransaction>? lastly)
    {
        using var tx = _db.BeginTransaction();
        var ids = new List<long>();
        using (var cmd = Command("select id from emails where folder_id in (select id from folders where " + folderFilter + ") limit $n", tx,
                   args.Append(("$n", (object?)Math.Max(1, maxEmails))).ToArray()))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        if (ids.Count > 0)
        {
            // Row ids read from this database: safe to inline.
            string list = " in (" + InlineIds(ids) + ")";
            QueueDeletedSources(InlineIds(ids), tx);
            foreach (var target in new[] { "body_fts where rowid", "attachments where email_id", "email_addr where email_id", "emails where id" })
            {
                Exec("delete from " + target + list, tx);
            }
            PruneAttachmentContents(tx);
            tx.Commit();
            return ids.Count;
        }

        int folders = Count("delete from folders where " + folderFilter, tx, args);
        if (folders > 0)
        {
            Exec("delete from addresses where is_me=0 and id not in (select addr_id from email_addr)", tx);
            Exec("delete from counterparties where id not in (select cp_id from addresses where cp_id is not null)", tx);
            Exec("update folders set done=0, oldest=null", tx);
        }
        lastly?.Invoke(tx);
        tx.Commit();
        return folders;
    }

    private void ForgetStore(string storeId, SqliteTransaction tx)
    {
        string? Meta(string key) => Scalar<string?>("select value from meta where key=$k", tx, ("$k", key));
        // Null: the store had no entry there.
        void Put(string key, string? value)
        {
            if (value != null)
                Exec("insert into meta(key,value) values($k,$v) on conflict(key) do update set value=excluded.value", tx, ("$k", key), ("$v", value));
        }
        RemoveCheckedStoreSources("store_id=$s", new[] { ("$s", (object?)storeId) }, tx);
        Put(StoreOwnAddresses.MetaKey, StoreOwnAddresses.Without(Meta(StoreOwnAddresses.MetaKey), storeId));
    }

    private int Count(string sql, SqliteTransaction tx, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, tx, args);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Row of an address, created with its counterparty on first sight; null for an empty address.</summary>
    private long? AddressId(string? address, string? name, SqliteTransaction tx)
    {
        var addr = CounterpartyResolver.Normalize(address);
        if (addr.Length == 0) return null;

        var existing = Scalar<long?>("select id from addresses where addr=$a", tx, ("$a", addr));
        if (existing != null) return existing;

        long? counterparty = _ownAddresses.Contains(addr) ? null : CounterpartyFor(CounterpartyResolver.KeyOf(addr), name, tx);
        return Scalar<long>("insert into addresses(addr, name, cp_id, is_me) values($a,$n,$c,$me) returning id", tx,
            ("$a", addr), ("$n", string.IsNullOrWhiteSpace(name) ? null : name!.Trim()), ("$c", counterparty), ("$me", counterparty == null ? 1 : 0));
    }

    /// <summary>
    /// Emails whose body has not been read yet, newest first.
    /// </summary>
    public IReadOnlyList<ArchiveEmailRef> NextPendingBodies(int max)
    {
        lock (_gate)
        {
            var result = new List<ArchiveEmailRef>();
            using var cmd = Command("select e.id, e.entry_id, f.store_id from emails e join folders f on f.id=e.folder_id where (e.body_state=0 or (e.body_state=4 and e.retry_utc<=$now) or (e.body_state=1 and e.retry_utc<=$now and exists(select 1 from attachments where email_id=e.id and state='retryable')) or (e.body_state=1 and (exists(select 1 from attachments where email_id=e.id and problem='reader-revision') ))) order by (e.retry_utc is null),e.retry_utc desc,e.received desc limit $n", null, ("$n", Math.Max(1, Math.Min(500, max))), ("$now", ToUnix(DateTime.UtcNow)));
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new ArchiveEmailRef { Id = r.GetInt64(0), EntryId = r.GetString(1), StoreId = r.GetString(2) });
            return result;
        }
    }

    /// <summary>Stores the whole body, recipients and deduplicated attachment text atomically.</summary>
    public void SaveBody(long emailId, ArchiveBody body, string? requestId = null)
    {
        lock (_gate)
        {
            string digest = RequestDigest(new { emailId, body });
            if (ReadReceipt(requestId, nameof(SaveBody), digest) != null) return;
            RequireIndexCapacity();
            using var tx = _db.BeginTransaction();
            if (!SaveBodyIn(tx, emailId, body)) return;
            SaveReceipt(requestId, nameof(SaveBody), digest, string.Empty, tx);
            tx.Commit();
        }
    }

    /// <summary>
    /// Several bodies in one transaction: the commit (fsync) is the dominant cost of a single save, so a group pays it once.
    /// All or nothing; a caller that sees it fail saves the bodies one by one to isolate the bad one.
    /// </summary>
    public void SaveBodies(IReadOnlyList<ArchiveBodyItem> items, string? requestId = null)
    {
        lock (_gate)
        {
            string digest = RequestDigest(items);
            if (ReadReceipt(requestId, nameof(SaveBodies), digest) != null) return;
            RequireIndexCapacity();
            using var tx = _db.BeginTransaction();
            foreach (var item in items) SaveBodyIn(tx, item.EmailId, item.Body);
            SaveReceipt(requestId, nameof(SaveBodies), digest, string.Empty, tx);
            tx.Commit();
        }
    }

    /// <returns>False when the email no longer exists or was deleted: nothing was written.</returns>
    private bool SaveBodyIn(SqliteTransaction tx, long emailId, ArchiveBody body)
    {
        {
            if (body.Attachments.Any(a => a.State == "indexed" && a.ReaderRevision != ArchiveAttachment.CurrentReaderRevision))
                throw new InvalidOperationException("Il testo degli allegati proviene da una revisione del lettore superata; rileggere gli originali.");
            if (Scalar<long>("select count(*) from emails where id=$id and body_state<>3", tx, ("$id", emailId)) == 0) return false;
            // Without a body (headers only, encrypted) subject and preview stay searchable.
            if (!body.Unavailable)
            {
                using var hash = SHA256.Create();
                var version = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    body.Text,
                    body.ModifiedUtc,
                    body.SourceVersion,
                    body.RecipientsLimited,
                    body.AttachmentsLimited,
                    recipients = body.Recipients.OrderBy(r => r.Address, StringComparer.Ordinal).ThenBy(r => r.IsCc).Select(r => new { r.Address, r.Name, r.IsCc }),
                    attachments = body.AttachmentNames.OrderBy(n => n, StringComparer.Ordinal),
                    contents = body.Attachments.OrderBy(a => a.SourceId, StringComparer.Ordinal).Select(a => ArchiveAttachmentReader.IsReceipt(a.Name)
                        ? new ArchiveAttachment { SourceId = a.SourceId, Name = a.Name, State = "excluded", Problem = "inline-or-receipt", ReaderRevision = a.ReaderRevision }
                        : a)
                }))));
                var previous = Scalar<string?>("select body_version from emails where id=$id", tx, ("$id", emailId));
                Exec("update emails set body_version=$v,text_limited=$limited,full_body=$body,source_version=$source,modified_utc=$modified where id=$id", tx,
                    ("$id", emailId), ("$v", version), ("$body", body.Text), ("$source", body.SourceVersion), ("$modified", body.ModifiedUtc?.Ticks),
                    ("$limited", body.Text.Length > Security.AttachmentTextExtractor.WholeDocumentChars ? 1 : 0));
                var subject = Scalar<string?>("select subject from emails where id=$id", tx, ("$id", emailId)) ?? string.Empty;
                var text = body.Text;
                Exec("delete from body_fts where rowid=$id", tx, ("$id", emailId));
                Exec("insert into body_fts(rowid, subject, body) values($id,$s,$b)", tx, ("$id", emailId), ("$s", subject), ("$b", text));
                // A short opening for the result list, separate from the full encrypted original.
                Exec("update emails set preview=$p where id=$id", tx, ("$p", text.Length > PreviewChars ? text.Substring(0, PreviewChars) : text), ("$id", emailId));
            }

            Exec("delete from email_addr where email_id=$id and role<>0", tx, ("$id", emailId));
            foreach (var recipient in body.Recipients)
            {
                var addressId = AddressId(recipient.Address, recipient.Name, tx);
                if (addressId == null) continue;
                Exec("insert or ignore into email_addr(email_id, addr_id, role) values($e,$a,$r)", tx, ("$e", emailId), ("$a", addressId), ("$r", recipient.IsCc ? 2 : 1));
                Exec("update counterparties set two_way=1 where two_way=0 and id=(select cp_id from addresses where id=$a)", tx, ("$a", addressId));
            }
            long attempts = Scalar<long>("select retry_count from emails where id=$id", tx, ("$id", emailId));
            bool retry = body.Retryable || body.Attachments.Any(a => a.State == "retryable");
            // A source that keeps failing (extractor missing, a file that always times out) stops being re-read.
            bool giveUp = retry && attempts >= MaxRetries;
            Exec("delete from attachments where email_id=$id", tx, ("$id", emailId));
            foreach (var attachment in body.Attachments.Count > 0 ? body.Attachments : body.AttachmentNames.Select((name, i) => new ArchiveAttachment { Name = name, SourceId = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList())
            {
                bool limited = attachment.TextLimited || attachment.State == "limited";
                string state = _indexAttachmentText == false ? "excluded" : giveUp && attachment.State == "retryable" ? "unavailable" : attachment.State;
                string? problem = state == "excluded" && _indexAttachmentText == false ? "disabled" : attachment.Problem;
                string? contentHash = null;
                if (_indexAttachmentText != false && attachment.Text.Length > 0 && state is "indexed" or "limited" or "facts-only")
                {
                    using var contentDigest = SHA256.Create();
                    contentHash = attachment.Version ?? Convert.ToBase64String(contentDigest.ComputeHash(Encoding.UTF8.GetBytes(attachment.Text)));
                    Exec("insert into attachment_contents(hash,text,reader_revision) values($hash,$text,$rev) on conflict(hash) do update set text=excluded.text,reader_revision=excluded.reader_revision where attachment_contents.reader_revision<>excluded.reader_revision or length(excluded.text)>length(attachment_contents.text)", tx,
                        ("$hash", contentHash), ("$text", attachment.Text), ("$rev", attachment.ReaderRevision));
                }
                Exec("insert into attachments(email_id,name,source_id,version,state,problem,text_limited,content_hash,reader_revision,size,type) values($id,$n,$s,$v,$state,$p,$l,$hash,$rev,$size,$type)", tx,
                    ("$id", emailId), ("$n", attachment.Name), ("$s", attachment.SourceId), ("$v", attachment.Version), ("$state", state), ("$p", problem), ("$l", limited && _indexAttachmentText != false ? 1 : 0), ("$hash", contentHash), ("$rev", attachment.ReaderRevision), ("$size", attachment.Size), ("$type", attachment.Type));
                long row = Scalar<long>("select last_insert_rowid()", tx);
                string terms = contentHash != null && state != "facts-only" ? attachment.Text : string.Empty;
                Exec("insert into attachment_fts(rowid,name,body) values($r,$n,$b)", tx, ("$r", row), ("$n", attachment.Name), ("$b", terms));
            }
            PruneAttachmentContents(tx);
            bool again = retry && !giveUp;
            Exec("update emails set body_state=$s,recipients_limited=$r,attachments_limited=$a,retry_utc=$retry,retry_count=$n where id=$id", tx,
                ("$s", (int)(body.Unavailable ? body.Retryable && !giveUp ? ArchiveBodyState.Retryable : ArchiveBodyState.Unavailable : ArchiveBodyState.Indexed)),
                ("$r", body.RecipientsLimited ? 1 : 0), ("$a", body.AttachmentsLimited ? 1 : 0), ("$retry", again ? NextRetry(attempts) : null), ("$n", again ? attempts + 1 : 0), ("$id", emailId));
            return true;
        }
    }


    private static string RequestDigest<T>(T value)
    {
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private string? ReadReceipt(string? id, string operation, string digest)
    {
        if (id == null) return null;
        if (ArchivePipe.BatchIdentity(id, out var peer, out long sequence) && ConfirmedRanges(peer).Any(r => r.First <= sequence && sequence <= r.Last))
            throw new InvalidOperationException("This batch was already acknowledged; it cannot be applied again.");
        using var command = Command("select operation,digest,result from rpc_batches where id=$id", null, ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != operation || reader.GetString(1) != digest)
            throw new InvalidDataException("A batch request ID was reused with different content.");
        return reader.GetString(2);
    }

    private sealed class BatchRange
    {
        public long First { get; set; }
        public long Last { get; set; }
    }

    private List<BatchRange> ConfirmedRanges(string peer)
    {
        var ranges = JsonSerializer.Deserialize<List<BatchRange>>(GetMeta("rpc_ack:" + peer) ?? "[]");
        if (ranges == null || ranges.Any(r => r.First <= 0 || r.Last < r.First)) throw new InvalidDataException("Unreadable archive receipt confirmations.");
        return ranges;
    }

    internal void AcknowledgeBatches(string peer, IReadOnlyList<long> sequences)
    {
        if (!Guid.TryParseExact(peer, "N", out _) || sequences.Count > 32 || sequences.Any(n => n <= 0))
            throw new ArgumentException("Invalid batch acknowledgement.");
        lock (_gate)
        {
            var ranges = ConfirmedRanges(peer);
            ranges.AddRange(sequences.Select(n => new BatchRange { First = n, Last = n }));
            var merged = new List<BatchRange>();
            foreach (var range in ranges.OrderBy(r => r.First))
            {
                if (merged.Count > 0 && range.First - 1 <= merged[merged.Count - 1].Last)
                    merged[merged.Count - 1].Last = Math.Max(merged[merged.Count - 1].Last, range.Last);
                else merged.Add(range);
            }
            using var tx = _db.BeginTransaction();
            foreach (long sequence in sequences)
                Exec("delete from rpc_batches where id=$id", tx, ("$id", peer + ":" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Exec("insert or replace into meta(key,value) values($k,$v)", tx, ("$k", "rpc_ack:" + peer), ("$v", JsonSerializer.Serialize(merged)));
            TouchPeer(peer, tx);
            tx.Commit();
        }
    }

    private void SaveReceipt(string? id, string operation, string digest, string result, SqliteTransaction tx)
    {
        if (id == null) return;
        Exec("insert into rpc_batches(id,operation,digest,result) values($id,$op,$hash,$result)", tx,
            ("$id", id), ("$op", operation), ("$hash", digest), ("$result", result));
        if (ArchivePipe.BatchIdentity(id, out var peer, out _)) TouchPeer(peer, tx);
    }

    private void TouchPeer(string peer, SqliteTransaction tx) =>
        Exec("insert or replace into meta(key,value) values($k,$v)", tx, ("$k", "rpc_time:" + peer), ("$v", UnixText(DateTime.UtcNow)));

    /// <summary>A receipt guards a retry from the same Outlook session; a peer idle this long has ended.</summary>
    internal static readonly TimeSpan PeerIdleLimit = TimeSpan.FromDays(7);

    internal void PruneIdlePeers(DateTime nowUtc)
    {
        lock (_gate)
        {
            var idle = new List<string>();
            using (var command = Command("select substr(key,10) from meta where key like 'rpc_time:%' and cast(value as integer) < $cutoff", null, ("$cutoff", ToUnix(nowUtc - PeerIdleLimit))))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) idle.Add(reader.GetString(0));
            if (idle.Count == 0) return;
            using var tx = _db.BeginTransaction();
            foreach (var peer in idle)
            {
                Exec("delete from rpc_batches where substr(id,1,33)=$p", tx, ("$p", peer + ":"));
                Exec("delete from meta where key in ($a,$t)", tx, ("$a", "rpc_ack:" + peer), ("$t", "rpc_time:" + peer));
            }
            tx.Commit();
        }
    }

    /// <summary>After this many failed reads a retryable source is marked unavailable; a refresh starts over.</summary>
    internal const int MaxRetries = 8;

    // 5 minutes, doubling: about 21 hours over all attempts.
    private static long NextRetry(long attempts) => ToUnix(DateTime.UtcNow.AddMinutes(5 * Math.Pow(2, Math.Min(attempts, MaxRetries))));

    public void SetBodyState(long emailId, ArchiveBodyState state)
    {
        lock (_gate)
        {
            long attempts = Scalar<long?>("select retry_count from emails where id=$id", null, ("$id", emailId)) ?? 0;
            if (state == ArchiveBodyState.Retryable && attempts >= MaxRetries) state = ArchiveBodyState.Unavailable;
            bool again = state == ArchiveBodyState.Retryable;
            Exec("update emails set body_state=$s,retry_utc=$r,retry_count=$n where id=$id", null, ("$s", (int)state), ("$id", emailId),
                ("$r", again ? NextRetry(attempts) : null), ("$n", again ? attempts + 1 : 0));
        }
    }

    /// <summary>Emails matching the query: by relevance when it has text, otherwise newest first.</summary>
    public IReadOnlyList<ArchiveHit> Search(ArchiveQuery query)
    {
        var codes = ArchiveSearchText.Codes(query.Text);
        if (codes.Count > 100) throw new ArgumentException("Troppi codici nella ricerca: restringi la richiesta a 100 termini al massimo.");
        var fts = codes.Count == 0 ? ArchiveText.BuildFtsQuery(query.Text) : "";
        var sql = new StringBuilder("select e.id, e.entry_id, f.store_id, e.subject, e.sender_name, a.addr, e.received, e.preview, e.thread_id,e.body_state,e.body_version,e.text_limited,e.recipients_limited,e.attachments_limited,e.message_id,e.sent_utc,exists(select 1 from archive_search_jobs j where j.email_id=e.id) from ");
        if (codes.Count > 0)
        {
            var sets = codes.Select((code, i) => "select rowid from (" + CodeCandidates(code, "$code" + i, query) + ")");
            sql.Append("(select rowid from (").Append(string.Join(" union ", sets)).Append(")) m join emails e on e.id=m.rowid ");
        }
        else if (fts.Length > 0)
        {
            var sources = new List<string>
            {
                "select rowid, bm25(body_fts, 4.0, 1.0) as score from body_fts where body_fts match $q"
            };
            if (query.IncludeAttachmentText)
                sources.Add("select a.email_id, bm25(attachment_fts,2.0,1.0) from attachment_fts join attachments a on a.rowid=attachment_fts.rowid join emails ae on ae.id=a.email_id where attachment_fts match $q and ae.body_state=1");
            sql.Append("(select rowid, sum(score) as score from (").Append(string.Join(" union all ", sources))
                .Append(" limit -1 offset 0) group by rowid) m join emails e on e.id=m.rowid ");
        }
        else sql.Append("emails e ");
        sql.Append("join folders f on f.id=e.folder_id left join addresses a on a.id=e.sender_id where e.body_state<>3 ");
        var args = new List<(string, object?)>();
        if (fts.Length > 0) args.Add(("$q", fts));
        for (int i = 0; i < codes.Count; i++)
        {
            args.Add(("$code" + i, codes[i].ToUpperInvariant()));
            if (codes[i].Length >= 3) args.Add(("$code" + i + "fts", ArchiveSearchText.Literal(codes[i])));
        }
        if (query.AllowedStoreIds != null)
        {
            var stores = query.AllowedStoreIds.Select((store, i) => { args.Add(("$store" + i, store)); return "$store" + i; }).ToArray();
            sql.Append("and f.store_id in (").Append(stores.Length == 0 ? "null" : string.Join(",", stores)).Append(") ");
        }
        if (query.EmailIds != null)
        {
            // Numeric row ids, never SQL text supplied by the caller.
            sql.Append("and e.id in (").Append(query.EmailIds.Count > 0 ? string.Join(",", query.EmailIds) : "null").Append(") ");
        }
        if (query.EmailRefs != null)
        {
            if (query.EmailRefs.Count > 500) throw new ArgumentException("Troppi riferimenti email: massimo 500 per richiesta.");
            var refs = new List<string>();
            for (int i = 0; i < query.EmailRefs.Count; i++)
            {
                refs.Add($"(e.entry_id=$refEntry{i} and f.store_id=$refStore{i})");
                args.Add(("$refEntry" + i, query.EmailRefs[i].EntryId));
                args.Add(("$refStore" + i, query.EmailRefs[i].StoreId));
            }
            sql.Append("and (").Append(refs.Count == 0 ? "0" : string.Join(" or ", refs)).Append(") ");
        }
        string role = query.Role switch { ArchivePartyRole.Sender => "and ea.role=0", ArchivePartyRole.Recipient => "and ea.role<>0", _ => string.Empty };
        if (query.CounterpartyId.HasValue)
        {
            sql.Append("and e.id in (select ea.email_id from email_addr ea join addresses x on x.id=ea.addr_id where x.cp_id=$cp ").Append(role).Append(") ");
            args.Add(("$cp", query.CounterpartyId.Value));
        }
        if (query.AddressIds != null)
        {
            // Row ids read from this database: safe to inline.
            sql.Append("and e.id in (select ea.email_id from email_addr ea where ea.addr_id in (").Append(query.AddressIds.Count > 0 ? string.Join(",", query.AddressIds) : "null").Append(") ").Append(role).Append(") ");
        }
        if (!string.IsNullOrWhiteSpace(query.Subject))
        {
            sql.Append("and e.subject like $subject escape '\\' ");
            args.Add(("$subject", LikePattern(query.Subject)));
        }
        if (query.FromUtc.HasValue)
        {
            sql.Append("and e.received>=$from ");
            args.Add(("$from", ToUnix(query.FromUtc.Value)));
        }
        if (query.ToUtc.HasValue)
        {
            sql.Append("and e.received<=$to ");
            args.Add(("$to", ToUnix(query.ToUtc.Value)));
        }
        AppendFactFilters(sql, args, query);
        sql.Append(fts.Length > 0 ? "order by m.score, e.id desc " : "order by e.received desc, e.id desc ").Append("limit $n offset $offset");
        args.Add(("$n", Math.Max(1, Math.Min(500, query.Max))));
        args.Add(("$offset", Math.Max(0, query.Offset)));

        lock (_gate)
        {
            var result = new List<ArchiveHit>();
            using var cmd = Command(sql.ToString(), null, args.ToArray());
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new ArchiveHit
                {
                    Id = r.GetInt64(0),
                    EntryId = r.GetString(1),
                    StoreId = r.GetString(2),
                    Subject = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                    SenderName = r.IsDBNull(4) ? string.Empty : r.GetString(4),
                    SenderAddress = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                    ReceivedUtc = FromUnix(r.GetInt64(6)),
                    Preview = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                    ThreadId = r.GetInt64(8),
                    BodyState = (ArchiveBodyState)r.GetInt32(9),
                    BodyVersion = r.IsDBNull(10) ? null : r.GetString(10),
                    TextLimited = r.GetInt64(11) != 0,
                    RecipientsLimited = r.GetInt64(12) != 0,
                    AttachmentsLimited = r.GetInt64(13) != 0,
                    MessageId = r.IsDBNull(14) ? string.Empty : r.GetString(14),
                    SentUtc = r.IsDBNull(15) ? null : FromUnix(r.GetInt64(15)),
                    SearchIndexPending = r.GetInt64(16) != 0
                });
            }
            r.Close();
            foreach (var hit in result)
            {
                hit.Attachments.AddRange(AttachmentMetadata(hit.Id));
                if (query.IncludeFacts) hit.Facts.AddRange(GetFacts(hit.Id, query.IncludeAttachmentText));
            }
            return result;
        }
    }

    private IReadOnlyList<ArchiveAttachment> AttachmentMetadata(long emailId)
    {
        var result = new List<ArchiveAttachment>();
        using var command = Command("select source_id,name,version,state,problem,text_limited,reader_revision,size,type from attachments where email_id=$id order by rowid", null, ("$id", emailId));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new ArchiveAttachment { SourceId = reader.IsDBNull(0) ? "" : reader.GetString(0), Name = reader.GetString(1), Version = reader.IsDBNull(2) ? null : reader.GetString(2), State = reader.GetString(3), Problem = reader.IsDBNull(4) ? null : reader.GetString(4), TextLimited = reader.GetInt64(5) != 0, ReaderRevision = reader.GetInt32(6), Size = reader.IsDBNull(7) ? null : reader.GetInt64(7), Type = reader.GetString(8) });
        return result;
    }

    /// <summary>"Contains" pattern for LIKE; the user's "*" and "?" become wildcards.</summary>
    private static string LikePattern(string? text) =>
        "%" + (text ?? string.Empty).Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace('*', '%').Replace('?', '_') + "%";

    /// <summary>
    /// Addresses of a person or company: those whose address or display name contains the text, plus every
    /// address of the counterparties whose name or domain does. The user's own addresses are left out.
    /// </summary>
    public IReadOnlyList<long> FindAddresses(string? text, int max = 500)
    {
        lock (_gate)
        {
            var result = new List<long>();
            using var cmd = Command(
                "select a.id from addresses a left join counterparties c on c.id=a.cp_id where a.is_me=0 and " +
                "(a.addr like $p escape '\\' or a.name like $p escape '\\' or c.name like $p escape '\\' or c.key like $p escape '\\') limit $n",
                null, ("$p", LikePattern(text)), ("$n", Math.Max(1, max)));
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetInt64(0));
            return result;
        }
    }

    /// <summary>Counterparty an address belongs to; null for an unknown or own address.</summary>
    public long? CounterpartyOf(string? address)
    {
        lock (_gate) return Scalar<long?>("select cp_id from addresses where addr=$a", null, ("$a", CounterpartyResolver.Normalize(address)));
    }
}
