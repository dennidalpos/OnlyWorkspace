using System;
using System.Collections.Generic;
using System.Linq;

namespace OnlyWorkspace.Core.Archive;

public static class ArchiveFlowRules
{
    /// <summary>Logical messages retain every physical location. Missing Message-ID never uses subject/date.</summary>
    public static List<ArchiveFlowMail> Consolidate(IEnumerable<ArchiveFlowMail> source, string seedStore = "", string seedEntry = "")
    {
        var result = new List<ArchiveFlowMail>();
        int unknown = 0;
        foreach (var group in source.GroupBy(m => m.MessageId.Length > 0 ? "m:" + m.MessageId : m.StoreId.Length > 0 && m.EntryId.Length > 0 ? "e:" + m.StoreId + "\n" + m.EntryId : "unknown:" + unknown++, StringComparer.Ordinal))
        {
            var chosen = group.OrderByDescending(m => m.StoreId == seedStore && m.EntryId == seedEntry)
                .ThenByDescending(m => m.BodyState == ArchiveBodyState.Indexed).ThenBy(m => m.ReceivedUtc).ThenBy(m => m.Id).First();
            chosen.Copies = group.SelectMany(m => m.Copies.Concat(new[] { new ArchiveEmailRef { Id = m.Id, StoreId = m.StoreId, EntryId = m.EntryId, FolderPath = m.FolderPath } }))
                .GroupBy(m => m.StoreId + "\n" + m.EntryId, StringComparer.Ordinal).Select(g => g.First()).ToList();
            chosen.LinkReasons = group.SelectMany(m => m.LinkReasons).Distinct(StringComparer.Ordinal).ToList();
            chosen.LinkFacts = group.SelectMany(m => m.LinkFacts).ToList();
            chosen.OutsideFilters = group.All(m => m.OutsideFilters);
            result.Add(chosen);
        }
        return result.OrderBy(m => m.SentUtc ?? m.ReceivedUtc).ThenBy(m => m.ReceivedUtc).ThenBy(m => m.StoreId, StringComparer.Ordinal).ThenBy(m => m.EntryId, StringComparer.Ordinal).ToList();
    }
}
