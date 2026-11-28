using OnlyWorkspace.Outlook.UI;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace OnlyWorkspace.Tests;

/// <summary>#191: the line closing an action carries web search and generation time, numbers only.</summary>
public sealed class PhaseTimingsTests
{
    private sealed class Captured : ILogEventSink
    {
        public readonly List<LogEvent> Events = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static LogEvent LogOnce(PhaseTimings timings, string op, long totalMs)
    {
        var sink = new Captured();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        timings.LogCompleted(logger, op, totalMs);
        return Assert.Single(sink.Events);
    }

    [Fact]
    public void WithWebSearch_LogsBothPhases()
    {
        var timings = new PhaseTimings();
        timings.AddWeb(49_000);
        timings.AddGeneration(4_500);

        var e = LogOnce(timings, "summary", 54_100);

        Assert.Equal(LogEventLevel.Information, e.Level);
        Assert.Equal(PhaseTimings.WithWebTemplate, e.MessageTemplate.Text);
        Assert.Equal("Completed \"summary\" in 54100 ms (web search 49000 ms, generation 4500 ms)", e.RenderMessage());
    }

    [Fact]
    public void WithoutWebSearch_SaysSoAndLogsGeneration()
    {
        var timings = new PhaseTimings();
        timings.AddGeneration(1_200);

        var e = LogOnce(timings, "compose_subject", 1_300);

        Assert.Equal(PhaseTimings.NoWebTemplate, e.MessageTemplate.Text);
        Assert.Equal("Completed \"compose_subject\" in 1300 ms (no web search, generation 1200 ms)", e.RenderMessage());
        Assert.False(e.Properties.ContainsKey("WebMs"));
    }

    [Fact]
    public void RepeatedPhases_AddUp()
    {
        var timings = new PhaseTimings();
        timings.AddWeb(0);
        timings.AddWeb(300);
        timings.AddGeneration(100);
        timings.AddGeneration(250);

        Assert.Equal(300, timings.WebMs);
        Assert.Equal(350, timings.GenerationMs);
    }

    [Fact]
    public void Properties_AreOperationAndDurationsOnly()
    {
        var timings = new PhaseTimings();
        timings.AddWeb(10);
        timings.AddGeneration(20);

        var e = LogOnce(timings, "reply", 40);

        Assert.Equal(new[] { "GenerationMs", "LatencyMs", "Op", "WebMs" }, e.Properties.Keys.OrderBy(k => k, System.StringComparer.Ordinal));
        foreach (var key in new[] { "LatencyMs", "WebMs", "GenerationMs" })
            Assert.IsType<long>(((ScalarValue)e.Properties[key]).Value);
    }
}
