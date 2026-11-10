using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>Reads a complete collection on the captured context. Errors never return a partial snapshot.</summary>
public static class PrivacyEntitySnapshot
{
    public static async Task<IReadOnlyList<PrivacyEntity>> ReadAsync(int count, Func<int, IReadOnlyList<PrivacyEntity>> read,
        Func<CancellationToken, Task> beforeRead, CancellationToken ct)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var result = new List<PrivacyEntity>();
        for (int i = 1; i <= count; i++)
        {
            ct.ThrowIfCancellationRequested(); await beforeRead(ct); ct.ThrowIfCancellationRequested();
            foreach (var entity in read(i)) result.Add(new PrivacyEntity { Category = entity.Category, Value = entity.Value });
        }
        ct.ThrowIfCancellationRequested(); return result;
    }
}
