using System;
using System.Threading;

namespace OnlyWorkspace.Core.Agents;

/// <summary>
/// Text of a structured answer as it streams in, for the caller that waits on it: an answer on many emails takes
/// minutes and is shown only once whole and checked, so the pane says meanwhile how much has arrived. Flows with the
/// async call (AsyncLocal); the clients report each chunk, nothing is reported outside an <see cref="Observe"/> scope.
/// </summary>
public static class StructuredProgress
{
    private static readonly AsyncLocal<Action<string>?> Current = new();

    public static IDisposable Observe(Action<string> onChunk)
    {
        var outer = Current.Value;
        Current.Value = onChunk;
        return new Scope(outer);
    }

    public static void Report(string chunk)
    {
        if (chunk.Length > 0) Current.Value?.Invoke(chunk);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action<string>? _outer;
        public Scope(Action<string>? outer) => _outer = outer;
        public void Dispose() => Current.Value = _outer;
    }
}
