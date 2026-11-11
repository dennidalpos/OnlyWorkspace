using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Storage;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the texts of «Privacy e dati» live outside MainConfigForm and are checked without a window.</summary>
public sealed class PrivacyPageTextTests
{
    private static LlmProfileConfig Cloud() => new()
    {
        Id = "qwen",
        DisplayName = "Qwen",
        Type = "qwen",
        BaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
        Model = "m",
        External = true,
        SecretRef = "profile_qwen_token"
    };

    [Fact]
    public void EgressStatus_WithoutConsentAndWithoutCloud_IsMutedAndOffersNothingToRevoke()
    {
        var config = new WorkspaceConfig { DefaultProfile = "local", Profiles = { new LlmProfileConfig { Id = "local", DisplayName = "Locale", Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "m" } } };
        var status = EgressStatus.Build(config, TestEgress.ResolveSecret);

        Assert.Equal(Tone.Muted, status.Tone);
        Assert.Contains("Non accettata", status.Text);
        Assert.Contains("non invia dati a server esterni", status.Text);
        Assert.False(status.CanAccept);
        Assert.False(status.CanRevoke);
    }

    [Fact]
    public void EgressStatus_WithoutConsentButACloudProfile_WarnsAndAllowsAccepting()
    {
        var config = new WorkspaceConfig { DefaultProfile = "qwen", Profiles = { Cloud() } };
        var status = EgressStatus.Build(config, TestEgress.ResolveSecret);

        Assert.Equal(Tone.Warning, status.Tone);
        Assert.Contains("dashscope-intl.aliyuncs.com", status.Text);
        Assert.Contains("• Qwen: invii bloccati", status.Text);
        Assert.True(status.CanAccept);
        Assert.False(status.CanRevoke);
    }

    [Fact]
    public void EgressStatus_WithCurrentConsent_IsSuccess_AndWithAnOldNoticeAsksRenewal()
    {
        var config = new WorkspaceConfig { DefaultProfile = "qwen", Profiles = { Cloud() } };
        config.DataEgress.Consent = DataEgress.Accept(config, @"PC\admin", "PC", DateTime.UtcNow, TestEgress.ResolveSecret, TestEgress.ConfirmedRegions(config));

        var current = EgressStatus.Build(config, TestEgress.ResolveSecret);
        Assert.Equal(Tone.Success, current.Tone);
        Assert.Contains("• Qwen: autorizzato", current.Text);
        Assert.True(current.CanRevoke);

        config.DataEgress.Consent!.NoticeVersion = 1;
        config.DataEgress.Consent.NoticeHash = "v1";
        var stale = EgressStatus.Build(config, TestEgress.ResolveSecret);
        Assert.Equal(Tone.Warning, stale.Tone);
        Assert.Contains("Da rinnovare", stale.Text);
    }

    [Fact]
    public void StorageText_SizeTotalAndSignature()
    {
        var one = new StorageItem { Name = "A", Path = @"C:\a", Bytes = 2048, Files = 1 };
        var many = new StorageItem { Name = "B", Path = @"C:\b", Bytes = 0, Files = 1234, Unreadable = 2, Detail = "txt 1" };
        var report = new StorageReport { Items = new[] { one, many }, FreeBytes = 1024L * 1024 * 1024 };

        Assert.EndsWith(" · 1 file", StorageText.Size(one));
        string size = StorageText.Size(many);
        Assert.Contains("1.234 file", size);
        Assert.Contains("2 non leggibili", size);
        Assert.EndsWith("\r\ntxt 1", size);

        Assert.Equal("Dimensione dei file su disco.", StorageText.SizeTip(one));
        Assert.Contains("divisa per tipo", StorageText.SizeTip(many));
        Assert.Contains("non sono contati", StorageText.SizeTip(many));

        string total = StorageText.Total(report, new DateTime(2026, 11, 11, 9, 5, 0));
        Assert.Contains("in 1.235 file", total);
        Assert.Contains("liberi sul disco dell'archivio", total);
        Assert.EndsWith("Calcolato alle 09:05.", total);
        Assert.StartsWith("Totale ", StorageText.Total(new StorageReport(), DateTime.Now));

        Assert.NotEqual(StorageText.Signature(report), StorageText.Signature(new StorageReport { Items = new[] { one } }));
        Assert.Equal(StorageText.Signature(report), StorageText.Signature(new StorageReport { Items = new[] { one, many } }));
    }
}
