using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OnlyWorkspace.Core.Archive;

/// <summary>Literal local facts for a restricted request; no model-generated archive content.</summary>
public static class ActionMailEvidence
{
    public static string? Facts(IEnumerable<ArchiveFact> facts, bool attachmentsAllowed = false)
    {
        var text = new StringBuilder();
        foreach (var fact in facts.Where(f => f.Resolved && (attachmentsAllowed || f.SourceId.Length == 0)))
            text.Append("\nFatto locale ").Append(fact.Kind)
                .Append(": ").Append(fact.Raw).Append(" — ").Append(fact.SourceName)
                .Append(" [").Append(fact.SourceId).Append(':').Append(fact.Start).Append(']');
        return text.Length > 0 ? text.ToString() : null;
    }
}
