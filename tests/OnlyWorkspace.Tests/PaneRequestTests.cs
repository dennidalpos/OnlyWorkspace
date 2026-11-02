using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Llm;
using Xunit;

namespace OnlyWorkspace.Tests;

/// <summary>What the pane sends is untrusted input: parsed, clamped and described without touching Outlook.</summary>
public class PaneRequestTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ReconstructionRequiresAnExplicitSeedAndRetainsItsLocalPreset()
    {
        var request = AnalysisRequest.Parse(Json("""
            {"requestId":7,"scope":{"kind":"flow","entryId":"E","storeId":"S"},"goal":{"preset":"reconstruct"}}
            """), out _)!;
        Assert.True(request.Goal.IsReconstruction);
        Assert.Equal("Ricostruisci la pratica", request.Goal.Label);
        Assert.Equal(7, request.RequestId);
        Assert.False(request.Attachments);
        Assert.Null(AnalysisRequest.Parse(Json("""{"scope":{"kind":"archive"},"goal":{"preset":"reconstruct"}}"""), out _));
        Assert.Null(AnalysisRequest.Parse(Json("""{"scope":{"kind":"flow","entryId":"E"},"goal":{"preset":"reconstruct"}}"""), out _));
    }

    [Fact]
    public void AnalysisRequest_IsParsedFromTheForm_AndFromItsOwnTextOnRetry()
    {
        var message = Json("""
            {"type":"analysis","scope":{"kind":"folders","subfolders":true,"person":"  mario@acme.it ","role":"any","subject":"offerta","days":30,"attachments":true,
              "folders":[{"entryId":"F1","storeId":"S1","path":"Posta\\Clienti\\Acme"},{"storeId":"S1"},"x"]},
             "goal":{"preset":"table","instruction":" importi "}}
            """);
        var request = AnalysisRequest.Parse(message, out var raw)!;

        Assert.Equal("folders", request.Kind);
        Assert.True(request.Subfolders);
        Assert.True(request.Attachments);
        Assert.Equal("mario@acme.it", request.Person);
        Assert.Equal(30, request.Days);
        Assert.Equal("F1", Assert.Single(request.Folders).EntryId);
        Assert.True(request.Goal.IsTable);
        Assert.Equal("importi", request.Goal.Instruction);
        Assert.Equal("cartella «Acme» e sottocartelle, da o a «mario@acme.it», oggetto «offerta», ultimi 30 giorni", request.ScopeLabel());

        // Rigenera/Riprova: the same request comes back as text.
        var again = AnalysisRequest.Parse(Json(JsonSerializer.Serialize(new { type = "analysis", request = raw })), out var rawAgain)!;
        Assert.Equal(request.ScopeLabel(), again.ScopeLabel());
        Assert.Equal(raw, rawAgain);
    }

    [Fact]
    public void AnalysisRequest_ClampsWhatThePaneSends_AndRefusesWhatIsNotARequest()
    {
        var request = AnalysisRequest.Parse(Json("""
            {"scope":{"kind":"accounts","role":"owner","days":99999,"person":"PERSON","stores":[{"id":"S1","name":"Lavoro"},{"name":"senza id"}]},"goal":{"preset":"delete-all"}}
            """.Replace("PERSON", new string('a', 500))), out _)!;

        Assert.Equal("sender", request.PersonRole);
        Assert.Equal(3650, request.Days);
        Assert.Equal(200, request.Person.Length);
        Assert.Equal("Lavoro", Assert.Single(request.Stores).Value);
        Assert.Equal(AnalysisGoal.Summary, request.Goal.Preset);
        Assert.True(request.IsAccounts);

        Assert.Null(AnalysisRequest.Parse(Json("""{"type":"analysis"}"""), out _));
        Assert.Null(AnalysisRequest.Parse(Json("""{"scope":"all"}"""), out _));
        Assert.Null(AnalysisRequest.Parse(Json("""{"request":"{not json"}"""), out _));
        Assert.Equal(0, AnalysisRequest.Parse(Json("""{"scope":{"days":-5}}"""), out _)!.Days);
    }

    [Fact]
    public void AnalysisRequest_TextFilterAndCustomGoal_SurviveRetry()
    {
        var request = AnalysisRequest.Parse(Json("""
            {"scope":{"kind":"folders","text":" rinnovo ","days":30,"folders":[{"entryId":"F1","path":"Posta\\Acme"}]},
             "goal":{"preset":"custom","instruction":"contratti in scadenza"}}
            """), out var raw)!;

        Assert.Equal("rinnovo", request.ToQuery().Keywords);
        Assert.Equal("cartella «Acme», testo «rinnovo», ultimi 30 giorni", request.ScopeLabel());
        Assert.Equal("Personalizzata", request.Goal.Label);
        Assert.Equal("contratti in scadenza", request.Goal.Describe());

        // The packed request is re-posted whole on retry; the analysis limit is the administrator's, never the page's (A124).
        var retried = AnalysisRequest.Parse(Json(JsonSerializer.Serialize(new { type = "mail_analysis", request = raw })), out var rawRetried)!;
        Assert.Equal(request.ScopeLabel(), retried.ScopeLabel());
        Assert.Equal(raw, rawRetried);
        Assert.DoesNotContain("\"max\"", raw);

        // A custom goal without text is a summary.
        Assert.Equal(AnalysisGoal.Summary, AnalysisRequest.Parse(Json("""{"scope":{},"goal":{"preset":"custom","instruction":" "}}"""), out _)!.Goal.Preset);
    }

    [Fact]
    public void AnalysisRequest_ArchiveScopeKeepsItsPersonFilterOnRetry()
    {
        var request = AnalysisRequest.Parse(Json("""
            {"scope":{"kind":"archive","person":"Acme","role":"any","days":0},"goal":{"preset":"summary"}}
            """), out var raw)!;

        Assert.Equal("Acme", request.Person);
        Assert.Equal("tutto l'archivio, da o a «Acme», senza limite di data", request.ScopeLabel());
        Assert.Equal(request.Person, AnalysisRequest.Parse(Json(JsonSerializer.Serialize(new { request = raw })), out _)!.Person);
    }

    [Fact]
    public void FlowAnalysisKeepsItsArchiveSeedAndAttachmentChoiceOnRetry()
    {
        var request = AnalysisRequest.Parse(Json("""
            {"scope":{"kind":"flow","id":42,"entryId":"E","storeId":"S","attachments":true},"goal":{"preset":"custom","instruction":"Ricostruisci le decisioni"}}
            """), out var raw)!;
        Assert.Equal("flow", request.Kind);
        Assert.Equal(42, request.ArchiveId);
        Assert.True(request.Attachments);
        Assert.Equal("pratica dell'email scelta", request.ScopeLabel());
        var retried = AnalysisRequest.Parse(Json(JsonSerializer.Serialize(new { request = raw })), out _)!;
        Assert.Equal(request.ArchiveId, retried.ArchiveId);
        Assert.Equal(request.EntryId, retried.EntryId);
    }

    [Fact]
    public void AnalysisRequest_ScopeLabel_SaysWhereAndWithWhichFilters()
    {
        Assert.Equal("conversazione selezionata", new AnalysisRequest { Kind = "thread", Person = "x", Days = 7 }.ScopeLabel());
        Assert.Equal("tutto l'archivio, senza limite di data", new AnalysisRequest { Kind = "archive", Subfolders = true }.ScopeLabel());
        Assert.Equal("2 cartelle, mittente «acme», ultimi 7 giorni",
            new AnalysisRequest { Folders = { new MailFolderRef(), new MailFolderRef() }, Person = "acme", Days = 7 }.ScopeLabel());

        var accounts = new AnalysisRequest { Kind = "accounts", Subfolders = true };
        Assert.Equal("Posta in arrivo di tutti gli account e sottocartelle, senza limite di data", accounts.ScopeLabel());
        accounts.Stores.Add(new("S1", "Lavoro"));
        accounts.WholeMailbox = true;
        Assert.Equal("intera cassetta di «Lavoro», senza limite di data", accounts.ScopeLabel());
        accounts.Stores.Add(new("S2", "Casa"));
        Assert.Equal("Posta inviata di 2 account", new AnalysisRequest { Stores = accounts.Stores }.AccountsLabel("Posta inviata"));
        Assert.Equal("tutte le cassette", new AnalysisRequest { WholeMailbox = true }.AccountsLabel("Posta in arrivo"));
        Assert.Equal(", destinatario «anna», oggetto «ordine»", new AnalysisRequest { Person = "anna", PersonRole = "recipient", Subject = "ordine" }.FiltersLabel());
    }

    [Theory]
    [InlineData("sender", "acme", null, false)]
    [InlineData("recipient", null, "acme", false)]
    [InlineData("any", "acme", "acme", true)]
    public void AnalysisRequest_Query_PutsThePersonWhereTheRoleSays(string role, string? sender, string? recipient, bool either)
    {
        var query = new AnalysisRequest { Person = "acme", PersonRole = role, Days = 10 }.ToQuery();
        Assert.Equal(sender, query.Sender);
        Assert.Equal(recipient, query.Recipient);
        Assert.Equal(either, query.EitherPerson);
        Assert.Equal(DateTime.Today.AddDays(-10), query.StartDate);
        Assert.Null(query.Subject);
        Assert.Null(new AnalysisRequest().ToQuery().StartDate);
    }

    [Fact]
    public void ActionItem_FromThePane_IsValidatedAgain()
    {
        var item = ActionItemParser.FromPane(Json("""
            {"kind":"appointment","title":"Sopralluogo","start":"2026-11-03T09:30:00Z","end":"non so","allDay":true,"location":"Cantiere","notes":7}
            """))!;
        Assert.True(item.IsAppointment);
        Assert.Equal(new DateTime(2026, 11, 3, 9, 30, 0), item.Start);
        Assert.Null(item.End);
        Assert.True(item.AllDay);
        Assert.Equal("Cantiere", item.Location);
        Assert.Equal(string.Empty, item.Notes);

        Assert.Equal("task", ActionItemParser.FromPane(Json("""{"kind":"script","title":"Inviare offerta","due":"2026-11-05"}"""))!.Kind);
        Assert.Null(ActionItemParser.FromPane(Json("""{"kind":"task","due":"2026-11-05"}""")));
        Assert.Null(ActionItemParser.FromPane(Json("""["task"]""")));
    }

    [Fact]
    public void Pec_EnvelopeIsRecognised_AndTheHeaderLinesOfTheMessageAreDropped()
    {
        Assert.True(PecMessage.IsEnvelope("POSTA CERTIFICATA: Invio fattura"));
        Assert.True(PecMessage.IsEnvelope("anomalia messaggio: prova"));
        Assert.False(PecMessage.IsEnvelope("Re: POSTA CERTIFICATA: Invio fattura"));

        Assert.Equal("Buongiorno,\nData: da definire", PecMessage.WithoutHeader("Da: a@pec.it\nA: b@pec.it\nData: 03/11/2026\nOggetto: Invio\nBuongiorno,\nData: da definire"));
        Assert.Equal(string.Empty, PecMessage.WithoutHeader("Da: a@pec.it"));
        Assert.Equal("Testo", PecMessage.WithoutHeader("Testo"));
    }

    [Fact]
    public void AttachmentCopyName_HasNoPathNorDirectionTricks_AndKeepsTheExtension()
    {
        Assert.Equal("..__.._Windows_evil.exe", AttachmentFacts.SafeFileName(@"..\/..\Windows\evil.exe"));
        Assert.Equal("fattura_fdp.exe", AttachmentFacts.SafeFileName("fattura‮fdp.exe"));
        var longName = AttachmentFacts.SafeFileName(new string('a', 300) + ".pdf");
        Assert.Equal(100, longName.Length);
        Assert.EndsWith(".pdf", longName);
    }

    [Theory]
    [InlineData("HTTP 401: unauthorized", "ha rifiutato le credenziali")]
    [InlineData("Response status code does not indicate success: 404 (Not Found).", "endpoint o modello non trovato")]
    [InlineData("HTTP 429", "sta limitando le richieste")]
    [InlineData("HTTP 503 Service Unavailable", "problema temporaneo (errore 503)")]
    [InlineData("HTTP 422: bad field", "ha rifiutato la richiesta (errore 422)")]
    public void Error_WithAnHttpStatus_NamesTheProfileAndTheLikelyCause(string message, string expected)
    {
        var text = LlmErrorText.Describe(new HttpRequestException(message), new LlmProfileConfig { Id = "ollama-lan", DisplayName = "Server locale" });
        Assert.StartsWith("Server locale", text);
        Assert.Contains(expected, text);
    }

    [Theory]
    [InlineData("OpenAI API returned HTTP 400: This model's maximum context length is 128000 tokens. However, your messages resulted in 190000 tokens.")]
    [InlineData("Anthropic API returned HTTP 400: prompt is too long: 250000 tokens > 200000 maximum")]
    [InlineData("Gemini API returned HTTP 400: The input token count (1500000) exceeds the maximum number of tokens allowed (1048576).")]
    [InlineData("Anthropic API returned HTTP 413 (Request Entity Too Large).")]
    public void Error_WhenTheProviderRefusesForContext_SaysTheTextIsTooLong(string message)
    {
        var text = LlmErrorText.Describe(new HttpRequestException(message), new LlmProfileConfig { Id = "cloud" });
        Assert.StartsWith("cloud: il testo è troppo lungo per il contesto del modello", text);
    }

    [Fact]
    public void Error_WhenTheTextDoesNotFitALocalProfile_IsTheExceptionsOwnMessage()
    {
        var text = LlmErrorText.Describe(new OnlyWorkspace.Core.Context.ContextTooLongException(40000, 12698), new LlmProfileConfig { Id = "ollama-lan" });
        Assert.Contains("troppo lungo per il profilo in uso", text);
        Assert.Contains("Non è stato inviato nulla", text);
    }

    [Fact]
    public void Error_WithoutStatus_TellsNetworkFromTlsFromTheClientsOwnRefusal()
    {
        var profile = new LlmProfileConfig { Id = "gemini" };
        Assert.StartsWith("Impossibile contattare gemini", LlmErrorText.Describe(new HttpRequestException("send failed", new WebException("refused", WebExceptionStatus.ConnectFailure)), profile));
        Assert.StartsWith("Connessione sicura (TLS) con gemini", LlmErrorText.Describe(new HttpRequestException("send failed", new WebException("tls", WebExceptionStatus.TrustFailure)), profile));
        Assert.Contains("ha interrotto o rifiutato la risposta", LlmErrorText.Describe(new HttpRequestException("Il modello ha rifiutato la richiesta."), profile));

        Assert.Contains("non ha risposto in tempo", LlmErrorText.Describe(new AggregateException(new TaskCanceledException()), profile));
        Assert.Contains("aumenta i timeout", LlmErrorText.Describe(new TimeoutException("Nessun testo entro 60 s."), profile));
        Assert.StartsWith("il modello ha restituito una risposta non valida", LlmErrorText.Describe(new JsonException("bad"), null));
        Assert.Equal("Operazione non riuscita. Riprova; se il problema continua, consulta il log.", LlmErrorText.Describe(new InvalidOperationException("altro"), profile));
    }

    [Theory]
    [InlineData("http", "Anthropic stream error: synthetic English detail")]
    [InlineData("http", "HTTP 400: context length exceeded; synthetic English detail")]
    [InlineData("http", "HTTP 422: synthetic English detail")]
    [InlineData("timeout", "synthetic English detail")]
    [InlineData("other", "synthetic English detail")]
    public void Error_NeverDisplaysRawSystemOrProviderDetails(string kind, string detail)
    {
        Exception error = kind == "http" ? new HttpRequestException(detail)
            : kind == "timeout" ? new TimeoutException(detail) : new InvalidOperationException(detail);
        var text = LlmErrorText.Describe(error, new LlmProfileConfig { Id = "fixture" });
        Assert.DoesNotContain("synthetic English detail", text);
        Assert.DoesNotContain("Dettaglio:", text);
        Assert.Contains(".", text);
    }

    [Fact]
    public void Error_PreservesApplicationPolicyAndStructuredOutputInstructions()
    {
        const string policy = "Invio bloccato: rinnova l'informativa nel ConfigTool.";
        Assert.Equal(policy, LlmErrorText.Describe(new OnlyWorkspace.Core.Privacy.DataEgressBlockedException(policy), null));
        Assert.Equal(ModelOutput.TruncatedMessage, LlmErrorText.Describe(new InvalidModelOutputException(ModelOutput.TruncatedMessage), null));
        var redirect = LlmErrorText.Describe(new LlmRequestException("fixture internal detail", 307), null);
        Assert.Contains("reindirizzamento HTTP, bloccato", redirect);
        Assert.DoesNotContain("fixture internal detail", redirect);
    }

    [Theory]
    [InlineData("Ollama", false)]
    [InlineData("OpenAI", false)]
    [InlineData("Anthropic", false)]
    [InlineData("Gemini", false)]
    [InlineData("Ollama", true)]
    [InlineData("OpenAI", true)]
    [InlineData("Anthropic", true)]
    [InlineData("Gemini", true)]
    public async Task ConnectionCheck_TranslatesHttpAndMalformedResponses(string provider, bool malformed)
    {
        using var http = new HttpClient(new ErrorResponseHandler(() => new HttpResponseMessage(malformed ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(malformed ? "synthetic English detail" : "{\"error\":{\"message\":\"synthetic English detail\"}}")
        })) { BaseAddress = new Uri("http://127.0.0.1/") };
        var profile = new LlmProfileConfig { Id = "fixture", Model = "fixture" };
        OnlyWorkspace.Core.Interfaces.ILlmClient client = provider switch
        {
            "Ollama" => new OllamaClient(profile, httpClient: http),
            "OpenAI" => new OpenAIClient(profile, httpClient: http),
            "Anthropic" => new AnthropicClient(profile, httpClient: http),
            _ => new GeminiClient(profile, httpClient: http)
        };
        try
        {
            var result = await client.CheckHealthAsync();
            Assert.False(result.IsHealthy);
            Assert.Contains(malformed ? "risposta non valida" : "ha rifiutato le credenziali", result.ErrorMessage);
            Assert.DoesNotContain("synthetic English detail", result.ErrorMessage);
        }
        finally { (client as IDisposable)?.Dispose(); }
    }

    [Theory]
    [InlineData("Ollama")]
    [InlineData("OpenAI")]
    [InlineData("Anthropic")]
    [InlineData("Gemini")]
    public async Task ConnectionCheck_PreservesReachabilityAndModelsWhenTheConfiguredModelIsMissing(string provider)
    {
        string json = provider == "Ollama" ? "{\"models\":[{\"name\":\"another-model\"}]}"
            : provider == "Gemini" ? "{\"models\":[{\"name\":\"models/another-model\"}]}"
            : "{\"data\":[{\"id\":\"another-model\"}]}";
        using var http = new HttpClient(new ErrorResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }))
            { BaseAddress = new Uri("http://127.0.0.1/") };
        var profile = new LlmProfileConfig { Id = "fixture", Model = "missing" };
        OnlyWorkspace.Core.Interfaces.ILlmClient client = provider switch
        {
            "Ollama" => new OllamaClient(profile, httpClient: http),
            "OpenAI" => new OpenAIClient(profile, httpClient: http),
            "Anthropic" => new AnthropicClient(profile, httpClient: http),
            _ => new GeminiClient(profile, httpClient: http)
        };
        try
        {
            var result = await client.CheckHealthAsync();
            Assert.False(result.IsHealthy);
            Assert.True(result.IsModelMissing);
            Assert.Equal(new[] { "another-model" }, result.AvailableModels);
            Assert.NotEmpty(result.Version);
            Assert.Contains("raggiungibile", result.ErrorMessage);
        }
        finally { (client as IDisposable)?.Dispose(); }
    }

    private sealed class ErrorResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _response;
        public ErrorResponseHandler(Func<HttpResponseMessage> response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) => Task.FromResult(_response());
    }
}
