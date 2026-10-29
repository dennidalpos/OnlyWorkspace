using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
    // Emails sent by or to any address of the counterparty $c, without those given up as gone.
    private const string PartyEmails =
        "(select distinct ea.email_id as id from email_addr ea join addresses a on a.id=ea.addr_id where a.cp_id=$c) ce " +
        "join emails e on e.id=ce.id and e.body_state<>3 join folders f on f.id=e.folder_id ";

    /// <summary>Counterparty of an indexed email: its sender, or the first recipient when the user sent it. Null when unknown.</summary>
    public long? CounterpartyOfEmail(string entryId, string? storeId = null)
    {
        lock (_gate)
        {
            return Scalar<long?>(
                "select a.cp_id from emails e join folders f on f.id=e.folder_id join email_addr ea on ea.email_id=e.id join addresses a on a.id=ea.addr_id " +
                "where e.entry_id=$e and ($s is null or f.store_id=$s) and e.body_state<>3 and a.cp_id is not null order by ea.role limit 1", null, ("$e", entryId), ("$s", storeId));
        }
    }

    /// <summary>Important counterparties open the lists.</summary>
    public void SetImportant(long counterpartyId, bool important)
    {
        lock (_gate) Exec("update counterparties set important=$v where id=$c", null, ("$v", important ? 1 : 0), ("$c", counterpartyId));
    }

    /// <summary>Counterparty key of an address: the user's choice for the address, then for its automatic key, then the automatic key.</summary>
    private string KeyFor(string addr, SqliteTransaction? tx)
    {
        var key = CounterpartyResolver.KeyOf(addr);
        return Scalar<string?>("select target from party_choice where source=$s", tx, ("$s", addr))
               ?? (key != addr ? Scalar<string?>("select target from party_choice where source=$s", tx, ("$s", key)) : null)
               ?? key;
    }

    /// <summary>Row of the counterparty with this key, created on first use.</summary>
    private long CounterpartyFor(string key, string? displayName, SqliteTransaction? tx)
    {
        Exec("insert or ignore into counterparties(key, name) values($k,$n)", tx, ("$k", key), ("$n", CounterpartyResolver.DefaultName(key, displayName)));
        return Scalar<long>("select id from counterparties where key=$k", tx, ("$k", key));
    }

    /// <summary>The user has written to this counterparty.</summary>
    internal bool IsTwoWay(long counterpartyId)
    {
        lock (_gate) return Scalar<long?>("select two_way from counterparties where id=$c", null, ("$c", counterpartyId)) == 1;
    }

    private void RefreshTwoWay(SqliteTransaction tx, params long[] ids)
    {
        foreach (long id in ids)
        {
            Exec("update counterparties set two_way=exists(select 1 from addresses a join email_addr r on r.addr_id=a.id where a.cp_id=$c and r.role<>0) where id=$c", tx, ("$c", id));
        }
    }

    /// <summary>
    /// The user says two counterparties are one (a company with two domains, a person's private address):
    /// every address of <paramref name="otherId"/> goes under <paramref name="intoId"/>, and so will the ones met later.
    /// </summary>
    public bool MergeCounterparties(long intoId, long otherId)
    {
        lock (_gate)
        {
            var into = Scalar<string?>("select key from counterparties where id=$c", null, ("$c", intoId));
            var other = Scalar<string?>("select key from counterparties where id=$c", null, ("$c", otherId));
            if (into == null || other == null || intoId == otherId) return false;

            using var tx = _db.BeginTransaction();
            Exec("update party_choice set target=$t where target=$o", tx, ("$t", into), ("$o", other));
            Exec("insert or replace into party_choice(source, target) values($o,$t)", tx, ("$o", other), ("$t", into));
            Exec("update addresses set cp_id=$t where cp_id=$o", tx, ("$t", intoId), ("$o", otherId));
            Exec("update counterparties set important=max(important, (select important from counterparties where id=$o)) where id=$t", tx, ("$t", intoId), ("$o", otherId));
            Exec("delete from counterparties where id=$o", tx, ("$o", otherId));
            RefreshTwoWay(tx, intoId);
            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// The user says an address does not belong with the others of its domain: it becomes a counterparty of its own.
    /// </summary>
    /// <returns>The new counterparty; null when the address is not one of this counterparty's or is the only one.</returns>
    public long? SplitAddress(long counterpartyId, string? address)
    {
        var addr = CounterpartyResolver.Normalize(address);
        lock (_gate)
        {
            var key = Scalar<string?>("select key from counterparties where id=$c", null, ("$c", counterpartyId));
            // The address a counterparty is named after stays: the others can leave it.
            if (key == null || addr.Length == 0 || addr == key) return null;
            if (Scalar<long?>("select id from addresses where addr=$a and cp_id=$c", null, ("$a", addr), ("$c", counterpartyId)) == null) return null;
            if (Scalar<long>("select count(*) from addresses where cp_id=$c", null, ("$c", counterpartyId)) < 2) return null;

            using var tx = _db.BeginTransaction();
            Exec("insert or replace into party_choice(source, target) values($a,$a)", tx, ("$a", addr));
            long own = CounterpartyFor(addr, Scalar<string?>("select name from addresses where addr=$a", tx, ("$a", addr)), tx);
            Exec("update addresses set cp_id=$n where addr=$a", tx, ("$n", own), ("$a", addr));
            RefreshTwoWay(tx, counterpartyId, own);
            tx.Commit();
            return own;
        }
    }

    /// <summary>Forgets the user's merges and splits that shaped this counterparty: its addresses go back to the automatic grouping.</summary>
    /// <returns>The counterparty to show afterwards: this one, or the one its addresses went to when nothing is left here.</returns>
    public long ResetCounterparty(long counterpartyId)
    {
        lock (_gate)
        {
            var key = Scalar<string?>("select key from counterparties where id=$c", null, ("$c", counterpartyId));
            if (key == null) return counterpartyId;

            using var tx = _db.BeginTransaction();
            Exec("delete from party_choice where target=$k or source in (select addr from addresses where cp_id=$c)", tx, ("$k", key), ("$c", counterpartyId));
            var addresses = new List<(long Id, string Addr, string? Name)>();
            using (var cmd = Command("select id, addr, name from addresses where cp_id=$c order by addr", tx, ("$c", counterpartyId)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) addresses.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            }

            long show = counterpartyId;
            var touched = new HashSet<long> { counterpartyId };
            bool kept = false;
            foreach (var (id, addr, name) in addresses)
            {
                long target = CounterpartyFor(KeyFor(addr, tx), name, tx);
                if (target == counterpartyId)
                {
                    kept = true;
                    continue;
                }
                Exec("update addresses set cp_id=$t where id=$a", tx, ("$t", target), ("$a", id));
                if (touched.Add(target) && touched.Count == 2) show = target;
            }
            if (kept) show = counterpartyId;
            else Exec("delete from counterparties where id=$c and important=0", tx, ("$c", counterpartyId));
            RefreshTwoWay(tx, new List<long>(touched).ToArray());
            tx.Commit();
            return show;
        }
    }

    /// <summary>What the archive knows about one counterparty; null when it does not exist.</summary>
    public CounterpartyProfile? GetCounterparty(long id, int maxThreads = 15, int maxDocuments = 20)
    {
        lock (_gate)
        {
            var profile = new CounterpartyProfile { Id = id };
            bool found = false;
            Rows("select name, key, important from counterparties where id=$c", id, r =>
            {
                found = true;
                profile.Name = r.GetString(0);
                profile.Key = r.GetString(1);
                profile.Important = r.GetInt64(2) != 0;
            });
            if (!found) return null;
            profile.Custom = Scalar<long?>("select 1 from party_choice where target=$k or source in (select addr from addresses where cp_id=$c) limit 1", null, ("$k", profile.Key), ("$c", id)) != null;

            Rows(
                "select count(*), min(e.received), max(e.received), coalesce(sum(case when sa.cp_id=$c then 1 else 0 end), 0) from " + PartyEmails +
                "left join addresses sa on sa.id=e.sender_id", id, r =>
            {
                profile.Emails = (int)r.GetInt64(0);
                profile.FirstUtc = r.IsDBNull(1) ? null : FromUnix(r.GetInt64(1));
                profile.LastUtc = r.IsDBNull(2) ? null : FromUnix(r.GetInt64(2));
                profile.FromThem = (int)r.GetInt64(3);
            });

            Rows(
                "select a.addr, a.name, (select count(distinct ea.email_id) from email_addr ea join emails e on e.id=ea.email_id and e.body_state<>3 where ea.addr_id=a.id) n from addresses a where a.cp_id=$c order by n desc, a.addr", id, r =>
            {
                profile.Addresses.Add(new CounterpartyAddress { Address = r.GetString(0), Name = Text(r, 1), Emails = (int)r.GetInt64(2) });
            });

            Rows("select strftime('%Y', e.received, 'unixepoch', 'localtime') y, count(*) from " + PartyEmails + "group by y order by y desc", id, r =>
            {
                profile.Years.Add(new KeyValuePair<string, int>(Text(r, 0), (int)r.GetInt64(1)));
            });

            // SQLite: with max() the other columns come from the row that holds the maximum, here the latest email of the thread.
            Rows(
                "select e.thread_id, count(*), max(e.received), e.subject, e.entry_id, f.store_id from " + PartyEmails +
                "group by e.thread_id order by 3 desc limit $n", id, maxThreads, r =>
            {
                profile.Threads.Add(new CounterpartyItem
                {
                    Emails = (int)r.GetInt64(1), DateUtc = FromUnix(r.GetInt64(2)), Title = Text(r, 3), EntryId = r.GetString(4), StoreId = r.GetString(5)
                });
            });

            Rows(
                "select t.name, e.received, e.entry_id, f.store_id from " + PartyEmails + "join attachments t on t.email_id=e.id order by e.received desc, t.name limit $n", id, maxDocuments, r =>
            {
                profile.Documents.Add(new CounterpartyItem { Title = r.GetString(0), DateUtc = FromUnix(r.GetInt64(1)), EntryId = r.GetString(2), StoreId = r.GetString(3) });
            });

            return profile;
        }
    }

    private void Rows(string sql, long counterpartyId, Action<SqliteDataReader> row) => Rows(sql, counterpartyId, null, row);

    private void Rows(string sql, long counterpartyId, int? max, Action<SqliteDataReader> row)
    {
        using var cmd = max.HasValue ? Command(sql, null, ("$c", counterpartyId), ("$n", Math.Max(1, max.Value))) : Command(sql, null, ("$c", counterpartyId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) row(r);
    }

    private static string Text(SqliteDataReader r, int column) => r.IsDBNull(column) ? string.Empty : r.GetString(column);
}
