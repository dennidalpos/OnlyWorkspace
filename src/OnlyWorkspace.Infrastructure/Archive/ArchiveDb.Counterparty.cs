using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
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
    /// The manual merges and splits are gone: every address goes back to the counterparty of its automatic key and the
    /// stored choices, with the importance marks, are dropped.
    /// </summary>
    private void RetirePartyChoices(SqliteTransaction tx)
    {
        if (!HasTable("party_choice", tx)) return;
        var addresses = new List<(long Id, string Addr, string? Name)>();
        using (var cmd = Command("select id, addr, name from addresses where cp_id is not null order by id", tx))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) addresses.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }
        foreach (var (id, addr, name) in addresses)
            Exec("update addresses set cp_id=$t where id=$a", tx, ("$t", CounterpartyFor(CounterpartyResolver.KeyOf(addr), name, tx)), ("$a", id));
        Exec("update counterparties set important=0", tx);
        Exec("delete from counterparties where id not in (select cp_id from addresses where cp_id is not null)", tx);
        Exec("update counterparties set two_way=exists(select 1 from addresses a join email_addr r on r.addr_id=a.id where a.cp_id=counterparties.id and r.role<>0)", tx);
        Exec("drop table party_choice", tx);
    }
}
