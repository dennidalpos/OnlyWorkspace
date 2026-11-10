using System.Text.Json;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Prompts;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Core.Validation;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

public sealed class P1DispatchPolicyTests
{
    public static IEnumerable<object[]> LocalConnectors => ProviderCatalog.All.Where(p => !p.IsExternal).Select(p => new object[] { p.Type });

    private static LlmProfileConfig Local() => new() { Id = "same-id", Model = "synthetic", ContextBudgetTokens = 1000 };
    private static WorkspaceConfig Config(LlmProfileConfig profile) => new() { DefaultProfile = profile.Id, Profiles = new() { profile } };

    private static void Accept(WorkspaceConfig config) => TestEgress.Accepted(config);

    [Theory]
    [MemberData(nameof(LocalConnectors))]
    public async Task RemoteLocalConnector_CannotBypassHostAttachmentsOrCards_AfterJsonRoundtrip(string type)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ow-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new LlmProfileConfig { Id = "p", Type = type, Model = "synthetic", BaseUrl = "https://remote.example.test/v1", External = false };
            var config = Config(profile);
            Assert.True(ConfigurationValidator.Validate(config).IsValid);
            var service = new FileConfigurationService(directory, directory);
            service.SaveConfiguration(config);
            config = new FileConfigurationService(directory, directory).LoadConfiguration();
            profile = config.Profiles.Single();
            Assert.False(profile.External); // effective classification must not overwrite the user's flag
            Assert.True(profile.IsExternal);
            Assert.DoesNotContain("IsExternal", File.ReadAllText(Path.Combine(directory, "config.json")));
            Assert.Equal(new[] { "remote.example.test" }, DataEgress.Hosts(config));
            Assert.False(SecurityContentAnalysis.AttachmentContentAllowed(profile, new AttachmentFacts("fixture.txt", 10)));
            var recorder = new RecordingClient();
            using var dispatch = new LlmRequestDispatcher(recorder, profile, () =>
            {
                if (!DataEgress.AllowsModel(config, profile)) throw new DataEgressBlockedException(DataEgress.BlockedMessage);
            }, audit: TestPrivacyAudit.Store);
            await Assert.ThrowsAsync<DataEgressBlockedException>(() => dispatch.CompleteStructuredAsync(new[] { ChatMessage.User("BODY") }, "{}"));
            Assert.Equal(0, recorder.Calls);
            // At Minimo only typed facts leave: free text stays blocked even after the authorization.
            TestEgress.Accepted(config, fullExposure: false);
            await Assert.ThrowsAsync<DataEgressBlockedException>(() => dispatch.CompleteStructuredAsync(new[] { ChatMessage.User("BODY") }, "{}"));
            Assert.Equal(0, recorder.Calls);
            Assert.False(SecurityContentAnalysis.AttachmentContentAllowed(profile, new AttachmentFacts("fixture.txt", 10)));
            profile.AllowAttachmentUpload = true;
            Assert.False(SecurityContentAnalysis.AttachmentContentAllowed(profile, new AttachmentFacts("fixture.txt", 10)));
            profile.AllowAttachmentUpload = false;
            profile.BaseUrl = type == ProviderCatalog.OpenAICompatible ? "http://127.0.0.1:1234/v1" : ProviderCatalog.Get(type).DefaultBaseUrl;
            Assert.False(profile.IsExternal);
            Assert.True(DataEgress.AllowsModel(Config(profile), profile));
        }
        finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, true); }
    }

    [Fact]
    public void EffectiveClassification_PreservesExplicitExternalFlag()
    {
        var p = Local(); p.External = true;
        Assert.True(p.IsExternal);
        Assert.False(DataEgress.AllowsModel(Config(p), p));
        p.External = false;
        Assert.False(p.IsExternal);
    }

    public static IEnumerable<object[]> PolicyChanges => Enumerable.Range(0, 10).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(PolicyChanges))]
    public async Task ChangedPolicyOrOldHistory_IsPreservedButNeverSent(int change)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ow-history-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = Local(); p.AllowAttachmentUpload = true;
            var conversation = ConversationPolicy.Create(p);
            conversation.Messages.Add(new HistoryChatMessage { Role = "assistant", Content = "ATTACHMENT-FACT-SECRET" });
            var storage = new DpapiHistoryStorageService(directory);
            await storage.SaveConversationAsync(conversation);
            switch (change)
            {
                case 0: p.AllowAttachmentUpload = false; break;
                case 1: p.BaseUrl = "https://remote.example.test/v1"; break;
                case 2: p.Type = "qwen"; break;
                case 3: p.Model = "other"; break;
                case 4: p.WebSearchEnabled = true; break;
                case 5: p.WebSearchActions.Clear(); break;
                case 6: p.SecretRef = "different-ref"; break;
                case 7: p.UseSystemProxy = true; break;
                case 8: conversation.PolicyKey = null; await storage.SaveConversationAsync(conversation); break;
                case 9: p.AuthType = "bearer"; break;
            }
            var restored = (await storage.LoadConversationAsync(conversation.Id))!;
            Assert.False(ConversationPolicy.Matches(restored, p));
            var next = ConversationPolicy.Create(p);
            next.Messages.Add(new HistoryChatMessage { Role = "user", Content = "NEW-QUESTION" });
            var recorder = new RecordingClient();
            using var dispatch = new LlmRequestDispatcher(recorder, p, audit: TestPrivacyAudit.Store);
            if (p.IsExternal)
                await Assert.ThrowsAsync<DataEgressBlockedException>(() => dispatch.CompleteStructuredAsync(next.Messages.Select(m => new ChatMessage(m.Role, m.Content)), "{}"));
            else await dispatch.CompleteStructuredAsync(next.Messages.Select(m => new ChatMessage(m.Role, m.Content)), "{}");
            Assert.DoesNotContain("ATTACHMENT-FACT-SECRET", string.Join("", recorder.Messages.Select(m => m.Content)));
            Assert.Contains("ATTACHMENT-FACT-SECRET", (await storage.LoadConversationAsync(conversation.Id))!.Messages.Single().Content);
            Assert.NotEqual(conversation.Id, next.Id);
        }
        finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, true); }
    }

    [Fact]
    public void PreviousBriefingPolicyCannotReuseDisplayedAgendaAsModelContext()
    {
        var profile = new LlmProfileConfig { Id = "legacy-briefing", Model = "synthetic", External = true };
        // Frozen fingerprints for this exact profile: revision 3 could save a local agenda as normal content.
        const string revision3 = "FzRJnISPBVv1LHRDzO0UmZ6ujuddYq0aG6gM18b3teg=";
        // Revision 4 did not bind the pseudonymization options/catalog either.
        Assert.NotEqual("J7BqiuCT8DhyR9vugX74aT5RVvXztJCPUd03ZizCRI8=", ConversationPolicy.Key(profile));
        var old = new ConversationHistory
        {
            ProfileId = profile.Id,
            PolicyKey = revision3,
            Messages = { new HistoryChatMessage { Role = "assistant", Content = "CALENDARSENTINELPRIVATE" } }
        };
        var restored = JsonSerializer.Deserialize<ConversationHistory>(JsonSerializer.Serialize(old))!;
        Assert.False(ConversationPolicy.Matches(restored, profile));
        Assert.Equal("CALENDARSENTINELPRIVATE", Assert.Single(restored.Messages).Content);
        Assert.Null(restored.Messages[0].ModelContent);
        Assert.True(ConversationPolicy.Matches(ConversationPolicy.Create(profile), profile));
    }

    [Fact]
    public async Task UnchangedPolicy_SurvivesRestart_AndLateResultIsRejected()
    {
        var p = Local();
        var conv = JsonSerializer.Deserialize<ConversationHistory>(JsonSerializer.Serialize(ConversationPolicy.Create(p)))!;
        Assert.True(ConversationPolicy.Matches(conv, p));
        string key = ConversationPolicy.Key(p);
        p.BaseUrl = "HTTP://127.0.0.1:11434/";
        Assert.Equal(key, ConversationPolicy.Key(p));
        var recorder = new RecordingClient { BeforeResult = () => p.AllowAttachmentUpload = true };
        using var dispatch = new LlmRequestDispatcher(recorder, p, () =>
        {
            if (!ConversationPolicy.Matches(conv, p)) throw new OperationCanceledException();
        }, audit: TestPrivacyAudit.Store);
        await Assert.ThrowsAsync<OperationCanceledException>(() => dispatch.CompleteStructuredAsync(new[] { ChatMessage.User("QUESTION") }, "{}"));
        Assert.Equal(1, recorder.Calls);
    }

    [Theory]
    [InlineData("instructions")]
    [InlineData("schema")]
    [InlineData("history")]
    [InlineData("web")]
    public async Task FullRequestOverBudget_NeverReachesTheClient(string source)
    {
        var p = Local();
        var messages = new List<ChatMessage> { ChatMessage.System("Rules"), ChatMessage.User(new string('b', 3000)) };
        Assert.True(new ContextBuilder().Fits(messages.Last().Content, 1000));
        string schema = "{}";
        if (source == "schema") schema = new string('s', 550);
        else if (source == "instructions") messages[0] = ChatMessage.System(new string('i', 550));
        else messages.Insert(1, source == "history" ? ChatMessage.Assistant(new string('h', 550)) : PromptCatalog.CreateWebEvidenceMessage(new string('w', 550), "[W1] Source"));
        var recorder = new RecordingClient();
        using var dispatch = new LlmRequestDispatcher(recorder, p, audit: TestPrivacyAudit.Store);
        await Assert.ThrowsAsync<ContextTooLongException>(() => dispatch.CompleteStructuredAsync(messages, schema));
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task RenderedWritingOverhead_IsCheckedAtDispatch_WhileValidInputIsUnchanged()
    {
        var p = Local();
        var recorder = new RecordingClient();
        using var dispatch = new LlmRequestDispatcher(recorder, p, audit: TestPrivacyAudit.Store);
        var improve = PromptCatalog.CreateImprovePrompt(new string('x', 3500), "professionale");
        await Assert.ThrowsAsync<ContextTooLongException>(async () => { await foreach (var _ in dispatch.StreamChatAsync(improve)) { } });
        Assert.Equal(0, recorder.Calls);
        var valid = new[] { ChatMessage.System("Rules"), ChatMessage.Assistant("HISTORY"), ChatMessage.User("WHOLE-BODY-END"), ChatMessage.System("WEB-END") };
        await dispatch.CompleteStructuredAsync(valid, "{}");
        Assert.Equal(1, recorder.Calls);
        // Local requests are masked too, so the messages are copies with the same text.
        Assert.Equal(valid.Select(m => (m.Role, m.Content)), recorder.Messages.Select(m => (m.Role, m.Content)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestNotification_RequiresDispatch_AndRunsBeforeTheClient(bool streaming)
    {
        var profile = Local();
        int notifications = 0;
        var client = new RecordingClient { BeforeResult = () => Assert.Equal(1, notifications) };
        using var dispatch = new LlmRequestDispatcher(client, profile, requestStarting: () => notifications++, audit: TestPrivacyAudit.Store);
        var input = new[] { ChatMessage.User("Synthetic question") };
        var unused = dispatch.StreamChatAsync(input); // A local answer can leave this client entirely unused.
        Assert.Equal(0, notifications);
        Assert.Equal(0, client.Calls);
        if (streaming) { await foreach (var _ in unused) { } }
        else await dispatch.CompleteStructuredAsync(input, "{}");
        Assert.Equal(1, notifications);
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RejectedRequest_DoesNotNotifyDispatch(bool streaming, bool cancelled)
    {
        int notifications = 0;
        var client = new RecordingClient();
        using var dispatch = new LlmRequestDispatcher(client, Local(), requestStarting: () => notifications++, audit: TestPrivacyAudit.Store);
        using var cts = new CancellationTokenSource();
        if (cancelled) cts.Cancel();
        var input = new[] { ChatMessage.User(new string('x', 10000)) };
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            if (streaming) { await foreach (var _ in dispatch.StreamChatAsync(input, cts.Token)) { } }
            else await dispatch.CompleteStructuredAsync(input, "{}", cts.Token);
        });
        Assert.Equal(0, notifications);
        Assert.Equal(0, client.Calls);
    }

    private sealed class RecordingClient : ILlmClient
    {
        public int Calls;
        public List<ChatMessage> Messages = new();
        public Action? BeforeResult;
        public LlmTokenUsage? LastTokenUsage => null;
        public LlmCompletion? LastCompletion { get; init; }
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default)
        { Calls++; Messages = messages.ToList(); BeforeResult?.Invoke(); return Task.FromResult("ok"); }
        public async IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Calls++; Messages = messages.ToList(); await Task.Yield(); BeforeResult?.Invoke(); yield return "ok"; }
    }
}
