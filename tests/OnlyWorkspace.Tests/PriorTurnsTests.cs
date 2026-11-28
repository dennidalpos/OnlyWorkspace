using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the prior turns sent with a chat question, outside TaskPaneCoordinator, with no Outlook and no model.</summary>
public sealed class PriorTurnsTests
{
    // 3 000 tokens = 10 500 characters; a third of it (3 500) is the most the history may take.
    private const int Budget = 3000;

    private static List<ChatMessage> Prompt(string data = "dati") => new() { ChatMessage.System("sistema"), ChatMessage.User(data) };

    private static HistoryChatMessage Turn(string role, string content, string? modelContent = null) =>
        new() { Role = role, Content = content, ModelContent = modelContent };

    private static List<HistoryChatMessage> History(params HistoryChatMessage[] turns) => turns.ToList();

    [Fact]
    public void Build_WithoutHistory_IsSystemThenQuestion()
    {
        var result = PriorTurns.Build(Prompt(), "domanda", Budget, null, external: false);

        Assert.Equal(new[] { "system", "user" }, result.Select(m => m.Role));
        Assert.Equal("dati", result[1].Content);
    }

    [Fact]
    public void Build_AddsPriorTurnsOldestFirstAndDropsTheQuestionJustAdded()
    {
        var history = History(Turn("user", "u1"), Turn("assistant", "a1"), Turn("user", "u2"), Turn("assistant", "a2"), Turn("user", "domanda"));

        var result = PriorTurns.Build(Prompt(), "domanda", Budget, history, external: false);

        Assert.Equal(new[] { "sistema", "u1", "a1", "u2", "a2", "dati" }, result.Select(m => m.Content));
    }

    [Fact]
    public void Build_WhenTheConversationBelongsToAnotherProfile_SendsNoPriorTurns()
    {
        var result = PriorTurns.Build(Prompt(), "domanda", Budget, null, external: true);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Build_StopsAtTheFirstTurnThatDoesNotFit_KeepingTheNewest()
    {
        var history = History(Turn("user", new string('o', 100)), Turn("assistant", new string('m', 3600)), Turn("user", "u2"), Turn("assistant", "a2"), Turn("user", "domanda"));

        var result = PriorTurns.Build(Prompt(), "domanda", Budget, history, external: false);

        // 3 500 characters of room: a2 and u2 fit, the 3 600-character reply does not, so the older turns are lost too.
        Assert.Equal(new[] { "sistema", "u2", "a2", "dati" }, result.Select(m => m.Content));
    }

    [Fact]
    public void Build_TheDataComesFirst_AndLeavesTheHistoryWhatIsLeft()
    {
        var history = History(Turn("user", "u1"), Turn("assistant", "a1"), Turn("user", "domanda"));

        // 10 500 characters of budget, 10 499 taken by the data: no room for any prior turn.
        var result = PriorTurns.Build(Prompt(new string('d', 10499)), "domanda", Budget, history, external: false);

        Assert.Equal(new[] { "system", "user" }, result.Select(m => m.Role));
    }

    [Fact]
    public void Build_UsesTheModelContentOfATurnWhenThereIsOne()
    {
        var history = History(Turn("user", "u1"), Turn("assistant", "visibile con importi", "solo il permesso"), Turn("user", "domanda"));

        var result = PriorTurns.Build(Prompt(), "domanda", Budget, history, external: false);

        Assert.Contains(result, m => m.Content == "solo il permesso");
        Assert.DoesNotContain(result, m => m.Content == "visibile con importi");
    }

    [Fact]
    public void Build_SkipsATurnWhoseModelContentIsEmpty()
    {
        var history = History(Turn("user", "u1"), Turn("assistant", "tabella locale", ""), Turn("user", "domanda"));

        var result = PriorTurns.Build(Prompt(), "domanda", Budget, history, external: false);

        Assert.Equal(new[] { "sistema", "u1", "dati" }, result.Select(m => m.Content));
    }

    [Fact]
    public void Build_ForAnExternalProfile_RemovesTheTablesFromAssistantTurnsOnly()
    {
        string listing = "**2 email**\n\n| Ora | Da |\n|---|---|\n| 09:00 | Alfa |";
        var history = History(Turn("user", "| non toccare |"), Turn("assistant", listing), Turn("user", "domanda"));

        var external = PriorTurns.Build(Prompt(), "domanda", Budget, history, external: true);
        var local = PriorTurns.Build(Prompt(), "domanda", Budget, history, external: false);

        Assert.Equal("| non toccare |", external[1].Content);
        Assert.DoesNotContain("|", external[2].Content, StringComparison.Ordinal);
        Assert.Contains("**2 email**", external[2].Content, StringComparison.Ordinal);
        Assert.Contains("| 09:00 | Alfa |", local[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NeverOpensWithAnAssistantTurn()
    {
        var history = History(Turn("assistant", "a0"), Turn("user", "u1"), Turn("assistant", "a1"), Turn("user", "domanda"));

        // Room for a1 and u1 only after a0 is dropped by the rule: start from the newest, a0 would come first.
        var result = PriorTurns.Build(Prompt(), "domanda", Budget, history, external: false);

        Assert.Equal("user", result[1].Role);
        Assert.Equal("u1", result[1].Content);
    }

    [Fact]
    public void Build_IgnoresTurnsOfOtherRoles()
    {
        var history = History(Turn("user", "u1"), Turn("tool", "x"), Turn("assistant", "a1"), Turn("user", "domanda"));

        var result = PriorTurns.Build(Prompt(), "domanda", Budget, history, external: false);

        Assert.DoesNotContain(result, m => m.Content == "x");
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void Build_WithoutAUserMessageInThePrompt_FallsBackToTheInstruction()
    {
        var result = PriorTurns.Build(new List<ChatMessage> { ChatMessage.System("sistema") }, "domanda", Budget, null, external: false);

        Assert.Equal("domanda", result[^1].Content);
        Assert.Equal("user", result[^1].Role);
    }

    [Fact]
    public void Build_WithNoClientSideLimit_KeepsEveryTurn()
    {
        var history = History(Turn("user", new string('a', 50000)), Turn("assistant", "r"), Turn("user", "domanda"));

        var result = PriorTurns.Build(Prompt(), "domanda", 0, history, external: false);

        Assert.Equal(4, result.Count);
    }
}
