namespace OnlyWorkspace.Core.Interfaces;

/// <summary>
/// A client that can tell whether the current or last request streamed reasoning (thinking text or reasoning tokens).
/// Read by the capability probe only; reasoning is never shown or stored.
/// </summary>
public interface IReasoningObserver
{
    /// <summary>Reset when a request starts; set as soon as reasoning is seen, before the answer text.</summary>
    bool ReasoningObserved { get; }
}
