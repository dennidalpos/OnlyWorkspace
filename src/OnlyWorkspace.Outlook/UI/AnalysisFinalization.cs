using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// Stop during the single final request (E7) keeps the list of emails already on screen as the result, with no
/// further request; any other cancel (new chat, new turn) drops the turn.
/// </summary>
internal static class AnalysisFinalization
{
    internal const string Interrupted = "\n\n---\nAnalisi interrotta prima della risposta finale: resta l'elenco delle email trovate.";

    internal static async Task<bool> RunAsync(PaneOperations operations, CancellationTokenSource cts, StringBuilder result,
        Func<CancellationToken, Task> answer, Action<string> append)
    {
        bool stopped = false;
        try
        {
            cts.Token.ThrowIfCancellationRequested();
            await answer(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !operations.ResultToken(cts).IsCancellationRequested)
        {
            stopped = true;
            result.Append(Interrupted);
            append(Interrupted);
        }
        operations.ResultToken(cts).ThrowIfCancellationRequested();
        return stopped;
    }
}
