using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
    private const string CopyIdentitySql = "((coalesce(e.message_id,'')<>'' and other.message_id=e.message_id) or (coalesce(e.message_id,'')='' and coalesce(other.message_id,'')='' and other.entry_id=e.entry_id and of.store_id=f.store_id))";

    private void PruneUnusedAddresses(SqliteTransaction tx)
    {
        Exec("delete from addresses where is_me=0 and id not in (select addr_id from email_addr)", tx);
        Exec("delete from counterparties where important=0 and id not in (select cp_id from addresses where cp_id is not null)", tx);
    }
    private void QueueDeletedSources(string ids, SqliteTransaction tx)
    {
        long previous = Scalar<long>("select coalesce(max(id),0) from archive_deletions", tx);
        Exec("insert into archive_deletions(profile,store_id,entry_id,message_id,deleted_ticks) " +
            "select coalesce((select value from meta where key='profile'),''),f.store_id,e.entry_id,coalesce(e.message_id,''),$time " +
            "from emails e join folders f on f.id=e.folder_id where e.id in (" + ids + ") and " +
            "not exists(select 1 from emails other join folders of on of.id=other.folder_id where other.id not in (" + ids + ") and other.present=1 and " + CopyIdentitySql + ")",
            tx, ("$time", DateTime.UtcNow.Ticks));
        Exec("delete from archive_source_checks as s where exists(select 1 from archive_deletions d where d.id>$previous and " +
            "((d.message_id<>'' and s.message_id=d.message_id) or (d.message_id='' and s.message_id='' and s.store_id=d.store_id and s.entry_id=d.entry_id)))", tx,
            ("$previous", previous));
    }

    private const string UnseenSourceChecksSql = "from archive_source_checks s where s.checked_pass<>$p " +
        "and exists(select 1 from folders f where f.store_id=s.store_id and f.listed<>2) " +
        "and not exists(select 1 from emails e join folders f on f.id=e.folder_id where f.store_id=s.store_id and e.entry_id=s.entry_id and coalesce(e.message_id,'')=s.message_id)";

    private void AddUnseenSourceChecks(List<ArchiveEmailRef> result, int max)
    {
        using var cmd = Command("select s.id,s.entry_id,s.store_id " + UnseenSourceChecksSql + " order by s.id limit $max", null,
            ("$p", _pass), ("$max", Math.Max(0, Math.Min(500, max) - result.Count)));
        using var rows = cmd.ExecuteReader();
        while (rows.Read()) result.Add(new ArchiveEmailRef { Id = -rows.GetInt64(0), EntryId = rows.GetString(1), StoreId = rows.GetString(2) });
    }

    private bool HasUnseenSourceChecks()
    {
        return Scalar<long>("select count(*) " + UnseenSourceChecksSql, null, ("$p", _pass)) > 0;
    }

    private void ConfirmSourceChecks(IReadOnlyCollection<long> kept, IReadOnlyCollection<long> gone, SqliteTransaction tx)
    {
        var missing = new List<long>();
        var checkedIds = new List<long>();
        foreach (long id in kept) if (id < 0 && id != long.MinValue) checkedIds.Add(-id);
        foreach (long id in gone) if (id < 0 && id != long.MinValue) { checkedIds.Add(-id); missing.Add(-id); }
        if (checkedIds.Count == 0) return;
        Exec("insert into archive_deletions(profile,store_id,entry_id,message_id,deleted_ticks) " +
            "select coalesce((select value from meta where key='profile'),''),s.store_id,s.entry_id,s.message_id,$time " + UnseenSourceChecksSql +
            " and s.id in (" + InlineIds(missing) + ") and not exists(select 1 from emails e join folders f on f.id=e.folder_id where e.present=1 and " +
            "((s.message_id<>'' and e.message_id=s.message_id) or (s.message_id='' and coalesce(e.message_id,'')='' and f.store_id=s.store_id and e.entry_id=s.entry_id)))", tx,
            ("$p", _pass), ("$time", DateTime.UtcNow.Ticks));
        Exec("update archive_source_checks set checked_pass=$p where id in (" + InlineIds(checkedIds) + ")", tx, ("$p", _pass));
        Exec("delete from archive_source_checks where id in (" + InlineIds(missing) + ")", tx);
    }

    private void RemoveCheckedStoreSources(string filter, (string Name, object? Value)[] args, SqliteTransaction tx)
    {
        Exec("insert into archive_deletions(profile,store_id,entry_id,message_id,deleted_ticks) select " +
            "coalesce((select value from meta where key='profile'),''),s.store_id,s.entry_id,s.message_id,$time from archive_source_checks s where (" + filter + ") " +
            "and not exists(select 1 from emails e join folders f on f.id=e.folder_id where e.present=1 and ((s.message_id<>'' and e.message_id=s.message_id) or " +
            "(s.message_id='' and coalesce(e.message_id,'')='' and f.store_id=s.store_id and e.entry_id=s.entry_id)))", tx,
            args.Append(("$time", (object?)DateTime.UtcNow.Ticks)).ToArray());
        Exec("delete from archive_source_checks where " + filter, tx, args);
    }

    public IReadOnlyList<ArchiveDeletedSource> PendingDeletions(int max = 100)
    {
        lock (_gate)
        {
            var result = new List<ArchiveDeletedSource>();
            using var cmd = Command("select id,profile,store_id,entry_id,message_id,deleted_ticks from archive_deletions order by id limit $max", null, ("$max", Math.Max(1, Math.Min(1000, max))));
            using var rows = cmd.ExecuteReader();
            while (rows.Read()) result.Add(new ArchiveDeletedSource { Id = rows.GetInt64(0), OutlookProfile = rows.GetString(1), StoreId = rows.GetString(2),
                EntryId = rows.GetString(3), MessageId = rows.GetString(4), DeletedUtc = new DateTime(rows.GetInt64(5), DateTimeKind.Utc) });
            return result;
        }
    }

    /// <summary>Do not acknowledge until both history invalidation and WAL truncation have succeeded.</summary>
    internal bool CompleteDeletions(IReadOnlyCollection<long> ids)
    {
        lock (_gate)
        {
            using (var checkpoint = Command("pragma wal_checkpoint(TRUNCATE)", null))
            using (var row = checkpoint.ExecuteReader())
                if (!row.Read() || row.GetInt64(0) != 0) return false;
            Exec("delete from archive_deletions where id in (" + InlineIds(ids) + ")");
            return true;
        }
    }
}
