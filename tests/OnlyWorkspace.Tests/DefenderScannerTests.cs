using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Security;
using Xunit;

namespace OnlyWorkspace.Tests;

public class DefenderScannerTests
{
    [Fact]
    public void NoThreats_IsClean()
    {
        var r = DefenderScanner.Interpret(0, "Scan starting...\nScan finished.\nScanning C:\\t\\a.pdf found no threats.\n");
        Assert.Equal(AttachmentScanStatus.Clean, r.Status);
    }

    [Fact]
    public void ThreatFound_ReportsThreatName()
    {
        var output = "Scan starting...\nScan finished.\nScanning C:\\t\\a.com found 1 threats.\n\n<===========================LIST OF DETECTED THREATS==========================>\n" +
                     "----------------------------- Threat information ------------------------------\nThreat                  : Virus:DOS/EICAR_Test_File\nResources               : 1 total\n    file                : C:\\t\\a.com\n";
        var r = DefenderScanner.Interpret(2, output);
        Assert.Equal(AttachmentScanStatus.Threat, r.Status);
        Assert.Equal("Virus:DOS/EICAR_Test_File", r.Detail);
    }

    [Fact]
    public void ExitTwoWithoutThreatList_IsScanFailure()
    {
        var r = DefenderScanner.Interpret(2, "[Failed][0x80070002] File not found.\nCmdTool: Failed with hr = 0x80070002. Check log\n");
        Assert.Equal(AttachmentScanStatus.Failed, r.Status);
        Assert.Equal("errore 0x80070002", r.Detail);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void ZeroThreatCountCannotHideProcessFailure(int exitCode)
    {
        Assert.Equal(AttachmentScanStatus.Failed, DefenderScanner.Interpret(exitCode, "found 0 threats").Status);
    }

    [Fact]
    public void ThreatEvidenceSurvivesUnexpectedExitCode()
    {
        Assert.Equal(AttachmentScanStatus.Threat, DefenderScanner.Interpret(7, "found 1 threats\nThreat : Trojan:Test").Status);
    }

    [Fact]
    public void PositiveThreatCountWithoutName_IsStillAThreat()
    {
        var result = DefenderScanner.Interpret(2, "Scanning file found 2 threats");

        Assert.Equal(AttachmentScanStatus.Threat, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public void UnrecognisedFailure_ReportsExitCode()
    {
        var result = DefenderScanner.Interpret(7, "scan failed");

        Assert.Equal(AttachmentScanStatus.Failed, result.Status);
        Assert.Equal("codice 7", result.Detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ExplicitThreatCannotBeHiddenByZeroCount(int exitCode)
    {
        var result = DefenderScanner.Interpret(exitCode, "found 0 threats\nThreat : Trojan:Test");
        Assert.Equal(AttachmentScanStatus.Threat, result.Status);
        Assert.Equal("Trojan:Test", result.Detail);
    }
}
