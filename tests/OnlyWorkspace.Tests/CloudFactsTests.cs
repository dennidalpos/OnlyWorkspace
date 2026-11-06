using System.Net;
using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

public sealed class CloudFactsTests
{
    private static MailEvidencePack Pack() => MailEvidencePack.Chronological(new[] { new MailEvidence
    {
        Mail = new ChatMailItem { Subject = "PRIVATE-SUBJECT", EntryId = "PRIVATE-ENTRY", StoreId = "PRIVATE-STORE",
            Facts = new() { new ArchiveFact { Kind = ArchiveFactKind.Deadline, Resolved = true,
                Raw = "Scadenza riservata 30/11/2026, Anna Bianchi", Value = "2026-11-30", Date = "2026-11-30",
                Context = "PRIVATE-CONTEXT", SourceName = "PRIVATE-FILENAME" } } }, Text = "PRIVATE-BODY"
    } });

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("ollama")]
    public async Task AllTransportsRejectUnapprovedTextAndAcceptOnlyOpaqueFacts(string type)
    {
        int calls = 0; string? payload = null;
        const string answer = "{\"facts\":[\"[FATTO_1]\"]}";
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++; payload = await request.Content!.ReadAsStringAsync();
            string response = type switch
            {
                "anthropic" => JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = answer } }, stop_reason = "end_turn" }),
                "gemini" => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = answer } } }, finishReason = "STOP" } } }),
                "ollama" => JsonSerializer.Serialize(new { message = new { content = answer }, done = true, done_reason = "stop" }) + "\n",
                _ => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer }, finish_reason = "stop" } } })
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        })) { BaseAddress = new Uri("http://127.0.0.1:12345/") };
        var profile = new LlmProfileConfig { Type = type, External = true, Exposure = ExposureLevel.Standard,
            BaseUrl = http.BaseAddress.ToString(), Model = "synthetic" };
        using var raw = (IDisposable)LlmClientFactory.CreateClient(profile, httpClient: http);
        var client = (OnlyWorkspace.Core.Interfaces.ILlmClient)raw;
        await Assert.ThrowsAsync<DataEgressBlockedException>(() => client.CompleteStructuredAsync(new[] { ChatMessage.User("PRIVATE-BODY") }, "{}"));
        await Assert.ThrowsAsync<DataEgressBlockedException>(async () => { await foreach (var _ in client.StreamChatAsync(new[] { ChatMessage.User("PRIVATE-BODY") })) { } });
        Assert.Equal(0, calls);
        var audit = TestPrivacyAudit.Store;
        using var dispatcher = new LlmRequestDispatcher(client, profile, audit: audit);
        var pack = Pack(); var safe = new CloudFactsRequest(pack);
        pack.Items[0].Mail.Facts[0].Raw = "MUTATED";
        string result = await dispatcher.CompleteStructuredAsync(safe.Messages, CloudFactsRequest.Schema);
        var restored = safe.ReadAnswer(result);
        Assert.Equal(1, calls);
        foreach (string secret in new[] { "PRIVATE", "Anna", "2026-11-30", "30/11/2026", "MUTATED" }) Assert.DoesNotContain(secret, payload!);
        Assert.Contains("[FATTO_1]", payload!);
        var record = Assert.Single(audit.Read());
        Assert.Equal("completed", record.Outcome); Assert.Equal(1, record.Counts["DEADLINE"]);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(record));
        Assert.Contains("Anna Bianchi", Assert.Single(restored.Points).Text);
        Assert.Equal(new DateTime(2026, 11, 30), Assert.Single(safe.ReadActionItems(result)).Due);
    }

    [Fact]
    public void UnknownReferencesIncompleteAnswersAndUnresolvedFactsAreRefused()
    {
        var request = new CloudFactsRequest(Pack());
        Assert.Throws<InvalidOperationException>(() => request.ReadAnswer("{\"facts\":[\"[FATTO_99]\"]}"));
        Assert.Throws<InvalidOperationException>(() => request.ReadAnswer("{\"facts\":[]}"));
        var pack = Pack(); pack.Items[0].Mail.Facts[0].Resolved = false;
        Assert.Throws<DataEgressBlockedException>(() => new CloudFactsRequest(pack));
        Assert.Throws<DataEgressBlockedException>(() => CloudRequestGuard.Validate(new LlmProfileConfig { External = true }, request.Messages, "{}"));
        Assert.Throws<DataEgressBlockedException>(() => CloudRequestGuard.Validate(new LlmProfileConfig { External = true }, request.Messages.Select(m => new ChatMessage(m.Role, m.Content)), CloudFactsRequest.Schema));
    }

    [Fact]
    public void SimpleAuthorizationHasNoFabricatedDeclarationsAndRenewalPreservesHistory()
    {
        var profile = new LlmProfileConfig { Id = "cloud", External = true, Exposure = ExposureLevel.Standard };
        var config = new WorkspaceConfig { Profiles = new() { profile } };
        var first = DataEgress.Accept(config, "synthetic-user", "synthetic-pc", DateTime.UtcNow, _ => "synthetic-key", TestEgress.ConfirmedRegions(config));
        Assert.True(DataEgress.HasProfileGrant(config, profile, _ => "synthetic-key"));
        Assert.Empty(first.Company); Assert.Empty(first.LegalBasis);
        Assert.False(first.WorkersInformed); Assert.False(first.AiLiteracy);
        Assert.All(first.Providers, p => { Assert.False(p.NoTraining); Assert.False(p.ProcessingAgreement); });
        first.NoticeVersion = 4;
        Assert.False(DataEgress.AllowsModel(config, profile));
        DataEgress.Accept(config, "synthetic-user", "synthetic-pc", DateTime.UtcNow.AddSeconds(1), _ => "synthetic-key", TestEgress.ConfirmedRegions(config));
        Assert.Contains(config.DataEgress.History, e => e.Authorization?.NoticeVersion == 4);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
