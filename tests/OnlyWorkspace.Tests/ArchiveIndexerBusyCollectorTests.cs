using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>
/// 15/11/2026 (#259): the first Sintesi after Outlook started found the archive collector held by another instance
/// (ArchiveCollectorBusyException from SetOwnAddresses). The indexer must stay quiet (no error phase), wait 5 s
/// before retrying and hand out no archive, so no dictionary is read and nothing is sent to the service.
/// </summary>
public sealed class ArchiveIndexerBusyCollectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_BusyCollector_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private readonly List<ArchiveEngine> _engines = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var copy in _copies) if (Directory.Exists(copy)) TestArtifacts.DeleteDirectory(copy, true);
        if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true);
    }

    [Fact]
    public void BusyCollector_KeepsTheArchiveClosed_WithoutErrorAndWithNoStoresToRead()
    {
        RunOnUiThread(async () =>
        {
            var engine = await Task.Run(async () =>
            {
                var e = new ArchiveEngine(_directory);
                lock (_engines) _engines.Add(e);
                await e.Ready;
                return e;
            });

            // Another Outlook instance already owns the collector pipeline of this profile.
            var other = new ArchiveClient(FakeOutlook.Profile, _directory);
            await other.OpenAsync(true);
            await other.HeartbeatAsync("headers", null);

            var outlook = new FakeOutlook(Array.Empty<ArchiveHeader>(), _copies);
            using (var indexer = new ArchiveIndexer(outlook, new TestConfig(attachments: false), _directory, idleMs: () => int.MaxValue, launchEngine: () => true, timerTicks: false))
            {
                await indexer.ActivateAsync(Array.Empty<string>());

                var status = await indexer.GetStatusAsync();
                Assert.Equal("starting", status.Phase);
                Assert.Null(status.Error);

                // Within the 5 s wait: no archive handed out, no mailbox readable, so nothing reaches the service.
                Assert.Null(await indexer.OpenForSearchAsync());
                Assert.Equal(StoreScopeKind.None, (await indexer.GetAllowedSearchStoresAsync()).Kind);
            }

            await Task.Run(engine.Dispose);
        });
    }
}
