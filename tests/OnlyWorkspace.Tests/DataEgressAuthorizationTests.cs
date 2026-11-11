using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

public sealed class DataEgressAuthorizationTests
{
    private static LlmProfileConfig Profile(string id = "cloud") => new()
    {
        Id = id,
        DisplayName = id,
        Type = "openai",
        BaseUrl = "https://provider.example/v1",
        Model = "synthetic",
        Exposure = ExposureLevel.Extended,
        AllowAttachmentUpload = true,
        AuthType = "bearer",
        SecretRef = "model-key",
        WebSearchSecretRef = "web-key"
    };

    private static WorkspaceConfig Config(params LlmProfileConfig[] profiles) => new() { DefaultProfile = profiles[0].Id, Profiles = profiles.ToList() };
    private static string? Secret(string reference) => reference == "model-key" ? "synthetic-model-secret" : "synthetic-web-secret";
    private static DataEgressConsent Accept(WorkspaceConfig config, Func<string, string?>? resolver = null) =>
        DataEgress.Accept(config, "test", "synthetic", DateTime.UtcNow, resolver ?? Secret, TestEgress.ConfirmedRegions(config));

    [Fact]
    public void CatalogTwoGrantRequiresRenewalAndItsConversationCannotBeReused()
    {
        var profile = Profile();
        var config = Config(profile);
        var consent = Accept(config);
        // Recorded from the same synthetic profile with the embedded catalog at revision 2.
        consent.Grants[0].PolicyKey = "TGZ9hboZlWRCv2uQrGTmfVoZu1E1MEkPtx8X+F89Xic=";
        var oldConversation = new ConversationHistory
        {
            ProfileId = profile.Id,
            PolicyKey = "U6kDyQSHX2PdvPsscAVn9bk43C8Thb9CvjKSSZCoXbE="
        };

        Assert.False(DataEgress.AllowsModel(config, profile, Secret));
        Assert.False(ConversationPolicy.Matches(oldConversation, Profile()));
        Accept(config);
        Assert.True(DataEgress.AllowsModel(config, profile, Secret));
    }

    [Fact]
    public void AProfileOfTheSameServiceWithTheSamePermissionsIsCovered_ADifferentLevelOrKeyIsNot()
    {
        var profile = Profile();
        var config = Config(profile);
        Accept(config);
        var same = Profile("same"); same.Model = "another-model";
        var lower = Profile("lower"); lower.Exposure = ExposureLevel.Standard;
        var otherKey = Profile("other-key"); otherKey.SecretRef = "other-model-key";
        config.Profiles.AddRange(new[] { same, lower, otherKey });
        Assert.True(DataEgress.AllowsModel(config, same, Secret));
        Assert.False(DataEgress.AllowsModel(config, lower, Secret));
        Assert.False(DataEgress.AllowsModel(config, otherKey, Secret));
        Accept(config);
        Assert.Equal(3, config.DataEgress.Consent!.Grants.Count);
        Assert.All(config.Profiles, p => Assert.True(DataEgress.AllowsModel(config, p, Secret)));
    }

    [Fact]
    public void ReturningToAnAuthorizedCombinationOrChangingModelNeverAsksAgain()
    {
        var profile = Profile();
        profile.Exposure = ExposureLevel.Standard;
        var config = Config(profile);
        Accept(config);
        string standardBinding = profile.EgressAuthorizationKey;
        profile.Exposure = ExposureLevel.Extended;
        Assert.False(DataEgress.AllowsModel(config, profile, Secret));
        Accept(config);
        profile.Exposure = ExposureLevel.Standard;
        profile.Model = "a-model-never-seen";
        Assert.True(DataEgress.AllowsModel(config, profile, Secret));
        DataEgress.BindProfiles(config, Secret);
        // The combination keeps its grant, so the binding of its conversations is the same as before.
        Assert.Equal(standardBinding, profile.EgressAuthorizationKey);
        profile.Exposure = ExposureLevel.Extended;
        Assert.True(DataEgress.AllowsModel(config, profile, Secret));
    }

    [Fact]
    public void RememberedCombinationsAreLimitedPerServiceAndRevokeForgetsThemAll()
    {
        var profile = Profile();
        var config = Config(profile);
        for (int i = 0; i < DataEgress.GrantsPerService + 4; i++)
        {
            profile.SecretRef = "model-key-" + i;
            Accept(config, r => "synthetic-" + r);
        }
        Assert.Equal(DataEgress.GrantsPerService, config.DataEgress.Consent!.Grants.Count);
        Assert.True(DataEgress.AllowsModel(config, profile, r => "synthetic-" + r));
        profile.SecretRef = "model-key-0";
        Assert.False(DataEgress.AllowsModel(config, profile, r => "synthetic-" + r));
        profile.SecretRef = "model-key-5";
        Assert.True(DataEgress.AllowsModel(config, profile, r => "synthetic-" + r));
        DataEgress.Revoke(config, "u", "pc", DateTime.UtcNow);
        Assert.False(DataEgress.AllowsModel(config, profile, r => "synthetic-" + r));
        Accept(config, r => "synthetic-" + r);
        Assert.Single(config.DataEgress.Consent!.Grants);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
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
    [InlineData("model-key")]
    [InlineData("web-key")]
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
        // A renewed acceptance keeps the grant of an unchanged combination; a new one after Revoke gets a new salt.
        Assert.Equal(first.Grants[0].CredentialSalt, second.Grants[0].CredentialSalt);
        Assert.Equal(44, second.Grants[0].CredentialSalt.Length);
        DataEgress.Revoke(config, "u", "pc", DateTime.UtcNow);
        var third = Accept(config);
        Assert.NotEqual(first.Grants[0].CredentialSalt, third.Grants[0].CredentialSalt);
        Assert.NotEqual(first.Grants[0].CredentialFingerprint, third.Grants[0].CredentialFingerprint);
        second = third;
        string json = JsonSerializer.Serialize(config, ConfigJson.Write);
        string report = DataEgressReport.RenderHtml(config, new DataEgressWorkstation(), resolveSecret: Secret);
        Assert.DoesNotContain(Secret("model-key")!, json + report);
        Assert.DoesNotContain(Secret("web-key")!, json + report);
        Assert.DoesNotContain(second.Grants[0].CredentialFingerprint, report);
        var restored = JsonSerializer.Deserialize<WorkspaceConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.True(DataEgress.AllowsModel(restored, restored.Profiles[0], Secret));
        Assert.False(DataEgress.AllowsModel(restored, restored.Profiles[0]));
        Assert.Equal(string.Empty, restored.Profiles[0].EgressAuthorizationKey);
    }

    /// <summary>#086: the history keeps what was accepted, never the salts and fingerprints of past grants; only the current consent has them.</summary>
    [Fact]
    public void HistoryCopiesOfGrants_DropSaltsAndFingerprints_AndOlderEntriesAreScrubbed()
    {
        var config = Config(Profile());
        var first = Accept(config);
        // An entry written before the rule carries the proofs of its grant.
        config.DataEgress.History[0].Authorization!.Grants[0].CredentialSalt = first.Grants[0].CredentialSalt;
        config.DataEgress.History[0].Authorization!.Grants[0].CredentialFingerprint = first.Grants[0].CredentialFingerprint;
        var second = Accept(config);
        DataEgress.Revoke(config, "u", "pc", DateTime.UtcNow);

        Assert.True(config.DataEgress.History.Count >= 3);
        foreach (var entry in config.DataEgress.History)
        {
            var grants = entry.Authorization!.Grants;
            Assert.NotEmpty(grants);
            Assert.All(grants, g => { Assert.Equal(string.Empty, g.CredentialSalt); Assert.Equal(string.Empty, g.CredentialFingerprint); Assert.NotEmpty(g.PolicyKey); Assert.NotEmpty(g.Service); });
            Assert.Equal(DataEgressNotice.Text, entry.Authorization.NoticeText);
        }
        string json = JsonSerializer.Serialize(config, ConfigJson.Write);
        // Base64 holds + and /, which the serializer may escape: look for the escaped form too.
        foreach (var proof in new[] { first.Grants[0].CredentialSalt, first.Grants[0].CredentialFingerprint })
        {
            Assert.DoesNotContain(proof, json);
            Assert.DoesNotContain(JsonSerializer.Serialize(proof).Trim('"'), json);
        }
        Assert.NotEmpty(second.Grants[0].CredentialFingerprint);

        Assert.Null(config.DataEgress.Consent);
        var third = Accept(config);
        Assert.NotEmpty(third.Grants[0].CredentialFingerprint);
        Assert.True(DataEgress.AllowsModel(config, config.Profiles[0], Secret));
    }

    [Fact]
    public void Revoke_WithANullHistoryEntry_StillClearsTheAuthorizationBinding()
    {
        var config = Config(Profile());
        Accept(config);
        Assert.NotEmpty(config.Profiles[0].EgressAuthorizationKey);
        config.DataEgress.History.Add(null!);

        DataEgress.Revoke(config, "u", "pc", DateTime.UtcNow);

        Assert.Null(config.DataEgress.Consent);
        Assert.Equal(string.Empty, config.Profiles[0].EgressAuthorizationKey);
    }

    /// <summary>#120: a hand-edited history with a null entry or list must not stop a new acceptance.</summary>
    [Fact]
    public void Accept_WithNullHistoryEntriesOrList_SucceedsAndDropsTheNulls()
    {
        var config = Config(Profile());
        Accept(config);
        config.DataEgress.History.Add(null!);
        Assert.NotNull(Accept(config));
        Assert.DoesNotContain(null, config.DataEgress.History);

        config.DataEgress.History = null!;
        Assert.NotNull(Accept(config));
        Assert.NotEmpty(config.DataEgress.History);
    }

    /// <summary>#120: saving rewrites config.json without the proofs a pre-#086 history copy still carried.</summary>
    [Fact]
    public void SavingTheConfiguration_ScrubsProofsFromOlderHistoryEntries()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-egress-scrub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var service = new FileConfigurationService(directory, directory);
        var config = Config(Profile());
        Accept(config);
        // Distinct from the current consent's proofs, which legitimately stay in config.json.
        var grant = config.DataEgress.History[0].Authorization!.Grants[0];
        grant.CredentialSalt = "OLDSALTMARKER";
        grant.CredentialFingerprint = "OLDFINGERPRINTMARKER";

        service.SaveConfiguration(config);

        string written = File.ReadAllText(Path.Combine(directory, "config.json"));
        Assert.DoesNotContain("OLDSALTMARKER", written);
        Assert.DoesNotContain("OLDFINGERPRINTMARKER", written);
        Assert.Equal(string.Empty, config.DataEgress.History[0].Authorization!.Grants[0].CredentialSalt);
    }

    [Fact]
    public void HostOnlyV4ConsentNeedsRenewalAndItsSignedDeclarationsRemainInHistory()
    {
        var config = Config(Profile());
        var previous = Accept(config);
        previous.Grants.Clear();
        config.DataEgress.History.Clear(); // a real legacy entry has no authorization snapshot
        Assert.True(DataEgress.Current(previous));
        Assert.False(DataEgress.AllowsModel(config, config.Profiles[0], Secret));
        Accept(config);
        var snapshot = config.DataEgress.History[0].Authorization!;
        Assert.Empty(snapshot.Grants);
        Assert.Equal(DataEgressNotice.Text, snapshot.NoticeText);
        Assert.Equal(previous.Company, snapshot.Company);
        Assert.True(DataEgress.AllowsModel(config, config.Profiles[0], Secret));
    }

    [Fact]
    public void MissingCredentialsFailClosedWithoutPartialAcceptance_ADuplicatedGrantStillMatches()
    {
        var config = Config(Profile());
        Assert.Throws<InvalidOperationException>(() => Accept(config, _ => null));
        Assert.Null(config.DataEgress.Consent);
        Assert.Empty(config.DataEgress.History);
        var consent = Accept(config);
        Assert.False(DataEgress.AllowsModel(config, config.Profiles[0], _ => null));
        consent.Grants.Add(consent.Grants[0]);
        Assert.True(DataEgress.AllowsModel(config, config.Profiles[0], Secret));
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
    public void LoopbackNeedsNoConsent_AndALocalWebSearchReachesNothing()
    {
        var profile = new LlmProfileConfig { Id = "local", BaseUrl = "http://127.0.0.1:11434", Model = "synthetic" };
        var config = Config(profile);
        Assert.True(DataEgress.AllowsModel(config, profile));
        Assert.True(DataEgress.AllowsWeb(config, profile));
        profile.WebSearchEnabled = true;
        Assert.True(DataEgress.AllowsModel(config, profile));
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
