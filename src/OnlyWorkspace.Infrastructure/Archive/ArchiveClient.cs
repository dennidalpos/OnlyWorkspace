using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Infrastructure.Archive;

/// <summary>Async access to the per-user tray engine; no database connection in Outlook.</summary>
public sealed partial class ArchiveClient
{
    private readonly string _directory;
    private readonly CancellationToken _lifetime;
    private readonly string _peer = Guid.NewGuid().ToString("N");
    private readonly object _receipts = new();
    private readonly HashSet<long> _acknowledged = new();
    private long _batchSequence;
    public string Profile { get; }

    public ArchiveClient(string profile, string? directory = null, CancellationToken lifetime = default)
    {
        Profile = profile;
        _directory = directory ?? ArchiveDb.DefaultDirectory;
        _lifetime = lifetime;
    }

    private const int ConnectTimeoutMs = 5000;
    private const int ErrorSemTimeout = unchecked((int)0x80070079);

    /// <summary>
    /// .NET Framework waits 50 ms per WaitNamedPipe call when the token can be cancelled and throws its
    /// ERROR_SEM_TIMEOUT as IOException instead of waiting on while no server instance is listening (reference
    /// source 4.7.1, Pipe.cs ConnectInternal; .NET 10 retries). The engine listens on one instance at a time, so a busy
    /// moment must keep waiting here, within the same 5 s.
    /// </summary>
    private static async Task ConnectAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await pipe.ConnectAsync(Math.Max(1, ConnectTimeoutMs - (int)watch.ElapsedMilliseconds), ct).ConfigureAwait(false);
                return;
            }
            catch (IOException ex) when (ex.HResult == ErrorSemTimeout)
            {
                if (watch.ElapsedMilliseconds >= ConnectTimeoutMs) throw new TimeoutException("The archive engine did not accept the connection in time.", ex);
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }
    }

    internal async Task<T> CallAsync<T>(string operation, object?[] arguments, CancellationToken ct = default, string? requestId = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        deadline.CancelAfter(ArchivePipe.RequestTimeout(operation));
        using var pipe = new NamedPipeClientStream(".", ArchivePipe.Name(_directory), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        using var abort = deadline.Token.Register(() => pipe.Dispose());
        try
        {
            await ConnectAsync(pipe, deadline.Token).ConfigureAwait(false);
            ArchivePipe.ValidateServer(pipe);
            bool batch = requestId == null && operation is nameof(ArchiveDb.AddHeaders) or nameof(ArchiveDb.SaveBody);
            long sequence = batch ? Interlocked.Increment(ref _batchSequence) : 0;
            long[] confirmed;
            lock (_receipts) confirmed = batch ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Take(System.Linq.Enumerable.Where(_acknowledged, n => n < sequence), 32)) : Array.Empty<long>();
            var request = new ArchivePipeRequest
            {
                Profile = Profile, Peer = _peer, Operation = operation,
                Id = requestId ?? (batch ? _peer + ":" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : Guid.NewGuid().ToString("N")),
                Acknowledged = confirmed,
                Arguments = Array.ConvertAll(arguments, a => JsonSerializer.SerializeToElement(a, ArchivePipe.Json))
            };
            await ArchivePipe.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
            var response = await ArchivePipe.ReadAsync<ArchivePipeResponse>(pipe, deadline.Token).ConfigureAwait(false);
            if (response.Version != ArchivePipe.Version || response.Id != request.Id)
                throw new InvalidDataException("The archive response does not match this request or protocol version.");
            if (response.ErrorCode == nameof(ArchiveCollectorBusyException)) throw new ArchiveCollectorBusyException(response.Error!);
            if (response.Error != null) throw new InvalidOperationException(response.Error);
            var output = response.Result.Deserialize<T>(ArchivePipe.Json)!;
            if (batch)
                lock (_receipts) { foreach (long ack in confirmed) _acknowledged.Remove(ack); _acknowledged.Add(sequence); }
            return output;
        }
        catch (Exception ex) when (deadline.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested || _lifetime.IsCancellationRequested) throw new OperationCanceledException("Archive request cancelled.", ex, ct.IsCancellationRequested ? ct : _lifetime);
            throw new TimeoutException("The archive engine did not complete this request in time.", ex);
        }
    }

    public Task OpenAsync(bool create = false, CancellationToken ct = default) => CallAsync<object?>("Open", new object?[] { create }, ct);
    public Task DeleteAsync(CancellationToken ct = default) => CallAsync<object?>("Delete", Array.Empty<object?>(), ct);
    public Task<IReadOnlyList<ArchiveProfileStatus>> ProfilesAsync(CancellationToken ct = default) => CallAsync<IReadOnlyList<ArchiveProfileStatus>>("Profiles", Array.Empty<object?>(), ct);
    public Task<ArchiveProfileStatus> EngineStatusAsync(CancellationToken ct = default) => CallAsync<ArchiveProfileStatus>("Status", Array.Empty<object?>(), ct);
    public Task HeartbeatAsync(string phase, string? error, CancellationToken ct = default) => CallAsync<object?>("Heartbeat", new object?[] { phase, error }, ct);
    public Task SyncNowAsync(CancellationToken ct = default) => SetMetaAsync("sync_asked", "1", ct);
    public Task<IReadOnlyList<ArchiveOriginalRequest>> OriginalRequestsAsync(CancellationToken ct = default) => CallAsync<IReadOnlyList<ArchiveOriginalRequest>>("OriginalRequests", Array.Empty<object?>(), ct);
    public Task CompleteOriginalAsync(string id, string? text, string? error, CancellationToken ct = default) => CallAsync<object?>("CompleteOriginal", new object?[] { id, text, error }, ct);
    public Task<string> RequestOriginalAsync(string entryId, string storeId, CancellationToken ct = default) => CallAsync<string>("RequestOriginal", new object?[] { entryId, storeId }, ct);
    public Task<ArchiveOriginalResult> OriginalResultAsync(string id, CancellationToken ct = default) => CallAsync<ArchiveOriginalResult>("OriginalResult", new object?[] { id }, ct);
    public Task CancelOriginalAsync(string id, CancellationToken ct = default) => CallAsync<object?>("CancelOriginal", new object?[] { id }, ct);
}
