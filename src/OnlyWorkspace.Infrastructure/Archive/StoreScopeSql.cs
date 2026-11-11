using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

internal static class StoreScopeSql
{
    /// <summary>
    /// The predicate that limits <paramref name="column"/> to the scope, with its parameters added to <paramref name="args"/>;
    /// null when the scope is all. A missing scope or none matches nothing.
    /// </summary>
    public static string? Predicate(StoreScope? scope, string column, string parameterPrefix, List<(string, object?)> args)
    {
        if (scope != null && scope.IsAll) return null;
        if (scope == null || scope.IsNone) return column + " in (null)";
        var names = scope.StoreIds.Select((store, i) => { args.Add((parameterPrefix + i, store)); return parameterPrefix + i; });
        return column + " in (" + string.Join(",", names) + ")";
    }
}
