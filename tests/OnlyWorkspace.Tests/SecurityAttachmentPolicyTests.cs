using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Tests;

/// <summary>#177: which attachments the AI reading of «Verifica sicurezza» may include, and why one is left out.</summary>
public sealed class SecurityAttachmentPolicyTests
{
    private static LlmProfileConfig Local() => new() { Id = "l", Type = ProviderCatalog.Ollama, BaseUrl = "http://127.0.0.1:11434", Model = "qwen" };

    private static LlmProfileConfig Cloud(ExposureLevel level, bool upload) => new()
    {
        Id = "c", Type = ProviderCatalog.Ollama, BaseUrl = "https://cloud.example/v1", Model = "qwen-flash", External = true, Exposure = level, AllowAttachmentUpload = upload
    };

    private static AttachmentFacts Pdf(AttachmentScanStatus status = AttachmentScanStatus.Clean) => new("offerta.pdf", 10) { ScanStatus = status };

    [Fact]
    public void BeforeReading_ForALocalProfileWithACleanReadableCopy_IsAllowed()
    {
        var (reason, forbidden) = SecurityAttachmentPolicy.BeforeReading(Local(), Pdf(), hasSavedCopy: true);

        Assert.Null(reason);
        Assert.False(forbidden);
    }

    [Fact]
    public void BeforeReading_WhenTheProfileDoesNotSendAttachmentText_IsForbiddenByPolicy()
    {
        var (reason, forbidden) = SecurityAttachmentPolicy.BeforeReading(Cloud(ExposureLevel.Standard, upload: true), Pdf(), hasSavedCopy: true);

        Assert.Equal("invio del contenuto non consentito dal profilo", reason);
        Assert.True(forbidden);
    }

    [Fact]
    public void BeforeReading_ForACloudProfileThatAllowsUploadAtExtended_IsAllowed()
    {
        var (reason, forbidden) = SecurityAttachmentPolicy.BeforeReading(Cloud(ExposureLevel.Extended, upload: true), Pdf(), hasSavedCopy: true);

        Assert.Null(reason);
        Assert.False(forbidden);
    }

    [Theory]
    [InlineData(AttachmentScanStatus.Threat)]
    [InlineData(AttachmentScanStatus.Removed)]
    public void BeforeReading_AfterADefenderVerdict_IsExcludedButNotAPolicyRefusal(AttachmentScanStatus status)
    {
        var (reason, forbidden) = SecurityAttachmentPolicy.BeforeReading(Local(), Pdf(status), hasSavedCopy: true);

        Assert.Equal("escluso dopo il controllo antivirus", reason);
        Assert.False(forbidden);
    }

    [Fact]
    public void BeforeReading_ForAFormatWithoutATextReader_SaysSo()
    {
        var (reason, forbidden) = SecurityAttachmentPolicy.BeforeReading(Local(), new AttachmentFacts("tool.exe", 10), hasSavedCopy: true);

        Assert.Equal("formato senza estrazione del testo", reason);
        Assert.False(forbidden);
    }

    [Fact]
    public void BeforeReading_WithoutALocalCopy_SaysItIsNotAvailable()
    {
        var (reason, _) = SecurityAttachmentPolicy.BeforeReading(Local(), Pdf(), hasSavedCopy: false);

        Assert.Equal("copia locale non disponibile", reason);
    }

    [Fact]
    public void BeforeReading_ChecksThePolicyBeforeTheFormatAndTheCopy()
    {
        var (reason, forbidden) = SecurityAttachmentPolicy.BeforeReading(Cloud(ExposureLevel.Standard, upload: false), new AttachmentFacts("tool.exe", 10), hasSavedCopy: false);

        Assert.Equal("invio del contenuto non consentito dal profilo", reason);
        Assert.True(forbidden);
    }

    [Fact]
    public void AfterReading_ACompleteTextIsUsable() =>
        Assert.Null(SecurityAttachmentPolicy.AfterReading(new AttachmentExtraction { Text = "testo completo" }));

    [Fact]
    public void AfterReading_ATruncatedTextIsNeverUsedInPart() =>
        Assert.Equal("oltre il limite di lettura: nessun testo parziale usato",
            SecurityAttachmentPolicy.AfterReading(new AttachmentExtraction { Text = "parte", Truncated = true }));

    [Theory]
    [InlineData("scanned", null, "testo")]
    [InlineData(null, "errore", "testo")]
    [InlineData(null, null, "   ")]
    [InlineData(null, null, "")]
    public void AfterReading_AProblemAnErrorOrNoTextMeansNotReadable(string? problem, string? error, string text) =>
        Assert.Equal("testo non leggibile integralmente",
            SecurityAttachmentPolicy.AfterReading(new AttachmentExtraction { Problem = problem, Error = error, Text = text }));

    [Fact]
    public void LimitText_NamesTheAttachmentAndTheReason() =>
        Assert.Equal("«offerta.pdf»: contenuto non incluso nella lettura IA (copia locale non disponibile).",
            SecurityAttachmentPolicy.LimitText("offerta.pdf", "copia locale non disponibile"));
}
