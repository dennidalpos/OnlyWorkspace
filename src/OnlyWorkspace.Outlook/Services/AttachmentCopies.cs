using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>Attachment metadata plus the temp copies made for scanning/extraction; the caller deletes Directory.</summary>
public sealed class AttachmentCopies
{
    public AttachmentCopies(string directory, IReadOnlyList<AttachmentFacts> all, IReadOnlyList<KeyValuePair<AttachmentFacts, string>> saved)
    {
        Directory = directory;
        All = all;
        Saved = saved;
    }

    public string Directory { get; }
    public IReadOnlyList<AttachmentFacts> All { get; }
    public IReadOnlyList<KeyValuePair<AttachmentFacts, string>> Saved { get; }
    /// <summary>1-based attachment to resume copying from; 0 when every accepted attachment was copied.</summary>
    public int NextIndex { get; set; }

    /// <summary>
    /// Every accepted attachment copied into one folder by calls that each copy a couple (<paramref name="copyFirst"/>,
    /// then <paramref name="copyMore"/> into the same folder from an index), yielding the UI thread between calls.
    /// Null when the email is gone; the folder is deleted when a copy fails, the email changes or the copy is cancelled.
    /// </summary>
    internal static async Task<AttachmentCopies?> CollectAsync(Func<AttachmentCopies?> copyFirst, Func<string, int, AttachmentCopies?> copyMore, CancellationToken ct)
    {
        int uiThread = Environment.CurrentManagedThreadId;
        var slice = new UiSlice();
        AttachmentCopies? first;
        using (UiThreadWatch.Activity("attachment copy")) first = copyFirst();
        if (first == null || first.NextIndex == 0) return first;
        var saved = first.Saved.ToList();
        try
        {
            for (int next = first.NextIndex; next > 0;)
            {
                await slice.NextAsync(uiThread, ct);
                AttachmentCopies? more;
                using (UiThreadWatch.Activity("attachment copy")) more = copyMore(first.Directory, next);
                if (more == null || more.All.Count != first.All.Count || !more.All.Select(a => a.Name).SequenceEqual(first.All.Select(a => a.Name)))
                    throw new InvalidOperationException("L'email è cambiata durante la lettura degli allegati; riprovare.");
                // What this call found while copying belongs to the facts the caller holds.
                for (int i = 0; i < more.All.Count; i++)
                {
                    if (i + 1 < next) continue;
                    first.All[i].DetectedType = more.All[i].DetectedType;
                    first.All[i].ScanStatus = more.All[i].ScanStatus;
                    first.All[i].ScanDetail = more.All[i].ScanDetail;
                }
                foreach (var pair in more.Saved)
                    for (int i = 0; i < more.All.Count; i++)
                        if (ReferenceEquals(more.All[i], pair.Key)) saved.Add(new KeyValuePair<AttachmentFacts, string>(first.All[i], pair.Value));
                next = more.NextIndex;
            }
        }
        catch
        {
            try { if (System.IO.Directory.Exists(first.Directory)) System.IO.Directory.Delete(first.Directory, true); }
            catch (Exception ex) { OnlyWorkspaceLog.Logger.Debug(ex, "Attachment copies not removed"); }
            throw;
        }
        return new AttachmentCopies(first.Directory, first.All, saved);
    }
}
