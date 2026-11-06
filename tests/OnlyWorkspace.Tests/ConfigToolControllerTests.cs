using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Services;

namespace OnlyWorkspace.Tests;

/// <summary>#093: controllers extracted from MainConfigForm run without a form.</summary>
public sealed class ConfigToolControllerTests
{
    private static BackupRestoreFlow Flow(bool canWrite, List<string> log, Func<string, string, Task<ElevatedConfigWriter.ElevationResult>>? elevated = null) =>
        new("C:\\cfg", _ => canWrite, (backup, dir) => log.Add($"direct {backup} {dir}"), () => { log.Add("stage"); return "C:\\stg"; },
            dir => log.Add("unstage " + dir),
            elevated ?? ((arguments, staging) => { log.Add("elevated " + arguments); return Task.FromResult(new ElevatedConfigWriter.ElevationResult { Success = true }); }));

    [Fact]
    public async Task RestoreWritesDirectlyWhenTheUserMayWriteAndNeverStagesOrElevates()
    {
        var log = new List<string>();
        var result = await Flow(true, log).RestoreAsync("C:\\b.owbak");
        Assert.Equal(RestoreOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "direct C:\\b.owbak C:\\cfg" }, log);
    }

    [Theory]
    [InlineData(true, false, RestoreOutcome.Completed)]
    [InlineData(false, true, RestoreOutcome.Cancelled)]
    [InlineData(false, false, RestoreOutcome.Failed)]
    public async Task ElevatedRestoreMapsTheResultAndAlwaysRemovesTheStagingFolder(bool success, bool cancelled, RestoreOutcome expected)
    {
        var log = new List<string>();
        var result = await Flow(false, log, (_, _) => Task.FromResult(
            new ElevatedConfigWriter.ElevationResult { Success = success, Cancelled = cancelled, Message = "reason" })).RestoreAsync("C:\\b.owbak");
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(success ? string.Empty : "reason", result.Message);
        Assert.Equal(new[] { "stage", "unstage C:\\stg" }, log);
    }

    [Fact]
    public async Task ElevatedRestoreUsesTheQuotedRestoreAuthorizedCommandAndRemovesStagingWhenTheRunThrows()
    {
        var log = new List<string>();
        string? arguments = null, staged = null;
        var flow = Flow(false, log, (a, s) => { arguments = a; staged = s; throw new InvalidOperationException("boom"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.RestoreAsync("C:\\my backup\\b.owbak"));
        Assert.Equal("restore-authorized \"C:\\my backup\\b.owbak\" \"C:\\cfg\" \"C:\\stg\"", arguments);
        Assert.Equal("C:\\stg", staged);
        Assert.Equal("unstage C:\\stg", log[^1]);
    }

    [Theory]
    [InlineData(true, false, true, false, CloseReason.UserClosing, CloseAction.Block)]
    [InlineData(true, true, true, false, CloseReason.UserClosing, CloseAction.Block)]
    [InlineData(false, true, true, false, CloseReason.UserClosing, CloseAction.AskUnsaved)]
    [InlineData(false, true, false, false, CloseReason.WindowsShutDown, CloseAction.AskUnsaved)]
    [InlineData(false, false, true, false, CloseReason.UserClosing, CloseAction.HideToTray)]
    [InlineData(false, false, true, true, CloseReason.UserClosing, CloseAction.Close)]
    [InlineData(false, false, true, false, CloseReason.WindowsShutDown, CloseAction.Close)]
    [InlineData(false, false, false, false, CloseReason.UserClosing, CloseAction.Close)]
    public void ClosePolicyDecidesFromTheStateAlone(bool busy, bool dirty, bool tray, bool exiting, CloseReason reason, CloseAction expected) =>
        Assert.Equal(expected, ClosePolicy.Decide(busy, dirty, tray, exiting, reason));
}
