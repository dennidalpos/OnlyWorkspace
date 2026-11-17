using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the small sentences and shapes around an answer, outside TaskPaneCoordinator, with no Outlook and no model.</summary>
public sealed class PaneWordingTests
{
    private static object? Get(object source, string name) => source.GetType().GetProperty(name)!.GetValue(source);

    private static LlmProfileConfig Local() => new() { Id = "l", Type = ProviderCatalog.Ollama, BaseUrl = "http://127.0.0.1:11434", Model = "qwen" };

    private static LlmProfileConfig Cloud(ExposureLevel level) => new()
    {
        Id = "c", Type = ProviderCatalog.Ollama, BaseUrl = "https://cloud.example/v1", Model = "qwen-flash", External = true, Exposure = level
    };

    [Theory]
    [InlineData("Lucia", "lucia@example.test", "Lucia <lucia@example.test>")]
    [InlineData("Lucia", "", "Lucia")]
    [InlineData("Lucia", "  ", "Lucia")]
    [InlineData("Lucia", "/o=Org/ou=X/cn=lucia", "Lucia")]
    public void Sender_ShowsTheAddressOnlyWhenItIsReadable(string name, string address, string expected) =>
        Assert.Equal(expected, PaneWording.Sender(name, address));

    [Fact]
    public void EmailHeader_WithSender_HasSubjectSenderAndDate()
    {
        string header = PaneWording.EmailHeader("Ordine", "Lucia", "lucia@example.test", new DateTime(2026, 11, 2, 9, 30, 0), withSender: true);

        Assert.Equal("Oggetto: Ordine" + Environment.NewLine + "Da: Lucia <lucia@example.test>" + Environment.NewLine +
                     "Data: 02/11/2026 09:30" + Environment.NewLine + Environment.NewLine, header);
    }

    [Fact]
    public void EmailHeader_WithoutSender_KeepsOnlyTheSubject()
    {
        string header = PaneWording.EmailHeader("Ordine", "Lucia", "x@example.test", DateTime.Now, withSender: false);

        Assert.Equal("Oggetto: Ordine" + Environment.NewLine + Environment.NewLine, header);
    }

    [Fact]
    public void EmailHeader_WithNothingToSay_IsEmpty() =>
        Assert.Equal(string.Empty, PaneWording.EmailHeader(" ", "Lucia", "x", DateTime.Now, withSender: false));

    [Theory]
    [InlineData("https://cloud.example/v1/chat", "cloud.example")]
    [InlineData("http://127.0.0.1:11434", "127.0.0.1")]
    [InlineData("non un indirizzo", "non un indirizzo")]
    public void HostOf_GivesTheServerOrTheTextAsIs(string url, string expected) => Assert.Equal(expected, PaneWording.HostOf(url));

    [Fact]
    public void Badge_ForALocalProfile_NamesModelAndHostWithoutRegionOrWeb()
    {
        var badge = PaneWording.Badge(Local());

        Assert.Equal("qwen", Get(badge, "model"));
        Assert.Equal("127.0.0.1", Get(badge, "host"));
        Assert.Equal(string.Empty, Get(badge, "region"));
        Assert.Equal(string.Empty, Get(badge, "webHosts"));
        Assert.Empty((List<string>)Get(badge, "webActions")!);
    }

    [Fact]
    public void Badge_ForACloudProfile_NamesTheHostAndTheServiceRegion()
    {
        var badge = PaneWording.Badge(Cloud(ExposureLevel.Standard));

        Assert.Equal("cloud.example", Get(badge, "host"));
        Assert.NotEqual(string.Empty, (string)Get(badge, "region")!);
    }

    [Fact]
    public void WebActionLabels_IsEmptyWhileWebSearchIsOff()
    {
        var profile = Cloud(ExposureLevel.Extended);
        profile.WebSearchEnabled = false;

        Assert.Empty(PaneWording.WebActionLabels(profile));
    }

    [Fact]
    public void WebActionLabels_NamesTheActionsThatSearchTheWeb()
    {
        var profile = Cloud(ExposureLevel.Extended);
        profile.WebSearchEnabled = true;
        profile.WebSearchActions = new List<string> { WebSearchAction.Chat };

        var labels = PaneWording.WebActionLabels(profile);

        Assert.Single(labels);
        Assert.Equal(WebSearchAction.All.First(a => a.Key == WebSearchAction.Chat).Value, labels[0]);
    }

    [Fact]
    public void LeftOutNotice_IsNullWhenEverythingFit()
    {
        Assert.Null(PaneWording.LeftOutNotice(5, 5, "email"));
        Assert.Null(PaneWording.LeftOutNotice(6, 5, "email"));
    }

    [Fact]
    public void LeftOutNotice_SaysHowManyWereUsed() =>
        Assert.Equal("\n\n*Risposta basata su 3 appuntamenti su 7: gli altri non entrano nel contesto del profilo in uso.*", PaneWording.LeftOutNotice(3, 7, "appuntamenti"));

    [Fact]
    public void AttachmentNotice_WithNoDocuments_IsNull() =>
        Assert.Null(PaneWording.AttachmentNotice(Local(), Array.Empty<DocumentOutcome>()));

    [Fact]
    public void AttachmentNotice_ForALocalProfileThatReadSome_AddsTheCaution()
    {
        var read = new DocumentOutcome { Name = "a.pdf", Status = DocumentStatus.Read };

        string? notice = PaneWording.AttachmentNotice(Local(), new[] { read });

        Assert.NotNull(notice);
        Assert.EndsWith("\n\n*Importi e date letti dagli allegati: verificali sul documento.*", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentNotice_ForAnExternalProfile_NeverAddsTheCaution()
    {
        var read = new DocumentOutcome { Name = "a.pdf", Status = DocumentStatus.Read };

        string? notice = PaneWording.AttachmentNotice(Cloud(ExposureLevel.Extended), new[] { read });

        Assert.True(notice == null || !notice.Contains("verificali sul documento", StringComparison.Ordinal));
    }

    [Fact]
    public void WholeEmailsRefused_AtLocalOnly_SaysTheProfileSendsNothing() =>
        Assert.StartsWith("Il profilo cloud è al livello di esposizione «0 · Solo locale»", PaneWording.WholeEmailsRefused(Cloud(ExposureLevel.LocalOnly)), StringComparison.Ordinal);

    [Fact]
    public void WholeEmailsRefused_AtMinimal_ExplainsHowToUnlockTheAction() =>
        Assert.Equal(PaneWording.FactsOnly, PaneWording.WholeEmailsRefused(Cloud(ExposureLevel.Minimal)));
}
