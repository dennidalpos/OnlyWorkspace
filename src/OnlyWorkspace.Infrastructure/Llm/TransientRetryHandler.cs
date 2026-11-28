using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.Infrastructure.Llm;

/// <summary>
/// One more attempt after a short wait when a cloud provider answers with a temporary error: 429 (rate limit),
/// 500/502/503 (server side), 529 (Anthropic overload). Retry-After is honoured up to MaxWait; a longer one
/// means waiting is pointless and the error goes straight to the user. A 429 without Retry-After is a spent
/// quota (Gemini daily limit, Anthropic spend cap) and is not retried.
/// </summary>
public sealed class TransientRetryHandler : DelegatingHandler
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _defaultWait;

    public TransientRetryHandler(HttpMessageHandler inner, TimeSpan? defaultWait = null) : base(inner)
    {
        _defaultWait = defaultWait ?? TimeSpan.FromSeconds(2);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        if (status != 429 && status != 500 && status != 502 && status != 503 && status != 529) return response;

        var retryAfter = response.Headers.RetryAfter;
        if (status == 429 && retryAfter == null) return response;
        var wait = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow) ?? _defaultWait;
        if (wait > MaxWait) return response;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;

        OnlyWorkspaceLog.Logger.Warning("HTTP {Status} from {Host}: retrying once in {WaitMs} ms", status, request.RequestUri?.Host, (int)wait.TotalMilliseconds);
        response.Dispose();
        // Request bodies are buffered (StringContent), so the same message can be sent again.
        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
