using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

/// <summary>Contacts flow: who the user corresponds with in the chosen folders, and the addresses refused per address book.</summary>
public sealed partial class ArchiveDb
{
    private const int ContactSources = 3;

    /// <summary>Archived mail folders, with their email count, for the contact search scope.</summary>
    public IReadOnlyList<ArchiveScopeFolder> ScopeFolders()
    {
        lock (_gate)
        {
            var result = new List<ArchiveScopeFolder>();
            using var cmd = Command(
                "select f.id, f.store_id, coalesce(f.path,''), (select count(*) from emails e where e.folder_id=f.id and e.body_state<>3) " +
                "from folders f where f.listed=1 order by f.store_id, f.path", null);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new ArchiveScopeFolder { Id = r.GetInt64(0), StoreId = r.GetString(1), Path = r.GetString(2), Emails = r.GetInt32(3) });
            return result;
        }
    }

    /// <summary>
    /// External addresses met in <paramref name="folderIds"/>, without those refused for the address book of
    /// <paramref name="destinationStoreId"/>. Sources: the newest emails each one sent there, one per Message-ID,
    /// without dates that cannot be right (1970, future).
    /// </summary>
    public IReadOnlyList<ContactCandidate> ContactCandidates(IReadOnlyCollection<long> folderIds, string destinationStoreId)
    {
        long now = ToUnix(DateTime.UtcNow);
        string scope =
            "with mine as (select e.id, e.received, e.message_id, e.entry_id, e.subject, f.store_id, e.sender_id, e.sender_name from emails e join folders f on f.id=e.folder_id " +
            "where e.folder_id in (" + InlineIds(folderIds) + ") and e.body_state<>3 and f.listed=1 and e.received>86400 and e.received<=$max) ";
        lock (_gate)
        {
            var byId = new Dictionary<long, ContactCandidate>();
            using (var cmd = Command(scope +
                "select a.id, a.addr, " +
                // The name they last wrote with: the archive keeps the first one met, often a recipient's bare address.
                "coalesce((select m.sender_name from mine m where m.sender_id=a.id and coalesce(m.sender_name,'')<>'' order by m.received desc limit 1), a.name, ''), coalesce(a.cp_id,0), coalesce(c.key,a.addr), coalesce(c.name,a.addr), " +
                // Two-way: the user wrote to this address.
                "exists(select 1 from email_addr r join emails w on w.id=r.email_id join addresses s on s.id=w.sender_id where r.addr_id=a.id and r.role<>0 and s.is_me=1), " +
                "(select count(distinct ea.email_id) from email_addr ea join mine m on m.id=ea.email_id where ea.addr_id=a.id), " +
                "(select max(m.received) from mine m where m.sender_id=a.id) " +
                "from addresses a left join counterparties c on c.id=a.cp_id " +
                "where a.is_me=0 and a.id in (select ea.addr_id from email_addr ea join mine m on m.id=ea.email_id) " +
                "and a.addr not in (select addr from contact_rejects where store_id=$d) order by a.addr", null, ("$max", now + 86400), ("$d", destinationStoreId)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    byId[r.GetInt64(0)] = new ContactCandidate
                    {
                        Address = r.GetString(1), Name = r.GetString(2), CounterpartyId = r.GetInt64(3), GroupKey = r.GetString(4), GroupName = r.GetString(5),
                        TwoWay = r.GetInt64(6) != 0, Emails = r.GetInt32(7), LastFromUtc = r.IsDBNull(8) ? null : FromUnix(r.GetInt64(8))
                    };
                }
            }

            // A few rows more than needed per sender: copies of one message (Gmail labels) count once.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var cmd = Command(scope +
                "select sender_id, entry_id, store_id, coalesce(subject,''), received, coalesce(nullif(message_id,''), 'id:'||id) from " +
                "(select m.*, row_number() over (partition by m.sender_id order by m.received desc, m.id desc) n from mine m where m.sender_id is not null) where n<=12 " +
                "order by sender_id, received desc, id desc", null, ("$max", now + 86400)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    if (!byId.TryGetValue(r.GetInt64(0), out var candidate) || candidate.Sources.Count >= ContactSources) continue;
                    if (!seen.Add(r.GetInt64(0) + "|" + r.GetString(5))) continue;
                    candidate.Sources.Add(new ContactSourceEmail { EntryId = r.GetString(1), StoreId = r.GetString(2), Subject = r.GetString(3), ReceivedUtc = FromUnix(r.GetInt64(4)) });
                }
            }
            return byId.Values.ToList();
        }
    }

    /// <summary>The user does not want these addresses in this address book: they are not proposed again for it.</summary>
    public void RejectContacts(string storeId, IReadOnlyCollection<string> addresses)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var raw in addresses)
            {
                var addr = CounterpartyResolver.Normalize(raw);
                if (addr.Length == 0) continue;
                Exec("insert or replace into contact_rejects(store_id, addr, rejected_utc) values($s,$a,$t)", tx, ("$s", storeId), ("$a", addr), ("$t", ToUnix(DateTime.UtcNow)));
            }
            tx.Commit();
        }
    }

    public void UnrejectContact(string storeId, string address)
    {
        lock (_gate) Exec("delete from contact_rejects where store_id=$s and addr=$a", null, ("$s", storeId), ("$a", CounterpartyResolver.Normalize(address)));
    }

    public IReadOnlyList<ContactRejection> RejectedContacts(string storeId)
    {
        lock (_gate)
        {
            var result = new List<ContactRejection>();
            using var cmd = Command("select addr, rejected_utc from contact_rejects where store_id=$s order by addr", null, ("$s", storeId));
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new ContactRejection { Address = r.GetString(0), RejectedUtc = FromUnix(r.GetInt64(1)) });
            return result;
        }
    }
}
