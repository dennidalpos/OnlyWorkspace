using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

/// <summary>The model layer (#247) on the real factory, dispatcher and loopback HTTP: names the rules cannot see never leave.</summary>
public sealed partial class MaskedFreeTextTransportTests
{
    private const string NerText = "Riferimento: Luigi Verdi della Acme Costruzioni conferma il bonifico di prova";
    private static readonly string[] NerOriginals = { "Luigi Verdi", "Acme Costruzioni" };

    public static IEnumerable<object[]> NerTransports()
    {
        foreach (string provider in new[] { "openai", "anthropic", "gemini", "ollama" })
            foreach (bool stream in new[] { false, true })
                yield return new object[] { provider, stream };
    }

    [Theory]
    [MemberData(nameof(NerTransports))]
    public async Task Ner_MasksFreeTextNamesInEveryTransport(string provider, bool stream)
    {
        using var server = new Server("ner-" + provider + "-" + stream);
        var profile = Profile(server, provider, ExposureLevel.Standard);
        string? masked = null;
        var capture = server.ServeAsync(provider, request =>
        {
            masked = Strings(request).Single(s => s.Contains("conferma il bonifico"));
            return stream ? masked : JsonSerializer.Serialize(new { name = masked });
        });
        using var dispatch = new LlmRequestDispatcher(LlmClientFactory.CreateClient(profile), profile, audit: server.Audit,
            recognizer: new LiteralRecognizer("synthetic-1", ("Luigi Verdi", "FULLNAME"), ("Acme Costruzioni", "ORG")));
        string answer;
        if (stream)
        {
            var text = new StringBuilder();
            await foreach (string chunk in dispatch.StreamChatAsync(new[] { ChatMessage.User(NerText) }, server.Token)) text.Append(chunk);
            answer = text.ToString();
        }
        else
        {
            using var json = JsonDocument.Parse(await dispatch.CompleteStructuredAsync(new[] { ChatMessage.User(NerText) }, Schema, server.Token));
            answer = json.RootElement.GetProperty("name").GetString()!;
        }
        string wire = await capture;
        foreach (string original in NerOriginals) Assert.DoesNotContain(original, wire);
        Assert.Contains("[PERSONA_1]", masked);
        Assert.Contains("[AZIENDA_1]", masked);
        Assert.Equal(NerText, answer);
        var audit = Assert.Single(server.Audit.Read());
        Assert.Equal("completed", audit.Outcome);
        Assert.Equal("synthetic-1", audit.Revisions["ner"]);
        Assert.Equal(1, audit.Counts["PERSONA"]);
        Assert.Equal(1, audit.Counts["AZIENDA"]);
        Assert.False(server.Pending);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("ollama")]
    public async Task Ner_Failure_OpensNoConnection(string provider)
    {
        using var server = new Server("ner-fail-" + provider);
        var profile = Profile(server, provider, ExposureLevel.Standard);
        foreach (var recognizer in new INerRecognizer[]
        {
            new ThrowingRecognizer("missing-model", new DataEgressBlockedException("Modello assente")),
            new ThrowingRecognizer("dead-process", new IOException("pipe closed")),
            new ThrowingRecognizer("spent-budget", new TimeoutException())
        })
        {
            using var dispatch = new LlmRequestDispatcher(LlmClientFactory.CreateClient(profile), profile, audit: server.Audit, recognizer: recognizer);
            await Assert.ThrowsAsync<DataEgressBlockedException>(() => dispatch.CompleteStructuredAsync(new[] { ChatMessage.User(NerText) }, Schema, server.Token));
            await Assert.ThrowsAsync<DataEgressBlockedException>(async () =>
            {
                await foreach (var _ in dispatch.StreamChatAsync(new[] { ChatMessage.User(NerText) }, server.Token)) { }
            });
        }
        Assert.Equal(0, server.Requests);
        Assert.False(server.Pending);
        Assert.All(server.Audit.Read(), audit => Assert.NotEqual("completed", audit.Outcome));
    }

    internal sealed class LiteralRecognizer : INerRecognizer
    {
        private readonly (string Value, string Label)[] _entities;
        public LiteralRecognizer(string revision, params (string Value, string Label)[] entities) { ModelRevision = revision; _entities = entities; }
        public string ModelRevision { get; }
        public int Calls { get; private set; }
        public TimeSpan LastBudget { get; private set; }
        public IReadOnlyList<NerSpan> Recognize(string block, TimeSpan budget, CancellationToken token)
        {
            Calls++; LastBudget = budget;
            var spans = new List<NerSpan>();
            foreach (var (value, label) in _entities)
                for (int at = block.IndexOf(value, StringComparison.Ordinal); at >= 0; at = block.IndexOf(value, at + value.Length, StringComparison.Ordinal))
                    spans.Add(new NerSpan { Start = at, Length = value.Length, Label = label });
            return spans;
        }
    }

    private sealed class ThrowingRecognizer : INerRecognizer
    {
        private readonly Exception _failure;
        public ThrowingRecognizer(string revision, Exception failure) { ModelRevision = revision; _failure = failure; }
        public string ModelRevision { get; }
        public IReadOnlyList<NerSpan> Recognize(string block, TimeSpan budget, CancellationToken token) => throw _failure;
    }
}
