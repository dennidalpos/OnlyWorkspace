using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnlyWorkspace.Infrastructure.Security;

internal sealed class ProcessResult
{
    public ProcessResult(int exitCode, string output) { ExitCode = exitCode; Output = output; }
    public int ExitCode { get; }
    /// <summary>stdout followed by stderr.</summary>
    public string Output { get; }
}

/// <summary>Hidden child process with captured output, timeout and cancellation (net48 has no WaitForExitAsync).</summary>
internal static class ProcessRunner
{
    /// <returns>null when the timeout expired (process killed).</returns>
    /// <param name="memoryLimitBytes">Cap on the child's committed memory (0 = none).</param>
    public static async Task<ProcessResult?> RunAsync(string exe, string arguments, TimeSpan timeout, CancellationToken cancellationToken, bool lowPriority = false, long memoryLimitBytes = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var child = SuspendedProcess.Start(exe, arguments, lowPriority, memoryLimitBytes);
        var process = child.Process;
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (s, e) => exited.TrySetResult(true);
        process.EnableRaisingEvents = true;
        var stdout = child.Output.ReadToEndAsync();
        var stderr = child.Error.ReadToEndAsync();
        var complete = Task.WhenAll(exited.Task, stdout, stderr);
        var aborted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (timeoutCts.Token.Register(() => aborted.TrySetResult(true)))
        {
            if (!timeoutCts.IsCancellationRequested) child.Resume();
            if (await Task.WhenAny(complete, aborted.Task).ConfigureAwait(false) != complete)
            {
                child.Terminate();
                await Task.WhenAny(complete, Task.Delay(1000)).ConfigureAwait(false);
                // Observe asynchronous read errors after terminating the tree; never extend the deadline to drain it.
                _ = complete.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            await complete.ConfigureAwait(false);
        }

        var output = await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output);
    }
}
