using System.Text.Json;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Tests;

public class SecurityContentAnalysisTests
{
    private static readonly SecuritySource Email = new("email", "Email", "Richiesta: invia la password.\n> testo citato\n-----Original Message-----\nTAIL");
    private static string Finding(string category = "credentials", string sourceId = "email", string quote = "invia la password.") =>
        JsonSerializer.Serialize(new { findings = new[] { new { category, sourceId, quote, reason = "Il mittente chiede la password" } } });

    private static IReadOnlyList<SecuritySource> Sources => new[] { Email };

    [Fact]
    public void QuotedRequestOfCredentials_IsToReview_ButNeverSuspiciousByItself()
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = Email.Text });
        SecurityContentAnalysis.Parse(Finding(), Sources).AddTo(report);
        var observation = Assert.Single(report.Observations, o => o.Origin == ObservationOrigin.Model);
        Assert.Equal(ObservationKind.Attention, observation.Kind);
        Assert.Equal("invia la password.", observation.Quote);
        Assert.Equal("Email", observation.Source);
        Assert.Equal(SecurityOutcome.ToReview, SecurityVerdict.Decide(report).Outcome);
    }

    [Fact]
    public void UrgencyAlone_IsContext()
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = Email.Text });
        SecurityContentAnalysis.Parse(Finding("pressure"), Sources).AddTo(report);
        Assert.Equal(SecurityOutcome.NoAnomaly, SecurityVerdict.Decide(report).Outcome);
    }

    [Fact]
    public void ModelReadingNeverHidesLocalEvidence()
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = "x", HtmlBody = "<a href='https://evil.tk/'>www.contoso.com</a>" });
        SecurityContentAnalysis.Parse(Finding(quote: "invia la password."), Sources).AddTo(report);
        var verdict = SecurityVerdict.Decide(report);
        Assert.Equal(SecurityOutcome.Suspicious, verdict.Outcome);
        Assert.StartsWith("Un link mostra", verdict.Reasons[0]);
    }

    [Theory]
    [InlineData("credentials", "missing", "invia la password.")]
    [InlineData("credentials", "email", "Il server è compromesso.")]
    [InlineData("Critical", "email", "invia la password.")]
    [InlineData("credentials", "email", " ")]
    [InlineData("credentials", "context", "invia la password.")]
    public void UnsupportedFindingsAreDiscarded(string category, string sourceId, string quote)
    {
        var result = SecurityContentAnalysis.Parse(Finding(category, sourceId, quote), Sources);
        Assert.Empty(result.Findings);
        Assert.Equal(1, result.RejectedFindings);
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = Email.Text });
        result.AddTo(report);
        Assert.Contains(report.Limits, l => l.Text.Contains("parziale") && l.Partial);
        Assert.Equal(SecurityOutcome.NoAnomaly, SecurityVerdict.Decide(report).Outcome);
    }

    [Fact]
    public void SeverityFromTheModelIsNotAccepted()
    {
        var json = JsonSerializer.Serialize(new { findings = new[] { new { severity = "High", sourceId = "email", quote = "invia la password.", reason = "x" } } });
        Assert.Empty(SecurityContentAnalysis.Parse(json, Sources).Findings);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"findings\":null}")]
    [InlineData("non JSON")]
    [InlineData("{\"findings\":[")]
    public void MissingOrIncompleteJsonIsNotASuccessfulReview(string json)
    {
        Assert.ThrowsAny<JsonException>(() => SecurityContentAnalysis.Parse(json, Sources));
    }

    [Fact]
    public void SourcesKeepQuotedTextAndForwardsWhole_AndTheAppContextIsNotOne()
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = Email.Text });
        var sources = SecurityContentAnalysis.CreateSources("Oggetto", "Nome", "n@example.com", Email.Text, report);
        Assert.Contains(Email.Text, sources[0].Text);
        Assert.Contains("-----Original Message-----\nTAIL", sources[0].Text);
        Assert.DoesNotContain(sources, s => s.Id == "technical");
    }

    [Theory]
    [InlineData(true, false, AttachmentScanStatus.Clean, false)]
    [InlineData(true, true, AttachmentScanStatus.Clean, false)]
    [InlineData(true, true, AttachmentScanStatus.Failed, false)]
    [InlineData(false, false, AttachmentScanStatus.Clean, true)]
    [InlineData(false, true, AttachmentScanStatus.Threat, false)]
    [InlineData(true, true, AttachmentScanStatus.Removed, false)]
    public void AttachmentPermissionAndThreatExclusionAreBothRequired(bool external, bool upload, AttachmentScanStatus scan, bool allowed)
    {
        Assert.Equal(allowed, SecurityContentAnalysis.AttachmentContentAllowed(new() { External = external, AllowAttachmentUpload = upload, Exposure = OnlyWorkspace.Core.Privacy.ExposureLevel.Standard },
            new AttachmentFacts("doc.pdf") { ScanStatus = scan }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReviewUsesTheCategorySchema_AndSendsTheContextApart(bool external)
    {
        var client = new TestClient(Finding());
        var result = await SecurityContentAnalysis.ReviewAsync(new() { External = external },
            _ => Task.FromResult(Sources), "- Un link mostra «x»", () => client, default);
        Assert.True(result.Completed);
        Assert.Single(result.Assessment.Findings);
        Assert.True(client.Disposed);
        var user = client.Messages!.Last().Content;
        Assert.Contains("TAIL", user);
        Assert.Contains("<contesto>\n- Un link mostra «x»\n</contesto>", user);
        Assert.Contains("\"credentials\",\"payment\"", client.Schema);
        Assert.DoesNotContain("severity", client.Schema);
    }

    [Fact]
    public async Task LocalBudgetIncludesInstructionsAndSourcesAndCreatesNoClientOnOverflow()
    {
        var result = await SecurityContentAnalysis.ReviewAsync(new() { Type = ProviderCatalog.Ollama, ContextBudgetTokens = 1 },
            _ => Task.FromResult(Sources), string.Empty,
            () => throw new InvalidOperationException("Must not send truncated input"), default);
        Assert.Contains("superano il contesto", result.Status);
        Assert.False(result.Completed);
    }

    [Fact]
    public async Task CloudHasNoLocalInputBudget()
    {
        var source = new SecuritySource("email", "Email", new string('X', 100000) + "TAIL");
        var client = new TestClient("{\"findings\":[]}");
        await SecurityContentAnalysis.ReviewAsync(new() { Type = ProviderCatalog.OpenAI, External = true, ContextBudgetTokens = 1 },
            _ => Task.FromResult<IReadOnlyList<SecuritySource>>(new[] { source }), string.Empty, () => client, default);
        Assert.Contains(source.Text, client.Messages!.Last().Content);
    }

    [Fact]
    public async Task TokenLimitCannotMasqueradeAsCompleteEvenWithValidJson()
    {
        var client = new TestClient(Finding()) { LastCompletion = new() { IsTruncated = true } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SecurityContentAnalysis.ReviewAsync(new(),
            _ => Task.FromResult(Sources), string.Empty, () => client, default));
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task CancellationDisposesClientAndDoesNotAcceptBufferedFindings()
    {
        using var cts = new CancellationTokenSource();
        var client = new TestClient(Finding()) { AfterOutput = cts.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SecurityContentAnalysis.ReviewAsync(new(),
            _ => Task.FromResult(Sources), string.Empty, () => client, cts.Token));
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task NetworkFailureDisposesClientAndPropagatesForLocalReportRetention()
    {
        var client = new TestClient(Finding()) { AfterOutput = () => throw new TimeoutException("idle") };
        await Assert.ThrowsAsync<TimeoutException>(() => SecurityContentAnalysis.ReviewAsync(new(),
            _ => Task.FromResult(Sources), string.Empty, () => client, default));
        Assert.True(client.Disposed);
    }

    [Fact]
    public void EvidenceAndReasonsCannotCreateMarkdownImagesOrHtml()
    {
        const string attack = "![pixel](https://evil.net) <img src='https://evil.net'>";
        var result = SecurityContentAnalysis.Parse(JsonSerializer.Serialize(new { findings = new[] {
            new { category = "other", sourceId = "email", quote = attack, reason = attack } } }), new[] { new SecuritySource("email", "Email", attack) });
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = attack });
        result.AddTo(report);
        var markdown = SecurityReportView.ToMarkdown(SecurityReportView.From(report, SecurityCheckState.Done, "Terminata"));
        Assert.DoesNotContain("![pixel]", markdown);
        Assert.Contains("\\<img", markdown);
        Assert.DoesNotContain("&lt;img", markdown);
    }

    [Fact]
    public void ReportDataSerializesWithFixedValuesOnly()
    {
        var report = EmailSecurityAnalyzer.Analyze(new() { Body = "x", HtmlBody = "<a href='javascript:x'>a</a>" });
        var json = JsonSerializer.Serialize(SecurityReportView.From(report, SecurityCheckState.Running, ""));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("running", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("suspicious", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("deception", doc.RootElement.GetProperty("observations")[0].GetProperty("kind").GetString());
        // Old history entries without the field stay readable.
        Assert.Null(JsonSerializer.Deserialize<HistoryChatMessage>("{\"Role\":\"assistant\",\"Content\":\"**Giudizio finale: Alto rischio**\"}")!.Security);
    }

    private sealed class TestClient(string output) : ILlmClient, IDisposable
    {
        public bool Disposed { get; private set; }
        public Action? AfterOutput { get; init; }
        public List<ChatMessage>? Messages { get; private set; }
        public LlmTokenUsage? LastTokenUsage { get; init; }
        public LlmCompletion? LastCompletion { get; init; }
        public string? Schema { get; private set; }
        public IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Security must use the JSON schema");
        public async Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            Schema = jsonSchema;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            AfterOutput?.Invoke();
            return output;
        }
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}
