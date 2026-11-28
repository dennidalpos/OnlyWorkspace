using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

/// <summary>#249: attachment text going to an Extended profile is shown masked first; declining sends nothing.</summary>
public sealed class MaskPreviewTests
{
    private const string WithAttachment = "Riassumi.\n\n[Allegato: contratto.txt]\nContatto anna.rossi@esempio.test, cliente Zeta Consulting, rif. Alfa Beta. Totale 1.250,00 euro.";
    private const string NoAttachment = "Scrivi a anna.rossi@esempio.test, grazie.";

    private static string Scratch() => Path.Combine(Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") ?? Path.GetTempPath(), "ow-preview-" + Guid.NewGuid().ToString("N"));

    private static LlmProfileConfig Profile(ExposureLevel level) => new()
    {
        Id = "preview",
        Type = "qwen",
        Model = "synthetic",
        External = true,
        Exposure = level,
        ServiceRegion = ServiceRegion.Eu
    };

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
        { Calls++; Sent = string.Join("\n", messages.Select(m => m.Content)); await Task.CompletedTask; yield return Sent; }
    }

    private static async Task<string> Run(LlmRequestDispatcher dispatcher, string text)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in dispatcher.StreamChatAsync(new[] { ChatMessage.User(text) })) sb.Append(chunk);
        return sb.ToString();
    }

    [Fact]
    public async Task Preview_ShowsMaskedAttachmentText_AndNothingIsSentBeforeConfirmation()
    {
        var client = new Recorder(); MaskPreview? shown = null; int callsAtPreview = -1;
        using var dispatcher = new LlmRequestDispatcher(client, Profile(ExposureLevel.Extended), audit: new PseudonymizationAuditStore(Scratch()),
            preview: (p, _) => { shown = p; callsAtPreview = client.Calls; return Task.FromResult(new MaskPreviewDecision { Confirmed = true }); });
        string answer = await Run(dispatcher, WithAttachment);
        Assert.NotNull(shown); Assert.Equal(0, callsAtPreview); Assert.Equal(1, client.Calls);
        Assert.Contains("[Allegato: contratto.txt]", shown!.Texts.Single());
        Assert.DoesNotContain("anna.rossi@esempio.test", shown.Texts.Single());
        Assert.Contains(shown.Masks, m => m.Category == "EMAIL" && m.Original == "anna.rossi@esempio.test");
        Assert.DoesNotContain("anna.rossi@esempio.test", client.Sent);
        Assert.Contains("anna.rossi@esempio.test", answer); // restored on the PC only
    }

    [Fact]
    public async Task Cancel_SendsNothing_AndIsAStopNotAFailure()
    {
        var client = new Recorder(); var store = new PseudonymizationAuditStore(Scratch());
        var status = new ModelServiceStatus();
        using var dispatcher = new LlmRequestDispatcher(client, Profile(ExposureLevel.Extended), status: status, audit: store,
            preview: (_, _) => Task.FromResult(MaskPreviewDecision.Cancel()));
        await Assert.ThrowsAsync<MaskPreviewCancelledException>(() => Run(dispatcher, WithAttachment));
        Assert.Equal(0, client.Calls);
        Assert.Equal("cancelled", Assert.Single(store.Read()).Outcome);
    }

    [Fact]
    public async Task ManualEdits_AreRespectedInTheSentBody_AndAuditCountsOnlyTheNumber()
    {
        var client = new Recorder(); var store = new PseudonymizationAuditStore(Scratch());
        using var dispatcher = new LlmRequestDispatcher(client, Profile(ExposureLevel.Extended), audit: store,
            preview: (p, _) => Task.FromResult(new MaskPreviewDecision
            {
                Confirmed = true,
                Unmask = new[] { p.Masks.Single(m => m.Category == "IMPORTO").Placeholder },
                AddValues = new[] { "Zeta Consulting", "Alfa Beta", "assente" }
            }));
        string answer = await Run(dispatcher, WithAttachment);
        Assert.Contains("1.250,00 euro", client.Sent);          // unmasked by the user
        Assert.DoesNotContain("Zeta Consulting", client.Sent);  // added by the user
        Assert.DoesNotContain("Alfa Beta", client.Sent);
        Assert.DoesNotContain("anna.rossi@esempio.test", client.Sent);
        Assert.Contains("[MANUALE_1]", client.Sent); Assert.Contains("[MANUALE_2]", client.Sent);
        Assert.Contains("Zeta Consulting", answer); Assert.Contains("Alfa Beta", answer);
        var record = Assert.Single(store.Read());
        Assert.Equal(3, record.Counts[Pseudonymizer.ManualEditsKey]); // 1 unmask + 2 added values found in the text
        string json = System.Text.Json.JsonSerializer.Serialize(record);
        Assert.DoesNotContain("Zeta", json); Assert.DoesNotContain("1.250", json);
    }

    [Theory]
    [InlineData(ExposureLevel.Standard, WithAttachment)]
    [InlineData(ExposureLevel.Extended, NoAttachment)]
    public async Task NoPreview_AtStandard_OrWithoutAttachments(ExposureLevel level, string text)
    {
        var client = new Recorder(); int asked = 0;
        using var dispatcher = new LlmRequestDispatcher(client, Profile(level), audit: new PseudonymizationAuditStore(Scratch()),
            preview: (_, _) => { asked++; return Task.FromResult(MaskPreviewDecision.Cancel()); });
        await Run(dispatcher, text);
        Assert.Equal(0, asked); Assert.Equal(1, client.Calls);
    }

    [Fact]
    public void ManualEdits_NeverCutExistingPlaceholders_AndRestoreRoundTrips()
    {
        var mask = new Pseudonymizer();
        var masked = mask.Mask(new[] { ChatMessage.User("[Allegato: a.txt]\nMail x.y@esempio.test e PERSONA nel testo.") });
        var edited = mask.ApplyManualEdits(masked, null, new[] { "EMAIL", "PERSONA" });
        Assert.Contains("[EMAIL_1]", edited[0].Content);
        Assert.Contains("[MANUALE_1]", edited[0].Content);
        Assert.Equal("[Allegato: a.txt]\nMail x.y@esempio.test e PERSONA nel testo.", mask.Restore(edited[0].Content));
        Assert.True(mask.Produced(edited[0]));
    }
}
