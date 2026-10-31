using System;
using System.Threading;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// Cancellation of the pane's running turn, on the UI thread. Interrompi (ribbon, pane button, Esc) stops it and
/// the stopped turn keeps what it has done so far; any other cancel (a new operation, Nuova chat, a conversation
/// from the history, Outlook closing) drops the turn's result.
/// COM-free so tests can drive it.
/// </summary>
internal sealed class PaneOperations
{
    private CancellationTokenSource? _active;
    private CancellationTokenSource? _stopped;

    public bool HasWork => _active != null || _stopped != null;

    /// <summary>A new turn supersedes the running one, which loses its result.</summary>
    public CancellationTokenSource Begin()
    {
        Cancel();
        return _active = new CancellationTokenSource();
    }

    /// <summary>The source is the running turn's, or the stopped one's still finishing.</summary>
    public bool IsCurrent(CancellationTokenSource cts) => ReferenceEquals(_active, cts) || ReferenceEquals(_stopped, cts);

    /// <summary>
    /// The turn is over: work it started and no longer awaits (a parallel web search after «Nessuna email
    /// trovata», a list, an error) stops instead of spending a request and the quota (A155).
    /// </summary>
    public void End(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_active, cts)) _active = null;
        if (ReferenceEquals(_stopped, cts)) _stopped = null;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>Drops the running turn and its result.</summary>
    public void Cancel()
    {
        _stopped = null;
        Cancel(ref _active);
    }

    /// <summary>Interrompi: the running turn ends with what it has done.</summary>
    public void Stop()
    {
        if (_active != null) _stopped = _active;
        Cancel(ref _active);
    }

    /// <summary>
    /// Token for saving a turn's result: never cancelled after Interrompi, the turn's own otherwise
    /// (already cancelled when the result must be dropped).
    /// </summary>
    public CancellationToken ResultToken(CancellationTokenSource cts) =>
        ReferenceEquals(_stopped, cts) ? CancellationToken.None : cts.Token;

    private static void Cancel(ref CancellationTokenSource? field)
    {
        // The owner disposes its source in its finally block; disposing here would race with linked
        // token sources still in use by the HTTP stream.
        var cts = field;
        field = null;
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException) { }
    }
}
