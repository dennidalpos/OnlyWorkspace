
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>A149: Outlook reads in slices on the UI thread, each named for UiThreadWatch, and the store list kept between calls.</summary>
public sealed class UiThreadSliceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_UiSlices_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();

    public void Dispose()
    {
        foreach (var copy in _copies) if (Directory.Exists(copy)) TestArtifacts.DeleteDirectory(copy, true);
        if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true);
    }

    [Fact]
    public void IndexerOutlookCalls_AreNamed_AndTheTickAwaitsAreNot()
    {
        RunOnUiThread(async () =>
        {
            var engine = await Task.Run(async () => { var e = new ArchiveEngine(_directory); await e.Ready; return e; });
            try
            {
                var outlook = new FakeOutlook(new[] { Header("m1", FakeOutlook.Newest), Header("m2", FakeOutlook.Newest.AddMinutes(-1)) }, _copies);
                // UI messages run only while no synchronous add-in work holds the thread: none of them may see a name.
                var seen = new List<string?>();
                using var sampler = new System.Windows.Forms.Timer { Interval = 1 };
                sampler.Tick += (_, _) => seen.Add(UiThreadWatch.CurrentActivity);
                using (var indexer = new ArchiveIndexer(outlook, new TestConfig(attachments: false), _directory, idleMs: () => int.MaxValue, launchEngine: () => true, timerTicks: false))
                {
                    sampler.Start();
                    await indexer.ActivateAsync(Array.Empty<string>());
                    await TickUntilIdleAsync(indexer);
                    sampler.Stop();
                }

                Assert.NotEmpty(outlook.Calls);
                for (int i = 0; i < outlook.Calls.Count; i++)
                    Assert.StartsWith("archive indexing: ", outlook.Activities[i] ?? "(none) during " + outlook.Calls[i]);
                Assert.NotEmpty(seen);
                Assert.All(seen, Assert.Null);
            }
            finally { await Task.Run(engine.Dispose); }
        });
    }

    [Fact]
    public void StoreList_IsKept_UntilAnEventOrItsAge()
    {
        var now = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);
        int reads = 0;
        var cache = new StoreListCache(TimeSpan.FromSeconds(30), () => now);
        IReadOnlyList<OutlookStoreInfo> Read() { reads++; return new[] { new OutlookStoreInfo { StoreId = "s" + reads } }; }

        Assert.Equal("s1", cache.Get(Read)[0].StoreId);
        now = now.AddSeconds(29);
        Assert.Equal("s1", cache.Get(Read)[0].StoreId);
        cache.Invalidate();
        Assert.Equal("s2", cache.Get(Read)[0].StoreId);
        now = now.AddSeconds(30);
        Assert.Equal("s3", cache.Get(Read)[0].StoreId);
        Assert.Equal(3, reads);
    }

    [Fact]
    public void ConversationReads_RunOnTheUiThread_OnePerMessage_AndNamed()
    {
        RunOnUiThread(async () =>
        {
            int ui = Environment.CurrentManagedThreadId;
            int reads = 0, beats = 0;
            // A timer message comes only when no posted message is waiting: like input and paint, it starved behind
            // a chain of Task.Yield continuations (343 ms in Outlook, 27/10/2026).
            using var beat = new System.Windows.Forms.Timer { Interval = 10 };
            beat.Tick += (_, _) => beats++;
            beat.Start();
            await UiSlice.ForEachAsync(Enumerable.Range(0, 8), "conversation read", _ =>
            {
                Assert.Equal(ui, Environment.CurrentManagedThreadId);
                Assert.Equal("conversation read", UiThreadWatch.CurrentActivity);
                // A slow Outlook read: every one ends a 40 ms slice.
                Thread.Sleep(45);
                reads++;
            }, CancellationToken.None);
            beat.Stop();
            Assert.Equal(8, reads);
            Assert.True(beats >= 4, $"Only {beats} timer messages ran between 8 reads of 45 ms.");
            Assert.Null(UiThreadWatch.CurrentActivity);
        });
    }

    [Fact]
    public void Reads_WithoutAWinFormsContext_AreRefusedOffTheUiThread()
    {
        // Office can run a callback without a WinForms context: the continuation lands on the thread pool.
        int reads = 0;
        Task? loop = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            // The first read fills its slice: the second one would resume off this thread.
            loop = UiSlice.ForEachAsync(new[] { 1, 2 }, "conversation read", _ => { Thread.Sleep(45); reads++; }, CancellationToken.None);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        var error = Assert.Throws<AggregateException>(() => loop!.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void AttachmentCopies_AreCollectedInSlices_OnTheUiThread()
    {
        RunOnUiThread(async () =>
        {
            int ui = Environment.CurrentManagedThreadId;
            string dir = NewCopyDirectory();
            var facts = Facts("a.pdf", "b.pdf", "c.pdf");
            var calls = new List<int>();
            var copies = await AttachmentCopies.CollectAsync(
                () => Copy(dir, facts, 1, calls, ui),
                (d, next) => Copy(d, Facts("a.pdf", "b.pdf", "c.pdf"), next, calls, ui),
                CancellationToken.None);

            Assert.Equal(new[] { 1, 3 }, calls);
            Assert.NotNull(copies);
            Assert.Equal(3, copies!.Saved.Count);
            // Saved pairs and scan results refer to the facts of the first call.
            Assert.All(copies.Saved, pair => Assert.Contains(pair.Key, facts));
            Assert.Equal("copied", facts[2].ScanDetail);
            Assert.True(Directory.Exists(dir));
        });
    }

    [Fact]
    public void AttachmentCopies_OfAnEmailThatChanges_OrAStoppedCopy_LeaveNoFolder()
    {
        RunOnUiThread(async () =>
        {
            int ui = Environment.CurrentManagedThreadId;
            string changed = NewCopyDirectory();
            await Assert.ThrowsAsync<InvalidOperationException>(() => AttachmentCopies.CollectAsync(
                () => Copy(changed, Facts("a.pdf", "b.pdf", "c.pdf"), 1, new List<int>(), ui),
                (d, next) => Copy(d, Facts("a.pdf", "x.pdf", "c.pdf"), next, new List<int>(), ui),
                CancellationToken.None));
            Assert.False(Directory.Exists(changed));

            string stopped = NewCopyDirectory();
            using var cts = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AttachmentCopies.CollectAsync(
                () => { var first = Copy(stopped, Facts("a.pdf", "b.pdf", "c.pdf"), 1, new List<int>(), ui); cts.Cancel(); return first; },
                (d, next) => throw new InvalidOperationException("No copy after the stop."),
                cts.Token));
            Assert.False(Directory.Exists(stopped));
        });
    }

    private string NewCopyDirectory()
    {
        string dir = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static AttachmentFacts[] Facts(params string[] names) => names.Select(n => new AttachmentFacts(n, 10)).ToArray();

    /// <summary>Two copies per call from <paramref name="from"/>, like OutlookDataService.CopyAttachmentsInto.</summary>
    private static AttachmentCopies Copy(string dir, AttachmentFacts[] all, int from, List<int> calls, int ui)
    {
        Assert.Equal(ui, Environment.CurrentManagedThreadId);
        Assert.Equal("attachment copy", UiThreadWatch.CurrentActivity);
        calls.Add(from);
        var saved = new List<KeyValuePair<AttachmentFacts, string>>();
        int i = from;
        for (; i <= all.Length && i < from + 2; i++)
        {
            string path = Path.Combine(dir, all[i - 1].Name);
            File.WriteAllText(path, "x");
            all[i - 1].ScanDetail = "copied";
            saved.Add(new KeyValuePair<AttachmentFacts, string>(all[i - 1], path));
        }
        return new AttachmentCopies(dir, all, saved) { NextIndex = i <= all.Length ? i : 0 };
    }
}
