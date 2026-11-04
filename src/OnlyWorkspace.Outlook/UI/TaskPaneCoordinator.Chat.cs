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

    private async Task ExecuteChatAsync(string userInstruction, EmailRef? target = null, bool retry = false)
    {
        if (string.IsNullOrWhiteSpace(userInstruction)) return;

        var initialProfile = GetActiveProfile();
        if (initialProfile != null && _currentConversation.Messages.Count > 0 &&
            !ConversationPolicy.Matches(_currentConversation, initialProfile))
        {
            StartNewChat();
            Toast("Nuova conversazione: destinazione o permessi dei dati cambiati. Lo storico precedente resta consultabile.");
        }

        var cts = BeginOperation();
        var ct = cts.Token;
        int turn = _turn;
        var op = new Dictionary<string, string?> { ["type"] = "chat", ["prompt"] = userInstruction };
        ILlmClient? client = null;
        HistoryChatMessage? pendingUserMessage = null;
        LlmProfileConfig? activeProfile = null;

        var sw = Stopwatch.StartNew();
        PostStream(turn, new { type = "stream_start", kind = "chat", action = "Chat", prompt = userInstruction });

        try
        {
            // The email being written: the chat helps with its text and searches the mail only when asked to (Danny, 20/10/2026).
            var draft = _dataService.GetComposeDraft(_contextWindow);
            // A question about the files attached to the email being written reads them; a request to write with them uses them as a source.
            bool askDraftAttachments = draft != null && ChatWriting.AsksAboutAttachments(userInstruction) && !ChatWriting.AsksMailSearch(userInstruction);
            bool answerOnDraft = askDraftAttachments && (ChatWriting.AsksAttachmentAnalysis(userInstruction) || !ChatWriting.IsWriteRequest(userInstruction));
            bool writing = draft != null ? !ChatWriting.AsksMailSearch(userInstruction) && !answerOnDraft : ChatWriting.IsWriteRequest(userInstruction);
            bool requestsSelectedEmail = draft == null && IntentClassifier.RefersToSelectedEmail(userInstruction);
            EmailRef? boundEmail = null;
            if (requestsSelectedEmail)
            {
                var header = target == null ? _dataService.GetSelectedEmailHeader(_contextWindow) : null;
                boundEmail = target ?? (header == null ? null : new EmailRef(header.EntryId, header.StoreId));
                if (boundEmail == null)
                {
                    PostStream(turn, new { type = "stream_error", message = "Seleziona o apri un'email, poi riprova.", retryable = true, op });
                    return;
                }
                op["entryId"] = boundEmail.EntryId;
                op["storeId"] = boundEmail.StoreId;
            }

            activeProfile = GetActiveProfile();
            if (activeProfile == null)
            {
                PostNotConfigured();
                return;
            }

            pendingUserMessage = BeginConversationTurn(activeProfile, userInstruction, draft == null ? null : DraftRefOf(draft));
            int budget = ProviderCatalog.PromptBudget(activeProfile);
            // Prior turns take budget/3 below: data gets the rest so the whole prompt stays within budget (0 = no limit).
            int dataBudget = _currentConversation.Messages.Count > 1 ? budget - budget / 3 : budget;
            int maxSearchItems = _config?.Limits?.MaxSearchItems ?? 50;

            var timings = new ChatTimings();
            PostPhase(turn, "Comprensione della richiesta…");
            UserIntentResult intent;
            SelectedEmailInfo? selectedEmail = null;
            string evidenceKey = EvidenceKey("chat", userInstruction, activeProfile);
            var kept = draft == null && !requestsSelectedEmail ? ReusableEvidence(retry, evidenceKey) : null;
            var local = requestsSelectedEmail || writing ? null : LocalChatIntent.TryParse(userInstruction, DateTime.Now);
            var localFacts = new List<ArchiveFactFilter>();
            string factRemainder = requestsSelectedEmail || writing ? userInstruction : ArchiveSearchText.ParseChat(userInstruction, localFacts);
            timings.Local = local != null || localFacts.Count > 0;
            if (requestsSelectedEmail)
            {
                selectedEmail = await _dataService.GetEmailByEntryIdAsync(boundEmail!.EntryId, boundEmail.StoreId, ct);
                if (selectedEmail == null)
                {
                    PostStream(turn, new { type = "stream_error", message = "L'email originale non è più disponibile (spostata o eliminata).", retryable = false, op });
                    return;
                }
                PostEmailContext(turn, selectedEmail);
                pendingUserMessage.Email = HistoryRefOf(selectedEmail);
                intent = new UserIntentResult { Target = writing ? "write" : "general" };
            }
            else if (writing) intent = new UserIntentResult { Target = "write" };
            else if (answerOnDraft) intent = new UserIntentResult { Target = "general" };
            else if (kept != null) intent = new UserIntentResult { Target = "email" };
            else if (localFacts.Count > 0)
            {
                intent = IntentClassifier.ClassifyLocal("Trova le email " + factRemainder, DateTime.Now);
                intent.Target = "email";
            }
            else if (local != null) intent = new UserIntentResult { Target = "email", Sender = local.Sender, StartDate = local.From, EndDate = local.To };
            else
            {
                timings.Lap();
                intent = IntentClassifier.ClassifyLocal(userInstruction, DateTime.Now);
                timings.Local = true;
                timings.Intent = timings.Lap();
            }

            ct.ThrowIfCancellationRequested();
            writing = intent.Target == "write";
            IEnumerable<ChatMessage>? messages = null;
            string? notice = null;
            string listing = string.Empty;
            MailEvidencePack? mailPack = null;
            Task<WebSearchResult?>? webSearch = null;

            if (writing)
            {
                if (draft != null)
                {
                    PostDraftContext(turn, draft);
                    // The whole draft or nothing, like an email: a cut quoted thread would mislead the reply.
                    _contextBuilder.EnsureFits(draft.Body, budget);
                    if (draft.Selection.Length > 0) op["selection"] = draft.Selection;
                }
                PostPhase(turn, "Scrittura del testo…");
                if (selectedEmail != null) EnsureWholeEmails(activeProfile);
                var draftFiles = askDraftAttachments ? await DraftAttachmentsAsync(activeProfile, turn, ct) : EmailAttachments.None;
                if (draftFiles.Text.Length > 0) EnsureWholeEmails(activeProfile);
                if (askDraftAttachments) notice = AttachmentNoticeFor(activeProfile, draftFiles);
                var reference = selectedEmail == null ? null
                    : _contextBuilder.EnsureFits(EmailHeaderOf(selectedEmail, withSender: true) + _contextBuilder.CleanEmailBody(selectedEmail.Body ?? string.Empty, selectedEmail.Subject), budget);
                messages = PromptCatalog.CreateWritePrompt(userInstruction, draft, reference, draftFiles.Text.Length > 0 ? _contextBuilder.EnsureFits(draftFiles.Text, budget) : null);
            }
            else if (intent.Target == "calendar")
                messages = ChatCalendarPrompt(activeProfile, userInstruction, intent, dataBudget, maxSearchItems, turn, out notice);
            else if (intent.Target == "contacts")
                messages = ChatContactsPrompt(activeProfile, userInstruction, intent, dataBudget, maxSearchItems, turn, out notice);
            else if (intent.Target == "email")
            {
                // The general classifier has no mailbox filter either: do not label global results as PEC mail.
                if (LocalChatIntent.NamesPecMailbox(userInstruction))
                {
                    PostStream(turn, new { type = "stream_chunk", chunk = "Non posso applicare un filtro alla cassetta PEC in questa ricerca. Scegli la cassetta o la cartella PEC in Cerca e analizza, poi ripeti la richiesta." });
                    PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
                    return;
                }
                if (kept != null)
                {
                    // Riprova after a model failure: the emails and the web results already read for this question.
                    PostPhase(turn, "Email già lette per questa richiesta…");
                    _sourceReadUtc = kept.ReadUtc;
                    foreach (var source in kept.Inputs) TrackMailInput(source.EntryId, source.StoreId, source.MessageId);
                    notice = kept.Notice;
                    listing = kept.Listing;
                    mailPack = kept.Pack;
                    foreach (var document in kept.Documents) document.IncludedInRequest = false;
                    TrackDocuments(kept.Documents);
                    if (kept.Web != null) webSearch = ReuseWeb(kept.Web);
                    else if (ChatWebRequest.Wanted(intent.Target, userInstruction)) webSearch = StartWebSearch(activeProfile, userInstruction, turn, ct);
                    messages = PromptCatalog.CreateMailAnswerPrompt(userInstruction, mailPack.Render());
                    OnlyWorkspaceLog.Logger.Information("Chat retry on the evidence already read: {Emails} emails, web={Web}", mailPack.Count, kept.Web != null);
                }
                else
                {
                    // Web asked for on the mail: searched from the question while the emails are found and read.
                    if (ChatWebRequest.Wanted(intent.Target, userInstruction)) webSearch = StartWebSearch(activeProfile, userInstruction, turn, ct);
                    PostPhase(turn, "Ricerca nella posta di tutti gli account…");
                    var mailQuery = local?.ToEmailSearch() ?? intent.ToEmailSearch(userInstruction);
                    if (localFacts.Count > 0) { mailQuery.FactFilters = localFacts; mailQuery.Keywords = factRemainder; }
                    // «Quante mail…», «elenca le email…»: the local list is the answer, no model request.
                    bool listOnly = MailRequest.IsListOrCount(userInstruction);
                    var evidence = await BuildChatMailEvidenceAsync(activeProfile, userInstruction,
                        mailQuery, local?.Latest == true ? local.Count : 0, dataBudget, listOnly, turn, timings, ct);
                    if (listOnly)
                    {
                        PostStream(turn, new { type = "stream_chunk", chunk = evidence.Notice });
                        _currentConversation.Messages.Add(new HistoryChatMessage { Role = "assistant", Content = evidence.Listing, Timestamp = DateTimeOffset.UtcNow });
                        pendingUserMessage = null;
                        await SaveConversationAsync();
                        PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
                        return;
                    }
                    if (evidence.LocalAnswer != null)
                    {
                        string answer = evidence.LocalAnswer + evidence.Notice;
                        PostStream(turn, new { type = "stream_chunk", chunk = answer });
                        _currentConversation.Messages.Add(new HistoryChatMessage { Role = "assistant", Content = evidence.Listing + "\n" + answer, Timestamp = DateTimeOffset.UtcNow });
                        pendingUserMessage = null;
                        await SaveConversationAsync();
                        OnlyWorkspaceLog.Logger.Information("Chat answered from local facts: {Emails} emails", evidence.Found);
                        PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
                        return;
                    }
                    if (evidence.Pack.Count == 0)
                    {
                        PostStream(turn, new { type = "stream_chunk", chunk = (evidence.Listing.Length == 0 ? "Nessuna email trovata." : "Nessuna email leggibile.") + evidence.Notice });
                        PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
                        return;
                    }
                    notice = evidence.Notice;
                    listing = evidence.Listing;
                    mailPack = evidence.Pack;
                    messages = PromptCatalog.CreateMailAnswerPrompt(userInstruction, mailPack.Render());
                    _readEvidence = new ReadEvidence { Key = evidenceKey, ReadUtc = _sourceReadUtc, Pack = mailPack, Notice = notice, Listing = listing, Inputs = _inputSourcesForTurn.ToList(), Documents = DocumentSnapshot() };
                }
            }

            if (messages == null && selectedEmail != null && activeProfile.IsExternal && CloudFactsRequest.IsFactRequest(userInstruction))
            {
                var source = new ChatMailItem { EntryId = selectedEmail.EntryId, StoreId = selectedEmail.StoreId,
                    Subject = selectedEmail.Subject, Received = selectedEmail.ReceivedTime, Facts = ArchiveFactExtractor.Extract(selectedEmail.Body ?? "") };
                mailPack = MailEvidencePack.Chronological(new[] { new MailEvidence { Mail = source } });
                messages = Array.Empty<ChatMessage>();
            }
            if (messages == null)
            {
                // Only an explicit reference supplies the selected email and its attachments.
                var attachments = selectedEmail != null ? await ChatAttachmentsAsync(selectedEmail, activeProfile, turn, ct)
                    : answerOnDraft ? await DraftAttachmentsAsync(activeProfile, turn, ct) : EmailAttachments.None;
                notice = AttachmentNoticeFor(activeProfile, attachments);
                if (selectedEmail != null && (!string.IsNullOrWhiteSpace(selectedEmail.Body) || attachments.Text.Length > 0)) EnsureWholeEmails(activeProfile);
                if (answerOnDraft)
                {
                    if (attachments.Text.Length > 0) EnsureWholeEmails(activeProfile);
                    PostDraftContext(turn, draft!);
                    messages = DraftAnswerPrompt(userInstruction, draft!, attachments, budget);
                }
                else messages = ChatEmailPrompt(userInstruction, selectedEmail, attachments.Text, budget);
            }

            client = CreateLlmClient(activeProfile, wholeEmails: false);
            PostPhase(turn, "Generazione risposta…");
            var responseBuilder = new StringBuilder();
            IEnumerable<ChatMessage> answerMessages = WithPriorTurns(messages, userInstruction, budget);
            timings.Lap();
            if (webSearch != null)
            {
                answerMessages = await AddWebEvidenceAsync(activeProfile, answerMessages, webSearch, ct);
                if (_readEvidence?.Key == evidenceKey && webSearch.Status == TaskStatus.RanToCompletion && webSearch.Result is { } found) _readEvidence.Web = found;
            }
            else if (ChatWebRequest.Wanted(intent.Target, userInstruction))
                answerMessages = await AddWebEvidenceAsync(activeProfile, answerMessages, turn, ct);
            timings.Web = timings.Lap();
            if (mailPack != null)
            {
                PrepareDocuments(mailPack);
                // One structured request on the emails [Mn]; amounts, dates and references checked on the cited emails.
                var answered = await AnswerOnEvidenceAsync(client, answerMessages, mailPack, turn, ct, userInstruction);
                timings.FirstToken = timings.Lap();
                responseBuilder.Append(answered.Text);
                _mailSourcesForTurn = mailPack.Sources();
                PostStream(turn, new { type = "stream_chunk", chunk = answered.Text });
            }
            else
            {
                await foreach (var chunk in client.StreamChatAsync(answerMessages, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    if (responseBuilder.Length == 0) timings.FirstToken = timings.Lap();
                    responseBuilder.Append(chunk);
                    PostStream(turn, new { type = "stream_chunk", chunk });
                }
            }

            ct.ThrowIfCancellationRequested();

            _currentConversation.Messages.Add(new HistoryChatMessage
            {
                Role = "assistant",
                Content = listing.Length > 0 ? listing + "\n" + responseBuilder : responseBuilder.ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                WebSources = WebCitations.Used(responseBuilder.ToString(), _webForTurn.Sources),
                MailSources = new List<HistoryContextReference>(_mailSourcesForTurn),
                Documents = DocumentSnapshot()
            });
            pendingUserMessage = null;
            // Shown, not stored: it must not come back to the model as part of its own answer.
            if (notice != null) PostStream(turn, new { type = "stream_chunk", chunk = notice });
            await SaveConversationAsync();

            sw.Stop();
            var tokenUsage = client.LastTokenUsage;
            if (tokenUsage != null)
            {
                OnlyWorkspaceLog.Logger.Information("Chat token usage: Prompt={PromptTokens}, Output={OutputTokens}, Truncated={Truncated}",
                    tokenUsage.PromptTokens, tokenUsage.CompletionTokens, client.LastCompletion?.IsTruncated == true);
            }

            PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = client.LastCompletion?.IsTruncated == true, op, writing = WritingTargets(writing, draft) });
            OnlyWorkspaceLog.Logger.Information("Chat turn completed in {LatencyMs} ms", sw.ElapsedMilliseconds);
            OnlyWorkspaceLog.Logger.Information(
                "Chat phases: write={Write} local={Local} archive={Archive} intent={Intent} search={Search} read={Read} web={Web} answer={FirstToken} ms; emails={Emails} facts={Facts} whole={Originals}",
                writing, timings.Local, timings.Archive, timings.Intent, timings.Search, timings.Read, timings.Web, timings.FirstToken,
                timings.Emails, timings.Facts, timings.Originals);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Chat execution failed");
            PostStream(turn, new { type = "stream_error", message = LlmErrorText.Describe(ex, activeProfile), retryable = Retryable(ex), op });
        }
        finally
        {
            // A failed/cancelled turn must not leave a dangling user message: retry would duplicate it.
            if (pendingUserMessage != null)
            {
                _currentConversation.Messages.Remove(pendingUserMessage);
            }
            DisposeClient(client);
            EndOperation(cts);
        }
    }

    /// <summary>The user's message joins the conversation; another profile starts a new conversation.</summary>
    private HistoryChatMessage BeginConversationTurn(LlmProfileConfig activeProfile, string userInstruction, HistoryContextReference? email = null)
    {
        if (!ConversationPolicy.Matches(_currentConversation, activeProfile))
        {
            if (_currentConversation.Messages.Count > 0) Toast(ConversationPolicy.ChangedMessage);
            _currentConversation = ConversationPolicy.Create(activeProfile, Truncate(userInstruction, 40));
        }
        else if (_currentConversation.Messages.Count == 0)
        {
            _currentConversation.Title = Truncate(userInstruction, 40);
        }

        var message = new HistoryChatMessage
        {
            Role = "user",
            Content = userInstruction,
            Timestamp = DateTimeOffset.UtcNow,
            Email = email
        };
        _currentConversation.Messages.Add(message);
        return message;
    }

    private IEnumerable<ChatMessage> ChatCalendarPrompt(LlmProfileConfig profile, string userInstruction, UserIntentResult intent, int dataBudget, int maxSearchItems, int turn, out string? notice)
    {
        EnsureWholeEmails(profile);
        PostPhase(turn, "Ricerca nell'agenda…");
        var calQuery = new CalendarQuery
        {
            StartDate = intent.StartDate ?? DateTime.Today,
            EndDate = intent.EndDate is { } end ? end.TimeOfDay == TimeSpan.Zero ? end.Date.AddDays(1) : end.AddSeconds(1) : DateTime.Today.AddDays(7),
            MaxResults = maxSearchItems
        };
        var appts = _dataService.GetAppointments(calQuery);
        foreach (var a in appts)
        {
            _currentConversation.ContextReferences.Add(new HistoryContextReference
            {
                EntryId = a.EntryId,
                Subject = a.Subject,
                ItemType = "calendar"
            });
        }
        PostPhase(turn, $"Trovati {appts.Count} appuntamenti. Generazione risposta…");
        var contextData = _contextBuilder.FormatCalendarContext(appts, dataBudget, out int shown);
        notice = LeftOutNotice(shown, appts.Count, "appuntamenti");
        return PromptCatalog.CreateAnswerPrompt(userInstruction, contextData, "agenda");
    }

    private IEnumerable<ChatMessage> ChatContactsPrompt(LlmProfileConfig profile, string userInstruction, UserIntentResult intent, int dataBudget, int maxSearchItems, int turn, out string? notice)
    {
        EnsureWholeEmails(profile);
        PostPhase(turn, "Ricerca nei contatti…");
        var contactQuery = new ContactQuery
        {
            SearchTerm = intent.PersonName ?? intent.Keywords ?? userInstruction,
            MaxResults = maxSearchItems
        };
        var contacts = _dataService.GetContacts(contactQuery);
        foreach (var c in contacts)
        {
            _currentConversation.ContextReferences.Add(new HistoryContextReference
            {
                EntryId = c.EntryId,
                Subject = c.FullName,
                ItemType = "contact"
            });
        }
        PostPhase(turn, $"Trovati {contacts.Count} contatti. Generazione risposta…");
        var contextData = _contextBuilder.FormatContactsContext(contacts, dataBudget, out int shown);
        notice = LeftOutNotice(shown, contacts.Count, "contatti");
        return PromptCatalog.CreateAnswerPrompt(userInstruction, contextData, "contatti");
    }

    /// <summary>A question on the selected email (with its readable attachments), or a plain question.</summary>
    /// <summary>A question about the email being written: what is written so far and the text of its attachments, within the budget.</summary>
    private IEnumerable<ChatMessage> DraftAnswerPrompt(string userInstruction, ComposeDraft draft, EmailAttachments attachments, int budget)
    {
        var text = new StringBuilder();
        text.Append("Email in scrittura\nOggetto: ").Append(string.IsNullOrWhiteSpace(draft.Subject) ? "(vuoto)" : draft.Subject).Append('\n');
        if (!string.IsNullOrWhiteSpace(draft.To)) text.Append("A: ").Append(draft.To).Append('\n');
        if (!string.IsNullOrWhiteSpace(draft.OwnText)) text.Append("\n").Append(draft.OwnText).Append('\n');
        text.Append(attachments.Text);
        return PromptCatalog.CreateAnswerPrompt(userInstruction, _contextBuilder.EnsureFits(text.ToString(), budget), "email");
    }

    private IEnumerable<ChatMessage> ChatEmailPrompt(string userInstruction, SelectedEmailInfo? selectedEmail, string attachments, int budget)
    {
        if (selectedEmail == null || string.IsNullOrWhiteSpace(selectedEmail.Body) && attachments.Length == 0)
            return PromptCatalog.CreateChatPrompt(userInstruction);

        var cleanBody = EmailHeaderOf(selectedEmail, withSender: true) + _contextBuilder.CleanEmailBody(selectedEmail.Body ?? string.Empty, selectedEmail.Subject) + attachments;
        // The email comes first: it gets the whole budget and the prior turns what is left of it.
        var preparedBody = _contextBuilder.EnsureFits(cleanBody, budget);
        _currentConversation.ContextReferences.Add(new HistoryContextReference
        {
            EntryId = selectedEmail.EntryId,
            StoreId = selectedEmail.StoreId,
            MessageId = selectedEmail.MessageId, OutlookProfile = _dataService.GetProfileName(), CapturedUtc = _sourceReadUtc,
            Subject = selectedEmail.Subject,
            ItemType = "email"
        });
        // A question about the email, not a reply draft.
        return PromptCatalog.CreateAnswerPrompt(userInstruction, preparedBody, "email");
    }

    /// <summary>
    /// System message, prior turns from the newest back within budget/3 and what the data left free (a long
    /// history loses its oldest turns), then the question with its data.
    /// </summary>
    private List<ChatMessage> WithPriorTurns(IEnumerable<ChatMessage> messages, string userInstruction, int budget)
    {
        var messagesList = new List<ChatMessage>();
        var sysMsg = messages.FirstOrDefault(m => m.Role == "system");
        if (sysMsg != null) messagesList.Add(sysMsg);

        int dataChars = messages.LastOrDefault(m => m.Role == "user")?.Content.Length ?? 0;
        int historyRoom = Math.Min(ContextBuilder.MaxChars(budget / 3), Math.Max(0, ContextBuilder.MaxChars(budget) - dataChars));
        var priorTurns = new List<ChatMessage>();
        var active = GetActiveProfile();
        bool compatible = active != null && ConversationPolicy.Matches(_currentConversation, active);
        for (int i = compatible ? _currentConversation.Messages.Count - 2 : -1; i >= 0; i--)
        {
            var prior = _currentConversation.Messages[i];
            bool fromAssistant = string.Equals(prior.Role, "assistant", StringComparison.OrdinalIgnoreCase);
            if (!fromAssistant && !string.Equals(prior.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
            // The email tables already on screen do not go back to an external provider with every turn.
            string permitted = prior.ModelContent ?? prior.Content;
            string content = fromAssistant && active!.IsExternal ? ChatMailDigest.WithoutTables(permitted) : permitted;
            if (content.Length == 0) continue;
            if (content.Length > historyRoom) break;
            historyRoom -= content.Length;
            priorTurns.Add(fromAssistant ? ChatMessage.Assistant(content) : ChatMessage.User(content));
        }
        priorTurns.Reverse();
        // Providers expect the conversation to open with a user turn.
        if (priorTurns.Count > 0 && priorTurns[0].Role == "assistant") priorTurns.RemoveAt(0);
        messagesList.AddRange(priorTurns);

        messagesList.Add(messages.LastOrDefault(m => m.Role == "user") ?? ChatMessage.User(userInstruction));
        return messagesList;
    }

    private async Task SaveConversationAsync()
    {
        PreserveInputSources();
        if (_config?.History?.Enabled != true) return;
        try
        {
            await _historyStorage.SaveConversationAsync(_currentConversation);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to save conversation history");
        }
    }
}
