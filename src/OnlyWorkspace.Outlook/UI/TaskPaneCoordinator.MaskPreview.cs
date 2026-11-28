using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>#249: the pane shows the masked attachment text and waits for the user before an Esteso request leaves.</summary>
public partial class TaskPaneCoordinator
{
    private const int MaxPreviewEdits = 200;
    private readonly object _previewLock = new();
    private (string Id, TaskCompletionSource<MaskPreviewDecision> Source)? _pendingPreview;

    /// <summary>
    /// The dispatcher's preview hook. The message goes straight to the page, never through the replay record: it carries
    /// the original values, which must not enter the saved page history. A stop, a new request or the end of the pane declines.
    /// </summary>
    private Func<MaskPreview, CancellationToken, Task<MaskPreviewDecision>> MaskPreviewHandler(SynchronizationContext ui) => async (preview, ct) =>
    {
        var source = new TaskCompletionSource<MaskPreviewDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        string id = Guid.NewGuid().ToString("N");
        lock (_previewLock)
        {
            _pendingPreview?.Source.TrySetResult(MaskPreviewDecision.Cancel());
            _pendingPreview = (id, source);
        }
        using (ct.Register(() => source.TrySetResult(MaskPreviewDecision.Cancel())))
        {
            try
            {
                if (_isDisposed) return MaskPreviewDecision.Cancel();
                ui.Send(_ => _control.PostMessage(new
                {
                    type = "mask_preview",
                    id,
                    texts = preview.Texts,
                    masks = preview.Masks.Select(m => new { placeholder = m.Placeholder, category = m.Category, original = m.Original }).ToArray()
                }), null);
                return await source.Task.ConfigureAwait(true);
            }
            finally
            {
                lock (_previewLock) { if (_pendingPreview?.Id == id) _pendingPreview = null; }
                if (!_isDisposed) ui.Send(_ => _control.PostMessage(new { type = "mask_preview_close", id }), null);
            }
        }
    };

    private void ResolveMaskPreview(JsonElement message)
    {
        string? id = GetString(message, "id");
        TaskCompletionSource<MaskPreviewDecision>? source;
        lock (_previewLock) source = _pendingPreview is { } pending && pending.Id == id ? pending.Source : null;
        if (source == null) return;
        bool confirmed = message.TryGetProperty("confirmed", out var c) && c.ValueKind == JsonValueKind.True;
        source.TrySetResult(confirmed
            ? new MaskPreviewDecision { Confirmed = true, Unmask = Strings(message, "unmask"), AddValues = Strings(message, "add") }
            : MaskPreviewDecision.Cancel());
    }

    private static List<string> Strings(JsonElement message, string property) =>
        message.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty).Take(MaxPreviewEdits).ToList()
            : new List<string>();

    private void DeclinePendingMaskPreview()
    {
        lock (_previewLock) _pendingPreview?.Source.TrySetResult(MaskPreviewDecision.Cancel());
    }
}
