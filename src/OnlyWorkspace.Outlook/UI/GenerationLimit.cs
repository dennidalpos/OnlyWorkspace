using System;
using System.Threading;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// Overall time cap of one model generation (#45: a summary ran 1925 s on 19/11/2026). The first-token and idle
/// timeouts of the profile stay; this one bounds a stream that keeps trickling. Expiry is told apart from a stop
/// by the user through <see cref="Expired"/>.
/// </summary>
internal sealed class GenerationLimit : IDisposable
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(600);

    private readonly CancellationTokenSource _cts;
    private readonly TimeSpan _limit;
    private readonly CancellationToken _outer;

    private GenerationLimit(CancellationToken outer, TimeSpan limit)
    {
        _outer = outer;
        _limit = limit;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _cts.CancelAfter(limit);
    }

    public static GenerationLimit Start(CancellationToken ct, TimeSpan? limit = null) => new(ct, limit ?? Default);

    public CancellationToken Token => _cts.Token;

    /// <summary>True when the cap ended the generation, not the user.</summary>
    public bool Expired => _cts.IsCancellationRequested && !_outer.IsCancellationRequested;

    public TimeoutException Timeout() =>
        new($"La generazione ha superato il tempo massimo di {(int)_limit.TotalSeconds} s. Riprova o usa un modello più veloce.");

    public void Dispose() => _cts.Dispose();
}
