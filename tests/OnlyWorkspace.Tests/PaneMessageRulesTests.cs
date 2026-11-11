using System.Text.Json;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>#177: decisions of the pane coordinator that need neither Outlook nor WebView2.</summary>
public sealed class PaneMessageRulesTests
{
    [Theory]
    [InlineData("summary", true)]
    [InlineData("email_followup", true)]
    [InlineData("reply", true)]
    [InlineData("security", true)]
    [InlineData("action_items", true)]
    [InlineData("extract_data", true)]
    [InlineData("custom_reply", true)]
    [InlineData("chat", false)]
    [InlineData("briefing", false)]
    [InlineData("mail_analysis", false)]
    [InlineData("check_connection", false)]
    [InlineData(null, false)]
    public void NeedsEmail_OnlyForTheActionsOnTheSelectedEmail(string? type, bool expected) =>
        Assert.Equal(expected, PaneMessageRules.NeedsEmail(type));

    [Fact]
    public void ConnectionHealth_CarriesEveryFieldTheScriptReads()
    {
        using var ok = JsonDocument.Parse(JsonSerializer.Serialize(PaneMessageRules.ConnectionHealth(true, "0.9", 12, 340, null)));
        var root = ok.RootElement;
        Assert.Equal("connection_health_result", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("isHealthy").GetBoolean());
        Assert.Equal("0.9", root.GetProperty("version").GetString());
        Assert.Equal(12, root.GetProperty("modelsCount").GetInt32());
        Assert.Equal(340, root.GetProperty("latencyMs").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("errorMessage").ValueKind);
    }

    [Fact]
    public void ConnectionFailed_IsUnhealthyWithItsMessage_AndNoOtherFields()
    {
        using var failed = JsonDocument.Parse(JsonSerializer.Serialize(PaneMessageRules.ConnectionFailed(PaneMessageRules.NoProfileMessage)));
        var root = failed.RootElement;
        Assert.False(root.GetProperty("isHealthy").GetBoolean());
        Assert.Equal(PaneMessageRules.NoProfileMessage, root.GetProperty("errorMessage").GetString());
        Assert.False(root.TryGetProperty("version", out _));
        Assert.False(root.TryGetProperty("latencyMs", out _));
    }
}
