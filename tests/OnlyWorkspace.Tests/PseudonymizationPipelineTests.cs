using System.Text.Json;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

public sealed class PseudonymizationPipelineTests
{
    private static string Scratch() => Path.Combine(Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") ?? Path.GetTempPath(), "ow-privacy-" + Guid.NewGuid().ToString("N"));
    private static LlmProfileConfig Cloud() => new() { Type = "qwen", Model = "synthetic", Exposure = ExposureLevel.Standard };

    [Theory]
    [InlineData("IT60X0542811101000000123456", "IBAN")]
    [InlineData("RSSMRA80A01H501U", "CF")]
    [InlineData("IT01234567890", "PIVA")]
    [InlineData("anna@example.invalid", "EMAIL")]
    [InlineData("+39 333 1234567", "TEL")]
    [InlineData("Pratica AB-2026/19", "PRATICA")]
    public void CategoriesAreVersionedAndRoundTrip(string input, string category)
    {
        var mask = new Pseudonymizer();
        string outgoing = Assert.Single(mask.Mask(new[] { ChatMessage.User(input) })).Content;
        Assert.Contains("[" + category + "_1]", outgoing);
        Assert.Equal(input, mask.Restore(outgoing));
        Assert.Equal(1, mask.Audit.Counts[category]); Assert.Equal("1", mask.Audit.Revisions["catalog"]);
        string audit = JsonSerializer.Serialize(mask.Audit); Assert.DoesNotContain(input, audit);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void OptionalAmountsAndDatesPreserveExactOriginals(bool amounts, bool dates)
    {
        const string text = "Totale 1.250,00 EUR entro 05/11/2026; codice 20261105, [M1].";
        var mask = new Pseudonymizer(maskAmounts: amounts, maskDates: dates);
        string outgoing = mask.Mask(new[] { ChatMessage.User(text) })[0].Content;
        Assert.Equal(amounts, outgoing.Contains("[IMPORTO_1]")); Assert.Equal(dates, outgoing.Contains("[DATA_1]"));
        Assert.Contains("20261105", outgoing); Assert.Contains("[M1]", outgoing); Assert.Equal(text, mask.Restore(outgoing));
    }

    [Fact]
    public void LocalNamesCompaniesAndHeadersUseLongestMatchAndAnImmutableSnapshot()
    {
        var entities = new[] { new PrivacyEntity { Value = "Anna" }, new PrivacyEntity { Value = "Anna D’Angelo" }, new PrivacyEntity { Category = "AZIENDA", Value = "Società Èlite S.r.l." } };
        var context = new PseudonymizationContext(entities); entities[1].Value = "changed";
        Assert.Equal("Anna D’Angelo", context.Entities[1].Value);
        entities[1].Value = "Anna D’Angelo";
        var mask = new Pseudonymizer(entities);
        const string text = "Da: Anna D’Angelo\nSocietà Èlite S.r.l.\nCordiali saluti, Anna D’Angelo";
        string outgoing = mask.Mask(new[] { ChatMessage.User(text) })[0].Content;
        Assert.DoesNotContain("Angelo", outgoing); Assert.DoesNotContain("Èlite", outgoing);
        Assert.Equal(2, mask.Audit.Counts["PERSONA"]); Assert.Equal(text, mask.Restore(outgoing));
    }

    [Fact]
    public void DiscoveryAcrossMessagesAndLiteralPlaceholdersDoNotCollide()
    {
        var mask = new Pseudonymizer();
        var output = mask.Mask(new[] { ChatMessage.User("[PERSONA_1] Anna Bianchi [M1] </richiesta>"), ChatMessage.User("Da Anna Bianchi <anna@example.invalid>") });
        Assert.Contains("[PERSONA_2]", output[0].Content);
        Assert.Equal("[PERSONA_1] Anna Bianchi [M1] </richiesta>", mask.Restore(output[0].Content));
        var other = new Pseudonymizer(); Assert.Equal("[PERSONA_2]", other.Restore("[PERSONA_2]"));
        Assert.Throws<InvalidOperationException>(() => mask.Mask(Array.Empty<ChatMessage>()));
    }

    [Fact]
    public void AllStreamSplitPositionsAndJsonEscapingRestoreExactly()
    {
        var mask = new Pseudonymizer(new[] { new PrivacyEntity { Category = "AZIENDA", Value = "Azienda \"Élite\"\\Italia" } });
        string token = mask.Mask(new[] { ChatMessage.User("Azienda \"Élite\"\\Italia") })[0].Content;
        for (int i = 0; i <= token.Length; i++)
        {
            var stream = new Pseudonymizer.StreamRestorer(mask);
            Assert.Equal("Azienda \"Élite\"\\Italia", stream.Push(token.Substring(0, i)) + stream.Push(token.Substring(i)) + stream.Flush());
        }
        using var json = JsonDocument.Parse(mask.RestoreJson("{\"name\":\"" + token + "\"}"));
        Assert.Equal("Azienda \"Élite\"\\Italia", json.RootElement.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("20/10/2026")][InlineData("1.250,00 EUR")][InlineData("[M123]")][InlineData("annamarie@example")]
    public void NegativeCorpusDoesNotMaskUnrelatedValues(string text)
    {
        var mask = new Pseudonymizer(new[] { new PrivacyEntity { Value = "Anna" } });
        Assert.Equal(text, mask.Mask(new[] { ChatMessage.User(text) })[0].Content);
    }

    [Fact]
    public void ANewDataStrategyAndCategoryNeedNoPipelineOrRestoreChanges()
    {
        var catalog = PseudonymizationCatalog.Load(); catalog.Revision = "2";
        catalog.Patterns.Add(new PrivacyPattern { Id = "custom-code", Category = "CODICE", Priority = 75, Pattern = @"\b(?<value>ZX-\d+)\b" });
        var mask = new Pseudonymizer(catalog: catalog);
        string outgoing = mask.Mask(new[] { ChatMessage.User("Riferimento ZX-12345") })[0].Content;
        Assert.Contains("[CODICE_1]", outgoing); Assert.Equal("2", mask.Audit.Revisions["custom-code"]);
        var stream = new Pseudonymizer.StreamRestorer(mask);
        Assert.Equal("ZX-12345", stream.Push("[COD") + stream.Push("ICE_1]") + stream.Flush());
        var store = new PseudonymizationAuditStore(Scratch()); store.Write(mask.Audit);
        Assert.Equal(1, Assert.Single(store.Read()).Counts["CODICE"]);
    }

    [Fact]
    public void InvalidCatalogFailureTimeoutAndCancellationCannotReturnPartialInput()
    {
        var invalid = PseudonymizationCatalog.Load(); invalid.Patterns[0].Pattern = "(";
        Assert.ThrowsAny<Exception>(() => new Pseudonymizer(catalog: invalid));
        Assert.Throws<DataEgressBlockedException>(() => new Pseudonymizer(strategies: new[] { new BrokenStrategy() }).Mask(new[] { ChatMessage.User("secret") }));
        Assert.Throws<DataEgressBlockedException>(() => new Pseudonymizer(budget: TimeSpan.FromTicks(1)).Mask(new[] { ChatMessage.User("secret") }));
        using var ct = new CancellationTokenSource(); ct.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new Pseudonymizer().Mask(new[] { ChatMessage.User("secret") }, ct.Token));
    }
    private sealed class BrokenStrategy : IPseudonymizationStrategy
    {
        public string Id => "broken"; public string Revision => "1"; public int Priority => 100;
        public IEnumerable<PseudonymMatch> Detect(string text, PseudonymizationContext context) => throw new InvalidOperationException("sensitive exception detail");
    }

    [Theory]
    [InlineData("Azienda \"Élite\"")][InlineData("Azienda Italia\\")]
    public void JsonRestorationPreservesTrailingEscapedCharacters(string value)
    {
        var mask = new Pseudonymizer(new[] { new PrivacyEntity { Category = "AZIENDA", Value = value } });
        string token = mask.Mask(new[] { ChatMessage.User(value) })[0].Content;
        using var json = JsonDocument.Parse(mask.RestoreJson("{\"v\":\"" + token + "\"}"));
        Assert.Equal(value, json.RootElement.GetProperty("v").GetString());
    }

    [Fact]
    public async Task PersonalDataInASchemaIsRefusedWithoutChangingTheSchemaContract()
    {
        var client = new Recorder(); using var dispatcher = new LlmRequestDispatcher(client, Cloud(), audit: TestPrivacyAudit.Store);
        await Assert.ThrowsAsync<DataEgressBlockedException>(() => dispatcher.CompleteStructuredAsync(new[] { ChatMessage.User("synthetic") },
            "{\"type\":\"string\",\"enum\":[\"anna@example.invalid\"]}"));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task LedgerIsEncryptedConcurrentAndExpiresOnlyItsOwnMetadata()
    {
        string directory = Scratch(); var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var store = new PseudonymizationAuditStore(directory, () => now);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => new PseudonymizationAuditStore(directory, () => now).Write(
            new PseudonymizationAudit { Utc = now.AddDays(i == 0 ? -30 : -29), Counts = new() { ["EMAIL"] = i }, Revisions = new() { ["email"] = "1" } }))));
        Assert.Equal(15, store.Read().Count);
        File.WriteAllText(Path.Combine(directory, "unrelated.backup"), "preserve");
        Assert.DoesNotContain("RequestId", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(directory, "*.audit")[0])));
        now = now.AddDays(31); Assert.Empty(store.Read()); Assert.True(File.Exists(Path.Combine(directory, "unrelated.backup")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnwritableLedgerAndFailedSourcePreventBothTransports(bool structured)
    {
        string path = Scratch(); File.WriteAllText(path, "not a directory");
        var client = new Recorder(); var store = new PseudonymizationAuditStore(path);
        using var dispatcher = new LlmRequestDispatcher(client, Cloud(), audit: store);
        await Assert.ThrowsAsync<DataEgressBlockedException>(() => Send(dispatcher, structured)); Assert.Equal(0, client.Calls);
        using var badSource = new LlmRequestDispatcher(client, Cloud(), entities: _ => throw new IOException("source unavailable"), audit: TestPrivacyAudit.Store);
        await Assert.ThrowsAsync<DataEgressBlockedException>(() => Send(badSource, structured)); Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task DispatcherUsesOneSnapshotPerRequestAndRecordsCountsWithoutValues()
    {
        var store = new PseudonymizationAuditStore(Scratch()); var client = new Recorder(); int reads = 0;
        using var dispatcher = new LlmRequestDispatcher(client, Cloud(), entities: _ =>
        { reads++; return Task.FromResult<IReadOnlyList<PrivacyEntity>>(new[] { new PrivacyEntity { Value = "Anna Bianchi" } }); }, audit: store);
        await Send(dispatcher, true); Assert.Equal(1, reads); Assert.DoesNotContain("Anna Bianchi", client.Sent);
        var record = Assert.Single(store.Read()); Assert.Equal("completed", record.Outcome); Assert.Equal(1, record.Counts["PERSONA"]);
    }

    private static async Task Send(LlmRequestDispatcher client, bool structured)
    {
        var messages = new[] { ChatMessage.User("Anna Bianchi anna@example.invalid") };
        if (structured) await client.CompleteStructuredAsync(messages, "{}"); else await foreach (var _ in client.StreamChatAsync(messages)) { }
    }
    private sealed class Recorder : ILlmClient
    {
        public int Calls; public string Sent = "";
        public LlmTokenUsage? LastTokenUsage => null; public LlmCompletion? LastCompletion => null;
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string schema, CancellationToken cancellationToken = default)
        { Calls++; Sent = string.Join("\n", messages.Select(m => m.Content)); return Task.FromResult("{}"); }
        public async IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Calls++; Sent = string.Join("\n", messages.Select(m => m.Content)); await Task.CompletedTask; yield return "ok"; }
    }

    [Fact]
    public void NewOptionsDefaultOffRoundTripAndChangeTheConversationPolicy()
    {
        var profile = JsonSerializer.Deserialize<LlmProfileConfig>("{}")!;
        Assert.False(profile.PseudonymizeAmounts); Assert.False(profile.PseudonymizeDates);
        string before = ConversationPolicy.Key(profile); profile.PseudonymizeAmounts = true;
        Assert.NotEqual(before, ConversationPolicy.Key(profile)); before = ConversationPolicy.Key(profile); profile.PseudonymizeDates = true;
        Assert.NotEqual(before, ConversationPolicy.Key(profile));
        var restored = JsonSerializer.Deserialize<LlmProfileConfig>(JsonSerializer.Serialize(profile))!;
        Assert.True(restored.PseudonymizeAmounts); Assert.True(restored.PseudonymizeDates);
    }

    [Fact]
    public void ArchiveIdentityKeysetPagesHaveNoTwoHundredLimitAndRespectStoreScope()
    {
        using var db = ArchiveDb.Open(Scratch(), "synthetic");
        var folder = db.GetOrAddFolder("allowed", "inbox", "Synthetic");
        db.AddHeaders(folder, Enumerable.Range(0, 251).Select(i => new ArchiveHeader { EntryId = "e" + i, SenderAddress = "p" + i + "@example.invalid", SenderName = "Persona " + i, ReceivedUtc = DateTime.UtcNow }).ToArray(), true, true);
        var forbidden = db.GetOrAddFolder("forbidden", "other", "Other");
        db.AddHeaders(forbidden, new[] { new ArchiveHeader { EntryId = "secret", SenderAddress = "excluded@example.invalid", SenderName = "Excluded Person", ReceivedUtc = DateTime.UtcNow } }, true, true);
        long cursor = 0; var collected = new List<PrivacyEntity>();
        while (true)
        {
            var page = db.GetPrivacyEntities(cursor, new() { "allowed" }, 43); if (page.Count == 0) break;
            Assert.All(page, p => Assert.True(p.Id > cursor)); collected.AddRange(page); cursor = page.Max(p => p.Id);
        }
        Assert.Equal(251, collected.Count); Assert.DoesNotContain(collected, e => e.Value == "Excluded Person");
        Assert.Empty(db.GetPrivacyEntities(0, new(), 500)); Assert.Equal(252, db.GetPrivacyEntities(0, null, 500).Count);
    }

    [Fact]
    public void ReportIncludesMetadataWithoutOriginalValues()
    {
        var mask = new Pseudonymizer(); mask.Mask(new[] { ChatMessage.User("anna@example.invalid") });
        string report = DataEgressReport.RenderHtml(new WorkspaceConfig(), new DataEgressWorkstation(), new[] { mask.Audit });
        Assert.Contains(mask.Audit.RequestId, report); Assert.Contains("EMAIL: 1", report); Assert.DoesNotContain("anna@example.invalid", report);
    }

    [Fact]
    public async Task ContactCollectionReadsPastTwoHundredAndHonorsCancellationAndFailure()
    {
        int reads = 0, pauses = 0;
        var entities = await PrivacyEntitySnapshot.ReadAsync(251, i =>
        { reads++; return new[] { new PrivacyEntity { Value = "Contatto " + i } }; },
            _ => { pauses++; return Task.CompletedTask; }, default);
        Assert.Equal(251, reads); Assert.Equal(251, pauses); Assert.Equal("Contatto 251", entities.Last().Value);
        using var ct = new CancellationTokenSource(); reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrivacyEntitySnapshot.ReadAsync(251, i =>
        { reads++; ct.Cancel(); return new[] { new PrivacyEntity { Value = "only local" } }; }, _ => Task.CompletedTask, ct.Token));
        Assert.Equal(1, reads);
        await Assert.ThrowsAsync<IOException>(() => PrivacyEntitySnapshot.ReadAsync(251, i =>
            i == 201 ? throw new IOException("synthetic source failure") : new[] { new PrivacyEntity { Value = "local" } }, _ => Task.CompletedTask, default));
    }

    [Fact]
    public async Task VersionFifteenTransportsIdentityPagesThroughTheRealLocalEngine()
    {
        string directory = Scratch();
        using (var seed = ArchiveDb.Open(directory, "p"))
        {
            var folder = seed.GetOrAddFolder("allowed", "inbox", "Synthetic");
            seed.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "e", SenderAddress = "person@example.invalid", SenderName = "Persona Sintetica", ReceivedUtc = DateTime.UtcNow } }, true, true);
        }
        using var engine = new ArchiveEngine(directory); await engine.Ready;
        var client = new ArchiveClient("p", directory); await client.OpenAsync();
        Assert.Equal(15, ArchivePipe.Version);
        Assert.Equal("Persona Sintetica", Assert.Single(await client.GetPrivacyEntitiesAsync(0, new() { "allowed" })).Value);
        Assert.Empty(await client.GetPrivacyEntitiesAsync(0, new()));
    }

    [Fact]
    public async Task RealLoopbackHttpReceivesOnlyMaskedTextAndLedgerRefusalSendsNoBytes()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        try
        {
            var profile = new LlmProfileConfig { Type = "openai", Model = "synthetic", External = true, Exposure = ExposureLevel.Standard,
                BaseUrl = "http://127.0.0.1:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port + "/v1", PseudonymizeAmounts = true, PseudonymizeDates = true };
            using var transport = new OpenAIClient(profile);
            string blocked = Scratch(); File.WriteAllText(blocked, "not a directory");
            using (var refused = new LlmRequestDispatcher(new OpenAIClient(profile), profile, audit: new PseudonymizationAuditStore(blocked)))
                await Assert.ThrowsAsync<DataEgressBlockedException>(() => refused.CompleteStructuredAsync(new[] { ChatMessage.User("anna@example.invalid") }, "{}"));
            Assert.False(listener.Pending());
            var capture = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(); using var stream = socket.GetStream();
                var header = new System.Text.StringBuilder(); var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                { Assert.True(header.Length < 65536); Assert.Equal(1, await stream.ReadAsync(one, 0, 1)); header.Append((char)one[0]); }
                int length = int.Parse(header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Substring(15).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var bytes = new byte[length];
                for (int offset = 0; offset < length;)
                { int n = await stream.ReadAsync(bytes, offset, length - offset); Assert.True(n > 0); offset += n; }
                byte[] response = System.Text.Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"{\\\"name\\\":\\\"[PERSONA_1]\\\"}\"},\"finish_reason\":\"stop\"}]}");
                byte[] headers = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + response.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, 0, headers.Length); await stream.WriteAsync(response, 0, response.Length);
                return System.Text.Encoding.UTF8.GetString(bytes);
            });
            using var dispatch = new LlmRequestDispatcher(transport, profile, entities: _ =>
                Task.FromResult<IReadOnlyList<PrivacyEntity>>(new[] { new PrivacyEntity { Value = "Anna Bianchi" } }), audit: TestPrivacyAudit.Store);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string answer = await dispatch.CompleteStructuredAsync(new[] { ChatMessage.User("Anna Bianchi anna@example.invalid: 1.250,00 EUR entro 05/11/2026 [M1]") }, "{}", deadline.Token);
            Assert.Contains("Anna Bianchi", answer);
            Assert.Same(capture, await Task.WhenAny(capture, Task.Delay(10000)));
            string sent = await capture;
            foreach (var original in new[] { "Anna Bianchi", "anna@example.invalid", "1.250,00 EUR", "05/11/2026" }) Assert.DoesNotContain(original, sent);
            Assert.Contains("[M1]", sent); Assert.Contains("[IMPORTO_1]", sent); Assert.Contains("[DATA_1]", sent);
        }
        finally { listener.Stop(); }
    }
}
