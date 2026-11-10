using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
    /// <summary>Keyset page: identities only from authorized stores, no body reads or persisted dictionaries.</summary>
    public IReadOnlyList<PrivacyEntity> GetPrivacyEntities(long afterId, List<string>? storeIds, int take)
    {
        if (afterId < 0 || take < 1 || take > 1000) throw new ArgumentOutOfRangeException(nameof(take));
        lock (_gate)
        {
            var args = new List<(string, object?)> { ("$after", afterId), ("$take", take) };
            string scope = "";
            if (storeIds != null)
            {
                var parameters = storeIds.Select((s, i) => { args.Add(("$s" + i, s)); return "$s" + i; }).ToArray();
                scope = " and f.store_id in (" + (parameters.Length == 0 ? "null" : string.Join(",", parameters)) + ")";
            }
            using var cmd = Command("select a.id,a.name from addresses a where a.id>$after and length(trim(coalesce(a.name,'')))>=3 and exists(" +
                "select 1 from email_addr ea join emails e on e.id=ea.email_id join folders f on f.id=e.folder_id where ea.addr_id=a.id and e.present=1" + scope +
                ") order by a.id limit $take", null, args.ToArray());
            using var reader = cmd.ExecuteReader(); var result = new List<PrivacyEntity>();
            while (reader.Read()) result.Add(new PrivacyEntity { Id = reader.GetInt64(0), Value = reader.GetString(1) });
            return result;
        }
    }
}
