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
    private MailInputTracker? _inputs;
    private MailInputTracker Inputs => _inputs ??= new MailInputTracker(
        (entryId, storeId) => _dataService.GetEmailMessageId(entryId, storeId), _dataService.GetProfileName,
        () => _sourceReadUtc, source => _currentConversation.ContextReferences.Add(source));
    private void TrackMailInput(string entryId, string storeId, string messageId = "") => Inputs.Track(entryId, storeId, messageId);

    private void PreserveInputSources()
    {
        foreach (var source in _mailSourcesForTurn)
        {
            source.OutlookProfile = _dataService.GetProfileName();
            source.CapturedUtc = _sourceReadUtc;
        }
        foreach (var source in Inputs.Sources)
            if (!_currentConversation.ContextReferences.Contains(source)) _currentConversation.ContextReferences.Add(source);
    }

    private readonly ArchivedOriginalCache _originals = new();

    private Task<ArchiveBody?> ArchivedOriginalAsync(string entryId, string storeId, CancellationToken ct) =>
        _originals.GetAsync(entryId, storeId, () => CurrentArchive?.Db?.GetOriginalAsync(entryId, storeId, ct));

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
