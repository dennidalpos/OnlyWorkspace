using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
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
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Infrastructure.Security;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// "Cerca e analizza": what to look for (preset and/or free text), where (conversation, accounts, folders) and
/// the filters (person, subject, period). The list goes on screen before any model call; then one request reads
/// the emails oldest first, each as permitted local facts or a whole original with its
/// attachments (E7, 25/10/2026: before, a note per email condensed in groups). Calendar and follow-up presets run
/// their own scan on the same scope.
/// </summary>
public partial class TaskPaneCoordinator
{
    private static readonly TimeSpan AnalysisExtractorTimeout = TimeSpan.FromSeconds(60);
    /// <summary>The administrator's OCR setting for scans and images (E8); on when the configuration does not say.</summary>
    private bool OcrEnabled => _config?.AttachmentOcr != false;

    /// <summary>One email to analyse; Body is already loaded for conversation messages only.</summary>
    private sealed class AnalysisEmail
    {
        public string Subject = string.Empty;
        public string Sender = string.Empty;
        public DateTime Received;
        public string EntryId = string.Empty;
        public string StoreId = string.Empty;
        public string MessageId = string.Empty;
        public string? Body;
        /// <summary>Row of the email in the archive (whole-archive scope); 0 otherwise.</summary>
        public long ArchiveId;
        public bool ConversationLimited;
        public bool OutsideFilters;
        public List<string> LinkReasons = new();
        public DateTime? Sent;
    }

    private sealed class MailSearchCoverage
    {
        public int Scanned;
        public int FoldersFailed;
        public int IncompleteIndex;
        public int Unavailable;
        public int UnreadAttachments;
        /// <summary>Archive hits left out because another copy of the same message is read.</summary>
        public int Copies;
        public bool MetadataIncomplete, FactsIncomplete;
        /// <summary>Outlook conversation scope: messages read and in the conversation (more when it was cut).</summary>
        public int ConversationRead, ConversationTotal;

        public void Add(MailSearchCoverage other)
        {
            Scanned += other.Scanned;
            FoldersFailed += other.FoldersFailed;
            IncompleteIndex += other.IncompleteIndex;
            Unavailable += other.Unavailable;
            UnreadAttachments += other.UnreadAttachments;
            Copies += other.Copies;
            MetadataIncomplete |= other.MetadataIncomplete; FactsIncomplete |= other.FactsIncomplete;
            ConversationRead += other.ConversationRead; ConversationTotal += other.ConversationTotal;
        }

        public string Explain() => $"Ricerca locale: {Scanned} intestazioni esaminate, {FoldersFailed} cartelle non accessibili, " +
            $"{IncompleteIndex} email con indice incompleto, {Unavailable} originali non disponibili, " +
            $"{UnreadAttachments} allegati non leggibili. " +
            (Copies == 0 ? string.Empty : Copies == 1 ? "1 copia dello stesso messaggio in un'altra cartella non è stata letta di nuovo. "
                : $"{Copies} copie dello stesso messaggio in altre cartelle non sono state lette di nuovo. ") +
            (MetadataIncomplete ? "Intestazioni della catena incomplete. " : "") + (FactsIncomplete ? "Estrazione dei fatti incompleta. " : "") +
            ConversationNotice(ConversationRead, ConversationTotal) +
            "La cache Outlook può coprire solo parte della cassetta.";
    }

    /// <summary>The Outlook conversation was cut to its most recent messages: say how many were left out.</summary>
    private static string ConversationNotice(int read, int total) => total > read
        ? $"La conversazione ha {total} messaggi: letti i {read} più recenti, {total - read} più vecchi esclusi. "
        : string.Empty;

    private static string ConversationNotice(SelectedEmailInfo email) =>
        email.ThreadMessages is { Count: > 0 } thread ? ConversationNotice(thread.Count, email.ConversationTotal) : string.Empty;

    /// <summary>
    /// Emails read by the latest analysis: its follow-up questions are answered on them without reading the mailbox
    /// again. Only one is kept (whole originals can be large) and a new chat or conversation drops it.
    /// </summary>
    private (string Id, MailEvidencePack Pack)? _analysisEvidence;

    private bool AttachmentContentAllowed(LlmProfileConfig profile) => profile.SendsAttachmentText;

    private int MaxAnalysisEmails => Math.Max(1, _config?.Limits?.MaxAnalysisEmails ?? 300);

    /// <summary>Mail details are read only for an explicitly selected conversation or folder scope.</summary>
    private async Task SendAnalysisContextAsync(string? scope, int requestId)
    {
        var selectedScope = scope is "thread" or "folder" or "flow" ? scope : "none";
        var email = selectedScope != "none" && _dataService.CanActOnSelectedEmail(_contextWindow)
            ? _dataService.GetSelectedEmailHeader(_contextWindow) : null;
        var folder = selectedScope == "folder" && email != null ? _dataService.GetFolderOf(email.EntryId, email.StoreId) : null;
        var archiveStore = CurrentArchive is { } indexer ? await indexer.OpenForSearchAsync() : null;
        if (_isDisposed) return;
        var profile = GetActiveProfile();
        _control.PostMessage(new
        {
            type = "analysis_context",
            requestId,
            selectionScope = selectedScope,
            hasEmail = email != null,
            entryId = email?.EntryId,
            storeId = email?.StoreId,
            subject = email?.Subject,
            senderName = email?.SenderName,
            senderEmail = email != null && !email.SenderEmail.StartsWith("/", StringComparison.Ordinal) ? email.SenderEmail : null,
            folder = folder != null && folder.IsMailFolder ? new { entryId = folder.EntryId, storeId = folder.StoreId, path = folder.Path } : null,
            stores = _dataService.EnumerateStores().Where(s => s.IsOpen && s.StoreId.Length > 0).Select(s => new { id = s.StoreId, name = s.DisplayName }).ToList(),
            attachmentsAllowed = profile == null || AttachmentContentAllowed(profile),
            archive = archiveStore != null ? new { active = true, emails = (await archiveStore.GetStatsAsync()).Emails } : null,
            maxEmails = MaxAnalysisEmails,
            followUpDays = FollowUpMinDays
        });
    }

    private void PickAnalysisFolder()
    {
        var folder = _dataService.PickFolder();
        if (folder == null) return;
        if (!folder.IsMailFolder)
        {
            Toast("Scegli una cartella di posta.", "error");
            return;
        }
        _control.PostMessage(new { type = "analysis_folder", entryId = folder.EntryId, storeId = folder.StoreId, path = folder.Path });
    }

    private async Task<List<EmailSearchResult>> ScanMailHeadersAsync(IReadOnlyList<MailFolderRef> folders, bool subfolders,
        EmailSearchQuery query, MailSearchCoverage coverage, CancellationToken ct, int turn)
    {
        var result = new List<EmailSearchResult>();
        using var scan = _dataService.BeginMailHeaderScan(folders, subfolders, query);
        while (!scan.Complete)
        {
            ct.ThrowIfCancellationRequested();
            result.AddRange(scan.ReadNext(200));
            coverage.Scanned = scan.RowsScanned;
            coverage.FoldersFailed = scan.FoldersFailed;
            if (coverage.Scanned % 2000 < 200)
                PostStream(turn, new { type = "stream_phase", message = $"Ricerca locale: {coverage.Scanned} intestazioni esaminate, {result.Count} candidate…", scanned = coverage.Scanned, candidates = result.Count });
            await Task.Delay(1, ct);
        }
        return result.OrderByDescending(e => e.ReceivedTime).ToList();
    }

    /// <summary>Phase times of a chat turn, logged at its end to see where the time goes.</summary>
    private sealed class ChatTimings
    {
        private readonly Stopwatch _phase = Stopwatch.StartNew();
        public bool Local;
        public bool Archive;
        public long Intent, Search, Read, Web, FirstToken;
        public int Emails, Facts, Originals;

        /// <summary>Milliseconds since the previous lap.</summary>
        public long Lap()
        {
            long ms = _phase.ElapsedMilliseconds;
            _phase.Restart();
            return ms;
        }
    }

    private sealed class ChatMailEvidence
    {
        public MailEvidencePack Pack = new();
        public string Notice = string.Empty;
        /// <summary>Table shown before any model call; kept in the saved answer.</summary>
        public string Listing = string.Empty;
        public int Found;
        /// <summary>The answer from the archive's local facts: no model request.</summary>
        public string? LocalAnswer;
    }

    /// <summary>
    /// Mail for a chat question: the archive answers period, sender and words at once, Outlook only when there is no
    /// archive. The list goes on screen before any model call; each email then contributes its permitted facts or its whole
    /// text, and one final request reads them (until 20/10/2026 a note request per
    /// email took minutes; until 25/10/2026 notes beyond the context were condensed by the model).
    /// </summary>
    /// <param name="latest">Only the newest emails, for a question with no period; 0 = every email found.</param>
    /// <param name="listOnly">A list or count request: the list is the answer, nothing else is read.</param>
    private async Task<ChatMailEvidence> BuildChatMailEvidenceAsync(LlmProfileConfig profile,
        string question, EmailSearchQuery query, int latest, int budget, bool listOnly, int turn, ChatTimings timings, CancellationToken ct)
    {
        var coverage = new MailSearchCoverage();
        bool attachmentsAllowed = AttachmentContentAllowed(profile);
        bool attachments = _config?.Archive?.IndexAttachmentText == true && attachmentsAllowed;
        timings.Lap();
        var items = await FindChatMailInArchiveAsync(query, latest, attachments, coverage, turn, ct);
        timings.Archive = items != null;
        if (items == null && query.FactFilters.Count > 0)
            return new ChatMailEvidence { Notice = "I filtri sui fatti richiedono l'archivio locale disponibile; ricerca non eseguita." };
        items ??= await FindChatMailInOutlookAsync(ChatSearchFolders(), query, latest, attachments, coverage, turn, ct);
        timings.Search = timings.Lap();

        int more = Math.Max(0, items.Count - MaxAnalysisEmails);
        items = items.OrderByDescending(i => i.Received).Take(MaxAnalysisEmails).OrderBy(i => i.Sent ?? i.Received).ToList();
        timings.Emails = items.Count;
        var result = new ChatMailEvidence { Listing = ChatMailDigest.Listing(items, more), Found = items.Count + more };
        if (result.Listing.Length > 0) PostStream(turn, new { type = "stream_chunk", chunk = result.Listing + "\n" });
        string source = timings.Archive ? "archivio" : "Outlook";
        if (listOnly)
        {
            result.Notice = $"\n\n---\nRicerca posta ({source}): {coverage.Scanned} email esaminate, {result.Found} trovate. Elenco locale, nessuna richiesta al modello.";
            return result;
        }

        // «Importo della fattura 123?»: the facts extracted on this PC answer, nothing leaves it.
        if (LocalFactAnswer.TryAnswer(question, query.FactFilters, items) is { } local)
        {
            result.LocalAnswer = local;
            result.Notice = $"\n\n---\nRicerca posta ({source}): {result.Found} email trovate. Risposta dai dati estratti in locale, nessuna richiesta al modello. " +
                "Gli importi e le date sono quelli scritti nelle email citate.";
            return result;
        }

        var documents = new AnalysisStats();
        var reading = await ReadEvidenceAsync(items, _ => null,
            attachments, attachmentsAllowed, budget, documents, coverage, ExposurePolicy.Of(profile), turn, ct);
        // Interrompi while reading: the list on screen is the result, no request is sent.
        if (reading.Stopped) throw new OperationCanceledException(ct);
        result.Pack = reading.Pack;
        timings.Facts = reading.Pack.Facts;
        timings.Originals = reading.Pack.Whole;
        coverage.UnreadAttachments += documents.Documents.Count(d => d.Status != DocumentStatus.Read);
        timings.Read = timings.Lap();
        result.Notice = $"\n\n---\nRicerca posta ({source}): {coverage.Scanned} email esaminate, {result.Found} trovate, " +
            $"{timings.Facts} lette dai fatti locali, {timings.Originals} lette per intero. " +
            (coverage.Copies > 0 ? $"{coverage.Copies} copie dello stesso messaggio in altre cartelle non sono state lette di nuovo. " : string.Empty) +
            $"{coverage.FoldersFailed} cartelle non accessibili, {coverage.Unavailable} originali non disponibili, " +
            $"{coverage.UnreadAttachments} allegati non leggibili, {reading.TooLong.Count} email troppo lunghe per il profilo. " +
            ExposureNotice(reading) +
            (attachments ? string.Empty : "Il contenuto degli allegati non è stato cercato con questo profilo. ") +
            (coverage.MetadataIncomplete ? "Intestazioni della catena incomplete. " : "") + (coverage.FactsIncomplete ? "Estrazione dei fatti incompleta. " : "") +
            "La pertinenza semantica che non compare nel testo cercabile può sfuggire alla ricerca locale.";
        return result;
    }

    /// <summary>The emails as the final request reads them (<see cref="MailEvidenceReader"/>); missing originals count as unavailable.</summary>
    private async Task<EvidenceReading> ReadEvidenceAsync(IReadOnlyList<ChatMailItem> items,
        Func<ChatMailItem, string?> loadedBody, bool attachments, bool attachmentsAllowed, int budget, AnalysisStats documents,
        MailSearchCoverage coverage, ExposurePolicy policy, int turn, CancellationToken ct)
    {
        var reader = new MailEvidenceReader
        {
            Track = item => TrackMailInput(item.EntryId, item.StoreId, item.MessageId),
            Progress = (message, done, total) => PostProgress(turn, message, done, total),
            ReadBody = async (item, token) => loadedBody(item) ?? await ReadMailBodyAsync(item.EntryId, item.StoreId, token),
            ReadAttachments = attachments
                ? (item, progress, token) => ReadAnalysisAttachmentsAsync(
                    new AnalysisEmail { Subject = item.Subject, Received = item.Received, EntryId = item.EntryId, StoreId = item.StoreId },
                    attachmentsAllowed, documents, progress, token)
                : null,
            Context = _contextBuilder,
            Mode = policy.Evidence,
            PackTokens = policy.PackTokens
        };
        var reading = await reader.ReadAsync(items, attachmentsAllowed, budget, documents, ct);
        TrackDocuments(documents.Documents);
        coverage.Unavailable += reading.Missing.Count;
        return reading;
    }

    /// <summary>What the profile's exposure level kept out of the request, for the coverage line; empty when nothing.</summary>
    private static string ExposureNotice(EvidenceReading reading) =>
        (reading.NoFacts.Count > 0 ? $"{reading.NoFacts.Count} email senza fatti locali non inviate (il livello di esposizione non invia originali). " : string.Empty) +
        (reading.OverBudget.Count > 0 ? $"{reading.OverBudget.Count} email oltre il limite di dati del livello di esposizione non inviate. " : string.Empty);

    /// <summary>
    /// The single request on the pack: structured answer, citations checked against each cited email.
    /// Returns the markdown shown and saved, with the coverage line.
    /// </summary>
    private async Task<(string Text, MailAnswer Answer)> AnswerOnEvidenceAsync(ILlmClient client, IEnumerable<ChatMessage> prompt, MailEvidencePack pack, int turn, CancellationToken ct, string? instruction = null)
    {
        PrepareDocuments(pack);
        var watch = Stopwatch.StartNew();
        if (GetActiveProfile() is { } external && ExposurePolicy.SendsFactsOnly(external))
        {
            if (instruction == null || !CloudFactsRequest.IsFactRequest(instruction)) throw new DataEgressBlockedException(CloudFactsRequest.BlockedMessage);
            var safe = new CloudFactsRequest(pack, external.SendsAttachmentText, CloudFactsRequest.RequestedKind(instruction));
            var response = await client.CompleteStructuredAsync(safe.Messages, CloudFactsRequest.Schema, ct);
            ModelOutput.EnsureComplete(client.LastCompletion);
            var restored = safe.ReadAnswer(response);
            return (MailAnswerRenderer.Render(restored) + "\n\n" + MailAnswerRenderer.Coverage(pack, restored), restored);
        }
        string json;
        using (ObserveAnswer(turn, watch))
            json = await client.CompleteStructuredAsync(prompt, PromptCatalog.MailAnswerJsonSchema, ct);
        LogMailAnswer(client, pack, watch.ElapsedMilliseconds);
        ModelOutput.EnsureComplete(client.LastCompletion);
        var answer = MailAnswer.Parse(json);
        MailAnswerVerifier.Verify(answer, pack);
        OnlyWorkspaceLog.Logger.Information("Mail answer checked: {Points} points, {Unverified} not verified", answer.Points.Count(), MailAnswerRenderer.UnverifiedCount(answer));
        return (MailAnswerRenderer.Render(answer) + "\n\n" + MailAnswerRenderer.Coverage(pack, answer), answer);
    }

    /// <summary>
    /// While a structured answer streams in, the pane's phase says how much has arrived and which sections are written,
    /// at most once a second; the chunks arrive on a pool thread and the phase is posted on the UI thread.
    /// </summary>
    private IDisposable ObserveAnswer(int turn, Stopwatch watch)
    {
        var ui = SynchronizationContext.Current;
        var arrived = new StringBuilder();
        long lastMs = 0;
        bool posting = false;
        return StructuredProgress.Observe(chunk =>
        {
            string text;
            lock (arrived)
            {
                arrived.Append(chunk);
                if (posting || watch.ElapsedMilliseconds - lastMs < 1000) return;
                lastMs = watch.ElapsedMilliseconds;
                posting = true;
                text = arrived.ToString();
            }
            void Post()
            {
                lock (arrived) posting = false;
                PostPhase(turn, MailAnswerProgress.Describe(text, watch.Elapsed));
            }
            if (ui != null) ui.Post(_ => Post(), null); else Post();
        });
    }

    /// <summary>One line per final request: emails, whole originals, tokens and time.</summary>
    private static void LogMailAnswer(ILlmClient client, MailEvidencePack pack, long ms) =>
        OnlyWorkspaceLog.Logger.Information("Mail answer request: {Emails} emails ({Whole} whole), prompt={Prompt} output={Output} tokens, truncated={Truncated}, {Ms} ms",
            pack.Count, pack.Whole, client.LastTokenUsage?.PromptTokens, client.LastTokenUsage?.CompletionTokens, client.LastCompletion?.IsTruncated == true, ms);

    /// <summary>Header row of Cerca e analizza as a list item, with local facts when the email has an archive row.</summary>
    private async Task<List<ChatMailItem>> AnalysisItemsAsync(IReadOnlyList<AnalysisEmail> emails, CancellationToken ct)
    {
        var indexer = CurrentArchive;
        var db = indexer != null && emails.Any(e => e.ArchiveId > 0) ? await indexer.OpenForSearchAsync() : null;
        var archived = emails.Where(e => e.ArchiveId > 0).Select(e => e.ArchiveId).Distinct().ToList();
        var facts = db != null && archived.Count > 0 ? await db.SearchAsync(new ArchiveQuery { EmailIds = archived, AllowedStoreIds = await indexer!.GetAllowedSearchStoresAsync(), IncludeFacts = true, IncludeAttachmentText = false, Max = 500 }, ct) : new List<ArchiveHit>();
        var items = new List<ChatMailItem>(emails.Count);
        foreach (var e in emails)
        {
            var hit = facts.FirstOrDefault(h => h.Id == e.ArchiveId);
            items.Add(new ChatMailItem
            {
                ArchiveId = e.ArchiveId,
                EntryId = e.EntryId,
                StoreId = e.StoreId,
                Subject = e.Subject,
                SenderName = e.Sender,
                Received = e.Received,
                Sent = e.Sent,
                OutsideFilters = e.OutsideFilters,
                LinkReasons = e.LinkReasons,
                Facts = hit?.Facts.ToList() ?? new List<ArchiveFact>(),
                HasAttachments = hit?.Attachments.Count > 0
            });
        }
        return items;
    }

    /// <summary>
    /// The chat question's emails from the archive; null when it cannot answer (no archive, or a sender it does not
    /// know), so Outlook is scanned as before. Emails newer than the indexer's last pass come from the Inbox headers.
    /// </summary>
    private Func<IReadOnlyList<MailFolderRef>>? _chatSearchFoldersSource = null;
    private IReadOnlyList<MailFolderRef> ChatSearchFolders() => _chatSearchFoldersSource?.Invoke() ?? _dataService.GetChatSearchFolders();

    private async Task<List<ChatMailItem>?> FindChatMailInArchiveAsync(EmailSearchQuery query, int latest, bool attachments,
        MailSearchCoverage coverage, int turn, CancellationToken ct)
    {
        var db = CurrentArchive is { } indexer ? await indexer.OpenForSearchAsync() : null;
        if (db == null) return null;

        bool hasText = !string.IsNullOrWhiteSpace(query.Keywords);
        var archiveQuery = new ArchiveQuery
        {
            Text = hasText ? query.Keywords : null,
            FactFilters = query.FactFilters,
            FromUtc = query.StartDate?.ToUniversalTime(),
            ToUtc = query.EndDate?.ToUniversalTime(),
            IncludeAttachmentText = attachments,
            IncludeFacts = true
        };
        if (!string.IsNullOrWhiteSpace(query.Sender))
        {
            // The model may give alternatives («Acme, acmespa.com»).
            var ids = new HashSet<long>();
            foreach (var part in query.Sender!.Split(','))
                if (part.Trim().Length > 0) ids.UnionWith(await db.FindAddressesAsync(part.Trim(), ct: ct));
            if (ids.Count == 0) return null;
            archiveQuery.AddressIds = ids.ToList();
            archiveQuery.Role = ArchivePartyRole.Sender;
        }

        var hits = await SearchArchiveAsync(db, archiveQuery, attachments, coverage, ct, latest);

        var items = new List<ChatMailItem>(hits.Count);
        foreach (var h in hits)
        {
            foreach (var attachment in h.Attachments)
                TrackDocuments(new[] { new DocumentOutcome { Name = attachment.Name, SourceId = attachment.SourceId, EntryId = h.EntryId, StoreId = h.StoreId,
                    EmailSubject = h.Subject, EmailDate = h.ReceivedUtc.ToLocalTime(), Status = DocumentStatus.Reference } });
            items.Add(new ChatMailItem
            {
                ArchiveId = h.Id,
                EntryId = h.EntryId,
                StoreId = h.StoreId,
                Subject = h.Subject,
                MessageId = h.MessageId,
                Copies = h.Copies,
                SenderName = h.SenderName,
                SenderAddress = h.SenderAddress,
                Received = h.ReceivedUtc.ToLocalTime(),
                Sent = h.SentUtc?.ToLocalTime(),
                OutsideFilters = h.OutsideFilters,
                LinkReasons = h.LinkReasons,
                Preview = h.Preview,
                HasAttachments = h.Attachments.Count > 0,
                Facts = h.Facts.ToList()
            });
        }

        // Mailboxes left out of the archive (Le mie cassette) are read from Outlook as before.
        var archived = new HashSet<string>((await db.GetStoreCoverageAsync(ct)).Where(s => !s.Closed && s.Folders > 0).Select(s => s.StoreId),
            StringComparer.OrdinalIgnoreCase);
        var folders = ChatSearchFolders();
        var outside = folders.Where(f => !archived.Contains(f.StoreId)).ToList();
        var known = new HashSet<string>(items.Select(i => i.EntryId), StringComparer.OrdinalIgnoreCase);
        if (outside.Count > 0 && query.FactFilters.Count == 0)
        {
            var other = new MailSearchCoverage();
            items.AddRange((await FindChatMailInOutlookAsync(outside, query, latest, attachments, other, turn, ct)).Where(i => known.Add(i.EntryId)));
            coverage.Add(other);
        }

        // The indexer reads new mail about every 80 s: what arrived since is still only in Outlook.
        var recentFrom = DateTime.Now.AddMinutes(-10);
        var inside = folders.Where(f => archived.Contains(f.StoreId)).ToList();
        if (query.FactFilters.Count == 0 && !hasText && inside.Count > 0 && (query.EndDate == null || query.EndDate >= recentFrom))
        {
            var recentQuery = new EmailSearchQuery
            {
                Sender = query.Sender,
                StartDate = query.StartDate > recentFrom ? query.StartDate : recentFrom,
                EndDate = query.EndDate
            };
            var recent = await ScanMailHeadersAsync(inside, false, recentQuery, new MailSearchCoverage(), ct, turn);
            items.AddRange(recent.Where(r => known.Add(r.EntryId)).Select(FromHeader));
        }
        items = ChatMailDigest.OnePerMessage(items);
        if (latest > 0 && !items.Any(i => i.OutsideFilters)) items = items.OrderByDescending(i => i.Received).Take(latest).ToList();
        return items;
    }

    /// <summary>Without the archive: Inbox and Sent Items headers of the mailboxes, words checked on the originals.</summary>
    private async Task<List<ChatMailItem>> FindChatMailInOutlookAsync(IReadOnlyList<MailFolderRef> folders, EmailSearchQuery query, int latest,
        bool attachments, MailSearchCoverage coverage, int turn, CancellationToken ct)
    {
        bool checkOriginals = !string.IsNullOrWhiteSpace(query.Keywords);
        bool hasSearchTerms = MailSearchTerms.Terms(query.Keywords ?? string.Empty).Count > 0;
        var headerQuery = new EmailSearchQuery
        {
            KeywordsAny = true,
            Sender = query.Sender,
            // «Le ultime mail» with no period: the last week is enough to find them.
            StartDate = query.StartDate ?? (latest > 0 ? DateTime.Today.AddDays(-7) : null),
            EndDate = query.EndDate
        };
        var headers = await ScanMailHeadersAsync(folders, false, headerQuery, coverage, ct, turn);
        if (checkOriginals)
        {
            var candidates = new List<EmailSearchResult>();
            var checkedDocuments = new AnalysisStats();
            var slice = new UiSlice();
            for (int i = 0; i < headers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % 20 == 0) PostProgress(turn, $"Verifica locale di corpi e allegati: {i} di {headers.Count}…", i, headers.Count);
                await slice.YieldIfDueAsync(ct);
                var h = headers[i];
                var body = await ReadMailBodyAsync(h.EntryId, h.StoreId, ct);
                if (body == null) coverage.Unavailable++;
                var source = new AnalysisEmail { Subject = h.Subject, Received = h.ReceivedTime, EntryId = h.EntryId, StoreId = h.StoreId };
                var files = attachments
                    ? await ReadAnalysisAttachmentsAsync(source, true, checkedDocuments, _ => { }, ct)
                    : string.Empty;
                if (!hasSearchTerms || MailSearchTerms.Matches(query.Keywords ?? string.Empty, h.Subject + "\n" + body + files)) candidates.Add(h);
            }
            coverage.UnreadAttachments += checkedDocuments.Documents.Count(d => d.Status != DocumentStatus.Read);
            headers = candidates;
        }
        if (latest > 0) headers = headers.Take(latest).ToList();
        return headers.Select(FromHeader).ToList();
    }

    private static ChatMailItem FromHeader(EmailSearchResult h) => new()
    {
        EntryId = h.EntryId,
        StoreId = h.StoreId,
        Subject = h.Subject,
        MessageId = h.InternetMessageId ?? string.Empty,
        SenderName = h.SenderName,
        SenderAddress = h.SenderEmail,
        Received = h.ReceivedTime,
        Preview = h.Body.Length > 200 ? h.Body.Substring(0, 200) : h.Body
    };

    /// <summary>Emails in scope, newest first (conversation: oldest first, as it reads).</summary>
    private async Task<List<AnalysisEmail>?> ResolveAnalysisEmailsAsync(AnalysisRequest r, MailSearchCoverage coverage, CancellationToken ct, int turn)
    {
        if (r.Kind == "thread")
        {
            if (string.IsNullOrEmpty(r.EntryId)) return null;
            var selected = await _dataService.GetEmailByEntryIdAsync(r.EntryId, r.StoreId, ct);
            if (selected == null) return null;
            var messages = selected.ThreadMessages != null && selected.ThreadMessages.Count > 0 ? selected.ThreadMessages : new[] { selected };
            coverage.Scanned = messages.Count;
            coverage.ConversationRead = messages.Count;
            coverage.ConversationTotal = Math.Max(messages.Count, selected.ConversationTotal);
            return messages.Select(m => new AnalysisEmail
            {
                Subject = m.Subject,
                Sender = SenderOf(m),
                Received = m.ReceivedTime,
                EntryId = m.EntryId,
                StoreId = string.IsNullOrEmpty(m.StoreId) ? selected.StoreId : m.StoreId,
                MessageId = m.MessageId,
                Body = m.Body
            }).ToList();
        }

        if (r.Kind == "archive") return await ResolveArchiveEmailsAsync(r, coverage, ct);
        if (r.Kind == "flow")
        {
            var flowEmails = await ResolveFlowEmailsAsync(r, ct);
            coverage.Scanned = flowEmails.Count;
            return flowEmails;
        }

        bool accounts = r.IsAccounts;
        var folders = accounts ? _dataService.GetStoreFolders(r.Stores.Select(s => s.Key).ToList(), r.WholeMailbox) : r.Folders;
        // A whole mailbox is its root with everything below (trash, junk, drafts and outbox excluded).
        var localQuery = r.ToQuery();
        if (r.Text.Length == 0 && r.Goal.Instruction.Length > 0)
        {
            localQuery.Keywords = r.Goal.Instruction;
            localQuery.KeywordsAny = true;
        }
        bool searchAttachments = r.Attachments && GetActiveProfile() is { } profile && AttachmentContentAllowed(profile);
        string? searchText = !string.IsNullOrWhiteSpace(localQuery.Keywords) ? localQuery.Keywords : null;
        bool hasSearchTerms = MailSearchTerms.Terms(searchText ?? string.Empty).Count > 0;
        if (searchText != null) localQuery.Keywords = null;
        var headers = await ScanMailHeadersAsync(folders, r.Subfolders || (accounts && r.WholeMailbox), localQuery, coverage, ct, turn);
        if (searchText != null)
        {
            var candidates = new List<EmailSearchResult>();
            var inspected = new AnalysisStats();
            var slice = new UiSlice();
            for (int i = 0; i < headers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % 20 == 0) PostProgress(turn, $"Verifica locale dei testi e degli allegati: {i} di {headers.Count}…", i, headers.Count);
                await slice.YieldIfDueAsync(ct);
                var h = headers[i];
                var body = await ReadMailBodyAsync(h.EntryId, h.StoreId, ct);
                if (body == null) coverage.Unavailable++;
                var source = new AnalysisEmail { Subject = h.Subject, Received = h.ReceivedTime, EntryId = h.EntryId, StoreId = h.StoreId };
                var files = searchAttachments
                    ? await ReadAnalysisAttachmentsAsync(source, true, inspected, _ => { }, ct)
                    : string.Empty;
                if (!hasSearchTerms || MailSearchTerms.Matches(searchText, h.Subject + "\n" + body + files)) candidates.Add(h);
            }
            coverage.UnreadAttachments += inspected.Documents.Count(d => d.Status != DocumentStatus.Read);
            headers = candidates;
        }
        return headers
            .Select(h => new AnalysisEmail
            {
                Subject = h.Subject,
                Sender = string.IsNullOrWhiteSpace(h.SenderEmail) || h.SenderEmail.StartsWith("/", StringComparison.Ordinal) ? h.SenderName : $"{h.SenderName} <{h.SenderEmail}>",
                Received = h.ReceivedTime,
                EntryId = h.EntryId,
                StoreId = h.StoreId
            }).ToList();
    }

    private async Task<List<AnalysisEmail>> ResolveFlowEmailsAsync(AnalysisRequest request, CancellationToken ct)
    {
        var visible = request.EntryId.Length > 0 ? _dataService.GetEmailByEntryId(request.EntryId, request.StoreId) : null;
        var flow = await ReadFlowAsync(new ArchiveFlowSeed
        {
            Id = request.ArchiveId,
            EntryId = request.EntryId,
            StoreId = request.StoreId,
            MessageId = visible?.MessageId ?? string.Empty,
            InReplyTo = visible?.InReplyTo ?? string.Empty,
            References = visible?.References ?? string.Empty,
            SentUtc = visible?.SentUtc,
            ConversationId = visible?.ConversationId ?? string.Empty,
            Subject = visible?.Subject ?? string.Empty,
            SenderAddress = visible?.SenderEmail ?? string.Empty,
            ReceivedUtc = visible?.ReceivedTime.ToUniversalTime()
        }, visible, complete: true, ct: ct);
        var bodies = (visible?.ThreadMessages ?? Array.Empty<SelectedEmailInfo>()).Concat(visible == null ? Array.Empty<SelectedEmailInfo>() : new[] { visible })
            .Where(e => e.EntryId.Length > 0).GroupBy(e => e.StoreId + "\n" + e.EntryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Body, StringComparer.OrdinalIgnoreCase);
        return flow.Timeline.Select(e => new AnalysisEmail
        {
            Subject = e.Subject,
            Sender = e.Sender,
            Received = e.ReceivedUtc.ToLocalTime(),
            Sent = e.SentUtc?.ToLocalTime(),
            OutsideFilters = e.OutsideFilters,
            LinkReasons = e.LinkReasons,
            EntryId = e.EntryId,
            StoreId = e.StoreId,
            MessageId = e.MessageId,
            ArchiveId = e.Id,
            ConversationLimited = flow.ConversationLimited,
            Body = bodies.TryGetValue(e.StoreId + "\n" + e.EntryId, out var body) ? body : null
        }).ToList();
    }

    /// <summary>
    /// Whole-archive scope: the emails that match the goal's words best (newest first without words), across
    /// every indexed mailbox. The person filter covers all the addresses of a matching person or company.
    /// </summary>
    private async Task<List<AnalysisEmail>> ResolveArchiveEmailsAsync(AnalysisRequest r, MailSearchCoverage coverage, CancellationToken ct)
    {
        var result = new List<AnalysisEmail>();
        var db = CurrentArchive is { } indexer ? await indexer.OpenForSearchAsync() : null;
        if (db == null) return result;

        var query = new ArchiveQuery
        {
            // The words asked for in the filters; without them, the goal's own words rank the emails.
            Text = r.Text.Length > 0 ? r.Text : r.Goal.Instruction,
            Subject = r.Subject.Length > 0 ? r.Subject : null,
            FromUtc = r.Days > 0 ? DateTime.Today.AddDays(-r.Days).ToUniversalTime() : (DateTime?)null,
            Role = r.PersonRole switch { "sender" => ArchivePartyRole.Sender, "recipient" => ArchivePartyRole.Recipient, _ => ArchivePartyRole.Any },
            IncludeAttachmentText = r.Attachments && _config?.Archive?.IndexAttachmentText == true &&
                GetActiveProfile() is { } attachmentProfile && AttachmentContentAllowed(attachmentProfile),
            FactFilters = r.FactFilters,
            Max = 500
        };
        if (r.Person.Length > 0) query.AddressIds = await db.FindAddressesAsync(r.Person);

        foreach (var h in await SearchArchiveAsync(db, query, r.Attachments, coverage, ct))
        {
            result.Add(new AnalysisEmail
            {
                Subject = h.Subject,
                Sender = string.IsNullOrWhiteSpace(h.SenderAddress) || h.SenderAddress.StartsWith("/", StringComparison.Ordinal) ? h.SenderName : $"{h.SenderName} <{h.SenderAddress}>",
                Received = h.ReceivedUtc.ToLocalTime(),
                Sent = h.SentUtc?.ToLocalTime(),
                OutsideFilters = h.OutsideFilters,
                LinkReasons = h.LinkReasons,
                EntryId = h.EntryId,
                StoreId = h.StoreId,
                ArchiveId = h.Id
            });
        }
        return result;
    }

    /// <summary>
    /// Every archive email matching the query, one per message. With words, the emails whose index is incomplete
    /// are checked on their Outlook original, so a missing index never reads as no match.
    /// </summary>
    private async Task<List<ArchiveHit>> SearchArchiveAsync(ArchiveClient db, ArchiveQuery query, bool attachments,
        MailSearchCoverage coverage, CancellationToken ct, int latest = 0)
    {
        query.AllowedStoreIds = CurrentArchive is { } scopeIndexer
            ? await scopeIndexer.GetAllowedSearchStoresAsync() : new List<string>();
        if (query.FactFilters.Count == 0) query.Text = ArchiveSearchText.Parse(query.Text, query.FactFilters);
        string queryText = query.Text ?? string.Empty;
        async Task<List<ArchiveHit>> ReadPagesAsync()
        {
            var pages = new List<ArchiveHit>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                query.Offset = pages.Count;
                query.Max = 500;
                var page = await db.SearchAsync(query, ct);
                pages.AddRange(page);
                if (page.Count < query.Max) break;
            }
            return pages;
        }
        var hits = await ReadPagesAsync();
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var matched = new HashSet<long>(hits.Select(h => h.Id));
            query.Text = null;
            foreach (var h in await ReadPagesAsync())
            {
                ct.ThrowIfCancellationRequested();
                coverage.Scanned++;
                if (coverage.Scanned % 500 == 0) await Task.Delay(1, ct);
                if (matched.Contains(h.Id)) continue;
                if (!h.SearchIndexPending && !h.TextLimited && h.BodyState == ArchiveBodyState.Indexed &&
                    (!attachments || !h.Attachments.Any(a => a.TextLimited || a.State is not ("indexed" or "reference" or "excluded" or "facts-only")))) continue;
                coverage.IncompleteIndex++;
                var body = await ReadMailBodyAsync(h.EntryId, h.StoreId, ct);
                if (body == null) { coverage.Unavailable++; continue; }
                string searchable = h.Subject + "\n" + body;
                if (attachments && GetActiveProfile() is { } profile && AttachmentContentAllowed(profile))
                {
                    var candidate = new AnalysisEmail
                    {
                        Subject = h.Subject,
                        Received = h.ReceivedUtc.ToLocalTime(),
                        EntryId = h.EntryId,
                        StoreId = h.StoreId
                    };
                    var extracted = new AnalysisStats();
                    searchable += await ReadAnalysisAttachmentsAsync(candidate, true, extracted, _ => { }, ct);
                    coverage.UnreadAttachments += extracted.Documents.Count(d => d.Status != DocumentStatus.Read);
                }
                if (MailSearchTerms.Matches(queryText, searchable))
                {
                    hits.Add(h);
                    matched.Add(h.Id);
                }
            }
        }
        else coverage.Scanned = hits.Count;
        if (latest > 0) hits = ArchiveHit.OnePerMessage(hits, out _).Take(latest).ToList();
        var related = await db.ExpandRelatedAsync(hits.Select(h => h.Id).ToList(), new ArchiveFlowSeed { AllowedStoreIds = query.AllowedStoreIds, IncludeAttachmentText = query.IncludeAttachmentText }, ct);
        coverage.Copies = related.Copies; coverage.MetadataIncomplete |= related.MetadataIncomplete; coverage.FactsIncomplete |= related.FactsIncomplete;
        return related.Hits;
    }

    private void PostProgress(int turn, string message, int done, int total)
    {
        PostStream(turn, new { type = "stream_phase", message, done, total });
    }

    /// <summary>What an analysis found and read before asking the model: reused by Riprova (see <see cref="ReadEvidence"/>).</summary>
    private sealed class AnalysisRead
    {
        public string Key { get; set; } = string.Empty;
        public DateTime ReadUtc { get; set; }
        public List<AnalysisEmail> Found { get; set; } = new();
        public MailSearchCoverage Coverage { get; set; } = new();
        public List<ChatMailItem> Items { get; set; } = new();
        public EvidenceReading Reading { get; set; } = new();
        public List<DocumentOutcome> Documents { get; set; } = new();
        public List<HistoryContextReference> Inputs { get; set; } = new();
        public WebSearchResult? Web { get; set; }
    }

    private AnalysisRead? _analysisRead;

    private async Task ExecuteMailAnalysisAsync(AnalysisRequest request, string raw, bool retry = false)
    {
        if (request.Goal.IsReconstruction)
        {
            await ReconstructPracticeAsync(request);
            return;
        }
        var goal = request.Goal;
        var op = new Dictionary<string, string?> { ["type"] = "mail_analysis", ["request"] = raw };

        // Calendar preset = the action-item scan (pre-filter, duplicates, tick boxes) on the chosen scope.
        if (goal.IsCalendar)
        {
            await ExecuteCalendarScanAsync(op, "Ricerca · " + goal.Label, request.ScopeLabel(), request.Attachments,
                (coverage, ct, turn) => ResolveAnalysisEmailsAsync(request, coverage, ct, turn));
            return;
        }
        // Unanswered sent emails: a local scan of Sent Items, no model involved.
        if (goal.IsFollowUps)
        {
            await ExecuteFollowUpScanAsync(request, op);
            return;
        }

        var cts = BeginOperation();
        var ct = cts.Token;
        int turn = _turn;
        ILlmClient? client = null;
        LlmProfileConfig? activeProfile = null;
        var sw = Stopwatch.StartNew();
        string scopeLabel = request.ScopeLabel();
        PostStream(turn, new { type = "stream_start", kind = "action", action = "Ricerca · " + goal.Label, prompt = goal.Instruction.Length > 0 ? goal.Instruction : null });

        try
        {
            activeProfile = GetActiveProfile();
            if (activeProfile == null)
            {
                PostNotConfigured();
                return;
            }

            // Riprova after a model failure: the emails found and read for this same request are used again.
            string analysisKey = EvidenceKey("analysis", raw, activeProfile);
            var kept = _analysisRead is { } previous && RetryEvidence.Reusable(retry, previous.Key, previous.ReadUtc, analysisKey, DateTime.UtcNow) ? previous : null;
            if (kept != null)
            {
                _sourceReadUtc = kept.ReadUtc;
                foreach (var source in kept.Inputs) TrackMailInput(source.EntryId, source.StoreId, source.MessageId);
                OnlyWorkspaceLog.Logger.Information("Mail analysis retry on the emails already read: {Emails}, web={Web}", kept.Reading.Pack.Count, kept.Web != null);
            }
            PostPhase(turn, kept != null ? "Email già lette per questa richiesta…" : "Ricerca delle email…");
            await Task.Yield();
            var coverage = kept?.Coverage ?? new MailSearchCoverage();
            var found = kept?.Found ?? await ResolveAnalysisEmailsAsync(request, coverage, ct, turn);
            if (found == null)
            {
                PostStream(turn, new
                {
                    type = "stream_error",
                    message = request.EntryId.Length > 0
                    ? "L'email scelta per questa analisi non è più disponibile (spostata o eliminata)."
                    : "Seleziona o apri un'email, poi riprova.",
                    retryable = true,
                    op
                });
                return;
            }
            if (found.Count == 0)
            {
                PostStream(turn, new { type = "stream_chunk", chunk = $"Nessuna candidata trovata ({scopeLabel}). {coverage.Explain()}" });
                PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
                return;
            }

            bool contentAllowed = AttachmentContentAllowed(activeProfile);
            int budget = ProviderCatalog.PromptBudget(activeProfile);
            string goalText = goal.Describe();
            if (request.Kind == "flow") goalText += " Ricostruisci in ordine cronologico tappe, decisioni, punti aperti e sintesi finale. Segnala espressamente le email non disponibili e le lacune di copertura.";
            // The answer's web search runs on the goal while the emails are read.
            var webSearch = request.Kind != "flow" && !goal.IsTable && !MailRequest.IsListOrCount(goal.Instruction)
                ? kept?.Web != null ? ReuseWeb(kept.Web) : StartWebSearch(activeProfile, goalText, turn, ct) : null;

            int more = Math.Max(0, found.Count - MaxAnalysisEmails);
            var emails = found.OrderByDescending(e => e.Received).Take(MaxAnalysisEmails).OrderBy(e => e.Sent ?? e.Received).ToList();
            var items = kept?.Items ?? await AnalysisItemsAsync(emails, ct);
            var stats = new AnalysisStats
            {
                Found = found.Count,
                Oldest = emails.Min(e => e.Received),
                Newest = emails.Max(e => e.Received)
            };
            OnlyWorkspaceLog.Logger.Information("Mail analysis started: {Emails} emails, preset {Preset}, attachments={Attachments}", emails.Count, goal.Preset, request.Attachments);

            // The list first, with no model call: it stays the result if the request is stopped.
            var result = new StringBuilder(ChatMailDigest.Listing(items, more));
            PostStream(turn, new { type = "stream_chunk", chunk = result + "\n" });

            object? table = null;
            string? analysisId = null;
            var reading = new EvidenceReading();
            if (MailRequest.IsListOrCount(goal.Instruction))
                stats.Analyzed = 0;
            else
            {
                PostPhase(turn, "Lettura delle email…");
                var bodies = emails.Where(e => e.Body != null).ToDictionary(e => e.StoreId + "\n" + e.EntryId, e => e.Body!, StringComparer.OrdinalIgnoreCase);
                if (kept != null)
                {
                    reading = kept.Reading;
                    stats.Documents.AddRange(kept.Documents);
                    TrackDocuments(kept.Documents);
                }
                else
                {
                    reading = await ReadEvidenceAsync(items,
                        item => bodies.TryGetValue(item.StoreId + "\n" + item.EntryId, out var b) ? b : null,
                        request.Attachments, contentAllowed, budget, stats, coverage, ExposurePolicy.Of(activeProfile), turn, ct);
                    if (!reading.Stopped && reading.Pack.Count > 0)
                        _analysisRead = new AnalysisRead
                        {
                            Key = analysisKey,
                            ReadUtc = _sourceReadUtc,
                            Found = found,
                            Coverage = coverage,
                            Items = items,
                            Reading = reading,
                            Documents = stats.Documents.ToList(),
                            Inputs = Inputs.Sources.ToList()
                        };
                }
                stats.TooLong.AddRange(reading.TooLong);
                stats.NotExposed.AddRange(reading.NoFacts.Concat(reading.OverBudget));
                stats.Analyzed = reading.Pack.Count;
                stats.Stopped = reading.Stopped;
                var pack = reading.Pack;
                _mailSourcesForTurn = pack.Sources();

                // Interrompi keeps the list as the result; any other cancel drops it.
                if (stats.Stopped && _operations.ResultToken(cts).IsCancellationRequested) ct.ThrowIfCancellationRequested();
                if (stats.Stopped)
                {
                    result.Append(AnalysisFinalization.Interrupted);
                    PostStream(turn, new { type = "stream_chunk", chunk = AnalysisFinalization.Interrupted });
                }
                else if (pack.Count == 0)
                {
                    const string none = "\n\nNessuna email è stata letta.";
                    result.Append(none);
                    PostStream(turn, new { type = "stream_chunk", chunk = none });
                }
                else
                {
                    client = CreateLlmClient(activeProfile, wholeEmails: false);
                    stats.Stopped = await AnalysisFinalization.RunAsync(_operations, cts, result, async token =>
                    {
                        PostPhase(turn, "Scrittura del risultato…");
                        string answerText;
                        if (goal.IsTable)
                        {
                            var watch = Stopwatch.StartNew();
                            string json;
                            using (ObserveAnswer(turn, watch))
                                json = await client.CompleteStructuredAsync(PromptCatalog.CreateMailTablePrompt(goalText, pack.Render()), PromptCatalog.MailTableJsonSchema, token);
                            LogMailAnswer(client, pack, watch.ElapsedMilliseconds);
                            ModelOutput.EnsureComplete(client.LastCompletion);
                            var parsed = MailTable.Parse(json);
                            MailAnswerVerifier.Verify(parsed, pack);
                            stats.Relevant = parsed.Rows.Where(r => r.Source >= 1 && r.Source <= pack.Count).Select(r => r.Source).Distinct().Count();
                            answerText = (parsed.Rows.Count == 0 ? "Nessun dato pertinente trovato nelle email lette."
                                : $"{(parsed.Rows.Count == 1 ? "1 riga" : parsed.Rows.Count + " righe")} per l'obiettivo richiesto. Verifica i valori con i documenti originali prima di usarli.") +
                                "\n\n" + MailAnswerRenderer.Coverage(pack, parsed);
                            if (parsed.Rows.Count > 0) table = AnalysisTableMessage(parsed.Columns, MailAnswerRenderer.TableRows(parsed, pack));
                        }
                        else
                        {
                            var prompt = PromptCatalog.CreateMailAnswerPrompt(goalText, pack.Render());
                            if (webSearch != null)
                            {
                                prompt = await AddWebEvidenceAsync(activeProfile, prompt, webSearch, token);
                                if (_analysisRead?.Key == analysisKey && webSearch.Status == TaskStatus.RanToCompletion && webSearch.Result is { } web) _analysisRead.Web = web;
                            }
                            var answered = await AnswerOnEvidenceAsync(client, prompt, pack, turn, token, goal.Instruction);
                            stats.Relevant = answered.Answer.Points.SelectMany(p => p.Sources.Where(s => !p.InvalidSources.Contains(s))).Distinct().Count();
                            answerText = answered.Text;
                        }
                        result.Append("\n\n").Append(answerText);
                        PostStream(turn, new { type = "stream_chunk", chunk = "\n\n" + answerText });
                        analysisId = Guid.NewGuid().ToString("N");
                        _analysisEvidence = (analysisId, pack);
                    }, chunk => PostStream(turn, new { type = "stream_chunk", chunk }));
                }
            }

            var footer = await AnalysisFooterAsync(request, stats, coverage, emails, reading.Missing.Count, reading.Missing.Take(10).ToList());
            result.Append(footer);
            PostStream(turn, new { type = "stream_chunk", chunk = footer });
            await RecordActionInHistoryAsync(activeProfile, $"Analisi ({goal.Label}): {scopeLabel}", result.ToString(), _operations.ResultToken(cts));

            sw.Stop();
            PostStream(turn, new
            {
                type = "stream_end",
                latencyMs = sw.ElapsedMilliseconds,
                truncated = false,
                op,
                table,
                analysisNotes = analysisId != null,
                analysisId
            });
            OnlyWorkspaceLog.Logger.Information(
                "Mail analysis: {Analyzed}/{Found} emails, {Whole} whole, {Relevant} cited, {Docs} attachments, stopped={Stopped}, {LatencyMs} ms",
                stats.Analyzed, stats.Found, reading.Pack.Whole, stats.Relevant, stats.Documents.Count, stats.Stopped, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Mail analysis failed");
            PostStream(turn, new { type = "stream_error", message = LlmErrorText.Describe(ex, activeProfile), retryable = Retryable(ex), op });
        }
        finally
        {
            DisposeClient(client);
            EndOperation(cts);
        }
    }

    /// <summary>Table of the analysis for the pane, copyable as TSV and saved as CSV with the list separator of the user's locale.</summary>
    private static object AnalysisTableMessage(IReadOnlyList<string> columns, List<string[]> rows)
    {
        var headers = columns.Concat(new[] { "Email" }).ToArray();
        var separator = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator == ";" ? ';' : ',';
        return new
        {
            documentType = "analisi",
            fields = Array.Empty<string[]>(),
            lineHeaders = headers,
            lines = rows,
            tsv = AnalysisTable.ToDelimited(headers, rows, '\t', quote: false),
            csv = AnalysisTable.ToDelimited(headers, rows, separator, quote: true)
        };
    }

    /// <summary>Closing of an analysis: counts, coverage, archive still being built, originals no longer found.</summary>
    private async Task<string> AnalysisFooterAsync(AnalysisRequest request, AnalysisStats stats, MailSearchCoverage coverage,
        IReadOnlyList<AnalysisEmail> emails, int missing, List<string> missingFlowMail)
    {
        var footer = "\n\n---\n" + AnalysisReport.BuildFooter(stats) + "\n" + coverage.Explain();
        if (request.Kind == "archive" && CurrentArchive?.Db is { } resultArchive && (await resultArchive.GetStatsAsync()).BodiesPending is int pendingBodies && pendingBodies > 0)
        {
            footer += $"\nArchivio ancora in costruzione: {pendingBodies} email sono trovate solo per oggetto e mittente, non per testo e destinatari.";
        }
        if (request.Kind == "flow" && emails.Any(e => e.ConversationLimited))
            footer += "\nOutlook ha restituito solo i 20 messaggi più recenti della conversazione visibile. L'archivio può mostrarne altri; la copertura del flusso resta incerta.";
        if (missing > 0)
        {
            if (emails.Any(e => e.ArchiveId > 0) && CurrentArchive is { } missingIndexer) await missingIndexer.ReportMissingAsync();
            footer += request.Kind == "flow"
                ? FlowAnalysisReport.MissingOriginals(missing, missingFlowMail)
                : missing == 1
                    ? "\n1 email non è più dove l'archivio l'aveva trovata (spostata o eliminata) e non è stata letta: l'archivio la cerca di nuovo nei prossimi momenti liberi."
                    : $"\n{missing} email non sono più dove l'archivio le aveva trovate (spostate o eliminate) e non sono state lette: l'archivio le cerca di nuovo nei prossimi momenti liberi.";
        }
        return footer;
    }

    /// <summary>
    /// Text of the readable attachments, appended to the email text; every attachment gets an outcome in
    /// <paramref name="stats"/> (inline images of signatures are ignored).
    /// </summary>
    private async Task<string> ReadAnalysisAttachmentsAsync(AnalysisEmail email, bool contentAllowed, AnalysisStats stats, Action<string> progress, CancellationToken ct)
    {
        TrackMailInput(email.EntryId, email.StoreId);
        var stored = await ArchivedOriginalAsync(email.EntryId, email.StoreId, ct);
        bool archivedWhole = false;
        if (stored != null && contentAllowed)
        {
            // An attachment the archive did not read whole (scan, password, pending) must not fail the whole request:
            // each one is then read from Outlook with its own outcome, and only whole texts reach the model.
            try { OnlyWorkspace.Infrastructure.Archive.ArchiveAttachmentReader.WholeText(stored); archivedWhole = true; }
            catch (InvalidOperationException ex) { OnlyWorkspaceLog.Logger.Information("Archived attachments not whole, read from Outlook: {Reason}", ex.Message); }
        }
        if (archivedWhole)
        {
            var text = new StringBuilder();
            foreach (var attachment in stored!.Attachments.Where(a => a.State is "indexed" or "reference"))
            {
                stats.Documents.Add(new DocumentOutcome
                {
                    Name = attachment.Name,
                    SourceId = attachment.SourceId,
                    EmailSubject = email.Subject,
                    EmailDate = email.Received,
                    EntryId = email.EntryId,
                    StoreId = email.StoreId,
                    Status = attachment.State == "reference" ? DocumentStatus.Reference : DocumentStatus.Read
                });
                text.Append("\n\n--- Allegato: ").Append(attachment.Name).Append(" ---\n")
                    .Append(attachment.State == "reference" ? attachment.ReferenceDescription() : attachment.Text);
            }
            return text.ToString();
        }
        var copies = await _dataService.CopyAttachmentsAsync(email.EntryId, email.StoreId,
            a => contentAllowed && DocumentOutcome.CanRead(a) && !a.IsInline && !a.IsPecReceiptData && AttachmentTextFormats.HasTextReader(a.Extension), ct);
        if (copies == null)
        {
            stats.Documents.Add(new DocumentOutcome
            {
                Name = "Allegati non accessibili",
                EmailSubject = email.Subject,
                EmailDate = email.Received,
                EntryId = email.EntryId,
                StoreId = email.StoreId,
                Status = DocumentStatus.Error
            });
            return string.Empty;
        }

        try
        {
            var sb = new StringBuilder();
            // Two documents read at a time (one helper process each, low priority); texts join in the email's order.
            var extractions = new List<(string Name, DocumentOutcome Outcome, Task<OnlyWorkspace.Core.Security.AttachmentExtraction> Text)>();
            using var slots = new SemaphoreSlim(2);
            foreach (var attachment in copies.All.Where(a => !a.IsInline && !a.IsPecReceiptData))
            {
                var outcome = new DocumentOutcome { Name = attachment.Name, EmailSubject = email.Subject, EmailDate = email.Received };
                stats.Documents.Add(outcome);
                outcome.EntryId = email.EntryId; outcome.StoreId = email.StoreId;
                outcome.SourceId = (copies.All.ToList().IndexOf(attachment) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                TrackDocuments(new[] { outcome });

                var path = copies.Saved.Where(s => s.Key == attachment).Select(s => s.Value).FirstOrDefault();
                if (!contentAllowed || path == null || !DocumentOutcome.CanRead(attachment))
                {
                    outcome.Status = DocumentOutcome.NotCopied(attachment, AttachmentTextFormats.HasTextReader(attachment.Extension), contentAllowed);
                    continue;
                }
                extractions.Add((attachment.Name, outcome, ExtractInSlotAsync(path, attachment.Name)));
            }
            // Every helper ends before the copies are deleted, also when one fails or the request is stopped.
            try { await Task.WhenAll(extractions.Select(e => e.Text)); }
            catch (Exception) when (extractions.All(e => e.Text.IsCompleted)) { }
            foreach (var (name, outcome, text) in extractions)
            {
                var extraction = await text;
                outcome.ApplyExtraction(extraction);
                if (outcome.WasRead)
                {
                    sb.Append("\n\n[Allegato: ").Append(name).Append("]\n").Append(extraction.Text);
                }
                else if (extraction.Error != null)
                {
                    OnlyWorkspaceLog.Logger.Warning("Attachment extraction failed: {Error}", extraction.Error);
                }
            }

            async Task<OnlyWorkspace.Core.Security.AttachmentExtraction> ExtractInSlotAsync(string path, string name)
            {
                await slots.WaitAsync(ct);
                try
                {
                    progress(name);
                    return await AttachmentTextExtractor.ExtractAsync(path, AttachmentTextExtractor.WholeDocumentChars, AnalysisExtractorTimeout, ct, OcrEnabled);
                }
                finally { slots.Release(); }
            }

            // External profile without consent: the model still learns which documents the email carries.
            if (!contentAllowed)
            {
                var names = copies.All.Where(a => !a.IsInline && !a.IsPecReceiptData).Select(a => a.Name).ToList();
                if (names.Count > 0) sb.Append("\n\n[Allegati, contenuto non incluso con provider esterni: ").Append(string.Join(", ", names)).Append(']');
            }
            return sb.ToString();
        }
        finally
        {
            DeleteScanDirectory(copies.Directory);
        }
    }

    /// <summary>Question typed after an analysis: one request on the same emails, citations checked again.</summary>
    private async Task ExecuteAnalysisQuestionAsync(string question, string? analysisId)
    {
        if (analysisId == null || _analysisEvidence is not { } kept || kept.Id != analysisId)
        {
            Toast("L'analisi originale non è più disponibile. Avvia una nuova ricerca.", "error");
            return;
        }
        var pack = kept.Pack;

        var cts = BeginOperation();
        var ct = cts.Token;
        int turn = _turn;
        ILlmClient? client = null;
        LlmProfileConfig? activeProfile = null;
        var op = new Dictionary<string, string?> { ["type"] = "analysis_question", ["question"] = question, ["analysisId"] = analysisId };
        var sw = Stopwatch.StartNew();
        PostStream(turn, new { type = "stream_start", kind = "action", action = "Domanda sull'analisi", prompt = question });

        try
        {
            activeProfile = GetActiveProfile();
            if (activeProfile == null)
            {
                PostNotConfigured();
                return;
            }

            client = CreateLlmClient(activeProfile, wholeEmails: false);
            _mailSourcesForTurn = pack.Sources();
            PostPhase(turn, "Generazione in corso…");
            var answerPrompt = await AddWebEvidenceAsync(activeProfile, PromptCatalog.CreateMailAnswerPrompt(question, pack.Render()), turn, ct);
            var answered = await AnswerOnEvidenceAsync(client, answerPrompt, pack, turn, ct, question);
            PostStream(turn, new { type = "stream_chunk", chunk = answered.Text });

            await RecordActionInHistoryAsync(activeProfile, "Domanda sull'analisi: " + Truncate(question, 60), answered.Text, ct);
            sw.Stop();
            PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op, analysisNotes = true, analysisId });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Analysis question failed");
            PostStream(turn, new { type = "stream_error", message = LlmErrorText.Describe(ex, activeProfile), retryable = Retryable(ex), op });
        }
        finally
        {
            DisposeClient(client);
            EndOperation(cts);
        }
    }
}
