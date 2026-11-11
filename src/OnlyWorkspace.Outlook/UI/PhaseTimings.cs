using Serilog;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// Durations of an action's web search and generation for the log line that closes it: numbers only, never email
/// text, so provider and local latency can be told apart without the privacy audit (#191).
/// </summary>
internal sealed class PhaseTimings
{
    public const string WithWebTemplate = "Completed {Op} in {LatencyMs} ms (web search {WebMs} ms, generation {GenerationMs} ms)";
    public const string NoWebTemplate = "Completed {Op} in {LatencyMs} ms (no web search, generation {GenerationMs} ms)";

    /// <summary>Null while no search was attempted; a skipped or failed search still counts its time.</summary>
    public long? WebMs { get; private set; }

    public long GenerationMs { get; private set; }

    public void AddWeb(long ms) => WebMs = (WebMs ?? 0) + ms;

    public void AddGeneration(long ms) => GenerationMs += ms;

    public void LogCompleted(ILogger logger, string? op, long totalMs)
    {
        if (WebMs is long web) logger.Information(WithWebTemplate, op, totalMs, web, GenerationMs);
        else logger.Information(NoWebTemplate, op, totalMs, GenerationMs);
    }
}
