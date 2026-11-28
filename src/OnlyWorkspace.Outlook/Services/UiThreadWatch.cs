using System;
using System.Diagnostics;
using System.Threading;
using OnlyWorkspace.Infrastructure.Logging;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>
/// Notices when Outlook's UI thread stops processing messages for more than 250 ms and logs how long, with the
/// add-in work under way at that moment. Outlook disables an add-in that holds this thread too long; on a PC where
/// that happened the log says which work to look at. A UI timer beats every 100 ms, a background thread watches it.
/// </summary>
internal static class UiThreadWatch
{
    private const int BeatMs = 100;
    private const int StallMs = 250;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long _lastBeatMs;
    // Each thread's own name; the watcher reads the UI thread's, so work named on another thread is not counted.
    [ThreadStatic] private static string? _current;
    private static volatile string? _activity;
    private static int _uiThread;
    private static WinFormsTimer? _beat;
    private static Thread? _watcher;
    private static volatile bool _stopped;

    /// <summary>Call on the Outlook UI thread.</summary>
    public static void Start()
    {
        if (_beat != null) return;
        _uiThread = Environment.CurrentManagedThreadId;
        _activity = _current;
        Interlocked.Exchange(ref _lastBeatMs, Clock.ElapsedMilliseconds);
        _beat = new WinFormsTimer { Interval = BeatMs };
        _beat.Tick += (_, _) => Interlocked.Exchange(ref _lastBeatMs, Clock.ElapsedMilliseconds);
        _beat.Start();
        _watcher = new Thread(Watch) { IsBackground = true, Name = "OnlyWorkspace UI watch" };
        _watcher.Start();
    }

    public static void Stop()
    {
        _stopped = true;
        _beat?.Stop();
    }

    /// <summary>The name this thread's work would be logged with if it stalled now; null outside the add-in's synchronous work.</summary>
    internal static string? CurrentActivity => _current;

    // A delayed heartbeat can predate a ribbon callback. Report that separately from the callback's own duration.
    internal static long BeatAgeMs => Math.Max(0, Clock.ElapsedMilliseconds - Interlocked.Read(ref _lastBeatMs));

    /// <summary>
    /// Names the add-in work running from now until the result is disposed (nested scopes restore the outer one).
    /// Synchronous work only: a scope held across an await names whatever the UI thread runs meanwhile.
    /// </summary>
    public static IDisposable Activity(string label)
    {
        var outer = _current;
        Set(label);
        return new Scope(outer);
    }

    private static void Set(string? label)
    {
        _current = label;
        if (Environment.CurrentManagedThreadId == _uiThread) _activity = label;
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _outer;
        private bool _done;
        public Scope(string? outer) => _outer = outer;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Set(_outer);
        }
    }

    private static void Watch()
    {
        long stalledFor = 0;
        string? during = null;
        while (!_stopped)
        {
            Thread.Sleep(BeatMs / 2);
            long since = Clock.ElapsedMilliseconds - Interlocked.Read(ref _lastBeatMs);
            if (since > StallMs)
            {
                stalledFor = since;
                during ??= _activity;
                continue;
            }
            if (stalledFor == 0) continue;
            // Outlook's own work (sync, search, another add-in) is no lead for this add-in: debug only.
            if (during != null) OnlyWorkspaceLog.Logger.Warning("Outlook UI thread blocked for about {Ms} ms during {Activity}", stalledFor, during);
            else OnlyWorkspaceLog.Logger.Debug("Outlook UI thread blocked for about {Ms} ms outside the add-in's work", stalledFor);
            stalledFor = 0;
            during = null;
        }
    }
}
