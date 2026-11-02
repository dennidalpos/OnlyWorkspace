using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OnlyWorkspace.Core.Agents;

namespace OnlyWorkspace.Outlook.UI;

public partial class TaskPaneCoordinator
{
    private readonly List<DocumentOutcome> _documentsForTurn = new();

    private void TrackDocuments(IEnumerable<DocumentOutcome> documents)
    {
        foreach (var document in documents)
        {
            if (_documentsForTurn.Contains(document)) continue;
            var reference = _documentsForTurn.FirstOrDefault(d =>
                document.SourceId.Length > 0 && d.SourceId == document.SourceId &&
                d.EntryId == document.EntryId && d.StoreId == document.StoreId);
            if (reference != null && !ReferenceEquals(reference, document))
            {
                if (document.Status == DocumentStatus.Reference) continue;
                _documentsForTurn.Remove(reference);
            }
            _documentsForTurn.Add(document);
        }
    }
    private List<DocumentOutcome> DocumentSnapshot() =>
        JsonSerializer.Deserialize<List<DocumentOutcome>>(JsonSerializer.Serialize(_documentsForTurn))!;

    private void PrepareDocuments(MailEvidencePack pack)
    {
        foreach (var d in _documentsForTurn)
            d.PreparedForRequest = d.WasRead && pack.Items.Any(e => e.Whole && e.Mail.EntryId == d.EntryId && e.Mail.StoreId == d.StoreId);
    }
    private void DocumentsDispatched()
    {
        foreach (var d in _documentsForTurn) if (d.PreparedForRequest && d.WasRead) d.IncludedInRequest = true;
    }
}
