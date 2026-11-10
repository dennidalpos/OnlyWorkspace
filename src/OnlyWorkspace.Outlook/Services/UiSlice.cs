using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>
/// A loop of Object Model reads on Outlook's UI thread pauses once its slice has run 40 ms: Outlook processes input
/// and repaints, and does not count the add-in as hung. A fixed pause per item cost time without bounding the slice.
/// </summary>
internal sealed class UiSlice
{
    private const int SliceMs = 40;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public async Task YieldIfDueAsync(CancellationToken ct)
    {
        if (_sw.ElapsedMilliseconds < SliceMs) return;
        // Timer-based: input and paint messages run before the loop resumes.
        await Task.Delay(1, ct);
        _sw.Restart();
    }

    /// <summary>
    /// Outlook reads (conversation emails, attachment copies) in 40 ms slices, each read named for
    /// <see cref="UiThreadWatch"/>. Call on the UI thread with a WinForms context (UiSync).
    /// </summary>
    public static async Task ForEachAsync<T>(IEnumerable<T> items, string activity, Action<T> read, CancellationToken ct)
    {
        int uiThread = Environment.CurrentManagedThreadId;
        var slice = new UiSlice();
        foreach (var item in items)
        {
            await slice.NextAsync(uiThread, ct);
            using (UiThreadWatch.Activity(activity)) read(item);
        }
    }

    /// <summary>
    /// <see cref="YieldIfDueAsync"/>, then back on <paramref name="uiThread"/>. Not Task.Yield: its continuation is a
    /// posted message, which Windows hands out before input, paint and timers, so a chain of them held Outlook 343 ms
    /// (A149, 27/10/2026). Without a WinForms context the continuation would land on the thread pool: that read is
    /// refused instead of touching the Object Model there.
    /// </summary>
    public async Task NextAsync(int uiThread, CancellationToken ct)
    {
        await YieldIfDueAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (Environment.CurrentManagedThreadId != uiThread)
            throw new InvalidOperationException("Outlook Object Model accessed outside the Outlook UI thread.");
    }
}
