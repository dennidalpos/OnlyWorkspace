using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed partial class ArchiveDb
{
    /// <summary>Only source ids are accepted; the selected-store boundary applies during traversal too.</summary>
    public ArchiveRelatedResult ExpandRelated(IReadOnlyList<long> seedIds, ArchiveFlowSeed policy)
    {
        if (seedIds == null || policy == null) throw new ArgumentNullException();
        lock (_gate)
        {
            var result = new ArchiveRelatedResult();
            var physical = new Dictionary<long, ArchiveFlowMail>();
            var seeds = new HashSet<long>(seedIds);
            foreach (long id in seeds)
            {
                if (physical.ContainsKey(id)) continue;
                var flow = GetFlow(new ArchiveFlowSeed { Id = id, Stores = policy.Stores, IncludeAttachmentText = policy.IncludeAttachmentText }, int.MaxValue - 1);
                result.MetadataIncomplete |= flow.MetadataIncomplete; result.FactsIncomplete |= flow.FactsIncomplete;
                foreach (var mail in flow.Timeline)
                    foreach (var copy in mail.Copies) { physical[copy.Id] = mail; }
            }
            var ids = physical.Keys.ToList();
            for (int i = 0; i < ids.Count; i += 500)
            {
                var query = new ArchiveQuery
                {
                    EmailIds = ids.Skip(i).Take(500).ToList(),
                    Stores = policy.Stores,
                    IncludeAttachmentText = policy.IncludeAttachmentText,
                    IncludeFacts = true,
                    Max = 500
                };
                foreach (var hit in Search(query))
                {
                    var mail = physical[hit.Id];
                    hit.Copies = mail.Copies; hit.LinkReasons = mail.LinkReasons; hit.LinkFacts = mail.LinkFacts;
                    hit.OutsideFilters = !mail.Copies.Any(copy => seeds.Contains(copy.Id));
                    result.Hits.Add(hit);
                }
            }
            result.Hits = ArchiveHit.OnePerMessage(result.Hits.OrderBy(h => h.OutsideFilters).ThenByDescending(h => h.BodyState == ArchiveBodyState.Indexed), out int copies)
                .OrderBy(h => h.SentUtc ?? h.ReceivedUtc).ThenBy(h => h.StoreId, StringComparer.Ordinal).ThenBy(h => h.EntryId, StringComparer.Ordinal).ToList();
            result.Copies = copies; return result;
        }
    }
}
