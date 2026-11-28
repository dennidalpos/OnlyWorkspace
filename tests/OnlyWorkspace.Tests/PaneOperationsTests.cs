using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>
/// Interrompi against the other cancels of the pane (A20): which turn keeps its partial result.
/// </summary>
public sealed class PaneOperationsTests
{
    [Fact]
    public void ClearChatRemainsBlockedUntilStoppedWorkFinishes()
    {
        var operations = new PaneOperations();
        Assert.False(operations.HasWork);
        var turn = operations.Begin();
        Assert.True(operations.HasWork);
        operations.Stop();
        Assert.True(operations.HasWork);
        operations.End(turn);
        Assert.False(operations.HasWork);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public async Task RepeatedStop_KeepsTheSameTurnAcrossFinalizationAwaits(int stops)
    {
        var operations = new PaneOperations();
        var turn = operations.Begin();
        for (int i = 0; i < stops; i++)
        {
            operations.Stop();
            await Task.Yield();
            Assert.True(operations.IsCurrent(turn));
            Assert.False(operations.ResultToken(turn).IsCancellationRequested);
        }
        operations.End(turn);
        operations.Stop();
        Assert.False(operations.IsCurrent(turn));
        var next = operations.Begin();
        operations.Stop();
        operations.Cancel();
        operations.Stop();
        Assert.False(operations.IsCurrent(next));
        Assert.True(operations.ResultToken(next).IsCancellationRequested);
        operations.End(next);
    }

    [Fact]
    public void Stop_CancelsTheTurn_ButKeepsItsResult()
    {
        var operations = new PaneOperations();
        var turn = operations.Begin();

        operations.Stop();

        Assert.True(turn.IsCancellationRequested);
        Assert.False(operations.ResultToken(turn).IsCancellationRequested);
    }

    [Fact]
    public void RunningTurn_SavesWithItsOwnToken()
    {
        var operations = new PaneOperations();
        var turn = operations.Begin();

        Assert.Equal(turn.Token, operations.ResultToken(turn));
    }

    [Fact]
    public void Cancel_DropsTheResult()
    {
        // Nuova chat, a conversation from the history, Outlook closing.
        var operations = new PaneOperations();
        var turn = operations.Begin();

        operations.Cancel();

        Assert.True(operations.ResultToken(turn).IsCancellationRequested);
    }

    [Fact]
    public void CancelAfterStop_DropsTheResult()
    {
        // Nuova chat while the stopped turn is still writing its partial result.
        var operations = new PaneOperations();
        var turn = operations.Begin();
        operations.Stop();

        operations.Cancel();

        Assert.True(operations.ResultToken(turn).IsCancellationRequested);
    }

    [Fact]
    public void NewTurn_DropsTheResultOfTheStoppedOne()
    {
        var operations = new PaneOperations();
        var first = operations.Begin();
        operations.Stop();

        var second = operations.Begin();

        Assert.True(operations.ResultToken(first).IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        Assert.Equal(second.Token, operations.ResultToken(second));
    }

    [Fact]
    public void NewTurn_CancelsTheRunningOne()
    {
        var operations = new PaneOperations();
        var first = operations.Begin();

        operations.Begin();

        Assert.True(first.IsCancellationRequested);
        Assert.True(operations.ResultToken(first).IsCancellationRequested);
    }

    [Fact]
    public void EndedOperations_AreNoLongerCancelled()
    {
        var operations = new PaneOperations();
        var turn = operations.Begin();
        operations.End(turn);

        // Its source is disposed: a late Interrompi must not touch it.
        operations.Stop();
        operations.Cancel();
    }

    [Fact]
    public void EndedTurn_StopsTheWebSearchItNoLongerAwaits()
    {
        var operations = new PaneOperations();
        var turn = operations.Begin();
        var search = Task.Delay(Timeout.Infinite, turn.Token);

        // «Nessuna email trovata»: the turn returns without awaiting the search started in parallel.
        operations.End(turn);

        Assert.True(search.IsCanceled);
    }

    [Fact]
    public void LateWebResult_LeavesTheNextTurnAlone()
    {
        var web = new TurnWebSources();
        web.Begin(1);
        web.Begin(2);
        var own = new List<WebSource> { new() { Url = "https://example.org/2" } };
        Assert.True(web.Take(2, own));

        Assert.False(web.Take(1, new List<WebSource> { new() { Url = "https://example.org/1" } }, inputCut: true));

        Assert.Same(own, web.Sources);
        Assert.False(web.InputCut);
    }

    [Fact]
    public void TurnWebSources_SayWhenTheModelSkippedTheSearch_OnlyForTheTurnItBelongsTo()
    {
        var web = new TurnWebSources();
        web.Begin(5);
        Assert.Null(web.Notice());
        Assert.False(web.MarkSkipped(4));
        Assert.Null(web.Notice());

        Assert.True(web.MarkSkipped(5));
        Assert.Equal(OnlyWorkspace.Infrastructure.Llm.WebSearchService.SkippedNotice, web.Notice());
        web.Take(5, new List<WebSource>(), inputCut: true);
        Assert.Equal(OnlyWorkspace.Infrastructure.Llm.WebSearchService.InputCutNotice + "\n" + OnlyWorkspace.Infrastructure.Llm.WebSearchService.SkippedNotice, web.Notice());

        web.Begin(6);
        Assert.Null(web.Notice());
    }

    [Fact]
    public void TurnWebSources_KeepTheCutNoticeAcrossSearchesOfTheTurn()
    {
        var web = new TurnWebSources();
        web.Begin(3);
        web.Take(3, new List<WebSource>(), inputCut: true);
        web.Take(3, new List<WebSource>());
        Assert.True(web.InputCut);

        web.Begin(4);
        Assert.False(web.InputCut);
        Assert.Empty(web.Sources);
    }
}
