using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

[Collection(WinFormsCollection.Name)]
public sealed class ArchiveIndexerInteractiveTests
{
    [Fact]
    public void IndexingFailurePublishesItalianStatusWithoutTheOriginalException()
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") ?? Path.GetTempPath(), "ow-index-error-" + Guid.NewGuid().ToString("N"));
        var copies = new List<string>();
        try
        {
            RunOnUiThread(async () =>
            {
                using var engine = new ArchiveEngine(directory);
                await engine.Ready;
                var outlook = new FakeOutlook(new[] { Header("email", FakeOutlook.Newest) }, copies)
                {
                    OnFolderStep = () => throw new InvalidOperationException("synthetic English detail")
                };
                using var indexer = new ArchiveIndexer(outlook, new TestConfig(false), directory,
                    () => int.MaxValue, () => true, timerTicks: false);
                await TickUntilAsync(indexer, async () => (await indexer.GetStatusAsync()).Phase == "error");
                var status = await indexer.GetStatusAsync();
                Assert.Equal("Indicizzazione non riuscita. Riprova; se il problema continua, consulta il log.", status.Error);
                Assert.DoesNotContain("synthetic English detail", status.Error);
                Assert.Equal(0, outlook.OffUiThread);
            });
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") == null)
            {
                foreach (var copy in copies) if (Directory.Exists(copy)) Directory.Delete(copy, true);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void InteractiveOperationDefersAcquisitionThenTheArchiveResumesWithoutAModel()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ow-interactive-local-" + Guid.NewGuid().ToString("N"));
        var copies = new List<string>();
        try
        {
            RunOnUiThread(async () =>
            {
                using var engine = new ArchiveEngine(directory);
                await engine.Ready;
                var outlook = new FakeOutlook(new[] { Header("email", FakeOutlook.Newest) }, copies);
                using var indexer = new ArchiveIndexer(outlook,
                    new TestConfig(new WorkspaceConfig { Archive = new() { Enabled = true } }), directory,
                    () => int.MaxValue, () => true, timerTicks: false);
                var client = new ArchiveClient(FakeOutlook.Profile, directory);
                ArchiveIndexer.InteractiveBegin();
                try
                {
                    for (int i = 0; i < 5; i++) await indexer.TickAsync();
                    Assert.Equal(0, (await client.GetStatsAsync()).BodiesIndexed);
                    Assert.DoesNotContain(outlook.Calls, call => call.StartsWith("ReadForArchive ", StringComparison.Ordinal));
                }
                finally { ArchiveIndexer.InteractiveEnd(); }
                await TickUntilAsync(indexer, async () => (await client.GetStatsAsync()).BodiesIndexed == 1);
                Assert.Equal(0, outlook.OffUiThread);
            });
        }
        finally
        {
            foreach (var copy in copies) if (Directory.Exists(copy)) Directory.Delete(copy, true);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
