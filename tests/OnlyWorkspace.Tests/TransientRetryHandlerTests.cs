using System.Net;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

public class TransientRetryHandlerTests
{
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }

        public SequenceHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            return _responses.Dequeue();
        }
    }

    private static async Task<(HttpResponseMessage Response, SequenceHandler Inner)> PostAsync(params HttpResponseMessage[] responses)
    {
        var inner = new SequenceHandler(responses);
        using var http = new HttpClient(new TransientRetryHandler(inner, TimeSpan.FromMilliseconds(1)));
        var response = await http.PostAsync("https://example.test/v1/messages", new StringContent("{\"a\":1}"));
        return (response, inner);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(529)]
    public async Task TemporaryError_IsRetriedOnceWithTheSameBody(int status)
    {
        var (response, inner) = await PostAsync(new HttpResponseMessage((HttpStatusCode)status), new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.Equal("{\"a\":1}", inner.LastBody);
    }

    [Fact]
    public async Task RateLimit_IsRetriedOnlyWithAShortRetryAfter()
    {
        var limited = new HttpResponseMessage((HttpStatusCode)429);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        var (response, inner) = await PostAsync(limited, new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);

        // No Retry-After: spent quota.
        var (quota, second) = await PostAsync(new HttpResponseMessage((HttpStatusCode)429));
        Assert.Equal(429, (int)quota.StatusCode);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task SecondFailure_IsReturnedWithoutFurtherAttempts()
    {
        var (response, inner) = await PostAsync(new HttpResponseMessage((HttpStatusCode)529), new HttpResponseMessage((HttpStatusCode)529), new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(529, (int)response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task OtherErrors_AndLongRetryAfter_AreNotRetried()
    {
        var (badRequest, first) = await PostAsync(new HttpResponseMessage(HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        Assert.Equal(1, first.Calls);

        var limited = new HttpResponseMessage((HttpStatusCode)429);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        var (response, second) = await PostAsync(limited);
        Assert.Equal(429, (int)response.StatusCode);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task RateLimit_WithPastRetryDate_RetriesImmediately()
    {
        var limited = new HttpResponseMessage((HttpStatusCode)429);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-1));

        var (response, inner) = await PostAsync(limited, new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CancellationDuringRetryWait_StopsBeforeSecondRequest()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
        var inner = new SequenceHandler(limited, new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(new TransientRetryHandler(inner));
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/messages"), cancellation.Token));
        Assert.Equal(1, inner.Calls);
    }
}
