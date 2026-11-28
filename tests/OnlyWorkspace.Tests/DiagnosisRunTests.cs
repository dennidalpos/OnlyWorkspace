using OnlyWorkspace.ConfigTool.Services;

namespace OnlyWorkspace.Tests;

/// <summary>#195: the state of «Diagnosi» (connection check, then probes) lives outside the form.</summary>
public sealed class DiagnosisRunTests
{
    [Fact]
    public void Idle_NothingRunsAndTheButtonHasNothingToCancel()
    {
        var run = new DiagnosisRun();
        Assert.False(run.Busy);
        Assert.False(run.CancelRunning());
    }

    [Fact]
    public void SecondPress_CancelsTheConnectionCheckFirst_ThenTheProbes()
    {
        var run = new DiagnosisRun();
        using var connection = new InteractiveRequest();
        using var probe = new CancellationTokenSource();
        Assert.True(run.BeginConnection(connection));
        Assert.True(run.ConnectionRunning);

        Assert.True(run.CancelRunning());
        Assert.True(connection.Token.IsCancellationRequested);
        run.EndConnection(connection);

        Assert.True(run.BeginProbe(probe));
        Assert.True(run.CancelRunning());
        Assert.True(probe.IsCancellationRequested);
    }

    [Fact]
    public void ButtonReuse_ABusyDiagnosisRefusesASecondConnectionCheck()
    {
        var run = new DiagnosisRun();
        using var first = new InteractiveRequest();
        using var second = new InteractiveRequest();
        Assert.True(run.BeginConnection(first));
        Assert.False(run.BeginConnection(second));
        Assert.False(second.Token.IsCancellationRequested);
    }

    [Fact]
    public void LateEndOfAnOlderRun_DoesNotFreeTheSlotOfANewerOne()
    {
        var run = new DiagnosisRun();
        using var older = new InteractiveRequest();
        using var newer = new InteractiveRequest();
        run.BeginConnection(older);
        run.EndConnection(older);
        run.BeginConnection(newer);
        run.EndConnection(older);
        Assert.True(run.ConnectionRunning);

        using var olderProbe = new CancellationTokenSource();
        using var newerProbe = new CancellationTokenSource();
        run.EndConnection(newer);
        run.BeginProbe(olderProbe);
        run.EndProbe(olderProbe);
        run.BeginProbe(newerProbe);
        run.EndProbe(olderProbe);
        Assert.True(run.IsProbe(newerProbe));
        Assert.False(run.IsProbe(olderProbe));
    }

    [Fact]
    public void ProfileChange_CancelsEverythingThatRuns_ConnectionEditsOnlyTheConnection()
    {
        var run = new DiagnosisRun();
        using var connection = new InteractiveRequest();
        using var probe = new CancellationTokenSource();
        run.BeginConnection(connection);
        run.BeginProbe(probe);

        run.CancelConnection();
        Assert.True(connection.Token.IsCancellationRequested);
        Assert.False(probe.IsCancellationRequested);

        run.CancelAll();
        Assert.True(probe.IsCancellationRequested);
    }
}
