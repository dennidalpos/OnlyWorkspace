using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Prompts;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.Outlook.UI;

public partial class TaskPaneCoordinator
{
    public Task ExecuteBriefingAsync() => new BriefingPresenter(this).ExecuteAsync();

    /// <summary>Coordinates the agenda, unread mail triage and streamed result on the Outlook UI thread.</summary>
    private sealed class BriefingPresenter
    {
        private readonly TaskPaneCoordinator _owner;

        public BriefingPresenter(TaskPaneCoordinator owner) => _owner = owner;

        private const int MaxBriefingEmails = 25;

        /// <summary>Today's agenda (deterministic) + LLM triage of unread emails; categories are applied only on click.</summary>
        public async Task ExecuteAsync()
        {
            _owner._pendingWebAction = WebSearchAction.Briefing;
            var cts = _owner.BeginOperation();
            var ct = cts.Token;
            int turn = _owner._turn;
            LlmProfileConfig? activeProfile = null;
            var op = new Dictionary<string, string?> { ["type"] = "briefing" };
            var sw = Stopwatch.StartNew();
            _owner.PostStream(turn, new { type = "stream_start", kind = "action", action = "Briefing di oggi", prompt = (string?)null });

            try
            {
                activeProfile = _owner.GetActiveProfile();
                if (activeProfile == null)
                {
                    _owner.PostNotConfigured();
                    return;
                }

                _owner.PostPhase(turn, "Lettura di agenda e posta non letta…");
                await Task.Yield();
                var appointments = _owner._dataService.GetAppointments(new CalendarQuery { StartDate = DateTime.Today, EndDate = DateTime.Today.AddDays(1), MaxResults = 30 })
                    .OrderBy(a => a.Start).ToList();
                var unread = _owner._dataService.GetUnreadInbox(MaxBriefingEmails);
                var categories = _owner._dataService.GetCategoryNames();

                var it = System.Globalization.CultureInfo.GetCultureInfo("it-IT");
                var agendaText = BriefingContext.FormatAgenda(appointments);

                var md = new StringBuilder();
                md.Append("**Agenda di ").Append(DateTime.Today.ToString("dddd d MMMM", it)).AppendLine("**").AppendLine().Append(agendaText);
                md.AppendLine().AppendLine("Copertura posta: fino alle ultime 25 email non lette della Posta in arrivo predefinita. Allegati letti solo se consentiti dal profilo.");

                if (unread.Count == 0)
                {
                    md.AppendLine().AppendLine("Nessuna email non letta in Posta in arrivo.");
                    _owner.PostStream(turn, new { type = "stream_chunk", chunk = md.ToString() });
                    _owner.PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
                    return;
                }

                var emails = new StringBuilder();
                var actionFacts = await _owner.ActionFactsAsync(
                    unread.Select(e => new ArchiveEmailRef { EntryId = e.EntryId, StoreId = e.StoreId }).ToList(),
                    _owner.AttachmentContentAllowed(activeProfile), ct);
                for (int i = 0; i < unread.Count; i++)
                {
                    var e = unread[i];
                    ct.ThrowIfCancellationRequested();
                    if (!ExposurePolicy.Of(activeProfile).WholeEmails)
                    {
                        _owner.TrackMailInput(e.EntryId, e.StoreId, e.InternetMessageId);
                        if (actionFacts.TryGetValue(e.StoreId + "\n" + e.EntryId, out var factsText))
                            emails.Append(BriefingContext.Email(i + 1, e, factsText, string.Empty, facts: true));
                        else md.AppendLine().Append("Email senza fatti locali utilizzabili: ").Append(SecurityContentAnalysis.MarkdownText(e.Subject));
                        continue;
                    }
                    var original = await _owner._dataService.GetEmailByEntryIdAsync(e.EntryId, e.StoreId, ct)
                        ?? throw new InvalidOperationException("Un'email del Briefing non è più disponibile: riprova per aggiornare l'elenco.");
                    // A source of the saved briefing: deleting its last copy removes the conversation.
                    _owner.TrackMailInput(original.EntryId, string.IsNullOrEmpty(original.StoreId) ? e.StoreId : original.StoreId, original.MessageId);
                    var attachments = await _owner.BuildAttachmentContextAsync(original, activeProfile, turn, ct);
                    var notRead = AnalysisReport.NotReadList(attachments.Documents);
                    if (notRead.Length > 0) md.AppendLine().Append("Allegati non letti (email ").Append(i + 1).Append("): ").AppendLine(SecurityContentAnalysis.MarkdownText(notRead));
                    ct.ThrowIfCancellationRequested();
                    emails.Append(BriefingContext.Email(i + 1, e, _owner._contextBuilder.CleanEmailBody(original.Body, original.Subject), attachments.Text));
                }
                await CompleteAsync(activeProfile, agendaText, md, emails.ToString(), unread, categories, turn, sw, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _owner.PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Error(ex, "Briefing failed");
                _owner.PostStream(turn, new { type = "stream_error", message = LlmErrorText.Describe(ex, activeProfile), retryable = Retryable(ex), op });
            }
            finally
            {
                _owner.EndOperation(cts);
            }
        }

        /// <summary>Completes the briefing from collected data; the agenda is displayed independently of model permissions.</summary>
        private async Task CompleteAsync(LlmProfileConfig activeProfile, string agendaText, StringBuilder md,
            string emailText, IReadOnlyList<EmailSearchResult> unread, IReadOnlyCollection<string> categories,
            int turn, Stopwatch sw, CancellationToken ct)
        {
            ILlmClient? client = null;
            var op = new Dictionary<string, string?> { ["type"] = "briefing" };
            try
            {
                int budget = ProviderCatalog.PromptBudget(activeProfile);
                int exposureBudget = ExposurePolicy.Of(activeProfile).PackTokens;
                if (exposureBudget > 0) _owner._contextBuilder.EnsureFits(emailText, exposureBudget);
                var messages = PromptCatalog.CreateBriefingPrompt(BriefingContext.AgendaForModel(activeProfile, agendaText), _owner._contextBuilder.EnsureFits(emailText, budget), categories, DateTime.Today).ToList();
                EnsurePromptFits(messages, activeProfile);

                // Collection already chose originals or local facts; the agenda above follows the same permission.
                client = _owner.CreateLlmClient(activeProfile, wholeEmails: false);
                _owner.PostPhase(turn, $"Analisi di {unread.Count} email non lette…");
                var timings = new PhaseTimings();
                var groundedPrompt = (await _owner.AddWebEvidenceAsync(activeProfile, messages, turn, ct, PromptCatalog.BriefingJsonSchema, timings)).ToList();
                EnsurePromptFits(groundedPrompt, activeProfile);
                var generation = Stopwatch.StartNew();
                var json = await client.CompleteStructuredAsync(groundedPrompt, PromptCatalog.BriefingJsonSchema, ct);
                timings.AddGeneration(generation.ElapsedMilliseconds);
                BriefingResult briefing;
                string? triageFailure = null;
                try
                {
                    ModelOutput.EnsureComplete(client.LastCompletion);
                    briefing = BriefingParser.Parse(json, unread.Count, categories);
                }
                catch (InvalidModelOutputException ex)
                {
                    // The agenda stays valid: the unread emails are listed without priorities, saying why.
                    OnlyWorkspaceLog.Logger.Warning(ex, "Briefing triage unusable");
                    briefing = new BriefingResult();
                    triageFailure = ex.Message + " Priorità e categorie non assegnate.";
                }

                var triaged = BriefingReport.Triaged(briefing);
                BriefingReport.AppendUnread(md, briefing, triaged, unread, triageFailure);
                _owner.PostStream(turn, new { type = "stream_chunk", chunk = md.ToString() });

                var items = BriefingReport.CategoryCards(triaged, unread);

                sw.Stop();
                // Reuse only the validated model result at Minimal: the display also lists local-only mail metadata.
                string? modelContent = ExposurePolicy.Of(activeProfile).WholeEmails ? null : BriefingReport.ModelContent(briefing, triaged);
                await _owner.RecordActionInHistoryAsync(activeProfile, "Briefing di oggi", md.ToString(), ct, modelContent: modelContent);
                _owner.PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = client.LastCompletion?.IsTruncated == true, op, items });
                timings.LogCompleted(OnlyWorkspaceLog.Logger, "briefing", sw.ElapsedMilliseconds);
            }
            finally
            {
                DisposeClient(client);
            }
        }
    }
}
