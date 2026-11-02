using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Tests;

public sealed class DataEgressReportTests
{
    private static readonly DataEgressWorkstation Pc = new()
    {
        Machine = "WS-PROVA", ExportedBy = @"DOM\admin", Windows = "Windows 11 Pro 25H2", ProductVersion = "1.2.3", Outlook = "16.0 x64",
        WebView2 = "140.0", ConfigPath = @"C:\ProgramData\OnlyWorkspace\config.json", GeneratedUtc = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc)
    };

    private static WorkspaceConfig Config()
    {
        var cloud = new LlmProfileConfig
        {
            Id = "qwen", DisplayName = "Qwen <cloud>", Type = "qwen", BaseUrl = "https://cloud.example/v1", Model = "qwen-flash", External = true,
            Exposure = ExposureLevel.Standard
        };
        var local = new LlmProfileConfig { Id = "local", DisplayName = "Locale", Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "gemma" };
        return new WorkspaceConfig { DefaultProfile = "local", Profiles = new() { cloud, local } };
    }

    [Fact]
    public void WithoutAcceptance_TheReportSaysSo_AndCarriesTheCurrentText()
    {
        var config = Config();
        string html = DataEgressReport.RenderHtml(config, Pc);
        Assert.Contains("Non autorizzata", html);
        Assert.Contains("WS-PROVA", html);
        Assert.Contains("(non ancora accettato)", html);
        Assert.Contains("<strong>bloccato</strong>", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AfterAcceptance_ItListsWhoEnabledTheEgress_TheServersAndTheProfiles_Escaped()
    {
        var config = Config();
        var signer = TestEgress.Signer("2026-10-20");
        signer.Company = "Rossi & Figli <Srl>";
        DataEgress.Accept(config, @"DOM\anna", "WS-PROVA", new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc), TestEgress.ResolveSecret);
        string html = DataEgressReport.RenderHtml(config, Pc);

        Assert.Contains("Autorizzata per tutti i profili configurati.", html);
        Assert.DoesNotContain("Rossi &amp; Figli &lt;Srl&gt;", html);
        Assert.DoesNotContain("<Srl>", html);
        Assert.Contains("Qwen &lt;cloud&gt;", html);
        Assert.Contains(@"DOM\anna", html);
        Assert.Contains("20/10/2026", html);
        Assert.Contains("cloud.example", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(ExposurePolicy.Label(ExposureLevel.Standard)), html);
        Assert.DoesNotContain("Fornitore di prova", html);
        Assert.DoesNotContain(DataEgress.LegalBases[2], html);
        Assert.DoesNotContain("Schede cloud ·", html);
        Assert.Contains(DataEgressNotice.Hash, html);
        Assert.Contains("utilizzabile", html);
    }

    [Fact]
    public void TheAcceptedText_IsReproducedEvenAfterTheNoticeChanges()
    {
        var config = Config();
        DataEgress.Accept(config, "u", "pc", DateTime.UtcNow, TestEgress.ResolveSecret);
        config.DataEgress.Consent!.NoticeText = "Testo firmato in passato, abbastanza lungo da essere un paragrafo intero.";
        config.DataEgress.Consent.NoticeHash = "vecchio";
        string html = DataEgressReport.RenderHtml(config, Pc);
        Assert.Contains("Testo firmato in passato", html);
        Assert.Contains("Da rinnovare", html);
    }
}
