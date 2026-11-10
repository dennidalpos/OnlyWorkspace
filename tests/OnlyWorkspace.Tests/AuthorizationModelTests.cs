using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Tests;

/// <summary>Authorizations decided once in the ConfigTool: Web Search per action and cloud cards per mailbox.</summary>
public sealed class AuthorizationModelTests
{
    private static LlmProfileConfig Cloud() => new() { Id = "c", Type = "anthropic", Model = "m", BaseUrl = "https://api.anthropic.com", External = true, Exposure = OnlyWorkspace.Core.Privacy.ExposureLevel.Standard };

    [Fact]
    public void WebSearch_RunsOnlyForTheChosenActionsOfAnEnabledProfile()
    {
        var profile = Cloud();
        Assert.Equal(WebSearchAction.Defaults(), profile.WebSearchActions);
        Assert.False(WebSearchAction.Uses(profile, WebSearchAction.Of("chat")));

        profile.WebSearchEnabled = true;
        profile.WebSearchActions = new() { WebSearchAction.Chat, WebSearchAction.Of("followup_draft")!, WebSearchAction.Security };
        // Standard blocks every web search (#155).
        profile.Exposure = OnlyWorkspace.Core.Privacy.ExposureLevel.Standard;
        Assert.False(WebSearchAction.Uses(profile, WebSearchAction.Security));
        profile.Exposure = OnlyWorkspace.Core.Privacy.ExposureLevel.Extended;
        Assert.True(WebSearchAction.Uses(profile, WebSearchAction.Of("chat")));
        Assert.True(WebSearchAction.Uses(profile, WebSearchAction.Of("followup_draft")));
        Assert.True(WebSearchAction.Uses(profile, WebSearchAction.Security));
        Assert.False(WebSearchAction.Uses(profile, WebSearchAction.Of("summary")));
        Assert.False(WebSearchAction.Uses(profile, WebSearchAction.Of("archive_get")));

        // A connector without a search mode never searches, whatever the saved flags say.
        profile.Type = "deepseek";
        Assert.False(WebSearchAction.Uses(profile, WebSearchAction.Chat));
    }

    [Fact]
    public void EveryPaneActionHasAKnownWebSearchId()
    {
        var ids = WebSearchAction.All.Select(a => a.Key).ToHashSet();
        foreach (var type in new[] { "chat", "summary", "reply", "custom_reply", "refine_reply", "email_followup", "followup_draft",
                     "security", "action_items", "extract_data", "briefing", "mail_analysis", "analysis_question" })
            Assert.Contains(WebSearchAction.Of(type)!, ids);
    }



}
