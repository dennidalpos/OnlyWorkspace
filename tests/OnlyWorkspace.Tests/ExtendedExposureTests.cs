using System.Text.Json;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Tests;

public sealed class ExtendedExposureTests
{
    [Theory]
    [InlineData("\"Extended\"")]
    [InlineData("4")]
    public void ExplicitNewValueRoundTripsAndKeepsStandardRules(string json)
    {
        var level = JsonSerializer.Deserialize<ExposureLevel>(json);
        Assert.Equal(ExposureLevel.Extended, level);
        Assert.Equal("\"Extended\"", JsonSerializer.Serialize(level));
        var extended = ExposurePolicy.For(level);
        var standard = ExposurePolicy.For(ExposureLevel.Standard);
        Assert.True(extended.Attachments); Assert.True(extended.Pseudonymize);
        Assert.Equal(standard.Evidence, extended.Evidence);
        Assert.Equal(standard.PackTokens, extended.PackTokens);
        Assert.Equal(WebSearchAction.All.Select(a => a.Key), extended.WebActions);
        Assert.Empty(standard.WebActions!);
        Assert.False(standard.AnyWeb);
        Assert.False(standard.AllowsWeb(WebSearchAction.Security));
        Assert.Equal("3 · Esteso", ExposurePolicy.Label(level));
    }

    [Theory]
    [InlineData(ExposureLevel.LocalOnly, false)]
    [InlineData(ExposureLevel.Minimal, false)]
    [InlineData(ExposureLevel.Standard, false)]
    [InlineData(ExposureLevel.Extended, true)]
    public void AttachmentsNeedBothExplicitPermissionsAndCurrentConsent(ExposureLevel level, bool allowed)
    {
        var profile = new LlmProfileConfig { Id = "cloud", Type = "openai", Model = "synthetic", Exposure = level, AllowAttachmentUpload = true };
        Assert.Equal(allowed, profile.SendsAttachmentText);
        var config = new WorkspaceConfig { Profiles = new() { profile } };
        Assert.False(DataEgress.AllowsModel(config, profile));
        if (level == ExposureLevel.LocalOnly)
        {
            Assert.Throws<InvalidOperationException>(() => TestEgress.Accepted(config, fullExposure: false));
            return;
        }
        TestEgress.Accepted(config, fullExposure: false);
        Assert.Equal(level != ExposureLevel.LocalOnly, DataEgress.AllowsModel(config, profile));
        config.DataEgress.Consent!.NoticeVersion = 3;
        Assert.False(DataEgress.AllowsModel(config, profile));
        profile.AllowAttachmentUpload = false; Assert.False(profile.SendsAttachmentText);
        profile.Type = "ollama"; profile.BaseUrl = "http://127.0.0.1:11434"; profile.External = false;
        Assert.True(profile.SendsAttachmentText);
    }

    [Fact]
    public void PermissionChangesInvalidateConversationAndKnownThreatsAreExcluded()
    {
        var profile = new LlmProfileConfig { Id = "c", Type = "openai", Exposure = ExposureLevel.Standard, AllowAttachmentUpload = true };
        string standard = ConversationPolicy.Key(profile);
        profile.Exposure = ExposureLevel.Extended;
        Assert.NotEqual(standard, ConversationPolicy.Key(profile));
        string extended = ConversationPolicy.Key(profile);
        profile.AllowAttachmentUpload = false;
        Assert.NotEqual(extended, ConversationPolicy.Key(profile));
        foreach (var state in new[] { AttachmentScanStatus.Threat, AttachmentScanStatus.Removed })
        {
            var facts = new AttachmentFacts("synthetic.txt") { ScanStatus = state };
            Assert.False(DocumentOutcome.CanRead(facts));
            Assert.Equal(DocumentStatus.Blocked, DocumentOutcome.NotCopied(facts, true, true));
        }
        var outcome = new DocumentOutcome();
        outcome.ApplyExtraction(new AttachmentExtraction { Text = "partial", Truncated = true });
        Assert.False(outcome.WasRead);
    }
}
