using System.Text;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

public sealed class AnalysisFinalizationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public async Task StopDuringTheFinalRequest_KeepsTheList_WithoutMoreRequests(int stops)
    {
        var operations = new PaneOperations();
        var cts = operations.Begin();
        var entered = new TaskCompletionSource<bool>();
        var result = new StringBuilder("LIST");
        var chunks = new List<string>();
        int requests = 0;
        var run = AnalysisFinalization.RunAsync(operations, cts, result, async ct =>
        {
            requests++;
            entered.SetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
        }, chunks.Add);
        await entered.Task;
        for (int i = 0; i < stops; i++) operations.Stop();
        Assert.True(await run);
        Assert.Equal(1, requests);
        Assert.Single(chunks);
        Assert.StartsWith("LIST\n\n---\nAnalisi interrotta prima della risposta finale", result.ToString());
        Assert.False(operations.ResultToken(cts).IsCancellationRequested);
        operations.End(cts);
    }

    [Fact]
    public async Task CompletedAnswer_IsNotMarkedInterrupted_ByALateStop()
    {
        var operations = new PaneOperations();
        var cts = operations.Begin();
        var result = new StringBuilder();
        Assert.False(await AnalysisFinalization.RunAsync(operations, cts, result, _ =>
        {
            result.Append("ANSWER");
            operations.Stop();
            return Task.CompletedTask;
        }, _ => { }));
        Assert.Equal("ANSWER", result.ToString());
        operations.End(cts);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("new-turn")]
    public async Task Discard_DoesNotProducePartial(string reason)
    {
        var operations = new PaneOperations();
        var cts = operations.Begin();
        var result = new StringBuilder();
        int chunks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AnalysisFinalization.RunAsync(operations, cts, result, ct =>
            {
                operations.Stop();
                if (reason == "cancel") operations.Cancel();
                if (reason == "new-turn") operations.End(operations.Begin());
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, _ => chunks++));
        Assert.Empty(result.ToString());
        Assert.Equal(0, chunks);
        operations.End(cts);
    }

    [Fact]
    public async Task StopBeforeTheRequest_SendsNothing_AndNewChatRevokesTheResult()
    {
        var operations = new PaneOperations();
        var cts = operations.Begin();
        operations.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AnalysisFinalization.RunAsync(operations, cts,
            new StringBuilder(), _ => throw new Exception("No new transport after Stop"), _ => operations.Cancel()));
        Assert.True(operations.ResultToken(cts).IsCancellationRequested);
        operations.End(cts);
    }
}
