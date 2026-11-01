using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Validation;

namespace OnlyWorkspace.Tests;

public sealed class DataEgressTests
{
    private static LlmProfileConfig Cloud(string id, string url) => new()
    {
        Id = id, DisplayName = id, Type = "qwen", BaseUrl = url, Model = "m", External = true, SecretRef = $"profile_{id}_token"
    };

    private static LlmProfileConfig Ollama(bool web) => new()
    {
        Id = "local", DisplayName = "local", Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "m", WebSearchEnabled = web
    };

    private static DataEgressSigner Signer(string date = "2026-10-20")
    {
        var signer = TestEgress.Signer(date);
        signer.Company = "Esempio Srl"; signer.FirstName = "Anna"; signer.LastName = "Bianchi"; signer.Role = "DPO"; signer.Email = "anna@esempio.example";
        return signer;
    }

    private static DataEgressConsent Accept(WorkspaceConfig config, DateTime? nowUtc = null) =>
        DataEgress.Accept(config, Signer(), TestEgress.Providers(config), @"PC\admin", "PC", nowUtc ?? DateTime.UtcNow, TestEgress.ResolveSecret);

    private static WorkspaceConfig Config(params LlmProfileConfig[] profiles) => new() { DefaultProfile = profiles[0].Id, Profiles = profiles.ToList() };

    [Fact]
    public void LocalProfiles_NeedNoNotice_ButOllamaWebSearchDoes()
    {
        var config = Config(Ollama(web: false));
        Assert.Empty(DataEgress.Hosts(config));
        Assert.True(DataEgress.AllowsModel(config, config.Profiles[0], TestEgress.ResolveSecret));
        Assert.True(DataEgress.AllowsWeb(config, config.Profiles[0], TestEgress.ResolveSecret));

        config.Profiles[0].WebSearchEnabled = true;
        Assert.Equal(new[] { "ollama.com" }, DataEgress.Hosts(config));
        Assert.True(DataEgress.AllowsModel(config, config.Profiles[0], TestEgress.ResolveSecret));
        Assert.False(DataEgress.AllowsWeb(config, config.Profiles[0], TestEgress.ResolveSecret));
    }

    [Fact]
    public void Acceptance_CoversTheServersOfThatMoment_AndANewServerIsBlocked()
    {
        var qwen = Cloud("qwen", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1");
        var config = Config(qwen);
        Assert.False(DataEgress.AllowsModel(config, qwen, TestEgress.ResolveSecret));
        Assert.Equal(new[] { "dashscope-intl.aliyuncs.com" }, DataEgress.Missing(config, TestEgress.ResolveSecret));

        var consent = Accept(config, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal(DataEgressNotice.Version, consent.NoticeVersion);
        Assert.True(DataEgress.AllowsModel(config, qwen, TestEgress.ResolveSecret));
        Assert.Empty(DataEgress.Missing(config, TestEgress.ResolveSecret));

        var other = Cloud("other", "https://api.other.example/v1");
        config.Profiles.Add(other);
        Assert.False(DataEgress.AllowsModel(config, other, TestEgress.ResolveSecret));
        Assert.True(DataEgress.AllowsModel(config, qwen, TestEgress.ResolveSecret));
        Assert.Equal(new[] { "api.other.example" }, DataEgress.Missing(config, TestEgress.ResolveSecret));
    }

    [Fact]
    public void ChangedNotice_AndRevocation_BlockAgain_AndHistoryKeepsBoth()
    {
        var qwen = Cloud("qwen", "https://cloud.example/v1");
        var config = Config(qwen);
        Accept(config);
        config.DataEgress.Consent!.NoticeHash = "older text";
        Assert.False(DataEgress.AllowsModel(config, qwen, TestEgress.ResolveSecret));

        Accept(config);
        DataEgress.Revoke(config, "u", "pc", DateTime.UtcNow);
        Assert.Null(config.DataEgress.Consent);
        Assert.False(DataEgress.AllowsModel(config, qwen, TestEgress.ResolveSecret));
        Assert.Equal(new[] { DataEgressEvent.Accepted, DataEgressEvent.Accepted, DataEgressEvent.Revoked }, config.DataEgress.History.Select(e => e.Action));
        Assert.Equal("Anna Bianchi, DPO, Esempio Srl <anna@esempio.example>", config.DataEgress.History[2].Responsible);
    }

    [Theory]
    [InlineData("", "Anna", "Bianchi", "DPO", "anna@esempio.example", "2026-10-20")]
    [InlineData("Esempio Srl", "Anna", "Bianchi", "", "anna@esempio.example", "2026-10-20")]
    [InlineData("Esempio Srl", "Anna", "Bianchi", "DPO", "anna", "2026-10-20")]
    [InlineData("Esempio Srl", "Anna", "Bianchi", "DPO", "anna@esempio.example", "2026-10-21")]
    [InlineData("Esempio Srl", "Anna", "", "DPO", "anna@esempio.example", "20/10/2026")]
    public void IncompleteSigner_IsRefused(string company, string first, string last, string role, string email, string date)
    {
        var signer = Signer(date);
        signer.Company = company; signer.FirstName = first; signer.LastName = last; signer.Role = role; signer.Email = email;
        Assert.NotNull(DataEgress.Problem(signer, new DateTime(2026, 10, 20)));
        Assert.Throws<ArgumentException>(() => DataEgress.Accept(Config(Ollama(false)), signer, new List<DataEgressProvider>(), "u", "pc", new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Consent_SurvivesConfigJson_AndAnIncompleteOneFailsValidation()
    {
        var config = Config(Cloud("qwen", "https://cloud.example/v1"));
        Accept(config);
        var read = JsonSerializer.Deserialize<WorkspaceConfig>(JsonSerializer.Serialize(config, ConfigJson.Write), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.True(DataEgress.AllowsModel(read, read.Profiles[0], TestEgress.ResolveSecret));
        Assert.Single(read.DataEgress.History);
        Assert.True(ConfigurationValidator.Validate(read).IsValid);

        read.DataEgress.Consent!.Email = "nessuna";
        Assert.Contains(ConfigurationValidator.Validate(read).Errors, e => e.Contains("uscita dei dati"));
    }

    [Fact]
    public void NoticeHash_IsStableAcrossLineEndings()
    {
        Assert.Equal(64, DataEgressNotice.Hash.Length);
        Assert.Contains("ollama.com", DataEgressNotice.Text);
    }

    [Fact]
    public void CompanyStatements_AreRequired()
    {
        var today = new DateTime(2026, 10, 20);
        Assert.Null(DataEgress.Problem(Signer(), today));
        foreach (var change in new Action<DataEgressSigner>[]
                 {
                     s => s.LegalBasis = "", s => s.LegalBasis = "inventata", s => s.Dpia = " ", s => s.WorkersInformed = false, s => s.AiLiteracy = false
                 })
        {
            var signer = Signer();
            change(signer);
            Assert.NotNull(DataEgress.Problem(signer, today));
        }
    }

    [Fact]
    public void ServerDeclarations_AreRequired_AndAcceptanceStoresThem()
    {
        var qwen = Cloud("qwen", "https://cloud.example/v1");
        var config = Config(qwen);
        foreach (var change in new Action<DataEgressProvider>[]
                 {
                     p => p.Provider = "", p => p.ProcessingAgreement = false, p => p.Region = "", p => p.TransferBasis = "x",
                     p => p.NoTraining = false, p => p.Retention = "", p => p.Level = ExposureLevel.LocalOnly
                 })
        {
            var providers = TestEgress.Providers(config);
            change(providers[0]);
            Assert.NotNull(DataEgress.Problem(providers[0]));
            Assert.Throws<ArgumentException>(() => DataEgress.Accept(config, Signer(), providers, "u", "pc", DateTime.UtcNow));
        }
        Assert.Throws<ArgumentException>(() => DataEgress.Accept(config, Signer(), new List<DataEgressProvider>(), "u", "pc", DateTime.UtcNow));

        var consent = Accept(config);
        Assert.Equal(DataEgressNotice.Text, consent.NoticeText);
        Assert.Equal("Fornitore di prova", Assert.Single(consent.Providers).Provider);
        Assert.Equal(DataEgressNotice.Hash, config.DataEgress.History[0].NoticeHash);
        Assert.Equal(new[] { "cloud.example: " + ExposurePolicy.Label(ExposureLevel.Minimal) }, config.DataEgress.History[0].Levels);
    }

    [Fact]
    public void Coverage_FollowsTheLevel_AndSoloLocaleNeverSends()
    {
        var qwen = Cloud("qwen", "https://cloud.example/v1");
        var config = Config(qwen);
        Assert.Equal(ExposureLevel.Minimal, qwen.Exposure);
        Accept(config);
        Assert.True(DataEgress.AllowsModel(config, qwen, TestEgress.ResolveSecret));

        qwen.Exposure = ExposureLevel.Standard;
        Assert.False(DataEgress.AllowsModel(config, qwen, TestEgress.ResolveSecret));
        Assert.Equal(new[] { "cloud.example" }, DataEgress.Missing(config, TestEgress.ResolveSecret));
        var providers = TestEgress.Providers(config);
        Assert.Equal(ExposureLevel.Standard, providers[0].Level);
        providers[0].Level = ExposureLevel.Minimal;
        Assert.Throws<ArgumentException>(() => DataEgress.Accept(config, Signer(), providers, "u", "pc", DateTime.UtcNow));
        Accept(config);
        Assert.True(DataEgress.AllowsModel(config, qwen, TestEgress.ResolveSecret));

        qwen.Exposure = ExposureLevel.LocalOnly;
        Assert.False(DataEgress.AllowsModel(config, qwen, TestEgress.ResolveSecret));
        Assert.Empty(DataEgress.Hosts(config));
        Assert.Equal(DataEgress.LocalOnlyMessage, DataEgress.BlockedMessageFor(qwen));
    }

    [Fact]
    public void Levels_LimitAttachmentsWebAndCloudCards()
    {
        var qwen = Cloud("qwen", "https://cloud.example/v1");
        qwen.AllowAttachmentUpload = true;
        qwen.WebSearchEnabled = true;
        qwen.WebSearchActions = new() { WebSearchAction.Chat, WebSearchAction.Security };

        qwen.Exposure = ExposureLevel.Minimal;
        Assert.False(qwen.SendsAttachmentText);
        Assert.False(WebSearchAction.Uses(qwen, WebSearchAction.Security));
        Assert.Empty(DataEgress.WebHosts(qwen));
        Assert.True(ExposurePolicy.Of(qwen).Pseudonymize);

        qwen.Exposure = ExposureLevel.Standard;
        Assert.False(qwen.SendsAttachmentText);
        Assert.True(WebSearchAction.Uses(qwen, WebSearchAction.Security));
        Assert.False(WebSearchAction.Uses(qwen, WebSearchAction.Chat));
        Assert.Equal(EvidenceMode.Original, ExposurePolicy.Of(qwen).Evidence);

        qwen.Exposure = (ExposureLevel)3;
        Assert.False(qwen.SendsAttachmentText);
        Assert.False(WebSearchAction.Uses(qwen, WebSearchAction.Chat));
        Assert.False(ExposurePolicy.Of(qwen).ModelAllowed);

        var local = Ollama(web: false);
        local.Exposure = ExposureLevel.LocalOnly;
        Assert.Same(ExposurePolicy.Local, ExposurePolicy.Of(local));
        Assert.True(local.SendsAttachmentText);
    }

    [Fact]
    public void ACloudDefault_CanBeSavedWithAnAdvisoryAndBlockedDispatch()
    {
        var qwen = Cloud("qwen", "https://cloud.example/v1");
        var config = Config(qwen, Ollama(false));
        Assert.NotNull(DataEgress.SelectionProblem(config));
        Assert.Contains(ConfigurationValidator.Validate(config).Warnings, w => w.Contains("predefinito"));
        Assert.True(ConfigurationValidator.Validate(config).IsValid);

        config.DefaultProfile = "local";
        Assert.Null(DataEgress.SelectionProblem(config));
        Accept(config);
        Assert.Null(DataEgress.SelectionProblem(config));

        qwen.Exposure = ExposureLevel.Standard;
        Accept(config);
        Assert.Null(DataEgress.SelectionProblem(config));
    }

    [Fact]
    public void AnOlderAcceptance_DoesNotInvalidateTheConfiguration()
    {
        var config = Config(Cloud("qwen", "https://cloud.example/v1"));
        config.DataEgress.Consent = new DataEgressConsent { NoticeVersion = 1, NoticeHash = "v1", Company = "Vecchia Srl", Hosts = new() { "cloud.example" } };
        Assert.True(ConfigurationValidator.Validate(config).IsValid);
        Assert.False(DataEgress.AllowsModel(config, config.Profiles[0], TestEgress.ResolveSecret));
    }
}
