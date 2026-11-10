using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>A137: a tick started without a WinForms context, while a pane command holds the indexer, stays on the UI thread.</summary>
public sealed class ArchiveIndexerUiThreadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_UiThread_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private readonly List<ArchiveEngine> _engines = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var copy in _copies) if (Directory.Exists(copy)) TestArtifacts.DeleteDirectory(copy, true);
        if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true);
    }

    [Fact]
    public void ContendedTick_WithoutWinFormsContext_ReadsOutlookOnTheUiThread()
    {
        RunOnUiThread(async () =>
        {
            var engine = await StartEngineAsync();
            var outlook = new FakeOutlook(new[] { Header("m1", FakeOutlook.Newest), Header("m2", FakeOutlook.Newest.AddMinutes(-1)) }, _copies);
            using (var indexer = new ArchiveIndexer(outlook, new TestConfig(attachments: false), _directory, idleMs: () => int.MaxValue, launchEngine: () => true, timerTicks: false))
            {
                await indexer.ActivateAsync(Array.Empty<string>());
                await indexer.TickAsync();
                int before = outlook.Calls.Count;

                var ui = SynchronizationContext.Current;
                // The status command keeps the indexer's semaphore across its archive calls.
                var status = indexer.GetStatusAsync();
                // Office can run a callback without a WinForms context.
                SynchronizationContext.SetSynchronizationContext(null);
                var tick = indexer.TickAsync();
                SynchronizationContext.SetSynchronizationContext(ui);
                await status;
                await tick;

                Assert.True(outlook.Calls.Count > before, "The contended tick did not reach Outlook.");
                await TickUntilIdleAsync(indexer);
            }
            Assert.Equal(0, outlook.OffUiThread);
            await StopEngineAsync(engine);
        });
    }

    private async Task<ArchiveEngine> StartEngineAsync() => await Task.Run(async () =>
    {
        var engine = new ArchiveEngine(_directory);
        lock (_engines) _engines.Add(engine);
        await engine.Ready;
        return engine;
    });

    private Task StopEngineAsync(ArchiveEngine engine) => Task.Run(() =>
    {
        lock (_engines) _engines.Remove(engine);
        engine.Dispose();
    });
}
