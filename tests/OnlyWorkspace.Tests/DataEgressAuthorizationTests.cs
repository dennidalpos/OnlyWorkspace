using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

public sealed class DataEgressAuthorizationTests
{
    private static LlmProfileConfig Profile(string id = "cloud") => new()
    {
        Id = id, DisplayName = id, Type = "openai", BaseUrl = "https://provider.example/v1", Model = "synthetic",
        Exposure = ExposureLevel.Extended, AllowAttachmentUpload = true, AuthType = "bearer", SecretRef = "model-key", WebSearchSecretRef = "web-key"
    };

    private static WorkspaceConfig Config(params LlmProfileConfig[] profiles) => new() { DefaultProfile = profiles[0].Id, Profiles = profiles.ToList() };
    private static string? Secret(string reference) => reference == "model-key" ? "synthetic-model-secret" : "synthetic-web-secret";
    private static DataEgressConsent Accept(WorkspaceConfig config, Func<string, string?>? resolver = null) =>
        DataEgress.Accept(config, "test", "synthetic", DateTime.UtcNow, resolver ?? Secret);

    [Fact]
    public void ASecondProfileOnTheSameHostCannotInheritAuthorization()
    {
        var profile = Profile();
        var config = Config(profile);
        Accept(config);
        var second = Profile("second");
        config.Profiles.Add(second);
        Assert.True(DataEgress.AllowsModel(config, profile, Secret));
        Assert.False(DataEgress.AllowsModel(config, second, Secret));
        Assert.Single(DataEgress.Missing(config, Secret));
        Accept(config);
        Assert.Equal(2, config.DataEgress.Consent!.Profiles.Count);
        Assert.True(DataEgress.AllowsModel(config, second, Secret));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void PrivacyChangesRequireExplicitRenewal(int change)
    {
        var profile = Profile();
        var config = Config(profile);
        Accept(config);
        switch (change)
        {
            case 0: profile.BaseUrl = "https://provider.example/another/v1"; break;
            case 1: profile.Exposure = ExposureLevel.Standard; break;
            case 2: profile.AllowAttachmentUpload = false; break;
            case 3: profile.WebSearchEnabled = true; break;
            case 4: profile.PseudonymizeAmounts = true; break;
            case 5: profile.PseudonymizeDates = true; break;
            case 6: profile.AuthType = "none"; break;
            case 7: profile.SecretRef = "different-key"; break;
            case 8: profile.BaseUrl = "https://provider.example:8443/v1"; break;
        }
        Assert.Equal(change is 4 or 5, DataEgress.AllowsModel(config, profile, Secret));
        Accept(config);
        Assert.True(DataEgress.AllowsModel(config, profile, Secret));
    }

    [Theory]
    [InlineData("model-key")] [InlineData("web-key")]
    public void RotatingAnActualCredentialUnderTheSameReferenceBlocksAndSeparatesHistory(string changed)
    {
        var profile = Profile();
        var config = Config(profile);
        Accept(config);
        var history = ConversationPolicy.Create(profile);
        string? Rotated(string key) => key == changed ? "replacement-synthetic-secret" : Secret(key);
        Assert.False(DataEgress.AllowsModel(config, profile, Rotated));
        DataEgress.BindProfiles(config, Rotated);
        Assert.False(ConversationPolicy.Matches(history, profile));
        Accept(config, Rotated);
        Assert.True(DataEgress.AllowsModel(config, profile, Rotated));
        Assert.False(ConversationPolicy.Matches(history, profile));
    }

    [Fact]
    public void CredentialsAreSaltedAndNeverAppearInConfigurationHistoryOrReport()
    {
        var profile = Profile();
        var config = Config(profile);
        var first = Accept(config);
        var second = Accept(config);
        Assert.NotEqual(first.Profiles[0].CredentialSalt, second.Profiles[0].CredentialSalt);
        Assert.NotEqual(first.Profiles[0].CredentialFingerprint, second.Profiles[0].CredentialFingerprint);
        string json = JsonSerializer.Serialize(config, ConfigJson.Write);
        string report = DataEgressReport.RenderHtml(config, new DataEgressWorkstation(), resolveSecret: Secret);
        Assert.DoesNotContain(Secret("model-key")!, json + report);
        Assert.DoesNotContain(Secret("web-key")!, json + report);
        Assert.DoesNotContain(second.Profiles[0].CredentialFingerprint, report);
        var restored = JsonSerializer.Deserialize<WorkspaceConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.True(DataEgress.AllowsModel(restored, restored.Profiles[0], Secret));
        Assert.False(DataEgress.AllowsModel(restored, restored.Profiles[0]));
        Assert.Equal(string.Empty, restored.Profiles[0].EgressAuthorizationKey);
    }

    [Fact]
    public void HostOnlyV4ConsentNeedsRenewalAndItsSignedDeclarationsRemainInHistory()
    {
        var config = Config(Profile());
        var previous = Accept(config);
        previous.Profiles.Clear();
        config.DataEgress.History.Clear(); // a real legacy entry has no authorization snapshot
        Assert.True(DataEgress.Current(previous));
        Assert.False(DataEgress.AllowsModel(config, config.Profiles[0], Secret));
        Accept(config);
        var snapshot = config.DataEgress.History[0].Authorization!;
        Assert.Empty(snapshot.Profiles);
        Assert.Equal(DataEgressNotice.Text, snapshot.NoticeText);
        Assert.Equal(previous.Company, snapshot.Company);
        Assert.True(DataEgress.AllowsModel(config, config.Profiles[0], Secret));
    }

    [Fact]
    public void MissingCredentialsAndDuplicateGrantsFailClosedWithoutPartialAcceptance()
    {
        var config = Config(Profile());
        Assert.Throws<InvalidOperationException>(() => Accept(config, _ => null));
        Assert.Null(config.DataEgress.Consent);
        Assert.Empty(config.DataEgress.History);
        var consent = Accept(config);
        Assert.False(DataEgress.AllowsModel(config, config.Profiles[0], _ => null));
        consent.Profiles.Add(consent.Profiles[0]);
        Assert.False(DataEgress.AllowsModel(config, config.Profiles[0], Secret));
    }

    [Fact]
    public void DisplayAndGenerationEditsDoNotRenewConsentAndEquivalentEndpointsAreNormalized()
    {
        var profile = Profile();
        var config = Config(profile);
        Accept(config);
        profile.DisplayName = "changed"; profile.Model = "another"; profile.Temperature = 0.8;
        profile.BaseUrl = "https://PROVIDER.EXAMPLE:443/v1/";
        Assert.True(DataEgress.AllowsModel(config, profile, Secret));
    }

    [Fact]
    public void LoopbackNeedsNoConsentWhileLanAndLocalWebDo()
    {
        var profile = new LlmProfileConfig { Id = "local", BaseUrl = "http://127.0.0.1:11434", Model = "synthetic" };
        var config = Config(profile);
        Assert.True(DataEgress.AllowsModel(config, profile));
        Assert.True(DataEgress.AllowsWeb(config, profile));
        profile.WebSearchEnabled = true;
        Assert.True(DataEgress.AllowsModel(config, profile));
        Assert.False(DataEgress.AllowsWeb(config, profile));
        Accept(config);
        Assert.True(DataEgress.AllowsWeb(config, profile));
        profile.BaseUrl = "http://192.0.2.1:11434";
        Assert.False(DataEgress.AllowsModel(config, profile));
    }

    [Fact]
    public void AnonymousExternalProfileIsGatedAndRevocationPreservesTheSignedRecord()
    {
        var profile = Profile(); profile.SecretRef = ""; profile.WebSearchSecretRef = ""; profile.AuthType = "none";
        var config = Config(profile);
        Assert.False(DataEgress.AllowsModel(config, profile));
        Accept(config);
        Assert.True(DataEgress.AllowsModel(config, profile));
        DataEgress.Revoke(config, "u", "pc", DateTime.UtcNow);
        Assert.False(DataEgress.AllowsModel(config, profile));
        Assert.Equal(DataEgressNotice.Text, config.DataEgress.History.Last().Authorization!.NoticeText);
        Assert.Equal(new[] { profile.Id }, config.DataEgress.History.Last().Profiles);
    }

    [Fact]
    public void ReloadingTheRealVaultRechecksBothCredentialsAndTheConversationBinding()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-egress-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); // retained under the gate's central TEMP for review
        var service = new FileConfigurationService(directory, directory);
        service.StoreSecret("model-key", Secret("model-key")!);
        service.StoreSecret("web-key", Secret("web-key")!);
        var config = Config(Profile());
        Accept(config, service.ResolveSecret);
        service.SaveConfiguration(config);
        config = service.LoadConfiguration();
        var conversation = ConversationPolicy.Create(config.Profiles[0]);
        Assert.True(DataEgress.AllowsModel(config, config.Profiles[0], service.ResolveSecret));
        service.StoreSecret("model-key", "rotated-model-secret");
        config = service.LoadConfiguration();
        Assert.False(DataEgress.AllowsModel(config, config.Profiles[0], service.ResolveSecret));
        Assert.False(ConversationPolicy.Matches(conversation, config.Profiles[0]));
        Accept(config, service.ResolveSecret);
        service.SaveConfiguration(config);
        config = service.LoadConfiguration();
        Assert.True(DataEgress.AllowsModel(config, config.Profiles[0], service.ResolveSecret));
        service.StoreSecret("web-key", "rotated-web-secret");
        config = service.LoadConfiguration();
        Assert.False(DataEgress.AllowsModel(config, config.Profiles[0], service.ResolveSecret));
    }
}
