using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

public partial class TaskPaneCoordinator
{
    private readonly System.Collections.Generic.List<HistoryContextReference> _inputSourcesForTurn = new();
    private void TrackMailInput(string entryId, string storeId, string messageId = "")
    {
        if (entryId.Length == 0) return;
        var existing = _inputSourcesForTurn.FirstOrDefault(s => s.EntryId == entryId && s.StoreId == storeId);
        if (existing != null) { if (messageId.Length > 0) existing.MessageId = messageId; return; }
        if (messageId.Length == 0) messageId = _dataService.GetEmailMessageId(entryId, storeId);
        var source = new HistoryContextReference { EntryId = entryId, StoreId = storeId,
            OutlookProfile = _dataService.GetProfileName(), MessageId = messageId, CapturedUtc = _sourceReadUtc };
        _inputSourcesForTurn.Add(source);
        _currentConversation.ContextReferences.Add(source);
    }

    private void PreserveInputSources()
    {
        foreach (var source in _mailSourcesForTurn)
        {
            source.OutlookProfile = _dataService.GetProfileName();
            source.CapturedUtc = _sourceReadUtc;
        }
        foreach (var source in _inputSourcesForTurn)
            if (!_currentConversation.ContextReferences.Contains(source)) _currentConversation.ContextReferences.Add(source);
    }

    // The archived original last read for a body: its attachments are read right after, and the same original (body
    // and attachment texts, which can be large) crossed the pipe twice.
    private (string EntryId, string StoreId, ArchiveBody? Original)? _lastOriginal;

    private async Task<ArchiveBody?> ArchivedOriginalAsync(string entryId, string storeId, CancellationToken ct)
    {
        if (_lastOriginal is { } last && last.EntryId == entryId && last.StoreId == storeId) return last.Original;
        var db = ArchiveIndexer.Current?.Db;
        var stored = db == null ? null : await db.GetOriginalAsync(entryId, storeId, ct);
        _lastOriginal = (entryId, storeId, stored);
        return stored;
    }

    private async Task<string?> ReadMailBodyAsync(string entryId, string storeId, CancellationToken ct = default)
    {
        var stored = await ArchivedOriginalAsync(entryId, storeId, ct);
        TrackMailInput(entryId, storeId, stored?.MessageId ?? "");
        var text = stored?.Text ?? _dataService.GetEmailBody(entryId, storeId);
        if (text?.Length > OnlyWorkspace.Infrastructure.Security.AttachmentTextExtractor.WholeDocumentChars)
            throw new MailBodyTooLongException();
        return text;
    }

    private void TrackSelectedInputs(SelectedEmailInfo email)
    {
        TrackMailInput(email.EntryId, email.StoreId, email.MessageId);
        foreach (var prior in email.ThreadMessages ?? Array.Empty<SelectedEmailInfo>())
            TrackMailInput(prior.EntryId, prior.StoreId, prior.MessageId);
    }
}
