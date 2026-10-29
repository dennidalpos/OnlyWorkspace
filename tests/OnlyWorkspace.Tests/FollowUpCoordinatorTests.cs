using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;
using Xunit;

namespace OnlyWorkspace.Tests;

public sealed class FollowUpCoordinatorTests
{
    private static readonly DateTime Today = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Local);
    private static LlmProfileConfig Profile(bool web = false) => new() { Id = "local", WebSearchEnabled = web };
    private static FollowUpMail Mail(string id, DateTime? date = null) => new()
    {
        EntryId = id, StoreId = "store", Subject = "Re: Ordine ABC-1234", Body = "Attendo conferma dell'ordine ABC-1234.",
        Sender = "Cliente <cliente@example.test>", SenderEmail = "cliente@example.test", ReceivedTime = date ?? Today
    };

    [Fact]
    public async Task ArchiveOff_UsesConversationAndRecordsItsLimit()
    {
        var selected = Mail("current");
        selected.ThreadMessages = new[] { Mail("prior", Today.AddDays(-1)), Mail("later", Today.AddDays(1)), selected };
        var model = new Model();
        FollowUpActionResult? saved = null;
        var chunks = new List<string>();
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = _ => selected,
            CreateClient = () => model,
            OnChunk = chunks.Add,
            SaveHistoryAsync = result => { saved = result; return Task.CompletedTask; }
        };

        var result = await workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None);

        Assert.Equal(FollowUpOutcome.Completed, result.Outcome);
        Assert.Contains("archivio email non attivo", result.Answer);
        // Precedents oldest first, the email itself last: [M1] prior, [M2] current.
        Assert.Equal(new[] { "prior", "current" }, result.MailSources.Select(s => s.EntryId));
        Assert.Contains("[M1] [Email del", model.PromptText);
        Assert.Contains("Collegamento: Stessa conversazione Outlook", model.PromptText);
        Assert.Contains("L'email da seguire è [M2].", model.PromptText);
        Assert.DoesNotContain("later", model.PromptText);
        Assert.Same(result, saved);
        Assert.Equal(result.Answer, string.Concat(chunks));
    }

    [Fact]
    public async Task ArchiveOn_DeduplicatesAndSkipsUnrelatedOrLaterMessages()
    {
        var selected = Mail("current");
        selected.ThreadMessages = new[] { Mail("thread", Today.AddDays(-2)) };
        var model = new Model();
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = reference => reference == null ? selected : Mail(reference.EntryId, Today.AddDays(-3)),
            SearchArchiveAsync = (_, _) => Task.FromResult<FollowUpArchiveResult?>(new FollowUpArchiveResult
            {
                SameParty = true,
                Candidates = new[]
                {
                    Hit("thread", "Re: Ordine ABC-1234", Today.AddDays(-2)),
                    Hit("archive", "Ordine ABC-1234", Today.AddDays(-3)),
                    Hit("archive", "Ordine ABC-1234", Today.AddDays(-3)),
                    Hit("unrelated", "Fattura diversa", Today.AddDays(-4), "Nessun riferimento condiviso")
                }
            }),
            CreateClient = () => model
        };

        var result = await workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None);

        Assert.Equal(new[] { "archive", "thread", "current" }, result.MailSources.Select(s => s.EntryId));
        Assert.DoesNotContain("archivio email non attivo", result.Answer);
        Assert.Contains("Collegamento: Stessa controparte e oggetto", model.PromptText);
        Assert.True(model.PromptText.IndexOf("Stessa controparte", StringComparison.Ordinal) < model.PromptText.IndexOf("Stessa conversazione", StringComparison.Ordinal));
        Assert.DoesNotContain("Fattura diversa", model.PromptText);
    }

    [Fact]
    public async Task ArchivedPrecedentUsesPersistedTextWithoutReopeningOutlook()
    {
        var selected = Mail("current");
        var model = new Model();
        string full = new string('x', 900) + " persistedprecedenttail";
        int archivedReads = 0;
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = reference => reference == null ? selected : throw new InvalidOperationException("An acquired precedent must not be reopened."),
            GetArchivedBodyAsync = (reference, token) =>
            {
                Assert.Equal("prior", reference.EntryId); Assert.Equal("store", reference.StoreId);
                archivedReads++; return Task.FromResult<string?>(full);
            },
            SearchArchiveAsync = (_, _) => Task.FromResult<FollowUpArchiveResult?>(new FollowUpArchiveResult
            { SameParty = true, Candidates = new[] { Hit("prior", "Ordine ABC-1234", Today.AddDays(-1), "Preview must stay out") } }),
            CreateClient = () => model
        };
        var result = await workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None);
        Assert.Equal(1, archivedReads);
        Assert.Contains("persistedprecedenttail", model.PromptText);
        Assert.DoesNotContain("Preview must stay out", model.PromptText);
        Assert.Equal(new[] { "prior", "current" }, result.MailSources.Select(s => s.EntryId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Precedents_KeepTheWholeOriginal_FromThreadOrArchive(bool archived)
    {
        var selected = Mail("current");
        var prior = Mail("prior", Today.AddDays(-1));
        prior.Body = new string('x', 900) + "\nImporto finale: 1234 euro, scadenza 22 ottobre.";
        selected.ThreadMessages = archived ? Array.Empty<FollowUpMail>() : new[] { prior, prior };
        var model = new Model();
        var reads = new List<string>();
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = reference =>
            {
                if (reference == null) return selected;
                reads.Add(reference.EntryId);
                return prior;
            },
            SearchArchiveAsync = (_, _) => Task.FromResult<FollowUpArchiveResult?>(archived
                ? new FollowUpArchiveResult { SameParty = true, Candidates = new[] { Hit("prior", prior.Subject, prior.ReceivedTime, "ANTEPRIMA NON DA INVIARE") } }
                : null),
            CreateClient = () => model
        };

        var result = await workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None);

        Assert.Contains(new ContextBuilder().CleanEmailBody(prior.Body, prior.Subject), model.PromptText);
        Assert.DoesNotContain("ANTEPRIMA NON DA INVIARE", model.PromptText);
        Assert.Equal(2, result.MailSources.Count);
        Assert.Equal(archived ? new[] { "prior" } : Array.Empty<string>(), reads);
    }

    [Fact]
    public async Task PrecedentOverTheReadingLimit_IsDeclaredMissing_AndTheActionGoesOn()
    {
        var model = new Model();
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = reference => reference == null ? Mail("current") : throw new InvalidOperationException("An unreadable precedent must not be reopened."),
            GetArchivedBodyAsync = (reference, token) => reference.EntryId == "huge"
                ? throw new InvalidOperationException("Il corpo supera il limite di lettura integrale; nessun testo è stato tagliato.")
                : Task.FromResult<string?>("Testo integrale del precedente leggibile."),
            SearchArchiveAsync = (_, _) => Task.FromResult<FollowUpArchiveResult?>(new FollowUpArchiveResult
            {
                SameParty = true,
                Candidates = new[] { Hit("huge", "Ordine ABC-1234", Today.AddDays(-2)), Hit("readable", "Ordine ABC-1234", Today.AddDays(-1)) }
            }),
            CreateClient = () => model
        };

        var result = await workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None);

        Assert.Equal(FollowUpOutcome.Completed, result.Outcome);
        Assert.Contains("Testo integrale del precedente leggibile.", model.PromptText);
        Assert.Contains("originali non disponibili [M1]", result.Answer);
        Assert.DoesNotContain("[M2]", result.Answer.Substring(result.Answer.IndexOf("originali non disponibili", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MissingPrecedent_ReportsCoverageWithoutSendingItsPreview()
    {
        var model = new Model();
        var chunks = new List<string>();
        FollowUpActionResult? saved = null;
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = reference => reference == null ? Mail("current") : null,
            SearchArchiveAsync = (_, _) => Task.FromResult<FollowUpArchiveResult?>(new FollowUpArchiveResult
            {
                SameParty = true,
                Candidates = new[] { Hit("missing", "Ordine ABC-1234", Today.AddDays(-1), "SEGRETO SOLO NELL'ANTEPRIMA") }
            }),
            CreateClient = () => model,
            OnChunk = chunks.Add,
            SaveHistoryAsync = result => { saved = result; return Task.CompletedTask; }
        };

        var result = await workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None);

        Assert.DoesNotContain("SEGRETO SOLO NELL'ANTEPRIMA", model.PromptText);
        Assert.Contains("Originale non disponibile", model.PromptText);
        Assert.Contains("originali non disponibili [M1]", result.Answer);
        // A missing precedent is not counted as a whole original or as local facts.
        Assert.Contains("Copertura: 1 email lette in ordine cronologico (1 per intero, 0 dai fatti locali), 1 originale non disponibile,", result.Answer);
        Assert.Equal(result.Answer, string.Concat(chunks));
        Assert.Same(result, saved);
    }

    [Fact]
    public void Coverage_CountsWholeOriginalsLocalFactsAndMissingOriginalsApart()
    {
        var pack = new OnlyWorkspace.Core.Agents.MailEvidencePack();
        pack.Items.Add(new OnlyWorkspace.Core.Agents.MailEvidence { Whole = true });
        pack.Items.Add(new OnlyWorkspace.Core.Agents.MailEvidence());
        pack.Items.Add(new OnlyWorkspace.Core.Agents.MailEvidence { Missing = true });
        pack.Items.Add(new OnlyWorkspace.Core.Agents.MailEvidence { Missing = true });
        var answer = OnlyWorkspace.Core.Agents.MailAnswer.Parse(Model.Json("Punto"));

        Assert.StartsWith("Copertura: 2 email lette in ordine cronologico (1 per intero, 1 dai fatti locali), 2 originali non disponibili, 1 citate",
            OnlyWorkspace.Core.Agents.MailAnswerRenderer.Coverage(pack, answer));
        pack.Items.RemoveAll(i => i.Missing);
        Assert.StartsWith("Copertura: 2 email lette in ordine cronologico (1 per intero, 1 dai fatti locali), 1 citate",
            OnlyWorkspace.Core.Agents.MailAnswerRenderer.Coverage(pack, answer));
    }

    [Fact]
    public async Task ArchivePrecedents_ReadOnlyTheTwelveNewestDistinctEarlierOriginals()
    {
        var model = new Model();
        var reads = new List<string>();
        var hits = Enumerable.Range(1, 14).Select(i => Hit("prior" + i, "Ordine ABC-1234", Today.AddDays(-i))).ToList();
        hits.Add(hits[0]);
        hits.Add(Hit("future", "Ordine ABC-1234", Today.AddDays(1)));
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = reference =>
            {
                if (reference == null) return Mail("current");
                reads.Add(reference.EntryId);
                return Mail(reference.EntryId, Today.AddDays(-1));
            },
            SearchArchiveAsync = (_, _) => Task.FromResult<FollowUpArchiveResult?>(new FollowUpArchiveResult { SameParty = true, Candidates = hits }),
            CreateClient = () => model
        };

        var result = await workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None);

        // The twelve newest, read oldest first.
        Assert.Equal(Enumerable.Range(1, 12).Reverse().Select(i => "prior" + i), reads);
        Assert.Equal(reads.Append("current"), result.MailSources.Select(s => s.EntryId));
    }

    [Fact]
    public async Task WholeLocalContextTooLarge_StopsBeforeWebModelAndHistory()
    {
        var selected = Mail("current");
        var prior = Mail("prior", Today.AddDays(-1));
        prior.Body = new string('x', 20_000) + " FINE DA CONSERVARE";
        selected.ThreadMessages = new[] { prior };
        var calls = 0;
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = _ => selected,
            CreateClient = () => { calls++; return new Model(); },
            SearchWebAsync = (_, _) => { calls++; throw new InvalidOperationException(); },
            SaveHistoryAsync = _ => { calls++; return Task.CompletedTask; }
        };
        var profile = Profile(true);
        profile.ContextBudgetTokens = 1000;

        await Assert.ThrowsAsync<ContextTooLongException>(() => workflow.ExecuteAsync(null, profile, new ContextBuilder(), true, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ArchiveOriginals_AreResolvedOnTheCallingUiThreadAfterAsyncSearch()
    {
        ArchiveIndexerHarness.RunOnUiThread(async () =>
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var reads = 0;
            var workflow = new FollowUpCoordinator
            {
                ResolveEmail = reference =>
                {
                    Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
                    Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                    if (reference != null) reads++;
                    return Mail(reference?.EntryId ?? "current", reference == null ? Today : Today.AddDays(-1));
                },
                SearchArchiveAsync = async (_, _) =>
                {
                    await Task.Run(() => Thread.Sleep(10));
                    return new FollowUpArchiveResult { SameParty = true, Candidates = new[] { Hit("prior", "Ordine ABC-1234", Today.AddDays(-1)) } };
                },
                CreateClient = () => new Model()
            };
            await workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None);
            Assert.Equal(1, reads);
        });
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextWithWebEvidence_IsCheckedBeforeTheFinalModel_CloudHasNoClientLimit(bool cloud)
    {
        var model = new Model();
        var profile = Profile(true);
        profile.ContextBudgetTokens = 1000;
        if (cloud) profile.Type = ProviderCatalog.OpenAI;
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = _ => Mail("current"),
            SearchWebAsync = (_, _) => Task.FromResult(new WebSearchResult { Text = new string('x', 20_000) + " FINE WEB" }),
            CreateClient = () => model
        };
        if (cloud)
        {
            await workflow.ExecuteAsync(null, profile, new ContextBuilder(), true, CancellationToken.None);
            Assert.Contains("FINE WEB", model.PromptText);
            Assert.Equal(1, model.Calls);
        }
        else
        {
            await Assert.ThrowsAsync<ContextTooLongException>(() => workflow.ExecuteAsync(null, profile, new ContextBuilder(), true, CancellationToken.None));
            Assert.Equal(0, model.Calls);
        }
    }

    [Fact]
    public async Task RetryKeepsOriginalMailWhenSelectionChanges_AndMovedMailStopsBeforeModel()
    {
        var selected = Mail("original");
        var lookup = new Dictionary<string, FollowUpMail> { [selected.EntryId] = selected };
        var model = new Model();
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = reference => reference == null ? selected : lookup.GetValueOrDefault(reference.EntryId),
            SearchArchiveAsync = (_, _) =>
            {
                selected = Mail("other");
                return Task.FromResult<FollowUpArchiveResult?>(null);
            },
            CreateClient = () => model
        };
        var first = await workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None);
        Assert.Equal("original", first.Email!.EntryId);
        var pinned = new FollowUpMailRef(first.Email!.EntryId, first.Email.StoreId);

        var retry = await workflow.ExecuteAsync(pinned, Profile(), new ContextBuilder(), false, CancellationToken.None);
        Assert.Equal("original", retry.Email!.EntryId);
        Assert.Contains("ABC-1234", model.PromptText);

        lookup.Remove("original");
        var missing = await workflow.ExecuteAsync(pinned, Profile(), new ContextBuilder(), false, CancellationToken.None);
        Assert.Equal(FollowUpOutcome.Missing, missing.Outcome);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task WebSearchIsPerActionAndSourcesReachHistory_AndAFailedSearchLeavesTheEmails()
    {
        var model = new Model { Response = Model.Json("Riscontro esterno [W1]") };
        var searches = 0;
        FollowUpActionResult? saved = null;
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = _ => Mail("current"),
            CreateClient = () => model,
            SearchWebAsync = (_, _) =>
            {
                searches++;
                var result = new WebSearchResult { Text = "Fonte sintetica" };
                result.Sources.Add(new WebSource { Title = "Documento", Url = "https://example.test/source" });
                return Task.FromResult(result);
            },
            SaveHistoryAsync = result => { saved = result; return Task.CompletedTask; }
        };

        await workflow.ExecuteAsync(null, Profile(true), new ContextBuilder(), false, CancellationToken.None);
        Assert.Equal(0, searches);
        var withWeb = await workflow.ExecuteAsync(null, Profile(true), new ContextBuilder(), true, CancellationToken.None);
        Assert.Equal(1, searches);
        Assert.Contains("[W1] Documento: https://example.test/source", model.PromptText);
        Assert.Equal("https://example.test/source", Assert.Single(saved!.WebSources).Url);
        Assert.Same(withWeb, saved);

        saved = null;
        await workflow.ExecuteAsync(null, Profile(true), new ContextBuilder(), false, CancellationToken.None);
        Assert.Equal(1, searches);
        Assert.Empty(saved!.WebSources);
        Assert.DoesNotContain("[W1]", model.PromptText);

        // D6: a failed search (QwenCloud's 180 s timeout) no longer stops the follow-up; the model is told the web is missing.
        saved = null;
        workflow.SearchWebAsync = (_, _) => throw new TimeoutException("search timed out");
        var withoutWeb = await workflow.ExecuteAsync(null, Profile(true), new ContextBuilder(), true, CancellationToken.None);
        Assert.Equal(FollowUpOutcome.Completed, withoutWeb.Outcome);
        Assert.Same(withoutWeb, saved);
        Assert.Empty(saved!.WebSources);
        Assert.Contains("mancano i riscontri web", model.PromptText);
        Assert.Equal(4, model.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), true, CancellationToken.None));
        Assert.Equal(4, model.Calls);

        // Cancelled during the search: nothing is answered or saved.
        saved = null;
        using var stop = new CancellationTokenSource();
        workflow.SearchWebAsync = (_, _) => { stop.Cancel(); throw new OperationCanceledException(stop.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow.ExecuteAsync(null, Profile(true), new ContextBuilder(), true, stop.Token));
        Assert.Null(saved);
        Assert.Equal(4, model.Calls);

        // The model answered without searching (tool not forceable): the follow-up goes on from the emails alone.
        workflow.SearchWebAsync = (_, _) => throw new WebSearchSkippedException("skipped");
        await workflow.ExecuteAsync(null, Profile(true), new ContextBuilder(), true, CancellationToken.None);
        Assert.Equal(5, model.Calls);
        Assert.DoesNotContain("mancano i riscontri web", model.PromptText);
        Assert.Empty(saved!.WebSources);
        Assert.DoesNotContain("Risultato Web Search", model.PromptText);
    }

    [Fact]
    public async Task ModelFailure_DoesNotSaveHistory()
    {
        var model = new Model { Fail = true };
        var saves = 0;
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = _ => Mail("current"),
            CreateClient = () => model,
            SaveHistoryAsync = _ => { saves++; return Task.CompletedTask; }
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None));
        Assert.Equal(1, model.Calls);
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task OutlookShutdown_DropsLatePromptReplyWithoutSavingHistory()
    {
        var model = new Model { DelayedResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Started = started;
        int saves = 0;
        var workflow = new FollowUpCoordinator
        {
            ResolveEmail = _ => Mail("current"),
            CreateClient = () => model,
            SaveHistoryAsync = _ => { saves++; return Task.CompletedTask; }
        };
        using var outlookLifetime = new CancellationTokenSource();
        var operation = workflow.ExecuteAsync(null, Profile(), new ContextBuilder(), false, outlookLifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        outlookLifetime.Cancel();
        model.DelayedResponse.SetResult("Risposta arrivata dopo la chiusura");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, saves);

        var restarted = new FollowUpCoordinator
        {
            ResolveEmail = _ => Mail("current"),
            CreateClient = () => new Model(),
            SaveHistoryAsync = _ => { saves++; return Task.CompletedTask; }
        };
        Assert.Equal(FollowUpOutcome.Completed, (await restarted.ExecuteAsync(null, Profile(), new ContextBuilder(), false, CancellationToken.None)).Outcome);
        Assert.Equal(1, saves);
    }

    private static ArchiveHit Hit(string id, string subject, DateTime date, string preview = "Conferma ordine ABC-1234") => new()
    {
        EntryId = id, StoreId = "store", Subject = subject, Preview = preview, SenderName = "Cliente", ReceivedUtc = date.ToUniversalTime()
    };

    private sealed class Model : ILlmClient
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public string Response { get; set; } = Json("Risposta sintetica");
        public static string Json(string text) => "{\"sections\":[{\"title\":\"Stato\",\"points\":[{\"text\":\"" + text + "\",\"sources\":[\"M1\"]}]}],\"gaps\":[]}";
        public TaskCompletionSource<string>? DelayedResponse { get; set; }
        public TaskCompletionSource<bool>? Started { get; set; }
        public string PromptText { get; private set; } = string.Empty;
        public LlmTokenUsage? LastTokenUsage => null;
        public LlmCompletion? LastCompletion { get; init; }
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("model failed");
            PromptText = string.Join("\n", messages.Select(m => m.Content));
            Started?.TrySetResult(true);
            return DelayedResponse == null ? Response : await DelayedResponse.Task;
        }

        public IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
