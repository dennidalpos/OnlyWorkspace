using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;
using static OnlyWorkspace.Tests.ArchiveIndexerHarness;

namespace OnlyWorkspace.Tests;

/// <summary>
/// A130 (25/10/2026): the engine requeues the scans left unread as soon as OCR is turned back on, while the indexer
/// kept the old setting up to 5 s and reread them without OCR: they stayed "ocr-off" and were never requeued again.
/// </summary>
public sealed class ArchiveIndexerOcrSwitchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_OcrSwitch_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _copies = new();
    private readonly List<ArchiveEngine> _engines = new();
    private volatile bool _engineOcr;

    [Fact]
    public void ScanRequeuedRightAfterAStep_IsReadWithOcr()
    {
        RunOnUiThread(async () =>
        {
            var config = new SwitchingConfig();
            await StartEngineAsync();
            var outlook = new FakeOutlook(new[] { Header("scan", FakeOutlook.Newest) }, _copies);
            outlook.ImageAttachments.Add("scan");
            using var indexer = new ArchiveIndexer(outlook, config, _directory, idleMs: () => int.MaxValue, launchEngine: () => true, timerTicks: false);
            await TickUntilIdleAsync(indexer);
            Assert.Equal("ocr-off", await StateAsync());

            // The last tick has just read the settings: the engine sees the switch before the indexer.
            SetOcr(config, true);
            await TickUntilAsync(indexer, async () => await StateAsync() != "ocr-off" || (await Client().GetStatsAsync()).BodiesPending == 0 && (await indexer.GetStatusAsync()).Phase == "idle");
            Assert.NotEqual("ocr-off", await StateAsync());
            Assert.Equal(0, outlook.OffUiThread);
        });
    }

    [Fact]
    public void OcrSwitchedDuringTheRead_TheReadIsNotSavedAndTheEmailIsReadAgain()
    {
        RunOnUiThread(async () =>
        {
            var config = new SwitchingConfig();
            await StartEngineAsync();
            var outlook = new FakeOutlook(new[] { Header("scan", FakeOutlook.Newest) }, _copies);
            outlook.ImageAttachments.Add("scan");
            int copies = 0;
            outlook.OnCopy = () => { if (++copies == 1) SetOcr(config, true); };
            using var indexer = new ArchiveIndexer(outlook, config, _directory, idleMs: () => int.MaxValue, launchEngine: () => true, timerTicks: false);
            await TickUntilAsync(indexer, async () => await StateAsync() is { } state && state != "ocr-off" ||
                copies > 0 && (await Client().GetStatsAsync()).BodiesPending == 0 && (await indexer.GetStatusAsync()).Phase == "idle");
            Assert.NotEqual("ocr-off", await StateAsync());
            Assert.Equal(2, copies);
        });
    }

    private void SetOcr(SwitchingConfig config, bool on)
    {
        _engineOcr = on;
        config.Set(on);
    }

    private ArchiveClient Client() => new(FakeOutlook.Profile, _directory);

    private async Task<string?> StateAsync() =>
        (await Client().SearchAsync(new ArchiveQuery { FromUtc = FakeOutlook.Newest.AddDays(-1), Max = 10 })).SingleOrDefault()?.Attachments.SingleOrDefault()?.State;

    private async Task StartEngineAsync() => await Task.Run(async () =>
    {
        var engine = new ArchiveEngine(_directory, null, null, attachmentOcr: () => _engineOcr);
        lock (_engines) _engines.Add(engine);
        await engine.Ready;
    });

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var copy in _copies) if (Directory.Exists(copy)) TestArtifacts.DeleteDirectory(copy, true);
        if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true);
    }

    /// <summary>config.json as the add-in sees it: a new copy on each load, a change noticed only through HasConfigurationChanged.</summary>
    private sealed class SwitchingConfig : IConfigurationService
    {
        private int _version, _loaded = -1;
        private bool _ocr;

        public void Set(bool ocr) { _ocr = ocr; _version++; }

        public WorkspaceConfig LoadConfiguration()
        {
            _loaded = _version;
            return new WorkspaceConfig { AttachmentOcr = _ocr, Archive = new WorkspaceArchiveConfig { Enabled = true, IndexAttachmentText = true } };
        }

        public bool HasConfigurationChanged() => _loaded != _version;
        public void SaveConfiguration(WorkspaceConfig config) => throw new NotSupportedException();
        public string? ResolveSecret(string secretRef) => null;
        public void StoreSecret(string secretRef, string secretValue) => throw new NotSupportedException();
        public bool GetUserFlag(string name) => false;
        public void SetUserFlag(string name, bool value) { }
    }
}
