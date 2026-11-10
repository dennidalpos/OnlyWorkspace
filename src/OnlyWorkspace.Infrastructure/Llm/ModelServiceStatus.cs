using System;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Infrastructure.Llm;

/// <summary>What the IA indicator shows: light, short label, reason of an error, when it was observed and where the data goes.</summary>
public sealed class ModelServiceSnapshot
{
    public ModelServiceSnapshot(StatusLight light, string label, string? reason, DateTime? atUtc, bool external = false)
    {
        Light = light;
        Label = label;
        Reason = reason;
        AtUtc = atUtc;
        External = external;
    }

    public StatusLight Light { get; }
    public string Label { get; }
    public string? Reason { get; }
    public DateTime? AtUtc { get; }
    public bool External { get; }
}

/// <summary>
/// State of the active profile's model service, from real requests only: yellow while one runs, green after an
/// answer, red after a failure of the service, grey until something is verified. A request begun under another
/// profile is ignored when it ends, so a late answer does not paint the new profile.
/// </summary>
public sealed class ModelServiceStatus
{
    private readonly object _gate = new();
    private string? _profileKey;
    private LlmProfileConfig? _profile;
    private int _generation;
    private int _running;
    private bool _unusable;
    private ModelServiceSnapshot _last = Unverified;

    private static readonly ModelServiceSnapshot Unverified = new(StatusLight.Off, "non verificato", null, null);

    /// <summary>Raised after every change, on the thread that caused it.</summary>
    public event Action? Changed;

    public ModelServiceSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                var shown = _running > 0 ? new ModelServiceSnapshot(StatusLight.Working, "richiesta in corso", null, null) : _last;
                return new ModelServiceSnapshot(shown.Light, shown.Label, shown.Reason, shown.AtUtc, _profile?.IsExternal == true);
            }
        }
    }

    /// <summary>The active profile, its key (provider, endpoint, model, credentials) and why it cannot be used, if it cannot.</summary>
    public void Configure(LlmProfileConfig? profile, string? key, string? blockedReason)
    {
        lock (_gate)
        {
            if (profile == null)
            {
                Reset(null, null);
                _last = new ModelServiceSnapshot(StatusLight.Error, "non configurato", blockedReason, DateTime.UtcNow);
                _unusable = true;
            }
            else
            {
                if (key != _profileKey || _unusable != (blockedReason != null)) Reset(profile, key);
                _profile = profile;
                // A policy refusal is a wait for the administrator's notice, not a fault of the service.
                if (blockedReason != null) _last = new ModelServiceSnapshot(StatusLight.Paused, "bloccato dall'informativa", blockedReason, DateTime.UtcNow);
                else if (_unusable) _last = Unverified;
                _unusable = blockedReason != null;
            }
        }
        Changed?.Invoke();
    }

    private void Reset(LlmProfileConfig? profile, string? key)
    {
        _profileKey = key;
        _profile = profile;
        _generation++;
        _running = 0;
        _last = Unverified;
    }

    /// <summary>A request to the model starts; dispose the returned scope with its outcome.</summary>
    public Request Begin()
    {
        int generation;
        lock (_gate)
        {
            generation = _generation;
            _running++;
        }
        Changed?.Invoke();
        return new Request(this, generation);
    }

    /// <summary>Bind an explicit connection check to the current profile, credentials and policy.</summary>
    public ConnectionCheck CaptureConnectionCheck()
    {
        lock (_gate) return new ConnectionCheck(this, _generation);
    }

    private bool IsCurrent(int generation)
    {
        lock (_gate) return generation == _generation;
    }

    private bool Report(int generation, bool healthy, string? reason)
    {
        lock (_gate)
        {
            if (generation != _generation || _unusable) return false;
            _last = healthy
                ? new ModelServiceSnapshot(StatusLight.Ok, "connessione riuscita", null, DateTime.UtcNow)
                : new ModelServiceSnapshot(StatusLight.Error, "errore", reason, DateTime.UtcNow);
        }
        Changed?.Invoke();
        return true;
    }

    private void End(int generation, Exception? error, bool cancelled)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            _running = Math.Max(0, _running - 1);
            if (error == null && !cancelled) _last = new ModelServiceSnapshot(StatusLight.Ok, "risposta ricevuta", null, DateTime.UtcNow);
            else if (!cancelled && error != null && IsServiceFailure(error))
                _last = new ModelServiceSnapshot(StatusLight.Error, "errore", LlmErrorText.Describe(error, _profile), DateTime.UtcNow);
        }
        Changed?.Invoke();
    }

    /// <summary>A refusal of our own policy or limits says nothing about the service; neither does a cancellation.</summary>
    public static bool IsServiceFailure(Exception error)
    {
        var root = error is AggregateException { InnerException: { } inner } ? inner : error;
        return root is not (DataEgressBlockedException or ContextTooLongException or OperationCanceledException) || root is TaskCanceledException;
    }

    public sealed class Request
    {
        private readonly ModelServiceStatus _owner;
        private readonly int _generation;
        private int _ended;

        internal Request(ModelServiceStatus owner, int generation)
        {
            _owner = owner;
            _generation = generation;
        }

        public void Succeeded() => Finish(null, false);

        /// <summary>A cancelled token is the user stopping or the policy changing: no verdict on the service.</summary>
        public void Failed(Exception error, CancellationToken token) => Finish(error, token.IsCancellationRequested);

        /// <summary>The caller stopped reading before the end: no verdict.</summary>
        public void Abandoned() => Finish(null, true);

        private void Finish(Exception? error, bool cancelled)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) _owner.End(_generation, error, cancelled);
        }
    }

    public sealed class ConnectionCheck
    {
        private readonly ModelServiceStatus _owner;
        private readonly int _generation;

        internal ConnectionCheck(ModelServiceStatus owner, int generation)
        {
            _owner = owner;
            _generation = generation;
        }

        /// <summary>False when configuration changed: the caller must also discard the obsolete UI result.</summary>
        public bool Report(bool healthy, string? reason) => _owner.Report(_generation, healthy, reason);
        public bool IsCurrent => _owner.IsCurrent(_generation);
    }
}
