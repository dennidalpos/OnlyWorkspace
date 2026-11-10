using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

public partial class TaskPaneCoordinator
{
    private int _flowRequestVersion;

    /// <summary>The selected email, or the one named by the pane; null when neither is there.</summary>
    private ArchiveFlowSeed? FlowSeed(long archiveId, string? entryId, string? storeId, out SelectedEmailInfo? visible)
    {
        if (archiveId == 0 && string.IsNullOrEmpty(entryId) && _dataService?.CanActOnSelectedEmail(_contextWindow) != true)
        {
            visible = null;
            return null;
        }
        visible = archiveId == 0 && string.IsNullOrEmpty(entryId)
            ? _dataService?.GetCurrentSelectedEmail(_contextWindow)
            : !string.IsNullOrEmpty(entryId) ? _dataService?.GetEmailByEntryId(entryId!, storeId) : null;
        if (archiveId == 0 && visible == null && string.IsNullOrEmpty(entryId)) return null;
        return new ArchiveFlowSeed
        {
            Id = string.IsNullOrEmpty(entryId) ? archiveId : 0,
            EntryId = visible?.EntryId ?? entryId ?? string.Empty,
            StoreId = visible?.StoreId ?? storeId ?? string.Empty,
            MessageId = visible?.MessageId ?? string.Empty,
            InReplyTo = visible?.InReplyTo ?? string.Empty,
            References = visible?.References ?? string.Empty,
            SentUtc = visible?.SentUtc,
            ConversationId = visible?.ConversationId ?? string.Empty,
            Subject = visible?.Subject ?? string.Empty,
            SenderAddress = visible?.SenderEmail ?? string.Empty,
            ReceivedUtc = visible?.ReceivedTime.ToUniversalTime()
        };
    }

    private async Task ReconstructPracticeAsync(AnalysisRequest request)
    {
        int version = Interlocked.Increment(ref _flowRequestVersion);
        try
        {
            var seed = FlowSeed(request.ArchiveId, request.EntryId, request.StoreId, out var visible);
            if (FlowSeedAvailability.BeforeRead(seed) is { } noSeed) throw new ReconstructionUnavailableException(noSeed);
            var flow = await ReadFlowAsync(seed!, visible, localReconstruction: true);
            if (_isDisposed || version != Volatile.Read(ref _flowRequestVersion)) return;
            if (FlowSeedAvailability.AfterRead(seed!, visible != null, flow) is { } gone) throw new ReconstructionUnavailableException(gone);
            var coverage = CurrentArchive?.Db is { } coverageDb
                // Possible links too: without their mailbox the pane labelled them «Cassetta non inventariata».
                ? (await coverageDb.GetStoreCoverageAsync()).Where(c => flow.Timeline.Concat(flow.PossibleLinks).Any(m => string.Equals(m.StoreId, c.StoreId, StringComparison.OrdinalIgnoreCase))).ToList()
                : new List<ArchiveStoreCoverage>();
            if (_isDisposed || version != Volatile.Read(ref _flowRequestVersion)) return;
            _control.PostMessage(new
            {
                type = "analysis_reconstruction",
                requestId = request.RequestId,
                seed = new { id = seed!.Id, entryId = seed.EntryId, storeId = seed.StoreId, subject = visible?.Subject ?? flow.Timeline.FirstOrDefault(m => m.Id == seed.Id || m.Copies.Any(c => c.StoreId == seed.StoreId && c.EntryId == seed.EntryId))?.Subject ?? seed.Subject },
                items = flow.Timeline.Select(FlowItem).ToList(),
                possible = flow.PossibleLinks.Select(FlowItem).ToList(),
                coverage,
                metadataIncomplete = flow.MetadataIncomplete,
                factsIncomplete = flow.FactsIncomplete,
                limitReached = flow.LimitReached,
                conversationLimited = flow.ConversationLimited,
                outlookConnected = true
            });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to reconstruct correspondence chain");
            if (version == Volatile.Read(ref _flowRequestVersion) && !_isDisposed)
                _control.PostMessage(new
                {
                    type = "analysis_reconstruction",
                    requestId = request.RequestId,
                    items = Array.Empty<object>(),
                    possible = Array.Empty<object>(),
                    coverage = Array.Empty<object>(),
                    refused = ex is FlowRefusedException || ex is ReconstructionUnavailableException
                        ? ex.Message : "La pratica non può essere ricostruita. Riprova; se il problema continua, consulta il log."
                });
        }
    }

    private async Task<ArchiveFlow> ReadFlowAsync(ArchiveFlowSeed seed, SelectedEmailInfo? visible = null,
        bool complete = false, CancellationToken ct = default, bool localReconstruction = false)
    {
        var indexer = CurrentArchive;
        var db = indexer == null ? null : await indexer.OpenForSearchAsync();
        var allowed = indexer == null ? new List<string>() : await indexer.GetAllowedSearchStoresAsync();
        var chosen = allowed == null ? null : new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        if (db == null || chosen?.Count == 0) throw new FlowRefusedException(true);
        if (seed.StoreId.Length > 0 && chosen != null && !chosen.Contains(seed.StoreId))
            throw new FlowRefusedException(chosen.Count == 0);
        seed.AllowedStoreIds = allowed;
        seed.IncludeAttachmentText = _config?.Archive?.IndexAttachmentText == true && (localReconstruction || GetActiveProfile() is { } flowProfile && AttachmentContentAllowed(flowProfile));
        var flow = await db.GetFlowAsync(seed, complete ? int.MaxValue - 1 : 20000, ct);
        ct.ThrowIfCancellationRequested();
        if (chosen != null)
        {
            if (seed.Id > 0 && flow.Timeline.Any(e => e.Id == seed.Id && !chosen.Contains(e.StoreId)))
                throw new FlowRefusedException(chosen.Count == 0);
            flow.Timeline.RemoveAll(e => !chosen.Contains(e.StoreId));
            flow.PossibleLinks.RemoveAll(e => !chosen.Contains(e.StoreId));
        }
        flow.ConversationLimited = visible?.ConversationLimited == true;
        if (visible != null)
        {
            var visibleItems = (visible.ThreadMessages ?? Array.Empty<SelectedEmailInfo>()).Concat(new[] { visible });
            var known = new HashSet<string>(flow.Timeline.Select(e => e.StoreId + "\n" + e.EntryId), StringComparer.OrdinalIgnoreCase);
            foreach (var item in visibleItems)
            {
                if (item.EntryId.Length == 0 || item.StoreId.Length == 0 || chosen != null && !chosen.Contains(item.StoreId)) continue;
                if (!known.Add(item.StoreId + "\n" + item.EntryId)) continue;
                flow.Timeline.Add(new ArchiveFlowMail
                {
                    EntryId = item.EntryId,
                    StoreId = item.StoreId,
                    MessageId = item.MessageId,
                    References = item.References,
                    SentUtc = item.SentUtc,
                    InReplyTo = item.InReplyTo,
                    ConversationId = item.ConversationId,
                    Subject = item.Subject,
                    Sender = item.SenderName,
                    ReceivedUtc = item.ReceivedTime.ToUniversalTime(),
                    VisibleOnly = true,
                    BodyState = ArchiveBodyState.Pending
                });
                var added = flow.Timeline[flow.Timeline.Count - 1];
                var attachments = _dataService.CopyAttachments(item.EntryId, item.StoreId, _ => false);
                if (attachments != null) added.Attachments.AddRange(attachments.All.Select(a => a.Name));
            }
        }
        flow.Timeline = ArchiveFlowRules.Consolidate(flow.Timeline, seed.StoreId, seed.EntryId);
        ArchiveDb.LinkFlowParents(flow.Timeline);
        if (!complete && flow.Timeline.Count >= 20000) flow.LimitReached = true;
        return flow;
    }

    /// <summary>The seed lies outside the archive scope; an empty scope means the archive is off (deny-all).</summary>
    internal sealed class FlowRefusedException : InvalidOperationException
    {
        public FlowRefusedException(bool archiveOff) : base(archiveOff
            ? "Archivio spento o non disponibile: la pratica non può essere ricostruita."
            : "Questa cassetta è esclusa dall'archivio: la pratica non può essere ricostruita.")
        { ArchiveOff = archiveOff; }

        public bool ArchiveOff { get; }
    }

    private sealed class ReconstructionUnavailableException : InvalidOperationException
    {
        public ReconstructionUnavailableException(string message) : base(message) { }
    }

    private static object FlowItem(ArchiveFlowMail mail) => new
    {
        id = mail.Id,
        entryId = mail.EntryId,
        storeId = mail.StoreId,
        subject = mail.Subject,
        sender = mail.Sender,
        date = (mail.SentUtc ?? mail.ReceivedUtc).ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
        bodyState = mail.BodyState.ToString().ToLowerInvariant(),
        visibleOnly = mail.VisibleOnly,
        parentEntryId = mail.ParentEntryId,
        parentStoreId = mail.ParentStoreId,
        attachments = mail.Attachments,
        receivedDate = mail.ReceivedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
        copies = mail.Copies.Select(c => new { id = c.Id, entryId = c.EntryId, storeId = c.StoreId, folderPath = c.FolderPath }),
        linkReasons = mail.LinkReasons,
        linkFacts = mail.LinkFacts.Select(f => new { kind = f.Kind.ToString(), value = f.Value, source = f.SourceName, context = f.Context }),
        outsideFilters = mail.OutsideFilters
    };
}
