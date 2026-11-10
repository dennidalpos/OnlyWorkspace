using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

/// <summary>Real factory, dispatcher and HTTP on loopback; no provider account or Outlook data.</summary>
public sealed class MaskedFreeTextTransportTests
{
    private const string Schema = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"],\"additionalProperties\":false}";
    private const string Text = "Da Anna Bianchi <anna@example.test>: conferma il bonifico di 1234,56 euro e la commissione di EURO 4,50 entro il 15/11/2026 su IT60X0542811101000000123456";
    private static readonly string[] Originals = { "Anna Bianchi", "anna@example.test", "IT60X0542811101000000123456", "1234,56", "4,50", "15/11/2026" };

    public static IEnumerable<object[]> Transports()
    {
        foreach (string provider in new[] { "openai", "anthropic", "gemini", "ollama" })
            foreach (var exposure in new[] { ExposureLevel.Standard, ExposureLevel.Extended })
                foreach (bool stream in new[] { false, true })
                    yield return new object[] { provider, exposure, stream };
    }

    private static LlmProfileConfig Profile(Server server, string provider, ExposureLevel exposure) => new()
    {
        Id = "masked-http",
        Type = provider,
        Model = "synthetic",
        External = true,
        Exposure = exposure,
        ServiceRegion = ServiceRegion.Eu,
        UseSystemProxy = false,
        BaseUrl = server.Address + (provider == "ollama" ? "/" : provider == "gemini" ? "/v1beta" : "/v1")
    };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task MaskedText_RoundtripsThroughEveryTransport(string provider, ExposureLevel exposure, bool stream)
    {
        using var server = new Server(provider + "-" + exposure + "-" + stream);
        var profile = Profile(server, provider, exposure);
        string? masked = null;
        var capture = server.ServeAsync(provider, request =>
        {
            masked = Strings(request).Single(s => s.Contains("conferma il bonifico"));
            return stream ? masked : JsonSerializer.Serialize(new { name = masked });
        });
        using var dispatch = new LlmRequestDispatcher(LlmClientFactory.CreateClient(profile), profile, audit: server.Audit);
        if (stream)
        {
            var answer = new StringBuilder();
            await foreach (string chunk in dispatch.StreamChatAsync(new[] { ChatMessage.User(Text) }, server.Token)) answer.Append(chunk);
            Assert.Equal(Text, answer.ToString());
        }
        else
        {
            string answer = await dispatch.CompleteStructuredAsync(new[] { ChatMessage.User(Text) }, Schema, server.Token);
            using var json = JsonDocument.Parse(answer);
            Assert.Equal(Text, json.RootElement.GetProperty("name").GetString());
        }
        AssertMasked(await capture);
        Assert.NotNull(masked);
        Assert.Contains("conferma il bonifico", masked);
        Assert.Contains("[IMPORTO_1]", masked);
        Assert.Contains("[IMPORTO_2]", masked);
        Assert.Matches(@"\[[A-Z][A-Z0-9]*_\d+\]", masked);
        Assert.NotNull(dispatch.LastCompletion);
        Assert.False(dispatch.LastCompletion!.IsTruncated);
        var audit = Assert.Single(server.Audit.Read());
        Assert.Equal("completed", audit.Outcome);
        Assert.Equal("4", audit.Revisions["catalog"]);
        Assert.Equal(2, audit.Counts["IMPORTO"]);
        foreach (string category in new[] { "PERSONA", "EMAIL", "IBAN", "IMPORTO", "DATA" })
            Assert.True(audit.Counts[category] > 0);
        Assert.False(server.Pending);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("ollama")]
    public async Task Rejections_OpenNoConnection_ForStructuredAndStreaming(string provider)
    {
        using var server = new Server(provider + "-refused");
        var profile = Profile(server, provider, ExposureLevel.Minimal);
        using (var dispatch = new LlmRequestDispatcher(LlmClientFactory.CreateClient(profile), profile, audit: server.Audit))
            await RefusesBoth(dispatch, server.Token);
        foreach (var exposure in new[] { ExposureLevel.Standard, ExposureLevel.Extended })
        {
            profile.Exposure = exposure;
            var bare = LlmClientFactory.CreateClient(profile);
            try { await RefusesBoth(bare, server.Token); }
            finally { (bare as IDisposable)?.Dispose(); }
            using var dispatch = new LlmRequestDispatcher(LlmClientFactory.CreateClient(profile), profile,
                validatePolicy: null, status: null, requestStarting: null,
                maskMessages: (_, _) => throw new DataEgressBlockedException("Synthetic masking refusal"), audit: server.Audit);
            await RefusesBoth(dispatch, server.Token);
        }
        Assert.False(server.Pending);
        File.WriteAllText(Path.Combine(server.DirectoryPath, "no-connection.txt"), "Minimal free text, unsealed Standard/Extended and masking refusal: no pending TCP connection.");
    }

    [Fact]
    public async Task FollowUp_StandardCloud_UsesMaskedHttpAndSavesRestoredCitedAnswer()
    {
        using var server = new Server("followup-standard");
        var profile = Profile(server, "openai", ExposureLevel.Standard);
        var config = TestEgress.Accepted(new WorkspaceConfig { DefaultProfile = profile.Id, Profiles = new() { profile } });
        Assert.True(DataEgress.AllowsModel(config, profile, TestEgress.ResolveSecret));
        var capture = server.ServeAsync("openai", request =>
        {
            string prompt = string.Join("\n", Strings(request));
            string email = Regex.Match(prompt, @"\[EMAIL_\d+\]").Value;
            Assert.NotEmpty(email);
            return JsonSerializer.Serialize(new
            {
                sections = new[] { new { title = "Stato", points = new[] {
                new { text = "Riscontro per " + email, sources = new[] { "M1", "M2" } }
            } } },
                gaps = Array.Empty<string>()
            });
        });
        var prior = new FollowUpMail
        {
            EntryId = "prior",
            StoreId = "synthetic-store",
            Subject = "Ordine ABC-1234",
            Sender = "Anna Bianchi <anna@example.test>",
            SenderEmail = "anna@example.test",
            Body = Text,
            ReceivedTime = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Local)
        };
        var current = new FollowUpMail
        {
            EntryId = "current",
            StoreId = prior.StoreId,
            Subject = "Re: Ordine ABC-1234",
            Sender = prior.Sender,
            SenderEmail = prior.SenderEmail,
            Body = Text,
            ReceivedTime = prior.ReceivedTime.AddDays(1),
            ThreadMessages = new[] { prior }
        };
        FollowUpActionResult? saved = null;
        int saves = 0;
        var chunks = new List<string>();
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = _ => current,
            CreateClient = () => new LlmRequestDispatcher(LlmClientFactory.CreateClient(profile), profile,
                validatePolicy: () => Assert.True(DataEgress.AllowsModel(config, profile, TestEgress.ResolveSecret)), audit: server.Audit),
            OnChunk = chunks.Add,
            SaveHistoryAsync = result => { saves++; saved = result; return Task.CompletedTask; }
        };
        var result = await workflow.ExecuteAsync(null, profile, new ContextBuilder(), false, server.Token);
        AssertMasked(await capture);
        Assert.Equal(FollowUpOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "prior", "current" }, result.MailSources.Select(s => s.EntryId));
        Assert.All(result.MailSources, source => Assert.Equal(prior.StoreId, source.StoreId));
        Assert.Contains("anna@example.test", result.Answer);
        Assert.Contains("[M1]", result.Answer);
        Assert.Contains("[M2]", result.Answer);
        Assert.DoesNotContain("[EMAIL_", result.Answer);
        Assert.Equal(result.Answer, string.Concat(chunks));
        Assert.Same(result, saved);
        Assert.Equal(1, saves);
        Assert.Equal(1, server.Requests);
        Assert.False(server.Pending);
        File.WriteAllText(Path.Combine(server.DirectoryPath, "local-answer.txt"), result.Answer);
    }

    public static IEnumerable<object[]> Exposures()
    {
        foreach (string provider in new[] { "openai", "anthropic", "gemini", "ollama" })
            foreach (var exposure in new[] { ExposureLevel.Standard, ExposureLevel.Extended })
                yield return new object[] { provider, exposure };
    }

    [Theory]
    [MemberData(nameof(Exposures))]
    public async Task SecurityReview_SendsMaskedSources_AndVerifiesRestoredQuotes(string provider, ExposureLevel exposure)
    {
        // Names after a line break, a +39 phone and accented letters: the cases JSON escapes used to hide from the mask.
        const string body = "Gentile cliente,\nla prego di chiamare\nMario Rossi al +39 333 1234567 e di versare 1.250,00 EUR entro il 05/11/2026 " +
            "sull'IBAN IT60X0542811101000000123456 (CF RSSMRA80A01H501U), sede in via Dante Alighieri 5/B, 20121 Milano (MI).\nSaluti,\nNiccolò Bàrberi";
        string[] originals = { "Mario Rossi", "Rossi", "Niccolò", "Bàrberi", "niccolo@example.test", "+39", "333 1234567", "1.250,00", "05/11/2026",
            "IT60X0542811101000000123456", "RSSMRA80A01H501U", "Alighieri", "20121 Milano" };
        using var server = new Server("security-" + provider + "-" + exposure);
        var profile = Profile(server, provider, exposure);
        string? sent = null;
        var capture = server.ServeAsync(provider, request =>
        {
            sent = Strings(request).Single(s => s.Contains("<fonte id=\"email\""));
            string call = Regex.Match(sent, @"chiamare\n(\[PERSONA_\d+\] al \[TEL_\d+\])").Groups[1].Value;
            string signature = Regex.Match(sent, @"Saluti,\n\[PERSONA_\d+\]").Value;
            return JsonSerializer.Serialize(new
            {
                findings = new[] {
                new { category = "payment", reason = "Chiede di chiamare " + call, sourceId = "email", quote = call },
                new { category = "identity", reason = "Firma", sourceId = "email", quote = signature },
                new { category = "other", reason = "Inventata", sourceId = "email", quote = "[PERSONA_99] al telefono" } }
            });
        });
        var report = OnlyWorkspace.Core.Security.EmailSecurityAnalyzer.Analyze(new() { Body = body });
        var sources = OnlyWorkspace.Core.Security.SecurityContentAnalysis.CreateSources("Bonifico", "Niccolò Bàrberi", "niccolo@example.test", body, report);
        var review = await OnlyWorkspace.Core.Security.SecurityContentAnalysis.ReviewAsync(profile, _ => Task.FromResult<IReadOnlyList<OnlyWorkspace.Core.Security.SecuritySource>>(sources),
            "- controllo locale", () => new LlmRequestDispatcher(LlmClientFactory.CreateClient(profile), profile,
                entities: _ => Task.FromResult<IReadOnlyList<PrivacyEntity>>(new[] { new PrivacyEntity { Value = "Mario Rossi" } }), audit: server.Audit), server.Token);
        string wire = await capture;
        using var json = JsonDocument.Parse(wire.Substring(wire.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4));
        string decoded = string.Join("\n", Strings(json.RootElement));
        foreach (string original in originals) { Assert.DoesNotContain(original, wire); Assert.DoesNotContain(original, decoded); }
        Assert.DoesNotContain("\\u002B", decoded);
        foreach (string category in new[] { "PERSONA", "EMAIL", "TEL", "IBAN", "CF", "IMPORTO", "DATA", "INDIRIZZO" })
            Assert.Matches(@"\[" + category + @"_\d+\]", sent);
        Assert.Contains("la prego di chiamare\n[PERSONA_", sent);
        Assert.True(review.Completed);
        Assert.Equal(new[] { "Mario Rossi al +39 333 1234567", "Saluti,\nNiccolò Bàrberi" }, review.Assessment.Findings.Select(f => f.Quote));
        Assert.All(review.Assessment.Findings, f => Assert.Contains(f.Quote, sources[0].Text));
        Assert.Equal("Chiede di chiamare Mario Rossi al +39 333 1234567", review.Assessment.Findings[0].Reason);
        Assert.Equal(1, review.Assessment.RejectedFindings);
        Assert.Equal("completed", Assert.Single(server.Audit.Read()).Outcome);
        Assert.False(server.Pending);
    }

    private static async Task RefusesBoth(ILlmClient client, CancellationToken token)
    {
        await Assert.ThrowsAsync<DataEgressBlockedException>(() => client.CompleteStructuredAsync(new[] { ChatMessage.User(Text) }, Schema, token));
        await Assert.ThrowsAsync<DataEgressBlockedException>(async () =>
        {
            await foreach (var _ in client.StreamChatAsync(new[] { ChatMessage.User(Text) }, token)) { }
        });
    }

    private static void AssertMasked(string wire)
    {
        foreach (string original in Originals) Assert.DoesNotContain(original, wire);
        using var json = JsonDocument.Parse(wire.Substring(wire.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4));
        string decoded = string.Join("\n", Strings(json.RootElement));
        foreach (string original in Originals) Assert.DoesNotContain(original, decoded);
        Assert.Contains("conferma il bonifico", decoded);
    }

    private static IEnumerable<string> Strings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) yield return element.GetString()!;
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) foreach (string value in Strings(property.Value)) yield return value;
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) foreach (string value in Strings(item)) yield return value;
    }

    private static string Response(string provider, string answer)
    {
        // Split inside a placeholder to exercise the real stream restorer, including structured output.
        var placeholder = Regex.Match(answer, @"\[[A-Z][A-Z0-9]*_\d+\]");
        int split = placeholder.Success ? placeholder.Index + placeholder.Length / 2 : answer.Length / 2;
        string[] chunks = { answer.Substring(0, split), answer.Substring(split) };
        string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
        return provider switch
        {
            "ollama" => string.Concat(chunks.Select(chunk => JsonSerializer.Serialize(new { message = new { content = chunk }, done = false }) + "\n")) +
                JsonSerializer.Serialize(new { message = new { content = "" }, done = true, done_reason = "stop" }) + "\n",
            "gemini" => string.Concat(chunks.Select(chunk => Event(new { candidates = new[] { new { content = new { parts = new[] { new { text = chunk } } } } } }))) +
                Event(new { candidates = new[] { new { finishReason = "STOP" } } }),
            "anthropic" => Event(new { type = "message_start", message = new { } }) +
                string.Concat(chunks.Select(chunk => Event(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = chunk } }))) +
                Event(new { type = "message_delta", delta = new { stop_reason = "end_turn" } }) + Event(new { type = "message_stop" }),
            _ => string.Concat(chunks.Select(chunk => Event(new { choices = new[] { new { delta = new { content = chunk } } } }))) +
                Event(new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } }) + "data: [DONE]\n\n"
        };
    }

    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
        private readonly CancellationTokenRegistration _stop;
        public string DirectoryPath { get; }
        public string Address => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        public CancellationToken Token => _deadline.Token;
        public bool Pending => _listener.Pending();
        public int Requests { get; private set; }
        public IPseudonymizationAuditStore Audit { get; }

        public Server(string scenario)
        {
            DirectoryPath = Path.Combine(Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") ?? Path.GetTempPath(),
                "masked-http-" + scenario + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            Audit = new PseudonymizationAuditStore(Path.Combine(DirectoryPath, "audit"));
            _listener.Start();
            _stop = Token.Register(_listener.Stop);
        }

        public async Task<string> ServeAsync(string provider, Func<JsonElement, string> answer)
        {
            using var socket = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(15));
            using var close = Token.Register(socket.Close);
            using var stream = socket.GetStream();
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.True(header.Length < 65536, "HTTP headers exceeded fixture limit.");
                Assert.Equal(1, await stream.ReadAsync(one, 0, 1, Token));
                header.Append((char)one[0]);
            }
            int length = int.Parse(header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Substring(15).Trim(), CultureInfo.InvariantCulture);
            Assert.InRange(length, 1, 1024 * 1024);
            var bytes = new byte[length];
            for (int offset = 0; offset < length;)
            {
                int read = await stream.ReadAsync(bytes, offset, length - offset, Token);
                Assert.True(read > 0, "Incomplete HTTP request body.");
                offset += read;
            }
            Requests++;
            string body = Encoding.UTF8.GetString(bytes), wire = header + body;
            File.WriteAllText(Path.Combine(DirectoryPath, "request.http"), wire);
            using var json = JsonDocument.Parse(body);
            byte[] response = Encoding.UTF8.GetBytes(Response(provider, answer(json.RootElement)));
            File.WriteAllBytes(Path.Combine(DirectoryPath, "response.txt"), response);
            string mime = provider == "ollama" ? "application/x-ndjson" : "text/event-stream";
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + mime + "\r\nContent-Length: " + response.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, 0, head.Length, Token);
            await stream.WriteAsync(response, 0, response.Length, Token);
            return wire;
        }

        public void Dispose()
        {
            _deadline.Cancel();
            _stop.Dispose();
            _deadline.Dispose();
            _listener.Stop();
        }
    }
}
