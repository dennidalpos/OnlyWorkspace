using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

public partial class TaskPaneCoordinator
{
    private Task ExecuteEmailFollowUpAsync(EmailRef? target) => new EmailFollowUpPresenter(this).ExecuteAsync(target);

    /// <summary>Connects the single-email follow-up workflow to the active Outlook pane and its history.</summary>
    private sealed class EmailFollowUpPresenter
    {
        private readonly TaskPaneCoordinator _owner;

        public EmailFollowUpPresenter(TaskPaneCoordinator owner) => _owner = owner;

        public async Task ExecuteAsync(EmailRef? target)
        {
            var cts = _owner.BeginOperation();
            var ct = cts.Token;
            var turn = _owner._turn;
            var sw = Stopwatch.StartNew();
            var op = new Dictionary<string, string?> { ["type"] = "email_followup" };
            LlmProfileConfig? profile = null;
            _owner.PostStream(turn, new { type = "stream_start", kind = "action", action = "Follow-up", prompt = (string?)null });
            try
            {
                // The ribbon callback returns before the email is read: Outlook times the add-in's callbacks.
                await Task.Yield();
                var selected = target == null ? await _owner._dataService.GetCurrentSelectedEmailAsync(_owner._contextWindow, ct)
                    : await _owner._dataService.GetEmailByEntryIdAsync(target.EntryId, target.StoreId, ct);
                if (selected == null)
                {
                    _owner.PostStream(turn, new { type = "stream_error", message = target == null ? "Seleziona o apri un'email, poi riprova." : "L'email originale non è più disponibile (spostata o eliminata).", retryable = target == null, op });
                    return;
                }
                op["entryId"] = selected.EntryId;
                op["storeId"] = selected.StoreId;
                _owner.PostEmailContext(turn, selected);
                profile = _owner.GetActiveProfile();
                if (profile == null) { _owner.PostNotConfigured(); return; }
                var workflow = new FollowUpCoordinator
                {
                    ResolveEmail = reference =>
                    {
                        _owner.TrackMailInput(reference?.EntryId ?? selected.EntryId, reference?.StoreId ?? selected.StoreId);
                        if (reference == null || reference.EntryId == selected.EntryId && reference.StoreId == selected.StoreId)
                            return FollowUpMailOf(selected, true);
                        // ExecuteAsync preserves the Outlook UI context across the archive await.
                        var original = _owner._dataService.GetEmailByEntryId(reference.EntryId, reference.StoreId);
                        if (original != null) _owner.TrackSelectedInputs(original);
                        return original == null ? null : FollowUpMailOf(original, false);
                    },
                    GetArchivedBodyAsync = async (reference, token) =>
                    {
                        var archive = _owner.CurrentArchive?.Db;
                        var stored = archive == null ? null : await archive.GetOriginalAsync(reference.EntryId, reference.StoreId ?? "", token);
                        if (stored == null) return null;
                        _owner.TrackMailInput(reference.EntryId, reference.StoreId ?? "", stored.MessageId);
                        return ArchiveAttachmentReader.WholeText(stored, false);
                    },
                    SearchArchiveAsync = SearchFollowUpArchiveAsync,

                    CreateClient = () => _owner.CreateLlmClient(profile),
                    SearchWebAsync = async (messages, token) =>
                        await _owner.SearchForActionAsync(profile, messages, turn, token) ?? throw new OperationCanceledException(token),
                    OnChunk = chunk => _owner.PostStream(turn, new { type = "stream_chunk", chunk }),
                    SaveHistoryAsync = async result =>
                    {
                        _owner._webForTurn.Take(turn, result.WebSources);
                        if (result.WebSkipped) _owner._webForTurn.MarkSkipped(turn);
                        _owner._mailSourcesForTurn = result.MailSources;
                        await _owner.RecordActionInHistoryAsync(profile, "Follow-up: " + result.Email!.Subject, result.Answer, ct, _owner.HistoryRefOf(selected), "Follow-up");
                    }
                };
                await workflow.ExecuteAsync(
                    target == null ? null : new FollowUpMailRef(target.EntryId, target.StoreId),
                    profile, _owner._contextBuilder, _owner.WebForTurn(profile), ct);
                _owner.PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _owner.PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Error(ex, "Email follow-up failed");
                _owner.PostStream(turn, new { type = "stream_error", message = LlmErrorText.Describe(ex, profile), retryable = Retryable(ex), op });
            }
            finally { _owner.EndOperation(cts); }
        }

        private static FollowUpMail FollowUpMailOf(SelectedEmailInfo email, bool includeThread) => new()
        {
            EntryId = email.EntryId,
            StoreId = email.StoreId,
            Subject = email.Subject,
            Body = email.Body,
            Sender = SenderOf(email),
            SenderEmail = email.SenderEmail,
            ReceivedTime = email.ReceivedTime,
            ThreadMessages = includeThread ? (email.ThreadMessages ?? Array.Empty<SelectedEmailInfo>())
                .Select(prior => FollowUpMailOf(prior, false)).ToList() : Array.Empty<FollowUpMail>()
        };

        private async Task<FollowUpArchiveResult?> SearchFollowUpArchiveAsync(FollowUpMail email, CancellationToken ct)
        {
            var archive = _owner.CurrentArchive is { } indexer ? await indexer.OpenForSearchAsync() : null;
            if (archive == null) return null;

            var party = await archive.CounterpartyOfEmailAsync(email.EntryId, email.StoreId, ct);
            if (!party.HasValue && !string.IsNullOrWhiteSpace(email.SenderEmail)) party = await archive.CounterpartyOfAsync(email.SenderEmail, ct);
            var subject = FollowUpMatcher.NormalizeSubject(email.Subject);
            var references = FollowUpMatcher.References(email.Subject + "\n" + email.Body);
            var candidates = new List<ArchiveHit>();
            if (subject.Length >= 4) candidates.AddRange(await archive.SearchAsync(new ArchiveQuery
            {
                CounterpartyId = party,
                Subject = subject,
                ToUtc = email.ReceivedTime.ToUniversalTime().AddSeconds(-1),
                Max = 60
            }, ct));
            foreach (var reference in references.Take(2))
                candidates.AddRange(await archive.SearchAsync(new ArchiveQuery
                {
                    CounterpartyId = party,
                    Text = reference,
                    ToUtc = email.ReceivedTime.ToUniversalTime().AddSeconds(-1),
                    Max = 30
                }, ct));
            return new FollowUpArchiveResult { SameParty = party.HasValue, Candidates = candidates };
        }
    }
}
