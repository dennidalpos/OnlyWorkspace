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
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

public partial class TaskPaneCoordinator
{
    public Task ExecuteBriefingAsync() => new BriefingPresenter(this).ExecuteAsync();

    /// <summary>Coordinates the agenda, unread mail triage and streamed result on the Outlook UI thread.</summary>
    private sealed class BriefingPresenter
    {
        private readonly TaskPaneCoordinator _owner;

        public BriefingPresenter(TaskPaneCoordinator owner) => _owner = owner;

        private const int EmailsPerRequest = 12;

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
                var scope = _owner.CurrentArchive is { } indexer ? await indexer.GetAllowedSearchStoresAsync() : StoreScope.None();
                ct.ThrowIfCancellationRequested();
                var unread = new List<EmailSearchResult>();
                int foldersFailed;
                using (var scan = _owner._dataService.BeginUnreadInboxScan(scope))
                {
                    while (!scan.Complete)
                    {
                        ct.ThrowIfCancellationRequested();
                        unread.AddRange(scan.ReadNext(100));
                        if (!scan.Complete) await Task.Yield();
                    }
                    foldersFailed = scan.FoldersFailed;
                }
                unread = BriefingReport.ByReceivedDate(unread).ToList();
                var categories = _owner._dataService.GetCategoryNames();

                var it = System.Globalization.CultureInfo.GetCultureInfo("it-IT");
                var agendaText = BriefingContext.FormatAgenda(appointments);

                var md = new StringBuilder();
                md.Append("**Agenda di ").Append(DateTime.Today.ToString("dddd d MMMM", it)).AppendLine("**").AppendLine().Append(agendaText);
                md.AppendLine().AppendLine("Copertura posta: Posta in arrivo delle caselle autorizzate. Allegati letti solo se consentiti dal profilo.");

                if (foldersFailed > 0) md.AppendLine().AppendLine($"Lettura incompleta in {foldersFailed} cartelle di Posta in arrivo.");
                if (unread.Count == 0)
                {
                    md.AppendLine().AppendLine(scope.IsNone
                        ? "Nessuna casella autorizzata disponibile per il Briefing."
                        : "Nessuna email non letta trovata nelle cartelle consultate.");
                    _owner.PostStream(turn, new { type = "stream_chunk", chunk = md.ToString() });
                    _owner.PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
                    return;
                }

                await CompleteAsync(activeProfile, agendaText, md, ReadBatchAsync, unread, categories, turn, sw, ct);

                async Task<Dictionary<int, string>> ReadBatchAsync(int start, int count)
                {
                    var entries = new Dictionary<int, string>();
                    var slice = new UiSlice();
                    for (int i = start; i < start + count; i++)
                    {
                        await slice.YieldIfDueAsync(ct);
                        ct.ThrowIfCancellationRequested();
                        var e = unread[i];
                        _owner.TrackMailInput(e.EntryId, e.StoreId, e.InternetMessageId);
                        var body = _owner._dataService.GetEmailBody(e.EntryId, e.StoreId);
                        if (body == null)
                        {
                            md.AppendLine().AppendLine($"Originale non più disponibile (email {i + 1}).");
                            continue;
                        }

                        var attachments = await _owner.BuildAttachmentContextAsync(
                            (accept, token) => _owner._dataService.CopyAttachmentsAsync(e.EntryId, e.StoreId, accept, token), activeProfile, turn, ct);
                        var notRead = AnalysisReport.NotReadList(attachments.Documents);
                        if (notRead.Length > 0)
                            md.AppendLine().Append("Allegati non letti (email ").Append(i + 1).Append("): ").AppendLine(SecurityContentAnalysis.MarkdownText(notRead));
                        entries.Add(i, BriefingContext.Email(i + 1, e, _owner._contextBuilder.CleanEmailBody(body, e.Subject), attachments.Text));
                    }
                    return entries;
                }
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
            Func<int, int, Task<Dictionary<int, string>>> readBatch, IReadOnlyList<EmailSearchResult> unread,
            IReadOnlyCollection<string> categories, int turn, Stopwatch sw, CancellationToken ct)
        {
            ILlmClient? client = null;
            var op = new Dictionary<string, string?> { ["type"] = "briefing" };
            var briefing = new BriefingResult();
            var summaries = new List<string>();
            var timings = new PhaseTimings();
            var referenceDate = DateTime.Today;
            bool truncated = false;
            string? triageFailure = null;
            try
            {
                if (!ExposurePolicy.Of(activeProfile).WholeEmails)
                {
                    triageFailure = "Il profilo attivo non consente il triage delle email originali.";
                    var slice = new UiSlice();
                    foreach (var email in unread)
                    {
                        await slice.YieldIfDueAsync(ct);
                        _owner.TrackMailInput(email.EntryId, email.StoreId, email.InternetMessageId);
                    }
                }
                else
                {
                    client = _owner.CreateLlmClient(activeProfile, wholeEmails: false);
                    for (int start = 0; start < unread.Count; start += EmailsPerRequest)
                    {
                        ct.ThrowIfCancellationRequested();
                        int count = Math.Min(EmailsPerRequest, unread.Count - start);
                        _owner.PostProgress(turn, $"Triage email {start + 1}–{start + count} di {unread.Count}…", start, unread.Count);
                        var entries = await readBatch(start, count);
                        await TriageAsync(entries.OrderBy(e => e.Key).ToList());
                    }
                }

                if (briefing.Emails.Count < unread.Count)
                {
                    int missing = unread.Count - briefing.Emails.Count;
                    triageFailure ??= $"Priorità e categoria non disponibili per {missing} email.";
                }
                briefing.Summary = string.Join("\n\n", summaries.Distinct(StringComparer.Ordinal));
                var triaged = BriefingReport.Triaged(briefing);
                BriefingReport.AppendUnread(md, briefing, triaged, unread, triageFailure);
                _owner.PostStream(turn, new { type = "stream_chunk", chunk = md.ToString() });

                var items = BriefingReport.CategoryCards(triaged, unread);
                sw.Stop();
                string? modelContent = ExposurePolicy.Of(activeProfile).WholeEmails ? null : BriefingReport.ModelContent(briefing, triaged);
                await _owner.RecordActionInHistoryAsync(activeProfile, "Briefing di oggi", md.ToString(), ct, modelContent: modelContent);
                _owner.PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated, op, items });
                timings.LogCompleted(OnlyWorkspaceLog.Logger, "briefing", sw.ElapsedMilliseconds);
            }
            finally
            {
                DisposeClient(client);
            }

            async Task TriageAsync(List<KeyValuePair<int, string>> entries)
            {
                if (entries.Count == 0) return;
                ct.ThrowIfCancellationRequested();
                try
                {
                    string emailText = string.Concat(entries.Select(e => e.Value));
                    int exposureBudget = ExposurePolicy.Of(activeProfile).PackTokens;
                    if (exposureBudget > 0) _owner._contextBuilder.EnsureFits(emailText, exposureBudget);
                    var messages = PromptCatalog.CreateBriefingPrompt(
                        BriefingContext.AgendaForModel(activeProfile, agendaText), emailText, categories, referenceDate);
                    var groundedPrompt = await _owner.AddWebEvidenceAsync(activeProfile, messages, turn, ct, PromptCatalog.BriefingJsonSchema, timings);
                    var generation = Stopwatch.StartNew();
                    string json = await client!.CompleteStructuredAsync(groundedPrompt, PromptCatalog.BriefingJsonSchema, ct);
                    timings.AddGeneration(generation.ElapsedMilliseconds);
                    ModelOutput.EnsureComplete(client.LastCompletion);

                    var batch = BriefingParser.Parse(json, unread.Count, categories);
                    var accepted = BriefingReport.AcceptBatch(briefing, batch, entries.Select(e => e.Key));
                    if (accepted.Count > 0 && batch.Summary.Length > 0) summaries.Add(batch.Summary);
                    if (entries.Count > 1 && accepted.Count > 0 && accepted.Count < entries.Count)
                        await TriageAsync(entries.Where(e => !accepted.Contains(e.Key)).ToList());
                    else if (accepted.Count == 0 && entries.Count > 1)
                        await SplitAsync();
                }
                catch (Exception ex) when (ex is ContextTooLongException or InvalidModelOutputException)
                {
                    if (entries.Count > 1) await SplitAsync();
                    else
                    {
                        truncated |= ex is InvalidModelOutputException && client?.LastCompletion?.IsTruncated == true;
                        OnlyWorkspaceLog.Logger.Warning(ex, "Briefing triage unavailable for email {Index}", entries[0].Key + 1);
                    }
                }

                async Task SplitAsync()
                {
                    int half = entries.Count / 2;
                    await TriageAsync(entries.GetRange(0, half));
                    await TriageAsync(entries.GetRange(half, entries.Count - half));
                }
            }
        }
    }
}
