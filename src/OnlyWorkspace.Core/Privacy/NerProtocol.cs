using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>The model behind the recognizer process: one block of text in, entities out.</summary>
public interface IPiiModel
{
    IReadOnlyList<NerSpan> Recognize(string block, CancellationToken token);
}

/// <summary>Length-prefixed UTF-8 JSON frames between the add-in and the recognizer process (one request, one reply).</summary>
public static class NerProtocol
{
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    private sealed class Request { public string Text { get; set; } = string.Empty; public int BudgetMs { get; set; } }
    private sealed class Reply { public List<NerSpan>? Spans { get; set; } public string? Error { get; set; } }

    private static async Task WriteFrameAsync(Stream stream, object value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType());
        if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("Frame too large.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), 0, 4, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<T?> ReadFrameAsync<T>(Stream stream, CancellationToken token) where T : class
    {
        var head = new byte[4];
        if (!await FillAsync(stream, head, token).ConfigureAwait(false)) return null;
        int length = BitConverter.ToInt32(head, 0);
        if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException("Invalid frame length.");
        var body = new byte[length];
        if (!await FillAsync(stream, body, token).ConfigureAwait(false)) throw new EndOfStreamException();
        return JsonSerializer.Deserialize<T>(body);
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        for (int offset = 0; offset < buffer.Length;)
        {
            int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
            if (read == 0) { if (offset == 0) return false; throw new EndOfStreamException(); }
            offset += read;
        }
        return true;
    }

    /// <summary>
    /// Recognizer side: answers requests until the client closes. A model failure is reported, never swallowed.
    /// <paramref name="validateClient"/> runs once the first request arrived (a pipe client can only be identified after it sent data);
    /// when it throws, nothing is answered.
    /// </summary>
    public static async Task ServeAsync(Stream stream, IPiiModel model, CancellationToken token, Action? validateClient = null)
    {
        while (!token.IsCancellationRequested)
        {
            var request = await ReadFrameAsync<Request>(stream, token).ConfigureAwait(false);
            if (request == null) return;
            validateClient?.Invoke();
            validateClient = null;
            Reply reply;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(Math.Max(1, request.BudgetMs));
                var spans = model.Recognize(request.Text, deadline.Token).ToList();
                // A model that returns after its budget ran out (for example by waking on the cancelled token) has not answered in time.
                deadline.Token.ThrowIfCancellationRequested();
                reply = new Reply { Spans = spans };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { reply = new Reply { Error = ex.GetType().Name }; }
            await WriteFrameAsync(stream, reply, token).ConfigureAwait(false);
        }
    }

    /// <summary>Add-in side. Throws when the recognizer does not answer within the budget or answers with an error.</summary>
    public static IReadOnlyList<NerSpan> Call(Stream stream, string block, TimeSpan budget, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter((int)Math.Min(int.MaxValue, budget.TotalMilliseconds));
        try
        {
            var task = Task.Run(async () =>
            {
                await WriteFrameAsync(stream, new Request { Text = block, BudgetMs = (int)Math.Min(int.MaxValue, budget.TotalMilliseconds) }, deadline.Token).ConfigureAwait(false);
                return await ReadFrameAsync<Reply>(stream, deadline.Token).ConfigureAwait(false);
            });
            // A pipe read ignores the token on some runtimes, so the wait itself is bounded.
            if (!task.Wait((int)Math.Min(int.MaxValue, budget.TotalMilliseconds + 250), token)) throw new TimeoutException();
            var reply = task.Result ?? throw new EndOfStreamException();
            if (reply.Error != null || reply.Spans == null) throw new InvalidDataException("Recognizer error.");
            return reply.Spans;
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException && !token.IsCancellationRequested) { throw new TimeoutException(); }
        catch (AggregateException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(); }
    }
}
