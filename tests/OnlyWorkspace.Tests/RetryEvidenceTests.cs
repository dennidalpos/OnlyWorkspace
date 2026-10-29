using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Tests;

/// <summary>Riprova answers from what was already read only while nothing that decides the request changed (A150).</summary>
public sealed class RetryEvidenceTests
{
    private static readonly DateTime Read = new(2026, 10, 26, 20, 0, 0, DateTimeKind.Utc);

    private static LlmProfileConfig Profile(bool webSearch = false) =>
        new() { Id = "cloud", Type = "openai", Model = "m", BaseUrl = "https://api.example.test/v1", WebSearchEnabled = webSearch };

    [Fact]
    public void Reusable_OnlyForARetryOfTheSameRequestWhileRecent()
    {
        string key = RetryEvidence.Key("chat", "Fatture di ottobre?", Profile(), "c1");

        Assert.True(RetryEvidence.Reusable(true, key, Read, key, Read.AddMinutes(14)));
        Assert.False(RetryEvidence.Reusable(false, key, Read, key, Read.AddMinutes(1)));
        Assert.False(RetryEvidence.Reusable(true, key, Read, key, Read + RetryEvidence.KeptFor));
        Assert.False(RetryEvidence.Reusable(true, key, Read, key, Read.AddMinutes(-1)));
        Assert.False(RetryEvidence.Reusable(true, null, Read, key, Read.AddMinutes(1)));
    }

    [Fact]
    public void Key_ChangesWithQuestionPolicyConversationAndKind()
    {
        string key = RetryEvidence.Key("chat", "Fatture di ottobre?", Profile(), "c1");

        Assert.Equal(key, RetryEvidence.Key("chat", "Fatture di ottobre?", Profile(), "c1"));
        Assert.NotEqual(key, RetryEvidence.Key("chat", "Fatture di novembre?", Profile(), "c1"));
        Assert.NotEqual(key, RetryEvidence.Key("chat", "Fatture di ottobre?", Profile(webSearch: true), "c1"));
        Assert.NotEqual(key, RetryEvidence.Key("chat", "Fatture di ottobre?", Profile(), "c2"));
        Assert.NotEqual(key, RetryEvidence.Key("analysis", "Fatture di ottobre?", Profile(), "c1"));
    }
}
