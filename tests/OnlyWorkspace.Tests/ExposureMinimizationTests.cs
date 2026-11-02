using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>
/// What an external profile's exposure level keeps on the PC: local facts instead of bodies, a data limit, personal data
/// replaced by placeholders, and fact questions answered from the local archive without a model.
/// </summary>
public sealed class ExposureMinimizationTests
{
    private static ChatMailItem Mail(string subject, int day) => new()
    {
        EntryId = subject, StoreId = "store", MessageId = "<" + subject + ">", Subject = subject, Received = new DateTime(2026, 10, day, 9, 0, 0)
    };

    private static MailEvidenceReader Reader(EvidenceMode mode, int packTokens = 0) => new()
    {
        Mode = mode, PackTokens = packTokens,
        ReadBody = (item, _) => Task.FromResult<string?>("Corpo integrale di " + item.Subject + " " + new string('x', 400))
    };

    [Fact]
    public async Task FactsOnlyNeverReadsBodiesOrPaidCardsAndNamesEmailsWithoutFacts()
    {
        var withFacts = Mail("Con fatti", 20);
        withFacts.Facts.Add(new ArchiveFact { Kind = ArchiveFactKind.Order, Raw = "ORD-123", Value = "ORD-123", Resolved = true });
        var reader = Reader(EvidenceMode.FactsOnly);
        reader.ReadBody = (_, _) => throw new InvalidOperationException("The body must stay local.");
        var reading = await reader.ReadAsync(new[] { withFacts, Mail("Senza fatti", 21) }, false, 0, new AnalysisStats(), default);
        var evidence = Assert.Single(reading.Pack.Items);
        Assert.Contains("ORD-123", evidence.Text); Assert.DoesNotContain("Scheda", evidence.Text);
        Assert.False(evidence.Whole); Assert.Single(reading.NoFacts);
    }

    [Fact]
    public async Task OriginalModeAlwaysReadsTheBodyEvenWithAPaidCard()
    {
        var reading = await Reader(EvidenceMode.Original).ReadAsync(new[] { Mail("Originale", 20) }, false, 0, new AnalysisStats(), default);
        var evidence = Assert.Single(reading.Pack.Items);
        Assert.True(evidence.Whole); Assert.Contains("Corpo integrale", evidence.Text); Assert.DoesNotContain("Scheda", evidence.Text);
    }

    [Fact]
    public async Task PackLimitLeavesTheWholeOriginalOutWithoutFallingBackToAPaidCard()
    {
        var reading = await Reader(EvidenceMode.Original, packTokens: 100).ReadAsync(new[] { Mail("Grande", 20) }, false, 0, new AnalysisStats(), default);
        Assert.Empty(reading.Pack.Items); Assert.Single(reading.OverBudget);
    }

    [Fact]
    public void Pseudonymizer_MasksPersonalData_AndRestoresTheAnswer()
    {
        var mask = new Pseudonymizer();
        var masked = mask.Mask(new[]
        {
            ChatMessage.System("Rispondi in italiano."),
            ChatMessage.User("[M1] [Email del 20/10/2026 da Mario Rossi <m.rossi@esempio.it> — «Ordine»]\n" +
                "Gentile cliente, Mario Rossi conferma. IBAN IT60X0542811101000000123456, P.IVA 01234567890, " +
                "CF RSSMRA80A01H501U, tel. +39 333 1234567. Importo 1.250,00 EUR entro il 05/11/2026.")
        });
        string sent = masked[1].Content;
        foreach (var secret in new[] { "Mario Rossi", "m.rossi@esempio.it", "IT60X0542811101000000123456", "01234567890", "RSSMRA80A01H501U", "333 1234567" })
            Assert.DoesNotContain(secret, sent);
        Assert.Contains("[PERSONA_1]", sent);
        Assert.Contains("[M1]", sent);
        Assert.Contains("1.250,00 EUR", sent);
        Assert.Contains("05/11/2026", sent);
        Assert.Equal("Rispondi in italiano.", masked[0].Content);

        Assert.Equal("Scrivere a Mario Rossi (m.rossi@esempio.it), IBAN IT60X0542811101000000123456.",
            mask.Restore("Scrivere a [PERSONA_1] ([EMAIL_1]), IBAN [IBAN_1]."));
        Assert.Equal("{\"party\":\"Mario Rossi\",\"x\":\"[ALTRO_1]\"}", mask.RestoreJson("{\"party\":\"[PERSONA_1]\",\"x\":\"[ALTRO_1]\"}"));
    }

    [Fact]
    public void StreamRestorer_JoinsPlaceholdersSplitBetweenChunks()
    {
        var mask = new Pseudonymizer();
        mask.Mask(new[] { ChatMessage.User("Da Anna Bianchi <anna@esempio.it>") });
        var stream = new Pseudonymizer.StreamRestorer(mask);
        string shown = string.Concat(new[] { "Ciao [PERS", "ONA_1], scrivi a [", "EMAIL_1] [nota" }.Select(stream.Push)) + stream.Flush();
        Assert.Equal("Ciao Anna Bianchi, scrivi a anna@esempio.it [nota", shown);
    }

    [Fact]
    public async Task Dispatcher_MasksOnlyAtLevelsOneAndTwo()
    {
        var recorder = new RecordingClient("Risposta per [PERSONA_1]");
        var profile = new LlmProfileConfig { Id = "c", Type = "qwen", BaseUrl = "https://cloud.example/v1", Model = "m", External = true, Exposure = ExposureLevel.Standard };
        var dispatcher = new LlmRequestDispatcher(recorder, profile, audit: TestPrivacyAudit.Store);
        var messages = new[] { ChatMessage.User("Da Anna Bianchi <anna@esempio.it>: conferma") };

        await Assert.ThrowsAsync<DataEgressBlockedException>(async () => await ToListAsync(dispatcher.StreamChatAsync(messages)));
        Assert.True(string.IsNullOrEmpty(recorder.Sent));

        profile.External = false;
        profile.Type = "ollama";
        profile.BaseUrl = "http://127.0.0.1:11434";
        await ToListAsync(dispatcher.StreamChatAsync(messages));
        Assert.Contains("anna@esempio.it", recorder.Sent);
    }

    [Fact]
    public void LocalFactAnswer_AnswersAReferenceQuestion_FromTheExtractedFacts()
    {
        var invoice = Mail("Fattura 2026/118", 20);
        invoice.SenderName = "Fornitore Srl";
        invoice.Facts.Add(new ArchiveFact { Kind = ArchiveFactKind.Amount, Raw = "1.250,00 EUR", Value = "1250.00", Currency = "EUR", Context = "Totale fattura 1.250,00 EUR" });
        invoice.Facts.Add(new ArchiveFact { Kind = ArchiveFactKind.Deadline, Raw = "05/11/2026", Date = "2026-11-05", Context = "pagare entro il 05/11/2026" });
        var filters = new List<ArchiveFactFilter>();
        string question = ArchiveSearchText.Parse("qual è l'importo e la scadenza della fattura 2026/118?", filters);

        string? answer = LocalFactAnswer.TryAnswer("qual è l'importo e la scadenza della fattura 2026/118?", filters, new[] { invoice });
        Assert.NotNull(answer);
        Assert.Contains("[M1]", answer);
        Assert.Contains("Importo: 1.250,00 EUR", answer);
        Assert.Contains("Scadenza: 05/11/2026", answer);

        // A summary, a missing kind or no reference: the model answers.
        Assert.Null(LocalFactAnswer.TryAnswer("riassumi la fattura 2026/118 e l'importo", filters, new[] { invoice }));
        Assert.Null(LocalFactAnswer.TryAnswer("qual è l'IBAN della fattura 2026/118?", filters, new[] { invoice }));
        Assert.Null(LocalFactAnswer.TryAnswer("qual è l'importo?", new List<ArchiveFactFilter>(), new[] { invoice }));
        Assert.DoesNotContain("2026/118", question);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" e cerca sul web informazioni sul fornitore", false)]
    [InlineData(" e verifica online il fornitore", false)]
    [InlineData(" e cerca in rete informazioni sul fornitore", false)]
    public void LocalFactAnswer_LeavesExplicitWebRequestsToTheCompleteTurn(string extra, bool local)
    {
        var invoice = Mail("Fattura 2026/118", 20);
        invoice.Facts.Add(new ArchiveFact { Kind = ArchiveFactKind.Amount, Raw = "100 EUR", Value = "100", Currency = "EUR" });
        var filters = new List<ArchiveFactFilter>();
        ArchiveSearchText.Parse("fattura 2026/118", filters);

        var answer = LocalFactAnswer.TryAnswer("qual è l'importo della fattura 2026/118" + extra, filters, new[] { invoice });

        if (local) Assert.NotNull(answer);
        else Assert.Null(answer);
    }

    private static async Task<List<string>> ToListAsync(IAsyncEnumerable<string> stream)
    {
        var list = new List<string>();
        await foreach (var chunk in stream) list.Add(chunk);
        return list;
    }

    private sealed class RecordingClient : OnlyWorkspace.Core.Interfaces.ILlmClient
    {
        private readonly string _answer;
        public RecordingClient(string answer) => _answer = answer;
        public string Sent { get; private set; } = string.Empty;
        public LlmTokenUsage? LastTokenUsage => null;
        public LlmCompletion? LastCompletion => null;
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sent = string.Join("\n", messages.Select(m => m.Content));
            await Task.Yield();
            foreach (var part in new[] { _answer.Substring(0, 15), _answer.Substring(15) }) yield return part;
        }

        public Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default)
        {
            Sent = string.Join("\n", messages.Select(m => m.Content));
            return Task.FromResult(_answer);
        }
    }
}
