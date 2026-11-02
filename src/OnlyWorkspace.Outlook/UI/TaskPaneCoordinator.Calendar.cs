using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Prompts;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Infrastructure.Security;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

public partial class TaskPaneCoordinator
{
    private readonly Dictionary<string, List<ArchiveFact>> _actionFactsForTurn = new(StringComparer.Ordinal);

    /// <summary>Items ticked in the scan result: saved directly, with the same re-validation as the drafts.</summary>
    private void SaveActionItems(JsonElement message)
    {
        _control.PostMessage(_actionItemSaves.Save(message, _dataService.SaveActionItem,
            ex => OnlyWorkspaceLog.Logger.Error(ex, "Failed to save action item")));
    }

    // Relative dates ("venerdì") refer to the day the email arrived; unsent or odd items fall back to today.
    private static DateTime ReferenceDateOf(DateTime received) =>
        received.Year > 2000 && received <= DateTime.Now ? received.Date : DateTime.Today;

    private static string CandidateLabel(int count) => count == 1 ? "1 email candidata" : $"{count} email candidate";

    /// <summary>
    /// Meetings, calls and deadlines in the emails of a scope: local pre-filter, then one structured request on the
    /// candidates read whole (A109), each item citing its email and the date as written there, checked by the code.
    /// Nothing is written to Outlook here: the pane lists the items and the user picks.
    /// </summary>
    private async Task ExecuteCalendarScanAsync(Dictionary<string, string?> op, string title, string scopeLabel, bool attachments,
        Func<MailSearchCoverage, CancellationToken, int, Task<List<AnalysisEmail>?>> resolveEmails)
    {
        var cts = BeginOperation();
        var ct = cts.Token;
        int turn = _turn;
        ILlmClient? client = null;
        LlmProfileConfig? activeProfile = null;
        var sw = Stopwatch.StartNew();
        PostStream(turn, new { type = "stream_start", kind = "action", action = title, prompt = (string?)null });

        try
        {
            activeProfile = GetActiveProfile();
            if (activeProfile == null)
            {
                PostNotConfigured();
                return;
            }

            PostPhase(turn, "Ricerca delle email…");
            await Task.Yield();
            var coverage = new MailSearchCoverage();
            var headers = await resolveEmails(coverage, ct, turn);
            if (headers == null)
            {
                PostStream(turn, new { type = "stream_error", message = "Seleziona o apri un'email, poi riprova.", retryable = true, op });
                return;
            }
            if (headers.Count == 0)
            {
                PostStream(turn, new { type = "stream_chunk", chunk = $"Nessuna candidata trovata ({scopeLabel}). {coverage.Explain()}" });
                PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
                return;
            }

            string webNote = await CalendarWebNoteAsync(activeProfile, headers, turn, ct);
            // Collection below chooses permitted originals or local facts; the factory still checks the notice.
            client = CreateLlmClient(activeProfile, wholeEmails: false);
            int budget = ProviderCatalog.PromptBudget(activeProfile);
            var found = new List<ExtractedActionItem>();
            var candidates = new List<MailEvidence>();
            var extractionDiagnostics = new ExtractionDiagnostics();
            int read = 0, tooLong = 0, noFacts = 0;
            bool stopped = false, requested = false;
            bool pecContent = AttachmentContentAllowed(activeProfile);
            var documents = new AnalysisStats();
            var slice = new UiSlice();
            var actionFacts = await ActionFactsAsync(headers.Select(h => new ArchiveEmailRef { EntryId = h.EntryId, StoreId = h.StoreId }).ToList(), attachments && pecContent, ct);
            int evidenceChars = 0;
            int exposureBudget = ExposurePolicy.Of(activeProfile).PackTokens;
            bool factsOnly = !ExposurePolicy.Of(activeProfile).WholeEmails;

            for (; read < headers.Count; read++)
            {
                if (ct.IsCancellationRequested) { stopped = true; break; }
                var h = headers[read];
                if (read % 10 == 0) PostProgress(turn, $"Email {read + 1} di {headers.Count} · {CandidateLabel(candidates.Count)}…", read, headers.Count);
                try { await slice.YieldIfDueAsync(ct); }
                catch (OperationCanceledException) { stopped = true; break; }

                actionFacts.TryGetValue(h.StoreId + "\n" + h.EntryId, out var factsText);
                string evidence;
                if (factsOnly)
                {
                    // No original, PEC or attachment reads at Minimal, even when a conversation preloaded a body.
                    if (factsText == null) { noFacts++; continue; }
                    if (!CalendarCandidateFilter.IsCandidate(null, factsText)) continue;
                    TrackMailInput(h.EntryId, h.StoreId, h.MessageId);
                    evidence = factsText;
                }
                else
                {
                    // A body preloaded with the conversation is a source too (ReadMailBodyAsync records the others).
                    if (h.Body != null) TrackMailInput(h.EntryId, h.StoreId, h.MessageId);
                    var raw = h.Body ?? await ReadMailBodyAsync(h.EntryId, h.StoreId, ct);
                    var body = _contextBuilder.CleanEmailBody(raw ?? string.Empty, h.Subject);
                    var filterText = body;
                    // PEC: the envelope only says when the message was sent; what it says is in the attached email.
                    if (pecContent && PecMessage.IsEnvelope(h.Subject))
                    {
                        try
                        {
                            var original = await ReadPecMessageAsync(h.EntryId, h.StoreId, ct);
                            if (original.Length > 0)
                            {
                                body = original;
                                filterText = PecMessage.WithoutHeader(original);
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            stopped = true;
                            break;
                        }
                    }
                    if (attachments && pecContent)
                    {
                        string files;
                        try { files = await ReadAnalysisAttachmentsAsync(h, true, documents, _ => { }, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            stopped = true;
                            break;
                        }
                        body += files;
                        filterText += files;
                    }
                    if (string.IsNullOrWhiteSpace(body)) { coverage.Unavailable++; continue; }
                    if (!CalendarCandidateFilter.IsCandidate(h.Subject, filterText)) continue;
                    evidence = body + "\n" + factsText;
                }

                var mail = new ChatMailItem
                {
                    Facts = _actionFactsForTurn.TryGetValue(h.StoreId + "\n" + h.EntryId, out var typedFacts) ? typedFacts : new List<ArchiveFact>(),
                    ArchiveId = h.ArchiveId, EntryId = h.EntryId, StoreId = h.StoreId, MessageId = h.MessageId, Subject = h.Subject,
                    SenderName = h.Sender, Received = h.Received, Sent = h.Sent, OutsideFilters = h.OutsideFilters, LinkReasons = h.LinkReasons
                };
                string rendered = ChatMailDigest.Note(candidates.Count + 1, mail, evidence, includeDetails: !factsOnly);
                if (!_contextBuilder.Fits(rendered, budget) || (exposureBudget > 0 && !_contextBuilder.Fits(new string(' ', evidenceChars + rendered.Length), exposureBudget))) { tooLong++; continue; }
                evidenceChars += rendered.Length;
                candidates.Add(new MailEvidence { Mail = mail, Text = evidence, Whole = !factsOnly });
            }

            // One request on every candidate (A109), unless Interrompi came while reading: then nothing is sent.
            var pack = MailEvidencePack.Chronological(candidates);
            _mailSourcesForTurn = pack.Sources();
            if (!stopped && pack.Count > 0)
            {
                PostPhase(turn, pack.Count == 1 ? "Richiesta al modello su 1 email…" : $"Richiesta al modello su {pack.Count} email…");
                // Counted as sent from here: a stop during the request must not claim the emails stayed on the PC (A121).
                requested = true;
                try
                {
                    var extracted = await ExtractCalendarItemsAsync(client, pack, ct);
                    extractionDiagnostics.Merge(extracted.Diagnostics);
                    found.AddRange(extracted);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    stopped = true;
                }
            }

            // Interrompi keeps the reading summary; any other cancel drops it, before reading Outlook again.
            if (stopped && (read == 0 || _operations.ResultToken(cts).IsCancellationRequested)) ct.ThrowIfCancellationRequested();

            // Calendar material only: a settled date still to come.
            var items = ActionItemMerger.Merge(found, DateTime.Now, datedOnly: true);
            var inOutlook = InOutlook(items);

            var sb = new StringBuilder();
            sb.Append(stopped ? "Analisi interrotta: " : string.Empty)
              .Append($"lette {read} email su {headers.Count} ({scopeLabel}), ")
              .Append(CalendarScanSummary.Request(pack.Count, requested, stopped));
            sb[0] = char.ToUpperInvariant(sb[0]);
            if (!stopped)
                sb.Append(items.Count == 0 ? "Nessuna voce futura con una data certa."
                    : items.Count == 1 ? "Trovata 1 voce: controllala e aggiungila a Outlook."
                    : $"Trovate {items.Count} voci: controlla le date, seleziona quelle giuste e aggiungile a Outlook.");
            int existing = items.Count(inOutlook);
            if (existing > 0) sb.Append(existing == 1 ? " 1 è già in Outlook e non è selezionata." : $" {existing} sono già in Outlook e non sono selezionate.");
            int unverified = items.Count(i => i.Unverified.Count > 0);
            if (unverified > 0) sb.Append(unverified == 1 ? " 1 voce ha una data non verificata nell'email citata e non è selezionata." : $" {unverified} voci hanno una data non verificata nell'email citata e non sono selezionate.");
            sb.Append(' ').Append(coverage.Explain());
            if (extractionDiagnostics.IsPartial) sb.Append(' ').Append(extractionDiagnostics.Describe());
            if (tooLong > 0) sb.Append(tooLong == 1 ? " 1 email non letta: è troppo lunga per il contesto del profilo in uso." : $" {tooLong} email non lette: sono troppo lunghe per il contesto del profilo in uso.");
            if (noFacts > 0) sb.Append(noFacts == 1 ? " 1 email senza fatti locali utilizzabili." : $" {noFacts} email senza fatti locali utilizzabili.");
            var unreadDocuments = documents.Documents.Count(d => !d.WasRead);
            if (unreadDocuments > 0) sb.Append($" {unreadDocuments} allegati non letti; il loro contenuto può mancare dal risultato.");
            sb.Append(webNote);
            PostStream(turn, new { type = "stream_chunk", chunk = sb.ToString() });

            string Origin(ExtractedActionItem i)
            {
                if (i.Source < 1 || i.Source > pack.Count) return "email non indicata";
                var m = pack.Items[i.Source - 1].Mail;
                return $"[M{i.Source}] {m.Received:dd/MM/yyyy} · {Truncate(m.Subject, 60)}";
            }
            // The email each item comes from, shown on its card and openable from there (A121).
            object? SourceOf(ExtractedActionItem i)
            {
                if (i.Source < 1 || i.Source > pack.Count) return null;
                var m = pack.Items[i.Source - 1].Mail;
                return new { label = $"[M{i.Source}] {Truncate(m.Subject, 60)}", meta = m.Received.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture), entryId = m.EntryId, storeId = m.StoreId };
            }

            var payload = items.Select(i => new
            {
                kind = i.Kind,
                title = i.Title,
                when = i.WhenLabel,
                due = i.Due?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                start = i.Start?.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture),
                end = i.End?.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture),
                allDay = i.AllDay,
                location = i.Location,
                notes = i.Notes,
                source = SourceOf(i),
                dateText = i.DateText,
                selectable = true,
                exists = inOutlook(i),
                verified = i.Unverified.Count == 0,
                warning = i.Unverified.Count > 0 ? "Non verificata: " + string.Join("; ", i.Unverified) : null
            }).ToList();
            string Check(ExtractedActionItem i) => i.Unverified.Count == 0 ? string.Empty : "⚠ " + string.Join("; ", i.Unverified);

            foreach (var i in items)
            {
                sb.AppendLine().Append("- **").Append(i.IsAppointment ? "Appuntamento" : "Attività").Append("** — ").Append(i.Title).Append(" (").Append(i.WhenLabel).Append(") — ").Append(Origin(i));
                if (i.Unverified.Count > 0) sb.Append(" — ").Append(Check(i));
            }
            if (items.Count > 0 || stopped || webNote.Length > 0 || extractionDiagnostics.IsPartial)
            {
                // Source labels and scope stay visible locally, without re-entering a subsequent Minimal request.
                string? modelContent = factsOnly ? JsonSerializer.Serialize(items.Select(i => new
                {
                    i.Source, i.Kind, i.Title, i.DateText, i.Start, i.End, i.Due, i.AllDay, i.Location, i.Unverified
                })) : null;
                await RecordActionInHistoryAsync(activeProfile, factsOnly ? "Appuntamenti e scadenze" : $"{title}: {scopeLabel}",
                    sb.ToString(), _operations.ResultToken(cts), modelContent: modelContent);
            }

            var export = ListExport(new[] { "Tipo", "Titolo", "Quando", "Luogo", "Note", "Email", "Verifica" },
                items.Select(i => new[] { i.IsAppointment ? "Appuntamento" : "Scadenza", i.Title, (i.IsAppointment ? i.Start : i.Due)?.ToString(i.AllDay || !i.IsAppointment ? "dd/MM/yyyy" : "dd/MM/yyyy HH:mm") ?? string.Empty, i.Location, i.Notes, Origin(i), Check(i) }), extractionDiagnostics);

            sw.Stop();
            PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op, items = payload, export });
            OnlyWorkspaceLog.Logger.Information("Mailbox scan: {Read}/{Total} read, {Candidates} candidates in {Requests} request, {Items} items ({Unverified} not verified), {TooLong} too long, stopped={Stopped}, {LatencyMs} ms",
                read, headers.Count, pack.Count, requested ? 1 : 0, items.Count, unverified, tooLong, stopped, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            OnlyWorkspaceLog.Logger.Information("Mailbox scan cancelled after {LatencyMs} ms", sw.ElapsedMilliseconds);
            PostStream(turn, new { type = "stream_cancelled", message = "Operazione annullata." });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Mailbox scan failed");
            PostStream(turn, new { type = "stream_error", message = LlmErrorText.Describe(ex, activeProfile), retryable = Retryable(ex), op });
        }
        finally
        {
            DisposeClient(client);
            EndOperation(cts);
        }
    }

    /// <summary>
    /// Public notices and deadlines of the senders' companies, kept apart from what Outlook says. Short and about
    /// the companies (18/10/2026): "deadlines of the domains" brought back domain renewal pages, and a request for
    /// recent news ran past the 180 s limit three times.
    /// </summary>
    private async Task<string> CalendarWebNoteAsync(LlmProfileConfig activeProfile, List<AnalysisEmail> headers, int turn, CancellationToken ct)
    {
        if (!WebForTurn(activeProfile)) return string.Empty;
        var domains = SecurityWebQuery.SenderDomains(headers.Select(h => h.Sender), _dataService.GetOwnAddressList());
        if (domains == null)
        {
            OnlyWorkspaceLog.Logger.Information("Calendar web search skipped: no company domain among the senders");
            return string.Empty;
        }
        return await SeparateWebReferencesAsync(activeProfile, $"Aziende dei siti {domains}: avvisi e scadenze pubbliche che le riguardano",
            "Riferimenti web (separati dalle scadenze trovate in Outlook)", turn, ct);
    }

    /// <summary>Exact copies in authorized stores only; never a subject/date or Message-ID approximation.</summary>
    private async Task<Dictionary<string, string>> ActionFactsAsync(List<ArchiveEmailRef> copies, bool attachmentsAllowed, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (copies.Count == 0 || ArchiveIndexer.Current is not { } indexer) return result;
        var db = await indexer.OpenForSearchAsync();
        if (db == null) return result;
        var stores = await indexer.GetAllowedSearchStoresAsync();
        for (int start = 0; start < copies.Count; start += 500)
        {
            var hits = await db.SearchAsync(new ArchiveQuery { EmailRefs = copies.Skip(start).Take(500).ToList(),
                AllowedStoreIds = stores, IncludeFacts = true, IncludeAttachmentText = attachmentsAllowed, Max = 500 }, ct);
            var ids = hits.Select(h => h.Id).ToList();

            foreach (var hit in hits)
            {
                // Receipt-only material never enters model evidence, even when the profile permits attachments.
                var facts = hit.Facts.Where(f => f.SourceId.Length == 0 || attachmentsAllowed && hit.Attachments.Any(a => a.SourceId == f.SourceId && a.State == "indexed")).ToList();
                ct.ThrowIfCancellationRequested();
                _actionFactsForTurn[hit.StoreId + "\n" + hit.EntryId] = facts;
                if (ActionMailEvidence.Facts(facts, attachmentsAllowed) is { } text)
                    result[hit.StoreId + "\n" + hit.EntryId] = text;
            }
        }
        return result;
    }
}
