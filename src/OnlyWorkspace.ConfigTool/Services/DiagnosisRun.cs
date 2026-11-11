using System;
using System.Threading;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>
/// State of the «Diagnosi» of the profile being edited: the connection check (10 s) and the capability probes that follow it.
/// No controls: the form asks it what runs and cancels it; a run clears only its own slot, so a late end of an older run
/// never frees the slot of a newer one.
/// </summary>
internal sealed class DiagnosisRun
{
    private InteractiveRequest? _connection;
    private CancellationTokenSource? _probe;

    public bool ConnectionRunning => _connection != null;
    public bool ProbeRunning => _probe != null;
    public bool Busy => _connection != null || _probe != null;

    /// <summary>Takes the connection slot; false when a connection check or probes already run.</summary>
    public bool BeginConnection(InteractiveRequest run)
    {
        if (Busy) return false;
        _connection = run;
        return true;
    }

    public void EndConnection(InteractiveRequest run)
    {
        if (ReferenceEquals(_connection, run)) _connection = null;
    }

    /// <summary>Takes the probe slot; false when probes already run.</summary>
    public bool BeginProbe(CancellationTokenSource run)
    {
        if (_probe != null) return false;
        _probe = run;
        return true;
    }

    public bool IsProbe(CancellationTokenSource run) => ReferenceEquals(_probe, run);

    public void EndProbe(CancellationTokenSource run)
    {
        if (ReferenceEquals(_probe, run)) _probe = null;
    }

    /// <summary>The second press of the button: stops what runs, the connection check first. False when nothing ran.</summary>
    public bool CancelRunning()
    {
        if (_connection != null) { _connection.Cancel(); return true; }
        if (_probe != null) { _probe.Cancel(); return true; }
        return false;
    }

    public void CancelConnection() => _connection?.Cancel();

    public void CancelProbe() => _probe?.Cancel();

    /// <summary>The profile, its key or its connection settings changed or the form closes: nothing that runs applies any more.</summary>
    public void CancelAll()
    {
        _probe?.Cancel();
        _connection?.Cancel();
    }
}
