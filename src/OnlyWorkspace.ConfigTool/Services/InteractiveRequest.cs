using System;
using System.Threading;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>One user-triggered check, bounded across all its network stages.</summary>
internal sealed class InteractiveRequest : IDisposable
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly CancellationTokenSource _cancel = new();
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    public CancellationToken Token => _linked.Token;
    public bool TimedOut => _deadline.IsCancellationRequested && !_cancel.IsCancellationRequested;
    public InteractiveRequest(TimeSpan? timeout = null)
    {
        _deadline = new CancellationTokenSource(timeout ?? Timeout);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token, _deadline.Token);
    }
    public void Cancel() => _cancel.Cancel();
    public void Dispose() { _linked.Dispose(); _deadline.Dispose(); _cancel.Dispose(); }
}
