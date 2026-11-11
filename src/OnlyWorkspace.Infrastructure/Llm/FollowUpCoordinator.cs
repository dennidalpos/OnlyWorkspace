using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Prompts;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.Infrastructure.Llm;

public sealed class FollowUpMailRef
{
    public FollowUpMailRef(string entryId, string? storeId) { EntryId = entryId; StoreId = storeId; }
    public string EntryId { get; }
    public string? StoreId { get; }
}

public sealed class FollowUpMail
{
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public DateTime ReceivedTime { get; set; }
    public IReadOnlyList<FollowUpMail> ThreadMessages { get; set; } = Array.Empty<FollowUpMail>();
}

public sealed class FollowUpArchiveResult
{
    public bool SameParty { get; set; }
    public IReadOnlyList<ArchiveHit> Candidates { get; set; } = Array.Empty<ArchiveHit>();
}

public enum FollowUpOutcome { Completed, Missing }

public sealed class FollowUpActionResult
{
    public FollowUpOutcome Outcome { get; set; }
    public FollowUpMail? Email { get; set; }
    public string Answer { get; set; } = string.Empty;
    public List<WebSource> WebSources { get; set; } = new();
    /// <summary>The model answered without running the requested web search.</summary>
    public bool WebSkipped { get; set; }
    public List<HistoryContextReference> MailSources { get; set; } = new();
}

/// <summary>Outlook and archive reads are supplied by the UI-thread caller; the remaining action is testable without COM.</summary>
public sealed class FollowUpCoordinator
{
    public Func<FollowUpMailRef?, FollowUpMail?> ResolveEmail { get; set; } = _ => null;
    public Func<FollowUpMailRef, CancellationToken, Task<string?>> GetArchivedBodyAsync { get; set; } = (_, _) => Task.FromResult<string?>(null);
    public Func<FollowUpMail, CancellationToken, Task<FollowUpArchiveResult?>> SearchArchiveAsync { get; set; } = (_, _) => Task.FromResult<FollowUpArchiveResult?>(null);

    public Func<ILlmClient> CreateClient { get; set; } = () => throw new InvalidOperationException("No model client configured.");
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<WebSearchResult>> SearchWebAsync { get; set; } = (_, _) => throw new InvalidOperationException("Web Search is unavailable.");
    public Func<FollowUpActionResult, Task> SaveHistoryAsync { get; set; } = _ => Task.CompletedTask;
    public Action<string> OnChunk { get; set; } = _ => { };

    public async Task<FollowUpActionResult> ExecuteAsync(FollowUpMailRef? target, LlmProfileConfig profile, IContextBuilder contextBuilder, bool useWebSearch, CancellationToken ct)
    {
        var email = ResolveEmail(target);
        if (email == null) return new FollowUpActionResult { Outcome = FollowUpOutcome.Missing };
        var result = new FollowUpActionResult { Email = email };

        var evidence = new List<(string EntryId, string StoreId, string Subject, string Sender, DateTime Date, string? Body, string Relation)>();
        foreach (var prior in email.ThreadMessages)
        {
            if (prior.EntryId == email.EntryId && prior.StoreId == email.StoreId || prior.ReceivedTime >= email.ReceivedTime) continue;
            evidence.Add((prior.EntryId, prior.StoreId, prior.Subject, prior.Sender, prior.ReceivedTime,
                contextBuilder.CleanEmailBody(prior.Body, prior.Subject), "Stessa conversazione Outlook"));
        }

        var archive = await SearchArchiveAsync(email, ct);
        ct.ThrowIfCancellationRequested();
        if (archive != null)
        {
            var references = FollowUpMatcher.References(email.Subject + "\n" + email.Body);
            foreach (var hit in archive.Candidates.GroupBy(h => h.StoreId + "\n" + h.EntryId).Select(g => g.First()))
            {
                if (hit.ReceivedUtc.ToLocalTime() >= email.ReceivedTime ||
                    hit.EntryId == email.EntryId && hit.StoreId == email.StoreId ||
                    evidence.Any(e => e.EntryId == hit.EntryId && e.StoreId == hit.StoreId)) continue;
                var relation = FollowUpMatcher.Relation(email.Subject, hit.Subject, hit.Preview, references, archive.SameParty);
                if (relation == null) continue;
                evidence.Add((hit.EntryId, hit.StoreId, hit.Subject, hit.SenderName, hit.ReceivedUtc.ToLocalTime(), null, relation));
            }
        }
        // The 12 nearest precedents, then read oldest first with the email itself last (E7): one cited answer.
        evidence = evidence.GroupBy(e => (e.StoreId, e.EntryId)).Select(g => g.First())
            .OrderByDescending(e => e.Date).Take(12).OrderBy(e => e.Date).ToList();

        ct.ThrowIfCancellationRequested();

        var read = new List<MailEvidence>();
        var unavailable = new List<string>();
        for (int i = 0; i < evidence.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = evidence[i];
            // Thread messages already carry their Outlook originals. Archive previews are for matching only.
            var body = item.Body;
            if (body == null)
            {
                var reference = new FollowUpMailRef(item.EntryId, item.StoreId);
                string? stored;
                bool unreadable = false;
                try { stored = await GetArchivedBodyAsync(reference, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An original the archive cannot give whole (over the reading limit) is declared, never cut.
                    OnlyWorkspaceLog.Logger.Warning("Follow-up: archived precedent not read: {Kind}", ex.GetType().Name);
                    stored = null;
                    unreadable = true;
                }
                ct.ThrowIfCancellationRequested();
                if (stored != null) body = contextBuilder.CleanEmailBody(stored, item.Subject);
                else if (!unreadable)
                {
                    var original = ResolveEmail(reference);
                    if (original != null) body = contextBuilder.CleanEmailBody(original.Body, original.Subject);
                }
            }
            if (body == null) unavailable.Add($"[M{i + 1}]");
            read.Add(new MailEvidence
            {
                Mail = new ChatMailItem { EntryId = item.EntryId, StoreId = item.StoreId, Subject = item.Subject, SenderName = item.Sender, Received = item.Date },
                Text = "Collegamento: " + item.Relation + "\n" + (body ?? "Originale non disponibile: contenuto non analizzato."),
                Whole = body != null,
                Missing = body == null
            });
        }
        read.Add(new MailEvidence
        {
            Mail = new ChatMailItem { EntryId = email.EntryId, StoreId = email.StoreId, Subject = email.Subject, SenderName = email.Sender, SenderAddress = email.SenderEmail, Received = email.ReceivedTime },
            Text = "Email da seguire\n" + contextBuilder.CleanEmailBody(email.Body, email.Subject),
            Whole = true
        });
        var pack = new MailEvidencePack();
        pack.Items.AddRange(read);
        result.MailSources = pack.Sources();
        var messages = PromptCatalog.CreateFollowUpStatusPrompt(pack.Render(), pack.Count).ToList();
        ModelRequestContext.Prepare(profile, messages);
        if (useWebSearch)
        {
            if (!profile.WebSearchEnabled) throw new InvalidOperationException("Web Search non è abilitata per questo profilo.");
            try
            {
                var web = await SearchWebAsync(messages, ct);
                result.WebSources = web.Sources;
                web.AddEvidenceTo(messages);
            }
            catch (WebSearchSkippedException)
            {
                // The model saw no use for the web here: the follow-up rests on the emails alone, and the pane says so.
                result.WebSkipped = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // As the other actions (D6, 19/10/2026): a failed search (QwenCloud often hits 180 s) must not discard the follow-up.
                OnlyWorkspaceLog.Logger.Warning(ex, "Web search failed for profile {Profile}: the follow-up goes on without it", profile.Id);
                messages.Add(PromptCatalog.CreateWebFailedMessage(LlmErrorText.Describe(ex, profile)));
            }
        }

        ct.ThrowIfCancellationRequested();
        ModelRequestContext.Prepare(profile, messages);
        var answer = new StringBuilder();
        var client = CreateClient();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var json = await client.CompleteStructuredAsync(messages, PromptCatalog.MailAnswerJsonSchema, ct);
            OnlyWorkspaceLog.Logger.Information("Follow-up answer request: {Emails} emails, prompt={Prompt} output={Output} tokens, {Ms} ms",
                pack.Count, client.LastTokenUsage?.PromptTokens, client.LastTokenUsage?.CompletionTokens, watch.ElapsedMilliseconds);
            ct.ThrowIfCancellationRequested();
            ModelOutput.EnsureComplete(client.LastCompletion);
            var parsed = MailAnswer.Parse(json);
            MailAnswerVerifier.Verify(parsed, pack);
            var text = MailAnswerRenderer.Render(parsed) + "\n\n" + MailAnswerRenderer.Coverage(pack, parsed);
            answer.Append(text);
            OnChunk(text);
        }
        finally { (client as IDisposable)?.Dispose(); }
        if (unavailable.Count > 0)
        {
            var notice = "\n\nCopertura: originali non disponibili " + string.Join(", ", unavailable) + "; contenuto non analizzato.";
            answer.Append(notice);
            OnChunk(notice);
        }
        if (archive == null)
        {
            const string notice = "\n\nRicerca più ampia non disponibile: archivio email non attivo. Analizzata solo la conversazione Outlook.";
            answer.Append(notice);
            OnChunk(notice);
        }
        result.Answer = answer.ToString();
        result.WebSources = WebCitations.Used(result.Answer, result.WebSources);
        result.Outcome = FollowUpOutcome.Completed;
        ct.ThrowIfCancellationRequested();
        await SaveHistoryAsync(result);
        return result;
    }
}
