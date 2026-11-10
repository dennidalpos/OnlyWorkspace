using System.Net.Http;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

/// <summary>The pane's IA indicator: states come from real requests through the dispatcher, never from texts.</summary>
public sealed class ModelServiceStatusTests
{
    private static LlmProfileConfig Profile(string model = "synthetic") => new() { Id = "p", Model = model, ContextBudgetTokens = 1000 };

    private static ModelServiceStatus Configured(LlmProfileConfig profile, string key = "k1")
    {
        var status = new ModelServiceStatus();
        status.Configure(profile, key, null);
        return status;
    }

    [Fact]
    public void Unverified_UntilARequestOrACheck()
    {
        var status = Configured(Profile());
        Assert.Equal(StatusLight.Off, status.Current.Light);
        Assert.Equal("non verificato", status.Current.Label);
        status.CaptureConnectionCheck().Report(true, null);
        Assert.Equal(StatusLight.Ok, status.Current.Light);
        status.CaptureConnectionCheck().Report(false, "Credenziali rifiutate");
        Assert.Equal(StatusLight.Error, status.Current.Light);
        Assert.Equal("Credenziali rifiutate", status.Current.Reason);
    }

    [Fact]
    public void Configuration_NotConfiguredIsRedBlockedIsGreyAndANewProfileStartsAgain()
    {
        var status = new ModelServiceStatus();
        int changes = 0;
        status.Changed += () => changes++;
        status.Configure(null, null, "Nessun profilo IA configurato.");
        Assert.Equal(StatusLight.Error, status.Current.Light);
        Assert.Equal("non configurato", status.Current.Label);

        var profile = Profile();
        status.Configure(profile, "k1", "Informativa non compilata");
        Assert.Equal(StatusLight.Paused, status.Current.Light);
        Assert.Equal("Informativa non compilata", status.Current.Reason);
        status.Configure(profile, "k1", null);
        Assert.Equal(StatusLight.Off, status.Current.Light);

        status.CaptureConnectionCheck().Report(true, null);
        status.Configure(profile, "k1", null);
        Assert.Equal(StatusLight.Ok, status.Current.Light);
        Assert.False(status.Current.External);
        status.Configure(new LlmProfileConfig { Id = "cloud", Model = "synthetic", External = true, BaseUrl = "https://cloud.example.test/v1" }, "k2", null);
        Assert.Equal(StatusLight.Off, status.Current.Light);
        Assert.True(status.Current.External);
        Assert.Equal(6, changes);
    }

    [Fact]
    public void LateOutcomeOfAPreviousProfile_IsIgnored()
    {
        var status = Configured(Profile());
        var request = status.Begin();
        Assert.Equal(StatusLight.Working, status.Current.Light);
        status.Configure(Profile("other"), "k2", null);
        Assert.Equal(StatusLight.Off, status.Current.Light);
        request.Failed(new HttpRequestException("HTTP 500"), CancellationToken.None);
        Assert.Equal(StatusLight.Off, status.Current.Light);
        Assert.Null(status.Current.Reason);
    }

    [Fact]
    public void ConcurrentRequests_StayYellowUntilTheLastEnds()
    {
        var status = Configured(Profile());
        var first = status.Begin();
        var second = status.Begin();
        first.Succeeded();
        Assert.Equal(StatusLight.Working, status.Current.Light);
        second.Succeeded();
        second.Succeeded();
        Assert.Equal(StatusLight.Ok, status.Current.Light);
        Assert.NotNull(status.Current.AtUtc);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("credentials")]
    [InlineData("policy")]
    public void ConnectionCheck_CannotPaintANewConfiguration(string change)
    {
        var profile = Profile();
        var status = Configured(profile);
        var old = status.CaptureConnectionCheck();
        var request = status.Begin();
        if (change == "policy") status.Configure(profile, "k1", "Informativa non compilata");
        else status.Configure(change == "profile" ? Profile("other") : profile, "k2", null);
        var expected = status.Current;
        int notifications = 0;
        status.Changed += () => notifications++;
        Assert.False(old.IsCurrent);
        Assert.False(old.Report(true, null));
        Assert.False(old.Report(false, "vecchio errore"));
        request.Succeeded();
        Assert.Equal(expected.Light, status.Current.Light);
        Assert.Equal(expected.Reason, status.Current.Reason);
        Assert.Equal(0, notifications);
        // Returning to the same key/policy must not revive a stale check either.
        status.Configure(profile, "k1", null);
        Assert.False(old.Report(true, null));
        Assert.True(status.CaptureConnectionCheck().Report(true, null));
        Assert.Equal(StatusLight.Ok, status.Current.Light);
    }

    [Fact]
    public void BlockedConnectionCheck_CannotOverwriteTheNotice()
    {
        var status = Configured(Profile());
        status.Configure(Profile(), "k1", "Informativa non compilata");
        var check = status.CaptureConnectionCheck();
        Assert.True(check.IsCurrent); // Its local refusal can still be shown by the pane.
        Assert.False(check.Report(true, null));
        Assert.Equal(StatusLight.Paused, status.Current.Light);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("enumerator")]
    [InlineData("callback")]
    public async Task Dispatcher_ClosesTheRequestWhenOpeningFails(string stage)
    {
        var profile = Profile();
        var status = Configured(profile);
        var failure = new HttpRequestException("HTTP 503");
        var client = new ScriptedClient();
        if (stage == "stream") client.Open = () => throw failure;
        if (stage == "enumerator") client.Open = () => new FailedStream(failure);
        var dispatcher = new LlmRequestDispatcher(client, profile, null, status,
            stage == "callback" ? () => throw failure : null, audit: TestPrivacyAudit.Store);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in dispatcher.StreamChatAsync(new[] { ChatMessage.User("prova") })) { }
        });
        Assert.Equal(StatusLight.Error, status.Current.Light);
        Assert.False(client.ChunksRead);
        status.CaptureConnectionCheck().Report(true, null);
        await foreach (var _ in new LlmRequestDispatcher(new ScriptedClient(), profile, null, status, audit: TestPrivacyAudit.Store)
            .StreamChatAsync(new[] { ChatMessage.User("nuova prova") })) { }
        Assert.Equal(StatusLight.Ok, status.Current.Light);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatcher_MaskingRefusalSendsNothingAndLeavesNoRunningRequest(bool structured)
    {
        var profile = new LlmProfileConfig
        {
            Id = "cloud",
            Type = "qwen",
            Model = "synthetic",
            BaseUrl = "https://cloud.example.test/v1",
            Exposure = ExposureLevel.Standard
        };
        var status = Configured(profile);
        status.CaptureConnectionCheck().Report(true, null);
        var client = new ScriptedClient();
        bool notified = false;
        var dispatcher = new LlmRequestDispatcher(client, profile, null, status, () => notified = true,
            (_, _) => throw new DataEgressBlockedException("Mascheramento non completato: richiesta non inviata."), audit: TestPrivacyAudit.Store);
        await Assert.ThrowsAsync<DataEgressBlockedException>(async () =>
        {
            var messages = new[] { ChatMessage.User("contenuto sintetico") };
            if (structured) await dispatcher.CompleteStructuredAsync(messages, "{}");
            else await foreach (var _ in dispatcher.StreamChatAsync(messages)) { }
        });
        Assert.False(client.ChunksRead);
        Assert.False(notified);
        Assert.Equal(StatusLight.Ok, status.Current.Light);
    }

    [Fact]
    public void PolicyRefusalsAndCancellations_SayNothingAboutTheService()
    {
        var status = Configured(Profile());
        status.CaptureConnectionCheck().Report(true, null);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        status.Begin().Failed(new TaskCanceledException(), cancelled.Token);
        status.Begin().Failed(new DataEgressBlockedException("blocked"), CancellationToken.None);
        status.Begin().Failed(new OperationCanceledException("La policy del profilo è cambiata."), CancellationToken.None);
        status.Begin().Abandoned();
        Assert.Equal(StatusLight.Ok, status.Current.Light);

        // A timeout of the HTTP client is a TaskCanceledException without a cancelled token: the service did not answer.
        status.Begin().Failed(new TaskCanceledException(), CancellationToken.None);
        Assert.Equal(StatusLight.Error, status.Current.Light);
        Assert.Contains("non ha risposto in tempo", status.Current.Reason);
    }

    [Fact]
    public async Task Dispatcher_ReportsStreamAndStructuredOutcomes()
    {
        var profile = Profile();
        var status = Configured(profile);
        var client = new ScriptedClient();
        var dispatcher = new LlmRequestDispatcher(client, profile, null, status, audit: TestPrivacyAudit.Store);
        var messages = new[] { ChatMessage.User("domanda sintetica") };

        client.OnChunk = () => Assert.Equal(StatusLight.Working, status.Current.Light);
        var chunks = new List<string>();
        await foreach (var chunk in dispatcher.StreamChatAsync(messages)) chunks.Add(chunk);
        Assert.Equal(new[] { "uno", "due" }, chunks);
        Assert.Equal(StatusLight.Ok, status.Current.Light);

        client.Fail = new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).");
        await Assert.ThrowsAsync<HttpRequestException>(async () => { await foreach (var _ in dispatcher.StreamChatAsync(messages)) { } });
        Assert.Equal(StatusLight.Error, status.Current.Light);
        Assert.Contains("credenziali", status.Current.Reason);

        client.Fail = null;
        Assert.Equal("{}", await dispatcher.CompleteStructuredAsync(messages, "{}"));
        Assert.Equal(StatusLight.Ok, status.Current.Light);
        client.Fail = new HttpRequestException("HTTP 503");
        await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.CompleteStructuredAsync(messages, "{}"));
        Assert.Equal(StatusLight.Error, status.Current.Light);

        // A reader that stops after the first chunk leaves no request running and gives no verdict.
        client.Fail = null;
        status.CaptureConnectionCheck().Report(true, null);
        await foreach (var _ in dispatcher.StreamChatAsync(messages)) break;
        Assert.Equal(StatusLight.Ok, status.Current.Light);
    }

    private sealed class ScriptedClient : ILlmClient
    {
        public Exception? Fail;
        public Action? OnChunk;
        public Func<IAsyncEnumerable<string>>? Open;
        public bool ChunksRead;
        public LlmTokenUsage? LastTokenUsage => null;
        public LlmCompletion? LastCompletion => null;
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default) =>
            Fail != null ? Task.FromException<string>(Fail) : Task.FromResult("{}");
        public IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) =>
            Open?.Invoke() ?? ReadChunks();

        private async IAsyncEnumerable<string> ReadChunks()
        {
            ChunksRead = true;
            await Task.Yield();
            OnChunk?.Invoke();
            yield return "uno";
            if (Fail != null) throw Fail;
            yield return "due";
        }
    }

    private sealed class FailedStream(Exception error) : IAsyncEnumerable<string>
    {
        public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default) => throw error;
    }
}
