using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Infrastructure.Security;
using OnlyWorkspace.Outlook.UI;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace OnlyWorkspace.Outlook.Services;

public sealed class ArchiveStatus
{
    /// <summary>The administrator enabled the archive in the configuration.</summary>
    public bool Available { get; set; }
    /// <summary>The user activated it for this Outlook profile (the database exists).</summary>
    public bool Active { get; set; }
    public bool Paused { get; set; }
    /// <summary>off | starting | cleaning | headers | bodies | waiting | paused | limit | idle | error</summary>
    public string Phase { get; set; } = "off";
    public string? Error { get; set; }
    public ArchiveStats Stats { get; set; } = new();
    public long MaxBytes { get; set; }
    public long DiskFreeBytes { get; set; }
    public IReadOnlyList<string> StoreIds { get; set; } = Array.Empty<string>();
    /// <summary>Indicizza ora was asked: the indexer works without waiting for an idle PC until nothing is left.</summary>
    public bool IndexingNow { get; set; }
}

/// <summary>
/// Fills the mail archive in the background, one per Outlook process. Everything runs on the Outlook UI
/// thread from a timer, in slices of a few tens of milliseconds, only while no pane operation is running and,
/// unless the user asked Indicizza ora, while the user is not typing or clicking.
/// </summary>
public sealed class ArchiveIndexer : IArchiveIndexer, IDisposable
{
    private const int StartDelayMs = 60000;
    private const int WorkIntervalMs = 250;
    private const int WaitIntervalMs = 2000;
    private const int SleepIntervalMs = 15000;
    // Work only after this long without keyboard or mouse input; it stops as soon as the user is back.
    private const int IdleMs = 10000;
    // Indicizza ora while the user is at the PC: the same slices, further apart, so Outlook keeps most of its UI thread.
    private const int NowIntervalMs = 500;
    private const int SliceMs = 40;
    // Folders listed per call inside a slice: a few milliseconds each on a local store.
    private const int FolderListBatch = 20;
    private const string MetaPaused = ArchiveScope.PausedKey;
    private const string MetaStores = ArchiveScope.StoresKey;
    private static readonly TimeSpan SettingsEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NewMailEvery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FolderListEvery = TimeSpan.FromMinutes(2);
    // A moved or deleted email leaves the archive at the close of the next full check: 4 minutes plus its reading stay within the 5 minute goal.
    private static readonly TimeSpan RescanEvery = TimeSpan.FromMinutes(4);

    public static ArchiveIndexer? Current { get; private set; }

    private static int _interactive;

    /// <summary>A pane operation started: indexing yields until it ends, attachment reads are cancelled.</summary>
    public static void InteractiveBegin()
    {
        Interlocked.Increment(ref _interactive);
        Current?.CancelAttachmentReads();
    }

    public static void InteractiveEnd() => Interlocked.Decrement(ref _interactive);

    private readonly IArchiveMailSource _data;
    private readonly IConfigurationService _configService;
    private readonly WinFormsTimer _timer;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _stepRunning;
    private DateTime _connectAfterUtc;
    private readonly string _directory;
    private readonly Func<int> _idleMs;
    private readonly Func<bool> _launchEngine;
    private readonly TimeSpan _rescanEvery;

    private WorkspaceConfig? _config;
    private WorkspaceArchiveConfig? _settings;
    private DateTime _settingsReadUtc;
    private string? _profile;
    private ArchiveClient? _db;
    private bool _stopping;
    private string _phase = "off";
    private string? _error;
    // Something may be left from mailboxes no longer chosen: checked after opening and after a new choice.
    private bool _cleanup;
    // Mailbox choice last applied; the ConfigTool may change it in the meta table at any time.
    private string? _appliedScope;
    private bool _scopeKnown;
    private string? _publishedStores;
    // PSTs' own addresses last applied (meta store_own_addresses) and the stores already guessed in this Outlook session.
    private string? _appliedOwn;
    private readonly HashSet<string> _ownGuessed = new(StringComparer.OrdinalIgnoreCase);
    private int _removed;

    private IReadOnlyList<MailFolderRef>? _folderList;
    // Listing in progress across ticks: thousands of folders listed in one call blocked Outlook until it disabled the add-in.
    private IArchiveFolderWalk? _folderWalk;
    private IReadOnlyCollection<string> _completedStores = Array.Empty<string>();
    private IReadOnlyDictionary<string, string>? _folderIssues;
    private DateTime _folderListUtc;
    private DateTime _lastPassUtc;
    // A full check was started: the pass that closes it follows its reading, without waiting for the next round of new mail.
    private bool _closeCheck;
    // When the next full check is due: it starts then, not at the next round of new mail up to 2 minutes later.
    private DateTime _checkDueUtc;
    private readonly Queue<ArchiveFolder> _work = new();
    private readonly Queue<ArchiveFolder> _recentWork = new();
    private ArchiveFolder? _folder;
    private IArchiveHeaderReader? _reader;
    private ArchiveFolder? _recentFolder;
    private IArchiveHeaderReader? _recentReader;
    private DateTime _recentUntilUtc;
    private bool _readBodiesNext;
    private int _batch = 100;
    private string? _loggedScope;
    private readonly Queue<ArchiveEmailRef> _pending = new();
    // Indicizza ora: idleness is not awaited until the work runs out; pane operations still come first.
    private bool _indexNow;

    public ArchiveIndexer(IArchiveMailSource data, IConfigurationService? configService = null)
        : this(data, configService ?? new FileConfigurationService(), OnlyWorkspace.Core.AppPaths.ArchiveDirectory, SystemIdleMs, () => ConfigToolLauncher.TryLaunch(startMinimized: true), timerTicks: true) { }

    /// <summary>Test seam: archive directory, idle probe, tray launch, full check interval replaced; without timer ticks the caller drives <see cref="TickAsync"/>.</summary>
    internal ArchiveIndexer(IArchiveMailSource data, IConfigurationService configService, string directory, Func<int> idleMs, Func<bool> launchEngine, bool timerTicks, TimeSpan? rescanEvery = null)
    {
        _data = new WatchedArchiveMailSource(data);
        _configService = configService;
        _directory = directory;
        _idleMs = idleMs;
        _launchEngine = launchEngine;
        _rescanEvery = rescanEvery ?? RescanEvery;
        _timer = new WinFormsTimer { Interval = StartDelayMs };
        if (timerTicks) _timer.Tick += OnTick;
        Current = this;
    }

    /// <summary>Call on the Outlook UI thread: the timer ticks there.</summary>
    public void Start() => _timer.Start();

    /// <summary>Outlook is closing: no more ticks. Nothing is released or written here.</summary>
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _timer.Stop();
        _lifetime.Cancel();
        CancelAttachmentReads();
    }

    /// <summary>
    /// Open archive for searches; null when it is not active. Opened on demand (UI thread): the first tick
    /// comes a minute after Outlook starts, the user may ask before.
    /// </summary>
    public ArchiveClient? Db => _db;

    public async Task<ArchiveClient?> OpenForSearchAsync()
    {
        using (await PaneLockAsync())
        {
            if (!_stopping && ReadSettings().Enabled) await EnsureOpenAsync(refresh: true);
            return _db;
        }
    }

    /// <summary>Explicit query scope; never confuse saved [] (all) with the DB's [] (none).</summary>
    public async Task<List<string>?> GetAllowedSearchStoresAsync()
    {
        using (await PaneLockAsync())
        {
            bool authorized = !_stopping && ReadSettings(refresh: true).Enabled && await EnsureOpenAsync(refresh: true);
            return ArchiveScope.ForQuery(authorized ? await StoreIdsAsync() : Array.Empty<string>(), authorized);
        }
    }

    /// <summary>
    /// A pane page reading an archive already open does not wait for the indexing step under way: a step can last
    /// seconds while it copies attachments, and every page switch waited behind it. Opening still takes the lock.
    /// </summary>
    private async Task<IDisposable?> PaneLockAsync()
    {
        if (_db != null) return null;
        await _operations.WaitAsync();
        return new Releaser(_operations);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;
        public Releaser(SemaphoreSlim gate) => _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private static int SystemIdleMs()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        return GetLastInputInfo(ref info) ? unchecked((int)((uint)Environment.TickCount - info.dwTime)) : int.MaxValue;
    }

    private async void OnTick(object? sender, EventArgs e) => await TickAsync();

    /// <summary>One timer tick; never throws.</summary>
    internal async Task TickAsync()
    {
        if (_stopping || _stepRunning) return;
        // Before the first await: a contended semaphore would otherwise resume the step on a pool thread.
        UiSync.Ensure();
        _stepRunning = true;
        await _operations.WaitAsync();
        try
        {
            if (_stopping) return;
            _error = null;
            _timer.Interval = await StepAsync();
            if (_db != null && !_stopping) await _db.HeartbeatAsync(_phase, _error);
        }
        catch (OperationCanceledException) when (_stopping) { }
        catch (OperationCanceledException)
        {
            _phase = "waiting";
            _error = null;
            _timer.Interval = WaitIntervalMs;
        }
        catch (Exception ex)
        {
            if (_stopping) return;
            // A folder or a store can fail (offline, closed PST): drop the current step and slow down.
            OnlyWorkspaceLog.Logger.Warning(ex, "Archive indexing step failed");
            _error = "Indicizzazione non riuscita. Riprova; se il problema continua, consulta il log.";
            _phase = "error";
            if ((_recentFolder ?? _folder) is { } failedFolder && _db != null)
            {
                try { await _db.SetFolderErrorAsync(failedFolder.Id, failedFolder.UnavailableMessage()); }
                catch (Exception failure) { OnlyWorkspaceLog.Logger.Warning("Archive folder failure could not be persisted: {Kind}", failure.GetType().Name); }
            }
            CloseReader();
            CloseRecentReader();
            _pending.Clear();
            _timer.Interval = SleepIntervalMs;
            if (ex is IOException || ex is TimeoutException || ex is ArchiveCollectorBusyException)
            {
                Close();
                _connectAfterUtc = DateTime.UtcNow.AddSeconds(15);
            }
        }
        finally { _operations.Release(); _stepRunning = false; }
    }

    /// <summary>One slice of work; returns the delay before the next one.</summary>
    private async Task<int> StepAsync()
    {
        if (!ReadSettings().Enabled)
        {
            Close();
            _phase = "off";
            return SleepIntervalMs;
        }
        if (!await EnsureOpenAsync())
        {
            if (UserArchiveOff()) _phase = "off";
            return SleepIntervalMs;
        }
        if (_stopping) return SleepIntervalMs;
        await PublishOpenStoresAsync();
        await ApplyScopeChangeAsync();
        await ApplyOwnAddressesAsync();
        if (_stopping) return SleepIntervalMs;
        // Asked by the tray for a search the user is waiting on: not held back by idleness, but one slice per tick.
        var originals = Stopwatch.StartNew();
        foreach (var request in await _db!.OriginalRequestsAsync())
        {
            if (_stopping) return SleepIntervalMs;
            if (originals.ElapsedMilliseconds >= SliceMs) return WorkIntervalMs;
            var original = await ReadCompleteAsync(request.EntryId, request.StoreId, _lifetime.Token);
            if (_stopping) return SleepIntervalMs;
            bool available = original != null && !original.Unavailable;
            string? text = null, error = available ? null : "Testo originale non disponibile dalla sorgente.";
            if (available)
            {
                try { text = ArchiveAttachmentReader.ReadableText(original!); }
                catch (InvalidOperationException ex) { error = ex.Message; }
            }
            await _db.CompleteOriginalAsync(request.Id, text, error);
        }
        if (await _db.GetMetaAsync("sync_asked") == "1")
        {
            await _db.SetMetaAsync("sync_asked", null);
            CloseReader();
            CloseRecentReader();
            _work.Clear();
            _recentWork.Clear();
            ResetFolderList();
            _lastPassUtc = default;
            await _db.RequestRescanAsync();
        }
        if (_cleanup && Volatile.Read(ref _interactive) == 0)
        {
            // Mailboxes taken out of the archive: database work only, no need to wait for an idle PC.
            var keep = await StoreIdsAsync();
            var sw = Stopwatch.StartNew();
            int removed;
            do
            {
                _removed += removed = await _db!.RemoveOtherStoresAsync(keep, 200);
            }
            while (removed > 0 && sw.ElapsedMilliseconds < SliceMs);
            if (removed > 0)
            {
                _phase = "cleaning";
                return WorkIntervalMs;
            }
            _cleanup = false;
            if (_removed > 0) OnlyWorkspaceLog.Logger.Information("Archive: {Removed} emails and folders of the mailboxes taken out removed", _removed);
            _removed = 0;
        }
        if (await _db!.GetMetaAsync(MetaPaused) == "1")
        {
            _phase = "paused";
            return SleepIntervalMs;
        }
        bool userActive = _idleMs() < IdleMs;
        if (Volatile.Read(ref _interactive) > 0 || (userActive && !_indexNow))
        {
            if (_phase is "headers" or "bodies" or "starting" or "cleaning") _phase = "waiting";
            return WaitIntervalMs;
        }
        int workInterval = userActive ? NowIntervalMs : WorkIntervalMs;
        var capacity = (await _db.GetStatsAsync()).DiskState;
        if (capacity == ArchiveCapacityState.Full)
        {
            _phase = "limit";
            _error = ArchiveCapacity.Message(capacity);
            return SleepIntervalMs;
        }
        if (capacity == ArchiveCapacityState.Warning) _error = ArchiveCapacity.Message(capacity);
        else if (_error == ArchiveCapacity.Message(ArchiveCapacityState.Warning) || _error == ArchiveCapacity.Message(ArchiveCapacityState.Full)) _error = null;

        if (_readBodiesNext)
        {
            _readBodiesNext = false;
            if (await StepBodiesAsync())
            {
                _phase = "bodies";
                return workInterval;
            }
        }
        if (await StepHeadersAsync())
        {
            _readBodiesNext = true;
            _phase = "headers";
            return workInterval;
        }
        if (await StepBodiesAsync())
        {
            _phase = "bodies";
            return workInterval;
        }
        if (_indexNow) OnlyWorkspaceLog.Logger.Information("Archive: Indicizza ora finished, back to indexing while the PC is idle");
        _indexNow = false;
        _phase = "idle";
        return SleepIntervalMs;
    }

    private WorkspaceArchiveConfig ReadSettings(bool refresh = false)
    {
        if (!refresh && _settings != null && DateTime.UtcNow - _settingsReadUtc < SettingsEvery) return _settings;
        _settingsReadUtc = DateTime.UtcNow;
        using var activity = UiThreadWatch.Activity("archive indexing: settings");
        if (_settings != null && !_configService.HasConfigurationChanged()) return _settings;
        try
        {
            _config = _configService.LoadConfiguration();
            _settings = _config.Archive ?? new WorkspaceArchiveConfig();
        }
        catch (Exception)
        {
            // Missing or invalid configuration: the pane already reports it.
            _config = null;
            _settings = new WorkspaceArchiveConfig();
        }
        return _settings;
    }

    private string Profile => _profile ??= _data.GetProfileName();

    /// <summary>
    /// Opens the database. Once the administrator allows the archive it is created for every open mailbox,
    /// unless this Windows user turned it off in the ConfigTool: no question in the pane.
    /// </summary>
    /// <param name="refresh">A search the user started: the choice made in the ConfigTool a moment ago applies.</param>
    private async Task<bool> EnsureOpenAsync(bool refresh = false)
    {
        if (UserArchiveOff(refresh))
        {
            if (_db != null) Close();
            return false;
        }
        if (_db != null) return true;
        if (DateTime.UtcNow < _connectAfterUtc) return false;
        await OpenAsync(create: !ArchiveDb.Exists(_directory, Profile));
        return _db != null;
    }

    private bool _userOff;
    private DateTime _userOffReadUtc;

    /// <summary>The user's choice in the ConfigTool, read again every few seconds rather than at every tick.</summary>
    private bool UserArchiveOff(bool refresh = false)
    {
        if (!refresh && _userOffReadUtc != default && DateTime.UtcNow - _userOffReadUtc < SettingsEvery) return _userOff;
        _userOffReadUtc = DateTime.UtcNow;
        using var activity = UiThreadWatch.Activity("archive indexing: user preference");
        try { return _userOff = _configService.GetUserFlag(ArchiveScope.UserOffFlag); }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Archive user preference unreadable");
            return _userOff = true;
        }
    }

    /// <summary>Lists the mailboxes Outlook has, so the ConfigTool can offer them; written only when they change.</summary>
    private async Task PublishOpenStoresAsync()
    {
        var stores = _data.EnumerateStores().Where(s => s.StoreId.Length > 0)
            .Select(s => new ArchiveOpenStore { Id = s.StoreId, Name = s.DisplayName, Open = s.IsOpen, HasAccount = s.HasAccount, IsDataFileStore = s.IsDataFileStore }).ToList();
        string json = JsonSerializer.Serialize(stores);
        if (json == _publishedStores) return;
        await _db!.SetMetaAsync(ArchiveScope.OpenStoresKey, json);
        _publishedStores = json;
    }

    /// <summary>
    /// Own addresses of the PSTs without an account: one open store per step is guessed from its Sent Items (once per
    /// Outlook session); the user's choices in the ConfigTool are kept. A changed meta value is applied to the archive.
    /// </summary>
    private async Task ApplyOwnAddressesAsync()
    {
        if (_stopping) return;
        // A table read of up to 200 rows: only while the user is away, like the rest of the indexing.
        if (Volatile.Read(ref _interactive) == 0 && _idleMs() >= IdleMs && _data.EnumerateStores()
                .FirstOrDefault(s => s.IsOpen && s.IsDataFileStore && !s.HasAccount && s.StoreId.Length > 0 && !_ownGuessed.Contains(s.StoreId)) is { } store)
        {
            _ownGuessed.Add(store.StoreId);
            if (_data.GuessStoreOwnAddresses(store.StoreId) is { } guess)
            {
                string? stored = await _db!.GetMetaAsync(StoreOwnAddresses.MetaKey);
                StoreOwnAddresses.Parse(stored).TryGetValue(store.StoreId, out var known);
                if (known == null || !known.Guessed.SequenceEqual(guess.Guessed) || !known.Names.SequenceEqual(guess.Names))
                {
                    await _db.SetMetaAsync(StoreOwnAddresses.MetaKey, StoreOwnAddresses.WithGuess(stored, store.StoreId, guess));
                    OnlyWorkspaceLog.Logger.Information("Archive: {Count} own addresses guessed for a mailbox without account", guess.Guessed.Count);
                }
            }
        }
        string? json = await _db!.GetMetaAsync(StoreOwnAddresses.MetaKey);
        if (json == _appliedOwn || _stopping) return;
        await SetOwnAddressesAsync(_db, json);
    }

    /// <summary>Accounts' addresses plus the PSTs' ones from <paramref name="json"/>: the user's own in the archive and in Outlook calls.</summary>
    private async Task SetOwnAddressesAsync(ArchiveClient db, string? json)
    {
        _data.SetStoreOwnAddresses(StoreOwnAddresses.All(json), StoreOwnAddresses.Names(json));
        await db.SetOwnAddressesAsync(_data.GetOwnAddressList());
        _appliedOwn = json;
    }

    /// <summary>A mailbox choice saved by the ConfigTool: drop the mailboxes taken out and list the folders again.</summary>
    private async Task ApplyScopeChangeAsync()
    {
        string? scope = await _db!.GetMetaAsync(MetaStores);
        if (_scopeKnown && scope == _appliedScope) return;
        bool first = !_scopeKnown;
        _appliedScope = scope;
        _scopeKnown = true;
        if (first) return;
        var storeIds = ArchiveScope.ParseStores(scope);
        ResetScope();
        OnlyWorkspaceLog.Logger.Information("Archive: mailbox choice changed, {Stores} mailboxes selected (0 = all)", storeIds.Count);
    }

    private void ResetScope()
    {
        _cleanup = true;
        _pending.Clear();
        CloseReader();
        CloseRecentReader();
        _work.Clear();
        _recentWork.Clear();
        ResetFolderList();
        _lastPassUtc = default;
    }

    private async Task OpenAsync(bool create = false)
    {
        try
        {
            var client = new ArchiveClient(Profile, _directory, _lifetime.Token);
            // Off the UI thread: the launch makes no Outlook call (about 50 ms on WS-DP01, 27/10/2026).
            if (!await Task.Run(_launchEngine))
                OnlyWorkspaceLog.Logger.Warning("Archive: tray launcher unavailable; trying the existing engine");
            await client.OpenAsync(create);
            if (_stopping) return;
            await SetOwnAddressesAsync(client, await client.GetMetaAsync(StoreOwnAddresses.MetaKey));
            await client.HeartbeatAsync("starting", null);
            _db = client;
            _error = null;
            _phase = "starting";
            _cleanup = true;
            _scopeKnown = false;
            _publishedStores = null;
            _timer.Interval = WaitIntervalMs;
            if (!_stopping) _timer.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is TimeoutException)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Archive database cannot be read");
            _phase = "error";
            _error = "Motore dell'archivio non disponibile. Riprova; se il problema continua, consulta il log.";
            _connectAfterUtc = DateTime.UtcNow.AddSeconds(15);
        }
    }

    private void Close()
    {
        CloseReader();
        CloseRecentReader();
        _work.Clear();
        _recentWork.Clear();
        _pending.Clear();
        ResetFolderList();
        _db = null;
    }

    private void ResetFolderList()
    {
        _folderWalk?.Dispose();
        _folderWalk = null;
        _folderList = null;
    }

    private void CloseReader()
    {
        _reader?.Dispose();
        _reader = null;
        _folder = null;
    }

    private void CloseRecentReader()
    {
        _recentReader?.Dispose();
        _recentReader = null;
        _recentFolder = null;
    }

    private async Task<IReadOnlyList<string>> StoreIdsAsync() =>
        ArchiveScope.ParseStores(_db == null ? null : await _db.GetMetaAsync(MetaStores));

    /// <summary>
    /// Refresh recent mail without discarding the historical table's current position. False while the folder list
    /// is still being read: it goes on at the next tick, a slice at a time.
    /// </summary>
    private async Task<bool> StartPassAsync()
    {
        if (_folderWalk != null || _folderList == null || DateTime.UtcNow - _folderListUtc > FolderListEvery)
        {
            if (_stopping) return false;
            if (_folderWalk == null)
            {
                var stores = await StoreIdsAsync();
                if (_stopping) return false;
                _folderWalk = _data.WalkArchiveFolders(stores.ToList());
            }
            var sw = Stopwatch.StartNew();
            bool listed;
            do listed = _folderWalk.Step(FolderListBatch);
            while (!listed && !_stopping && sw.ElapsedMilliseconds < SliceMs);
            if (!listed) return false;
            _folderList = _folderWalk.Folders;
            _completedStores = _folderWalk.CompleteStores;
            _folderIssues = _folderWalk.Issues;
            _folderWalk.Dispose();
            _folderWalk = null;
            _folderListUtc = DateTime.UtcNow;
            // The list is read every 2 minutes: Information only when it changes.
            string scope = string.Join("|", _folderList.Select(f => f.StoreId).Distinct().OrderBy(s => s, StringComparer.Ordinal)) + "#" + _folderList.Count;
            if (scope != _loggedScope) OnlyWorkspaceLog.Logger.Information("Archive: {Folders} mail folders in scope", _folderList.Count);
            else OnlyWorkspaceLog.Logger.Debug("Archive: {Folders} mail folders in scope", _folderList.Count);
            _loggedScope = scope;
        }
        _closeCheck = false;
        _checkDueUtc = default;
        var folders = new List<ArchiveFolder>();
        foreach (var f in _folderList) folders.Add(await _db!.GetOrAddFolderAsync(f.StoreId, f.EntryId, f.Path));
        _lifetime.Token.ThrowIfCancellationRequested();
        var open = _data.EnumerateStores().Where(s => s.IsOpen && s.StoreId.Length > 0).Select(s => s.StoreId).ToList();
        await _db!.SetListedFoldersAsync(folders.Select(f => f.Id).ToList(), _completedStores, open);
        await _db.SetMetaAsync("folder_issues", _folderIssues?.Count > 0 ? JsonSerializer.Serialize(_folderIssues) : null);
        if (folders.Count > 0 && folders.All(f => f.Done))
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            await _db.ConfirmInitialInventoryAsync();
            if (!await ConfirmUnseenCacheCopiesAsync())
            {
                _closeCheck = true;
                return false;
            }
            // Close a verified inventory before scheduling the next full reconciliation.
            if (await _db.FinishRescanAsync() is { } gone)
            {
                // A check closes every few minutes: Information only when it removed emails.
                if (gone > 0) OnlyWorkspaceLog.Logger.Information("Archive: full check closed, {Gone} no longer found", gone);
                else OnlyWorkspaceLog.Logger.Debug("Archive: full check closed, 0 no longer found");
                _checkDueUtc = DateTime.UtcNow + _rescanEvery;
            }
            else if (await _db.RescanDueAsync(_rescanEvery))
            {
                await _db.BeginRescanAsync(folders.Select(f => f.Id));
                _closeCheck = true;
                folders.Clear();
                foreach (var f in _folderList) folders.Add(await _db.GetOrAddFolderAsync(f.StoreId, f.EntryId, f.Path));
                OnlyWorkspaceLog.Logger.Debug("Archive: full check started on {Folders} folders", folders.Count);
            }
        }
        _work.Clear();
        foreach (var folder in folders) _recentWork.Enqueue(folder);
        foreach (var folder in folders.Where(f => !f.Done && f.Id != _folder?.Id)) _work.Enqueue(folder);
        _pending.Clear();
        _lastPassUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// A cache folder (IMAP, OST) cannot prove an email gone: Outlook is asked about each one the check did not see,
    /// a slice at a time. True when none is left to ask.
    /// </summary>
    private async Task<bool> ConfirmUnseenCacheCopiesAsync()
    {
        var unseen = await _db!.UnseenCacheCopiesAsync(50);
        if (unseen.Count == 0 || _stopping) return unseen.Count == 0;
        var open = new HashSet<string>(_data.EnumerateStores().Where(s => s.IsOpen).Select(s => s.StoreId), StringComparer.OrdinalIgnoreCase);
        var kept = new List<long>();
        var gone = new List<long>();
        var sw = Stopwatch.StartNew();
        foreach (var email in unseen)
        {
            if (_stopping || sw.ElapsedMilliseconds >= SliceMs) break;
            // A mailbox closed meanwhile proves nothing.
            bool? found = open.Contains(email.StoreId) ? _data.ArchiveItemExists(email.EntryId, email.StoreId) : null;
            (found == false ? gone : kept).Add(email.Id);
        }
        if (_stopping) return false;
        await _db.ConfirmCacheCopiesAsync(kept, gone);
        return false;
    }

    /// <summary>
    /// A folder did not open: if its mailbox is no longer open in Outlook its other queued folders would fail too,
    /// so they leave both queues and the next pass lists the folders again.
    /// </summary>
    private void DropIfStoreClosed(string storeId)
    {
        if (_data.EnumerateStores().Any(s => s.IsOpen && string.Equals(s.StoreId, storeId, StringComparison.OrdinalIgnoreCase))) return;
        int dropped = Drop(_work) + Drop(_recentWork);
        _folderList = null;
        _lastPassUtc = default;
        OnlyWorkspaceLog.Logger.Information("Archive: mailbox no longer open, {Folders} queued folders dropped", dropped);

        int Drop(Queue<ArchiveFolder> queue)
        {
            var keep = queue.Where(f => !string.Equals(f.StoreId, storeId, StringComparison.OrdinalIgnoreCase)).ToList();
            int count = queue.Count - keep.Count;
            queue.Clear();
            foreach (var folder in keep) queue.Enqueue(folder);
            return count;
        }
    }

    private async Task<bool> StepHeadersAsync()
    {
        if (_stopping) return false;
        if (_recentReader == null && _recentWork.Count == 0 &&
            (_lastPassUtc == default || DateTime.UtcNow - _lastPassUtc >= NewMailEvery || (_closeCheck && _reader == null && _work.Count == 0) ||
             (_checkDueUtc != default && DateTime.UtcNow >= _checkDueUtc)))
        {
            if (!await StartPassAsync()) return !_stopping;
            if (_stopping) return false;
        }
        bool recent = _recentReader != null || _recentWork.Count > 0;
        if (recent && _recentReader == null)
        {
            _recentFolder = _recentWork.Dequeue();
            var newest = _recentFolder.NewestUtc ?? DateTime.UtcNow.AddDays(-1);
            string? checkpoint = await _db!.GetMetaAsync("local_modified:" + _recentFolder.Id, _lifetime.Token);
            if (_stopping) return false;
            var since = long.TryParse(checkpoint, out long ticks) ? new DateTime(ticks, DateTimeKind.Utc).AddMinutes(-2) : DateTime.UtcNow.AddDays(-1);
            _recentUntilUtc = DateTime.UtcNow;
            _recentReader = _data.OpenArchiveReader(_recentFolder.EntryId, _recentFolder.StoreId, newest.AddMinutes(-2), null, since, _recentUntilUtc);
            if (_recentReader == null)
            {
                await _db!.SetFolderErrorAsync(_recentFolder.Id, _recentFolder.UnavailableMessage());
                DropIfStoreClosed(_recentFolder.StoreId);
                _recentFolder = null;
                return true;
            }
        }
        if (!recent && _reader == null)
        {
            if (_work.Count == 0) return false;
            var queued = _work.Dequeue();
            _folder = await _db!.GetOrAddFolderAsync(queued.StoreId, queued.EntryId, string.Empty);
            if (_stopping) return false;
            _reader = _data.OpenArchiveReader(_folder.EntryId, _folder.StoreId, null, _folder.OldestUtc?.AddMinutes(1));
            if (_reader == null)
            {
                await _db.SetFolderErrorAsync(_folder.Id, _folder.UnavailableMessage());
                DropIfStoreClosed(_folder.StoreId);
                _folder = null;
                return true;
            }
        }

        var sw = Stopwatch.StartNew();
        var reader = recent ? _recentReader! : _reader!;
        var folder = recent ? _recentFolder! : _folder!;
        var rows = reader.ReadBatch(_batch);
        if (_stopping) return false;
        bool end = reader.EndOfTable;
        await _db!.AddHeadersAsync(folder, rows, folderDone: !recent && end, verified: !recent && end, canDeleteAbsent: reader.CanDeleteAbsent);
        if (end)
        {
            if (recent)
            {
                await _db.SetMetaAsync("local_modified:" + folder.Id, _recentUntilUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), _lifetime.Token);
                CloseRecentReader();
            }
            else CloseReader();
        }

        if (sw.ElapsedMilliseconds > SliceMs * 3 / 2) _batch = Math.Max(25, _batch / 2);
        else if (sw.ElapsedMilliseconds < SliceMs / 2) _batch = Math.Min(400, _batch * 2);
        return true;
    }

    private async Task<bool> StepBodiesAsync()
    {
        var sw = Stopwatch.StartNew();
        bool worked = false;
        // Bodies read in this slice are saved together: one commit (fsync) for the group instead of one each.
        var group = new List<ArchiveBodyItem>();
        long groupChars = 0;
        do
        {
            if (_stopping) return worked;
            if (_pending.Count == 0)
            {
                await SaveGroupAsync(group);
                groupChars = 0;
                foreach (var next in await _db!.NextPendingBodiesAsync(5)) _pending.Enqueue(next);
                if (_pending.Count == 0) return worked;
            }
            if (_stopping) return worked;
            var email = _pending.Dequeue();
            worked = true;
            // A130: the engine requeues scans as soon as OCR is turned back on; read with the setting of now, not of the last step.
            bool includeAttachments = ReadSettings(refresh: true).IndexAttachmentText;
            bool ocr = OcrEnabled;
            var body = await ReadCompleteAsync(email.EntryId, email.StoreId, _lifetime.Token, includeAttachments, ocr);
            if (_stopping) return worked;
            if (body == null)
            {
                await SaveGroupAsync(group);
                groupChars = 0;
                await _db!.SetBodyStateAsync(email.Id, ArchiveBodyState.Retryable);
                await _db.RequestRescanAsync();
                continue;
            }
            // Switched during the read: nothing is saved, the email is still pending and is read again with the new setting.
            ReadSettings(refresh: true);
            if (OcrEnabled != ocr) continue;
            group.Add(new ArchiveBodyItem { EmailId = email.Id, Body = body });
            groupChars += body.Text.Length + body.Attachments.Sum(a => (long)a.Text.Length);
            if (group.Count >= MaxGroupBodies || groupChars >= MaxGroupChars)
            {
                await SaveGroupAsync(group);
                groupChars = 0;
            }
        }
        while (sw.ElapsedMilliseconds < SliceMs);
        await SaveGroupAsync(group);
        return true;
    }

    private const int MaxGroupBodies = 20;
    private const long MaxGroupChars = 2_000_000;

    /// <summary>
    /// One transaction for the group. If it fails (a stale attachment revision, a deleted email), the bodies are saved one by one,
    /// so the error surfaces for the body that causes it, as it did before grouping.
    /// </summary>
    private async Task SaveGroupAsync(List<ArchiveBodyItem> group)
    {
        if (group.Count == 0) return;
        var items = group.ToArray();
        group.Clear();
        if (items.Length == 1) { await _db!.SaveBodyAsync(items[0].EmailId, items[0].Body); return; }
        try { await _db!.SaveBodiesAsync(items); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var item in items) await _db!.SaveBodyAsync(item.EmailId, item.Body);
        }
    }


    private readonly List<CancellationTokenSource> _attachmentReads = new();

    private void CancelAttachmentReads()
    {
        foreach (var read in _attachmentReads.ToArray()) read.Cancel();
    }

    private bool OcrEnabled => _config?.AttachmentOcr != false;

    /// <param name="ocr">The OCR setting the caller read; null: the one loaded last.</param>
    private async Task<ArchiveBody?> ReadCompleteAsync(string entryId, string storeId, CancellationToken ct, bool includeAttachments = true, bool? ocr = null)
    {
        bool readScans = ocr ?? OcrEnabled;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
        read.CancelAfter(TimeSpan.FromMinutes(2));
        ct = read.Token;
        var body = _data.ReadForArchive(entryId, storeId);
        ct.ThrowIfCancellationRequested();
        if (body == null || body.Unavailable) return body;
        if (!includeAttachments) return body;
        Func<AttachmentFacts, bool> wanted = facts => (!facts.IsInline || facts.IsPecReceiptData) && AttachmentTextFormats.ArchiveReferenceReason(facts.Extension, facts.DetectedType) == null;
        var copies = _data.CopyAttachments(entryId, storeId, wanted, 1, CopiesPerCall);
        if (copies == null) { ct.ThrowIfCancellationRequested(); body.AttachmentsLimited = true; return body; }
        var directories = new List<string> { copies.Directory };
        _attachmentReads.Add(read);
        try
        {
            // Cancelled during the copy: the finally still deletes the copies.
            ct.ThrowIfCancellationRequested();
            var paths = copies.Saved.ToDictionary(p => p.Key, p => p.Value);
            // No limit on the number of attachments: the rest is copied a slice at a time, yielding the UI thread between slices.
            int uiThread = Environment.CurrentManagedThreadId;
            var slice = new UiSlice();
            for (int next = copies.NextIndex; next > 0;)
            {
                await slice.NextAsync(uiThread, ct);
                var more = _data.CopyAttachments(entryId, storeId, wanted, next, CopiesPerCall);
                if (more == null || more.All.Count != copies.All.Count) throw new InvalidOperationException("L'email è cambiata durante la lettura degli allegati; riprovare.");
                directories.Add(more.Directory);
                foreach (var pair in more.Saved) paths[copies.All[IndexOf(more.All, pair.Key)]] = pair.Value;
                next = more.NextIndex;
            }
            foreach (var facts in copies.All)
            {
                ct.ThrowIfCancellationRequested();
                paths.TryGetValue(facts, out var path);
                ArchiveAttachmentReader.Add(body, await ArchiveAttachmentReader.ReadAsync(facts, (body.Attachments.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), path, ct,
                    _db == null ? null : _db.GetAttachmentTextAsync, readScans));
            }
            // Extraction yields to the UI; the message may have changed meanwhile.
            ct.ThrowIfCancellationRequested();
            var current = _data.ReadForArchive(entryId, storeId);
            if (current == null || current.ModifiedUtc != body.ModifiedUtc) throw new InvalidOperationException("L'email è cambiata durante la lettura degli allegati; riprovare.");
            return body;
        }
        finally
        {
            _attachmentReads.Remove(read);
            string root = Path.GetFullPath(OnlyWorkspace.Core.AppPaths.ScanDirectory) + Path.DirectorySeparatorChar;
            foreach (var copy in directories)
            {
                string directory = Path.GetFullPath(copy);
                if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected archive attachment directory.");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }

    // Copies per Outlook call: a copy can be 30 MB, and Outlook disables an add-in that holds its UI thread.
    private const int CopiesPerCall = 2;

    private static int IndexOf(IReadOnlyList<AttachmentFacts> list, AttachmentFacts facts)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], facts)) return i;
        throw new InvalidOperationException("Allegato copiato fuori dall'elenco.");
    }

    // --- commands from the pane (UI thread) -------------------------------------------------------------

    public async Task<ArchiveStatus> GetStatusAsync()
    {
        using (await PaneLockAsync())
        {
            if (!_stopping && ReadSettings(refresh: true).Enabled) await EnsureOpenAsync(refresh: true);
            var settings = ReadSettings();
            var status = new ArchiveStatus
            {
                Available = settings.Enabled,
                Active = _db != null || (settings.Enabled && ArchiveDb.Exists(_directory, Profile)),
                Phase = settings.Enabled ? _phase : "off",
                Error = _error,
                MaxBytes = 0
            };
            try
            {
                var root = Path.GetPathRoot(_directory);
                if (!string.IsNullOrEmpty(root)) status.DiskFreeBytes = new DriveInfo(root!).AvailableFreeSpace;
            }
            catch (Exception)
            {
                // Free space is informative only.
            }
            if (_db != null)
            {
                if (await _db.GetMetaAsync("folder_issues") != null)
                    status.Error = "Elenco cartelle incompleto; alcune sorgenti non sono state verificate.";
                status.Stats = await _db.GetStatsAsync();
                status.Paused = await _db.GetMetaAsync(MetaPaused) == "1";
                status.StoreIds = await StoreIdsAsync();
                status.IndexingNow = _indexNow;
            }
            return status;
        }
    }

    /// <summary>Test seam: creates the archive and sets the mailboxes to index; empty = every open store.</summary>
    internal async Task ActivateAsync(IReadOnlyCollection<string> storeIds)
    {
        await _operations.WaitAsync();
        try
        {
            if (_stopping) return;
            if (!ReadSettings().Enabled) return;
            if (_db == null) await OpenAsync(create: true);
            if (_db == null) return;

            string scope = JsonSerializer.Serialize(storeIds);
            await _db.SetMetaAsync(MetaStores, scope);
            await _db.SetMetaAsync(MetaPaused, null);
            // New scope: drop the mailboxes taken out, list the folders again and start a pass at the next tick.
            _appliedScope = scope;
            _scopeKnown = true;
            ResetScope();
            _timer.Interval = WaitIntervalMs;
            if (!_stopping) _timer.Start();

        }
        finally { _operations.Release(); }
    }

    /// <summary>An archived email was not found in Outlook: the full check that follows moved emails is brought forward.</summary>
    public async Task ReportMissingAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_stopping) return;
            if (_db == null) return;
            await _db.RequestRescanAsync();
            // The check starts with a pass: do not wait for the next round of new mail.
            if (_work.Count == 0 && _reader == null) _lastPassUtc = default;

        }
        finally { _operations.Release(); }
    }

    /// <summary>
    /// Indicizza ora: index without waiting for the PC to be idle until nothing is left, in the usual short slices;
    /// it also resumes a paused archive and starts a pass at the next tick.
    /// </summary>
    public async Task IndexNowAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_stopping || _db == null) return;
            await _db.SetMetaAsync(MetaPaused, null);
            _indexNow = true;
            if (_work.Count == 0 && _reader == null && _recentReader == null && _recentWork.Count == 0) _lastPassUtc = default;
            _phase = "waiting";
            _timer.Interval = WorkIntervalMs;
            OnlyWorkspaceLog.Logger.Information("Archive: Indicizza ora asked");
        }
        finally { _operations.Release(); }
    }

    public async Task SetPausedAsync(bool paused)
    {
        if (paused) CancelAttachmentReads();
        await _operations.WaitAsync();
        try
        {
            if (_stopping) return;
            if (_db == null) return;
            await _db.SetMetaAsync(MetaPaused, paused ? "1" : null);
            if (paused)
            {
                _indexNow = false;
                CloseReader();
                CloseRecentReader();
                _work.Clear();
                _recentWork.Clear();
                _lastPassUtc = default;
            }
            _phase = paused ? "paused" : "waiting";
            if (!paused) _timer.Interval = WaitIntervalMs;

        }
        finally { _operations.Release(); }
    }

    /// <summary>Removes the archive of this Outlook profile; it can be activated again later.</summary>
    public async Task DeleteArchiveAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_stopping) return;
            var client = _db ?? new ArchiveClient(Profile, _directory, _lifetime.Token);
            Close();
            _indexNow = false;
            // Deleting means "no archive for me": without this the next step would create it again.
            try { _configService.SetUserFlag(ArchiveScope.UserOffFlag, true); }
            catch (Exception ex) { OnlyWorkspaceLog.Logger.Warning(ex, "Archive: the user choice could not be saved after deletion"); }
            _userOff = true;
            _userOffReadUtc = DateTime.UtcNow;
            await client.DeleteAsync();
            _error = null;
            _phase = "off";
            _lastPassUtc = default;

        }
        finally { _operations.Release(); }
    }

    public void Dispose()
    {
        Stop();
        _timer.Tick -= OnTick;
        _timer.Dispose();
        Close();
        _lifetime.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
