using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Infrastructure.Archive;

public sealed class ArchiveCollectorBusyException : InvalidOperationException
{
    public ArchiveCollectorBusyException(string message) : base(message) { }
}

/// <summary>Single owner of archives. Runs in ConfigTool; COM objects never cross this boundary.</summary>
public sealed partial class ArchiveEngine : IDisposable
{
    private readonly string _directory;
    private readonly FileStream _ownership;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, ArchiveDb> _archives = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArchiveProfileStatus> _status = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArchiveProfileStatus> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OriginalWork> _originals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KeyValuePair<string, DateTime>> _collectors = new(StringComparer.Ordinal);
    private readonly List<NamedPipeServerStream> _pipes = new();
    // Own lock: the listener keeps accepting connections while a command holds _gate.
    private readonly object _pipesGate = new();
    // Profiles being compacted or optimized outside _gate; their database lock is held for minutes.
    private readonly HashSet<string> _maintenance = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArchiveProfileStatus> _lastStatus = new(StringComparer.Ordinal);
    private const string MaintenanceText = "Manutenzione dell'archivio in corso: riprovare tra qualche minuto.";
    /// <summary>Test seam: runs inside maintenance, outside _gate.</summary>
    internal Action<string>? MaintenanceProbe;
    private readonly Task _listener;
    private readonly Timer _expiry;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _indexAttachmentText;
    private readonly Func<bool> _attachmentOcr;
    private readonly Func<string?> _ocrLanguage;
    private readonly DpapiHistoryStorageService _history;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => _ready.Task;
    private bool _disposed;

    private static readonly IReadOnlyDictionary<string, MethodInfo> Operations = ArchiveClient.DatabaseOperations.ToDictionary(n => n, n => typeof(ArchiveDb).GetMethod(n)!);
    private static readonly HashSet<string> CollectorOperations = new(StringComparer.Ordinal)
    {
        "Heartbeat", "OriginalRequests", "CompleteOriginal",
        nameof(ArchiveDb.SetOwnAddresses), nameof(ArchiveDb.GetOrAddFolder), nameof(ArchiveDb.SetListedFolders), nameof(ArchiveDb.AddHeaders),
        nameof(ArchiveDb.BeginRescan), nameof(ArchiveDb.ConfirmCacheCopies), nameof(ArchiveDb.FinishRescan), nameof(ArchiveDb.SetFolderError), nameof(ArchiveDb.RemoveOtherStores),
        nameof(ArchiveDb.SaveBody), nameof(ArchiveDb.SetBodyState), nameof(ArchiveDb.ConfirmInitialInventory),
    };

    private sealed class OriginalWork
    {
        internal string Profile = string.Empty;
        internal ArchiveOriginalRequest Request = new();
        internal ArchiveOriginalResult Result = new();
        internal DateTime StartedUtc = DateTime.UtcNow;
        internal bool Dispatched;
    }

    public ArchiveEngine(string? directory = null, Func<bool>? enabled = null) : this(directory, enabled, null) { }

    /// <param name="attachmentOcr">Administrator's OCR setting: turning it back on rereads what stayed unread (E8).</param>
    /// <param name="ocrLanguage">Language OCR reads on this PC ("" none, null not known yet): a new one rereads ocr-unavailable scans (A123).</param>
    public ArchiveEngine(string? directory, Func<bool>? enabled, Func<bool>? indexAttachmentText,
        Func<bool>? attachmentOcr = null, Func<string?>? ocrLanguage = null)
    {
        _ocrLanguage = ocrLanguage ?? (() => null);
        _directory = directory ?? ArchiveDb.DefaultDirectory;
        _history = new DpapiHistoryStorageService(string.Equals(Path.GetFullPath(_directory), Path.GetFullPath(ArchiveDb.DefaultDirectory), StringComparison.OrdinalIgnoreCase)
            ? OnlyWorkspace.Core.AppPaths.HistoryDirectory : Path.Combine(_directory, "history"));
        _enabled = enabled ?? (() => true);
        _indexAttachmentText = indexAttachmentText ?? (() => true);
        _attachmentOcr = attachmentOcr ?? (() => true);
        Directory.CreateDirectory(_directory);
        _ownership = new FileStream(Path.Combine(_directory, "engine.owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _expiry = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        _listener = Task.Run(ListenAsync);
    }

    /// <summary>
    /// Timer work. A busy engine skips it: blocked timer callbacks would pile up on the thread pool. An archive in
    /// maintenance holds its database lock for the whole compaction: its deletions wait for the end of it.
    /// </summary>
    internal void Sweep()
    {
        if (!Monitor.TryEnter(_gate)) return;
        try { ExpireOriginals(); foreach (var db in Idle()) ProcessDeletions(db); ScheduleFacts(); } finally { Monitor.Exit(_gate); }
    }

    /// <summary>Under _gate: the archives not in maintenance.</summary>
    private IEnumerable<ArchiveDb> Idle() => _archives.Where(pair => !_maintenance.Contains(pair.Key)).Select(pair => pair.Value).ToList();

    private async Task ListenAsync()
    {
        var clients = new List<Task>();
        bool first = true;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (clients.Count >= 7)
                {
                    await Task.WhenAny(clients).ConfigureAwait(false);
                    ObserveCompleted(clients);
                }
                var pipe = ArchivePipe.CreateServer(ArchivePipe.Name(_directory), first);
                _ready.TrySetResult(true);
                first = false;
                lock (_pipesGate) _pipes.Add(pipe);
                bool served = false;
                try
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    }
                    catch (IOException ex) when (!_stop.IsCancellationRequested)
                    {
                        // A client that connects and closes before the wait completes ends only this instance, not the listener.
                        OnlyWorkspaceLog.Logger.Warning("Archive connection closed before it was served: {Kind}", ex.GetType().Name);
                        continue;
                    }
                    ObserveCompleted(clients);
                    clients.Add(ServeAsync(pipe));
                    served = true;
                }
                finally
                {
                    // An instance created while Dispose ran is not in the list it closed: a leaked handle would make
                    // the next engine's first instance fail with access denied.
                    if (!served)
                    {
                        lock (_pipesGate) _pipes.Remove(pipe);
                        pipe.Dispose();
                    }
                }
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && (ex is OperationCanceledException || ex is ObjectDisposedException || ex is IOException)) { }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            OnlyWorkspaceLog.Logger.Error(ex, "Archive engine listener failed");
            throw;
        }
        finally { await Task.WhenAll(clients).ConfigureAwait(false); }
    }

    private static void ObserveCompleted(List<Task> clients)
    {
        foreach (var task in clients.Where(t => t.IsCompleted).ToList())
        {
            task.GetAwaiter().GetResult();
            clients.Remove(task);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var abort = timeout.Token.Register(() => pipe.Dispose());
            try
            {
                var request = await ArchivePipe.ReadAsync<ArchivePipeRequest>(pipe, timeout.Token).ConfigureAwait(false);
                timeout.CancelAfter(ArchivePipe.RequestTimeout(request.Operation));
                ArchivePipe.ValidateClient(pipe);
                var response = new ArchivePipeResponse { Id = request.Id };
                try
                {
                    ArchiveDb? maintained = null;
                    lock (_gate)
                    {
                        // Queued past its deadline: the client has already reported a failure, so it must not run now.
                        timeout.Token.ThrowIfCancellationRequested();
                        if (request.Version != ArchivePipe.Version) throw new InvalidDataException("Unsupported archive protocol version.");
                        bool batchId = ArchivePipe.BatchIdentity(request.Id, out var peer, out long sequence);
                        if ((!Guid.TryParseExact(request.Id, "N", out _) && !batchId) || request.Profile.Length > 512 || request.Operation.Length > 64 ||
                            request.Acknowledged == null || request.Acknowledged.Length > 32 ||
                            (batchId && (peer != request.Peer || request.Operation is not (nameof(ArchiveDb.AddHeaders) or nameof(ArchiveDb.SaveBody)))) ||
                            (request.Acknowledged.Length > 0 && (!batchId || request.Acknowledged.Any(n => n <= 0 || n >= sequence))))
                            throw new InvalidDataException("Invalid archive request identity.");
                        if (request.Operation is nameof(ArchiveDb.OptimizeIndex) or nameof(ArchiveDb.Compact)) maintained = BeginMaintenance(request, timeout.Token);
                        else response.Result = JsonSerializer.SerializeToElement(Execute(request, timeout.Token), ArchivePipe.Json);
                    }
                    if (maintained != null) response.Result = JsonSerializer.SerializeToElement(RunMaintenance(request, maintained), ArchivePipe.Json);
                }
                catch (Exception ex)
                {
                    var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                    response.Error = cause.Message;
                    response.ErrorCode = cause.GetType().Name;
                    response.Result = JsonSerializer.SerializeToElement<object?>(null);
                    OnlyWorkspaceLog.Logger.Warning("Archive command {Operation} failed: {Kind}", request.Operation, cause.GetType().Name);
                }
                await ArchivePipe.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (timeout.IsCancellationRequested || ex is IOException || ex is InvalidDataException || ex is JsonException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                if (!timeout.IsCancellationRequested) OnlyWorkspaceLog.Logger.Warning("Archive connection rejected: {Kind}", ex.GetType().Name);
            }
            finally { lock (_pipesGate) _pipes.Remove(pipe); }
        }
    }

    private ArchiveDb Store(string profile, bool create = false, CancellationToken ct = default)
    {
        if (_archives.TryGetValue(profile, out var db)) { db.ConfigureStorage(_indexAttachmentText(), _attachmentOcr(), _ocrLanguage()); return db; }
        if (!create && !ArchiveDb.Exists(_directory, profile)) throw new InvalidOperationException("L'archivio del profilo non è attivo.");
        db = ArchiveDb.Open(_directory, profile, ct.CanBeCanceled ? ct : _stop.Token);
        try
        {
            db.ConfigureStorage(_indexAttachmentText(), _attachmentOcr(), _ocrLanguage());
            _archives.Add(profile, db);
        }
        catch
        {
            db.Dispose();
            throw;
        }
        _status[profile] = new ArchiveProfileStatus { Profile = profile };
        ProcessDeletions(db);
        return db;
    }

    private object? Execute(ArchivePipeRequest request, CancellationToken ct)
    {
        var args = request.Arguments;
        if (!_enabled()) throw new InvalidOperationException("L'archivio non è abilitato nella configurazione amministrativa.");
        if (_maintenance.Count > 0 && MaintenanceAnswer(request, out var answer)) return answer;
        string Text(int index) => args[index].GetString() ?? string.Empty;
        if (request.Operation == "Profiles")
        {
            Discover();
            return _archives.Keys.Select(Status).Concat(_unreadable.Values).ToList();
        }
        if (request.Operation == "Open") { Store(request.Profile, args.Length == 1 && args[0].GetBoolean(), ct); return null; }
        if (request.Operation == "Delete")
        {
            if (_archives.TryGetValue(request.Profile, out var existing)) existing.Dispose();
            _archives.Remove(request.Profile);
            _status.Remove(request.Profile);
            _collectors.Remove(request.Profile);
            _unreadable.Remove(ArchiveDb.EngineFileOf(_directory, request.Profile));
            ArchiveDb.Delete(_directory, request.Profile);
            foreach (var id in _originals.Where(p => p.Value.Profile == request.Profile).Select(p => p.Key).ToList()) _originals.Remove(id);
            return null;
        }
        var db = Store(request.Profile, ct: ct);
        if (request.Acknowledged.Length > 0) db.AcknowledgeBatches(request.Peer, request.Acknowledged);
        if (CollectorOperations.Contains(request.Operation))
            ClaimCollector(request);
        switch (request.Operation)
        {
            case "Status": return Status(request.Profile);
            case "Heartbeat":
                var state = _status[request.Profile];
                state.LastOutlookUtc = DateTime.UtcNow;
                state.Phase = Text(0);
                state.Error = args[1].ValueKind == JsonValueKind.Null ? null : Text(1);
                return null;
            case "RequestOriginal":
                if (!Status(request.Profile).OutlookConnected) throw new InvalidOperationException("Outlook non è disponibile: la ricerca sull'indice resta utilizzabile.");
                var work = new OriginalWork
                {
                    Profile = request.Profile,
                    Request = new ArchiveOriginalRequest { Id = Guid.NewGuid().ToString("N"), EntryId = Text(0), StoreId = Text(1) }
                };
                _originals.Add(work.Request.Id, work);
                return work.Request.Id;
            case "OriginalRequests":
                ExpireOriginals();
                var pending = _originals.Values.Where(w => w.Profile == request.Profile && !w.Dispatched && !w.Result.Ready).Take(4).ToList();
                foreach (var item in pending) item.Dispatched = true;
                return pending.Select(w => w.Request).ToList();
            case "CompleteOriginal":
                if (_originals.TryGetValue(Text(0), out var completed) && completed.Profile == request.Profile)
                    completed.Result = new ArchiveOriginalResult { Ready = true, Text = args[1].ValueKind == JsonValueKind.Null ? null : Text(1), Error = args[2].ValueKind == JsonValueKind.Null ? null : Text(2) };
                return null;
            case "OriginalResult":
                ExpireOriginals();
                if (!_originals.TryGetValue(Text(0), out var result) || result.Profile != request.Profile)
                    throw new InvalidOperationException("Richiesta del testo scaduta o annullata.");
                if (result.Result.Ready) _originals.Remove(Text(0));
                return result.Result;
            case "CancelOriginal":
                if (_originals.TryGetValue(Text(0), out var cancelled) && cancelled.Profile == request.Profile)
                    _originals.Remove(Text(0));
                return null;
        }
        if (!Operations.TryGetValue(request.Operation, out var method)) throw new InvalidDataException("Unknown archive operation.");
        var parameters = method.GetParameters();
        bool batch = request.Operation is nameof(ArchiveDb.AddHeaders) or nameof(ArchiveDb.SaveBody);
        if (args.Length != parameters.Length - (batch ? 1 : 0)) throw new InvalidDataException("Invalid archive argument count.");
        var values = new object?[parameters.Length];
        for (int i = 0; i < args.Length; i++) values[i] = args[i].Deserialize(parameters[i].ParameterType, ArchivePipe.Json);
        if (batch) values[values.Length - 1] = request.Id;
        var output = method.Invoke(db, values);
        ScheduleFacts();
        if (request.Operation is nameof(ArchiveDb.FinishRescan) or nameof(ArchiveDb.RemoveOtherStores) or nameof(ArchiveDb.RemoveClosedStore)) ProcessDeletions(db);
        return request.Operation == nameof(ArchiveDb.AddHeaders)
            ? new ArchiveHeaderCommit { Added = (int)output!, Folder = (ArchiveFolder)values[0]! }
            : output;
    }

    private void ProcessDeletions(ArchiveDb db)
    {
        try
        {
            var pending = db.PendingDeletions();
            if (pending.Count == 0) return;
            foreach (string id in _originals.Where(pair => pending.Any(source => source.OutlookProfile == pair.Value.Profile &&
                         source.EntryId == pair.Value.Request.EntryId && source.StoreId == pair.Value.Request.StoreId)).Select(pair => pair.Key).ToArray())
                _originals.Remove(id);
            _history.InvalidateSourcesAsync(pending).GetAwaiter().GetResult();
            if (!db.CompleteDeletions(pending.Select(d => d.Id).ToArray()))
                OnlyWorkspaceLog.Logger.Warning("Archive history deleted; WAL checkpoint remains pending");
        }
        catch (HistoryCleanupIncompleteException ex)
        {
            OnlyWorkspaceLog.Logger.Warning("Archive deletion cleanup pending; unverified history files retained: {Files}", string.Join(", ", ex.Files));
        }
        catch (Exception ex) { OnlyWorkspaceLog.Logger.Warning("Archive deletion cleanup pending: {Kind}", ex.GetType().Name); }
    }

    /// <summary>Called under _gate: commands that would wait on a database in maintenance answer at once.</summary>
    private bool MaintenanceAnswer(ArchivePipeRequest request, out object? answer)
    {
        answer = null;
        if (!_maintenance.Contains(request.Profile)) return false;
        if (request.Operation == "Status") { answer = Status(request.Profile); return true; }
        // The indexer reads the pause flag: it waits as it does for a user pause.
        if (request.Operation == nameof(ArchiveDb.GetMeta) && request.Arguments.Length == 1 &&
            request.Arguments[0].ValueKind == JsonValueKind.String && request.Arguments[0].GetString() == ArchiveScope.PausedKey)
        {
            answer = "1";
            return true;
        }
        if (CollectorOperations.Contains(request.Operation)) throw new ArchiveCollectorBusyException(MaintenanceText);
        throw new InvalidOperationException(MaintenanceText);
    }

    /// <summary>Under _gate. Nothing is persisted: an interrupted compaction leaves no pause behind.</summary>
    private ArchiveDb BeginMaintenance(ArchivePipeRequest request, CancellationToken ct)
    {
        if (!_enabled()) throw new InvalidOperationException("L'archivio non è abilitato nella configurazione amministrativa.");
        if (_maintenance.Contains(request.Profile)) throw new InvalidOperationException(MaintenanceText);
        var db = Store(request.Profile, ct: ct);
        _lastStatus[request.Profile] = Status(request.Profile);
        _maintenance.Add(request.Profile);
        return db;
    }

    /// <summary>Outside _gate: other profiles and the listener stay available; the database serializes its own calls.</summary>
    private object? RunMaintenance(ArchivePipeRequest request, ArchiveDb db)
    {
        try
        {
            if (MaintenanceProbe is { } probe) db.Locked(() => probe(request.Operation));
            if (request.Operation == nameof(ArchiveDb.Compact)) db.Compact();
            else db.OptimizeIndex();
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _maintenance.Remove(request.Profile);
                // Deletions recorded before the maintenance were skipped by the timer.
                if (!_disposed && _archives.TryGetValue(request.Profile, out var current) && ReferenceEquals(current, db)) ProcessDeletions(db);
            }
        }
    }

    private ArchiveProfileStatus Status(string profile)
    {
        if (_maintenance.Contains(profile))
        {
            var last = _lastStatus.TryGetValue(profile, out var cached) ? cached : new ArchiveProfileStatus { Profile = profile };
            return new ArchiveProfileStatus
            {
                Profile = profile, Paused = true, Phase = "paused", OutlookConnected = last.OutlookConnected,
                LastOutlookUtc = last.LastOutlookUtc, Stats = last.Stats, Error = last.Error, LastCheckUtc = last.LastCheckUtc
            };
        }
        var db = Store(profile);
        var current = _status[profile];
        bool connected = current.LastOutlookUtc.HasValue && DateTime.UtcNow - current.LastOutlookUtc.Value < TimeSpan.FromSeconds(45);
        bool paused = db.GetMeta("paused") == "1";
        return new ArchiveProfileStatus
        {
            Profile = profile, Paused = paused, OutlookConnected = connected,
            LastOutlookUtc = current.LastOutlookUtc,
            Phase = paused ? "paused" : connected ? current.Phase : "waiting",
            Stats = db.GetStats(), Error = db.GetMeta("folder_issues") != null ? "Elenco cartelle incompleto; alcune sorgenti non sono state verificate." : current.Error,
            LastCheckUtc = long.TryParse(db.GetMeta("last_verified"), out long time) ? DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime : null
        };
    }

    private void ClaimCollector(ArchivePipeRequest request)
    {
        if (!Guid.TryParseExact(request.Peer, "N", out _)) throw new InvalidDataException("Invalid archive collector identity.");
        if (_collectors.TryGetValue(request.Profile, out var owner) && owner.Key != request.Peer && DateTime.UtcNow - owner.Value < TimeSpan.FromSeconds(45))
            throw new ArchiveCollectorBusyException("Un'altra istanza di Outlook sta acquisendo questo profilo; la ricerca resta disponibile.");
        _collectors[request.Profile] = new KeyValuePair<string, DateTime>(request.Peer, DateTime.UtcNow);
    }

    private void Discover()
    {
        foreach (var path in Directory.GetFiles(_directory, "*.engine.db"))
        {
            if (!path.EndsWith(".engine.db", StringComparison.OrdinalIgnoreCase)) continue;
            if (_archives.Values.Any(db => string.Equals(db.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                string? profile = ArchiveDb.ReadProfileName(path);
                if (profile == null || !string.Equals(ArchiveDb.EngineFileOf(_directory, profile), path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Archive profile identity unavailable or inconsistent.");
                Store(profile);
                _unreadable.Remove(path);
            }
            catch (Exception ex) when (ex is ArchiveUnreadableException || ex is Microsoft.Data.Sqlite.SqliteException || ex is IOException || ex is InvalidDataException)
            {
                _unreadable[path] = new ArchiveProfileStatus { Profile = "Archivio da ripristinare", Phase = "error", Error = ex.Message };
            }
        }
    }

    private void ExpireOriginals()
    {
        foreach (var id in _originals.Where(p => DateTime.UtcNow - p.Value.StartedUtc > TimeSpan.FromMinutes(3)).Select(p => p.Key).ToList())
            _originals.Remove(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready.TrySetCanceled();
        // .NET Framework: the wait of a pipe instance already closed can throw from its cancellation callback;
        // every callback still runs, and the databases must be closed anyway.
        try { _stop.Cancel(); }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is ObjectDisposedException)) { }
        _expiry.Dispose();
        lock (_pipesGate) foreach (var pipe in _pipes.ToArray()) pipe.Dispose();
        try { _listener.GetAwaiter().GetResult(); }
        finally
        {
            lock (_gate)
            {
                foreach (var db in _archives.Values) db.Dispose();
                _archives.Clear();
                _collectors.Clear();
                _originals.Clear();
            }
            _ownership.Dispose();
            _stop.Dispose();
        }
    }
}
