using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Tests;

/// <summary>Drives a real ArchiveIndexer on an STA thread with a WinForms loop, as UiSync expects of Outlook's UI thread.</summary>
internal static class ArchiveIndexerHarness
{
    public static void RunOnUiThread(Func<Task> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            context.Post(async _ =>
            {
                try { await test(); }
                catch (Exception ex) { error = ex; }
                finally { Application.ExitThread(); }
            }, null);
            Application.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "The UI thread did not finish.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    public static async Task TickUntilAsync(ArchiveIndexer indexer, Func<Task<bool>> done)
    {
        // Ticks return at once while background work (pipe, card task) is still running: on a saturated CI runner
        // 500 ticks took 13 s (A120), so the bound is also a minimum time, within RunOnUiThread's 2 minutes.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 500 || watch.Elapsed < TimeSpan.FromSeconds(60); i++)
        {
            await indexer.TickAsync();
            if (await done()) return;
            if (i >= 500) await Task.Delay(20);
        }
        var status = await indexer.GetStatusAsync();
        Assert.Fail($"Indexer did not reach the expected state (phase {status.Phase}: {status.Error}).");
    }

    public static Task TickUntilIdleAsync(ArchiveIndexer indexer) =>
        TickUntilAsync(indexer, async () => (await indexer.GetStatusAsync()).Phase == "idle");

    public static ArchiveHeader Header(string entryId, DateTime receivedUtc) => new()
    {
        EntryId = entryId,
        MessageId = "<" + entryId + "@example.test>",
        Subject = "Subject " + entryId,
        SenderName = "Sender",
        SenderAddress = "sender@example.test",
        ReceivedUtc = receivedUtc
    };
}

/// <summary>Synthetic Outlook: one inbox per open store, every Object Model call recorded with its thread.</summary>
internal sealed class FakeOutlook : IArchiveMailSource
{

    public const string Profile = "indexer-test";
    public const string DefaultStore = "store";
    public const string AttachmentWord = "zebrafinch";
    public static readonly DateTime Newest = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly IReadOnlyDictionary<string, ArchiveHeader[]> _rows;
    private readonly List<string> _copies;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;
    private int _quitAt = -1;

    public FakeOutlook(ArchiveHeader[] rows, List<string> copies) : this(new Dictionary<string, ArchiveHeader[]> { [DefaultStore] = rows }, copies) { }

    public FakeOutlook(IReadOnlyDictionary<string, ArchiveHeader[]> rowsByStore, List<string> copies) { _rows = rowsByStore; _copies = copies; }

    public List<string> Calls { get; } = new();
    /// <summary>The <see cref="UiThreadWatch"/> name in force during each call.</summary>
    public List<string?> Activities { get; } = new();
    public List<FakeReader> Readers { get; } = new();
    public List<string> CopyDirectories { get; } = new();
    public int OffUiThread { get; private set; }
    public Action<FakeReader, int>? OnBatch { get; set; }
    public Action? OnCopy { get; set; }
    public Func<string, string> Body { get; set; } = entryId => "Body of " + entryId;
    /// <summary>Empty folders listed after each inbox, to make the folder list take several steps.</summary>
    public int ExtraFoldersPerStore { get; set; }
    public Action? OnFolderStep { get; set; }
    public List<FakeFolderWalk> Walks { get; } = new();
    /// <summary>Stores removed from the Outlook profile: not listed, not open, their folders no longer open.</summary>
    public HashSet<string> ClosedStores { get; } = new();
    /// <summary>Stores whose folder tables describe a cache (IMAP, OST): they cannot prove an email absent.</summary>
    public HashSet<string> CacheStores { get; } = new();
    /// <summary>Folder EntryIDs deleted in Outlook: not listed, not opened.</summary>
    public HashSet<string> DeletedFolders { get; } = new();
    /// <summary>Folder EntryIDs still listed that Outlook cannot open.</summary>
    public HashSet<string> UnreadableFolders { get; } = new();
    public int FailedOpens { get; private set; }
    /// <summary>Why a store's folder list stopped short, as the walk reports it.</summary>
    public Dictionary<string, string> WalkIssues { get; } = new();
    public bool Stopped => _quitAt >= 0;
    public IEnumerable<string> CallsAfterQuit => Stopped ? Calls.Skip(_quitAt) : Enumerable.Empty<string>();

    public void Quit(ArchiveIndexer indexer)
    {
        _quitAt = Calls.Count;
        indexer.Stop();
    }

    // Every Outlook Object Model call; Dispose (release) is not counted.
    internal void Call(string name)
    {
        if (Environment.CurrentManagedThreadId != _uiThread) OffUiThread++;
        Calls.Add(name);
        Activities.Add(UiThreadWatch.CurrentActivity);
    }

    public string GetProfileName() { Call(nameof(GetProfileName)); return Profile; }
    public IReadOnlyCollection<string> GetOwnAddressList() { Call(nameof(GetOwnAddressList)); return new[] { "me@example.test" }.Concat(OwnFromStores).ToList(); }

    public IArchiveFolderWalk WalkArchiveFolders(ICollection<string>? storeIds)
    {
        Call(nameof(WalkArchiveFolders));
        var stores = _rows.Keys.Where(s => !ClosedStores.Contains(s) && (storeIds == null || storeIds.Count == 0 || storeIds.Contains(s))).ToArray();
        var folders = stores.SelectMany(s => new[] { new MailFolderRef { StoreId = s, EntryId = "inbox-" + s, Path = "Inbox" } }
            .Concat(Enumerable.Range(1, ExtraFoldersPerStore).Select(i => new MailFolderRef { StoreId = s, EntryId = $"extra-{s}-{i}", Path = "Inbox/Extra " + i })))
            .Where(f => !DeletedFolders.Contains(f.EntryId));
        var walk = new FakeFolderWalk(this, folders, stores, new Dictionary<string, string>(WalkIssues));
        Walks.Add(walk);
        return walk;
    }

    public IArchiveHeaderReader? OpenArchiveReader(string folderEntryId, string storeId, DateTime? newerThanUtc, DateTime? olderThanUtc, DateTime? modifiedSinceUtc = null, DateTime? modifiedUntilUtc = null)
    {
        Call(nameof(OpenArchiveReader));
        if (ClosedStores.Contains(storeId) || DeletedFolders.Contains(folderEntryId) || UnreadableFolders.Contains(folderEntryId))
        {
            FailedOpens++;
            return null;
        }
        var rows = (folderEntryId.StartsWith("inbox-", StringComparison.Ordinal) ? _rows[storeId] : Array.Empty<ArchiveHeader>()).Where(r => (newerThanUtc == null || r.ReceivedUtc > newerThanUtc) && (olderThanUtc == null || r.ReceivedUtc < olderThanUtc))
            .OrderByDescending(r => r.ReceivedUtc).ToArray();
        var reader = new FakeReader(this, rows, recent: modifiedSinceUtc != null, olderThanUtc, storeId);
        Readers.Add(reader);
        return reader;
    }

    public ArchiveBody? ReadForArchive(string entryId, string storeId)
    {
        Call(nameof(ReadForArchive) + " " + entryId);
        return new ArchiveBody { Text = Body(entryId), ModifiedUtc = Newest };
    }

    /// <summary>EntryIDs Outlook reports not found, and those it cannot tell about.</summary>
    public HashSet<string> NotFound { get; } = new();
    public HashSet<string> Unknown { get; } = new();

    public bool? ArchiveItemExists(string entryId, string storeId)
    {
        Call(nameof(ArchiveItemExists) + " " + entryId);
        if (Unknown.Contains(entryId)) return null;
        return !NotFound.Contains(entryId);
    }

    /// <summary>EntryIDs whose attachment is past the acquisition limit: Outlook does not save it, it cannot be read whole.</summary>
    public HashSet<string> OversizedAttachments { get; } = new();

    /// <summary>EntryIDs with this many text attachments instead of one: the indexer copies them a slice at a time.</summary>
    public Dictionary<string, int> ManyAttachments { get; } = new();

    /// <summary>EntryIDs whose one attachment is a picture without text: read only by OCR.</summary>
    public HashSet<string> ImageAttachments { get; } = new();
    public string AttachmentName { get; set; } = "note.txt";
    public int SavedAttachmentCopies { get; private set; }

    public AttachmentCopies? CopyAttachments(string entryId, string? storeId, Func<AttachmentFacts, bool> shouldSave, int fromIndex = 1, int maxCopies = int.MaxValue)
    {
        Call(nameof(CopyAttachments) + " " + entryId);
        // Same root as OutlookDataService: the indexer refuses to delete anything else.
        string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace", "scan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CopyDirectories.Add(directory);
        _copies.Add(directory);
        bool oversized = OversizedAttachments.Contains(entryId);
        int count = ManyAttachments.TryGetValue(entryId, out var many) ? many : 1;
        if (ImageAttachments.Contains(entryId))
        {
            var image = new AttachmentFacts("scan.png", 1024);
            var copied = new List<KeyValuePair<AttachmentFacts, string>>();
            if (shouldSave(image))
            {
                string imagePath = Path.Combine(directory, "1_scan.png");
                File.WriteAllBytes(imagePath, OcrExtractionTests.Png());
                copied.Add(new KeyValuePair<AttachmentFacts, string>(image, imagePath));
            }
            OnCopy?.Invoke();
            return new AttachmentCopies(directory, new[] { image }, copied);
        }
        var all = new List<AttachmentFacts>();
        var saved = new List<KeyValuePair<AttachmentFacts, string>>();
        int next = 0;
        for (int i = 1; i <= count; i++)
        {
            var facts = oversized ? new AttachmentFacts("big.txt", 31L * 1024 * 1024) : new AttachmentFacts(count == 1 ? AttachmentName : "note" + i + ".txt", 64);
            all.Add(facts);
            if (oversized || !shouldSave(facts) || i < fromIndex || next > 0) continue;
            if (saved.Count >= maxCopies) { next = i; continue; }
            string path = Path.Combine(directory, i + "_" + facts.Name);
            File.WriteAllText(path, "Allegato " + i + " con la parola " + AttachmentWord + ".");
            SavedAttachmentCopies++;
            saved.Add(new KeyValuePair<AttachmentFacts, string>(facts, path));
        }
        OnCopy?.Invoke();
        return new AttachmentCopies(directory, all, saved) { NextIndex = next };
    }

    public IReadOnlyList<OutlookStoreInfo> EnumerateStores()
    {
        Call(nameof(EnumerateStores));
        return _rows.Keys.Where(s => !ClosedStores.Contains(s))
            .Select(s => new OutlookStoreInfo { StoreId = s, DisplayName = s, IsOpen = true, IsDataFileStore = SentGuesses.ContainsKey(s) }).ToArray();
    }

    /// <summary>PSTs without account and the guess their Sent Items give.</summary>
    public Dictionary<string, StoreOwnEntry> SentGuesses { get; } = new();
    public List<string> OwnFromStores { get; private set; } = new();

    public StoreOwnEntry? GuessStoreOwnAddresses(string storeId)
    {
        Call(nameof(GuessStoreOwnAddresses));
        return SentGuesses.TryGetValue(storeId, out var guess) ? guess : null;
    }

    public void SetStoreOwnAddresses(IReadOnlyCollection<string> addresses, IReadOnlyCollection<string> names) => OwnFromStores = addresses.ToList();
}

/// <summary>Folder list handed out <c>max</c> folders per step, every step recorded as an Outlook call.</summary>
internal sealed class FakeFolderWalk : IArchiveFolderWalk
{
    private readonly FakeOutlook _owner;
    private readonly Queue<MailFolderRef> _pending;
    private readonly List<MailFolderRef> _listed = new();

    public FakeFolderWalk(FakeOutlook owner, IEnumerable<MailFolderRef> folders, IReadOnlyCollection<string> stores, IReadOnlyDictionary<string, string> issues)
    {
        _owner = owner;
        _pending = new Queue<MailFolderRef>(folders);
        CompleteStores = stores.Where(s => !issues.ContainsKey(s)).ToArray();
        Issues = issues;
    }

    public int Steps { get; private set; }
    public bool Disposed { get; private set; }
    public IReadOnlyList<MailFolderRef> Folders => _listed;
    public IReadOnlyCollection<string> CompleteStores { get; }
    public IReadOnlyDictionary<string, string> Issues { get; }

    public bool Step(int max)
    {
        _owner.Call(nameof(Step));
        Steps++;
        _owner.OnFolderStep?.Invoke();
        for (int i = 0; i < max && _pending.Count > 0; i++) _listed.Add(_pending.Dequeue());
        return _pending.Count == 0;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeReader : IArchiveHeaderReader
{
    private readonly FakeOutlook _owner;
    private readonly ArchiveHeader[] _rows;
    private int _next;

    private readonly string _storeId;

    public FakeReader(FakeOutlook owner, ArchiveHeader[] rows, bool recent, DateTime? olderThan, string storeId = FakeOutlook.DefaultStore)
    {
        _owner = owner;
        _storeId = storeId;
        _rows = rows;
        Recent = recent;
        OlderThan = olderThan;
    }

    public bool Recent { get; }
    public DateTime? OlderThan { get; }
    public List<List<ArchiveHeader>> Returned { get; } = new();
    public bool Disposed { get; private set; }
    public bool CanDeleteAbsent => !_owner.CacheStores.Contains(_storeId);

    public bool EndOfTable
    {
        get
        {
            _owner.Call(nameof(EndOfTable));
            return _next >= _rows.Length;
        }
    }

    public List<ArchiveHeader> ReadBatch(int max)
    {
        _owner.Call(nameof(ReadBatch));
        var batch = _rows.Skip(_next).Take(max).ToList();
        _next += batch.Count;
        Returned.Add(batch);
        _owner.OnBatch?.Invoke(this, Returned.Count);
        return batch;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class TestConfig : IConfigurationService
{
    private readonly WorkspaceConfig _config;
    public TestConfig(bool attachments) =>
        _config = new WorkspaceConfig { Archive = new WorkspaceArchiveConfig { Enabled = true, IndexAttachmentText = attachments } };
    public TestConfig(WorkspaceConfig config) => _config = config;
    public WorkspaceConfig LoadConfiguration() => _config;
    public bool HasConfigurationChanged() => false;
    public void SaveConfiguration(WorkspaceConfig config) => throw new NotSupportedException();
    public string? ResolveSecret(string secretRef) => null;
    public void StoreSecret(string secretRef, string secretValue) => throw new NotSupportedException();
    public Dictionary<string, bool> Flags { get; } = new();
    public bool GetUserFlag(string name) => Flags.TryGetValue(name, out var value) && value;
    public void SetUserFlag(string name, bool value) => Flags[name] = value;
}
