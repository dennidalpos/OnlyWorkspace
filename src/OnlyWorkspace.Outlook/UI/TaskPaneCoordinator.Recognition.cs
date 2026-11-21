using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Prompts;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Infrastructure.Privacy;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

// The local recognizer layer (#247, #289): created on first use, analysing an email in the background as it opens.
public partial class TaskPaneCoordinator
{
    private PiiRecognizerClient? _client;
    private CachingNerRecognizer? _ner;
    private CancellationTokenSource? _preAnalysis;
    private string? _preAnalysedEntryId;

    /// <summary>Null when the profile does not ask for the layer; otherwise a recognizer that blocks the request when the setup lacks the model.</summary>
    private INerRecognizer? RecognizerFor(LlmProfileConfig profile)
    {
        if (!profile.LocalRecognition) return null;
        if (_ner != null) return _ner;
        var directory = PiiRecognizerClient.InstalledModelDirectory();
        _client = directory == null ? null : PiiRecognizerClient.CreateInstalled(directory);
        return _ner = new CachingNerRecognizer(_client != null ? _client : new MissingRecognizer());
    }

    /// <summary>Email text as the actions send it: the thread with its notice, or the cleaned body.</summary>
    private string CleanBodyOf(SelectedEmailInfo email, bool isThread, string conversationNotice)
    {
        if (!isThread) return _contextBuilder.CleanEmailBody(email.Body, email.Subject);
        var sbThread = new StringBuilder();
        if (conversationNotice.Length > 0) sbThread.AppendLine("[" + conversationNotice.Trim() + "]").AppendLine();
        for (int i = 0; i < email.ThreadMessages!.Count; i++)
        {
            var msg = email.ThreadMessages[i];
            var cleanedPart = _contextBuilder.CleanEmailBody(msg.Body, msg.Subject);
            if (!string.IsNullOrWhiteSpace(cleanedPart))
            {
                sbThread.AppendLine($"[Messaggio #{i + 1} - {msg.SenderName} ({msg.ReceivedTime:dd/MM/yyyy HH:mm})]:");
                sbThread.AppendLine(cleanedPart);
                sbThread.AppendLine();
            }
        }
        return sbThread.ToString().Trim();
    }

    /// <summary>
    /// Opening an email warms the recognizer cache with the text the Sintesi would send, so the masking that follows
    /// finds every block ready. Best effort: any failure only leaves the cache empty, the request itself still blocks.
    /// </summary>
    private void PreAnalyzeOpenedEmail(string? entryId, string? storeId)
    {
        var profile = GetActiveProfile();
        if (string.IsNullOrEmpty(entryId) || profile == null || !profile.LocalRecognition || RecognizerFor(profile) is not CachingNerRecognizer ner) return;
        if (entryId == _preAnalysedEntryId) return;
        _preAnalysedEntryId = entryId;
        _preAnalysis?.Cancel();
        var cts = _preAnalysis = new CancellationTokenSource();
        _ = PreAnalyzeAsync(ner, profile, entryId!, storeId, cts);
    }

    private async Task PreAnalyzeAsync(CachingNerRecognizer ner, LlmProfileConfig profile, string entryId, string? storeId, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            var email = await _dataService.GetEmailByEntryIdAsync(entryId, storeId, ct);
            if (email == null) return;
            bool isThread = email.ThreadMessages != null && email.ThreadMessages.Count > 1;
            string body = CleanBodyOf(email, isThread, isThread ? ConversationNotice(email) : string.Empty);
            if (string.IsNullOrWhiteSpace(body)) return;
            body = EmailHeaderOf(email, withSender: !isThread) + body;
            var messages = PromptCatalog.CreateSummaryPrompt(_contextBuilder.EnsureFits(body, ProviderCatalog.PromptBudget(profile)));
            var texts = new System.Collections.Generic.List<string>();
            foreach (var message in messages) texts.Add(message.Content);
            await Task.Run(() => { foreach (var text in texts) ner.PreAnalyze(text, ct); }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Forgotten, so the next selection of the same email tries again.
            if (_preAnalysedEntryId == entryId) _preAnalysedEntryId = null;
            OnlyWorkspaceLog.Logger.Information("Pre-analysis of the opened email skipped: {Reason}", ex.Message);
        }
    }

    private void DisposeRecognizer()
    {
        _preAnalysis?.Cancel();
        _client?.Dispose();
        _client = null;
        _ner = null;
    }

    private sealed class MissingRecognizer : INerRecognizer
    {
        public string ModelRevision => throw new DataEgressBlockedException("Riconoscimento locale non installato: nessun dato inviato.");
        public System.Collections.Generic.IReadOnlyList<NerSpan> Recognize(string block, TimeSpan budget, CancellationToken token) =>
            throw new DataEgressBlockedException("Riconoscimento locale non installato: nessun dato inviato.");
    }
}
