using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>Mail inputs read during one turn, one entry per message; COM-free.</summary>
internal sealed class MailInputTracker
{
    private readonly List<HistoryContextReference> _sources = new();
    private readonly Func<string, string, string> _messageIdOf;
    private readonly Func<string> _profileName;
    private readonly Func<DateTime> _capturedUtc;
    private readonly Action<HistoryContextReference> _added;

    public MailInputTracker(Func<string, string, string> messageIdOf, Func<string> profileName, Func<DateTime> capturedUtc, Action<HistoryContextReference> added)
    {
        _messageIdOf = messageIdOf;
        _profileName = profileName;
        _capturedUtc = capturedUtc;
        _added = added;
    }

    public IReadOnlyList<HistoryContextReference> Sources => _sources;

    public void Clear() => _sources.Clear();

    public void Track(string entryId, string storeId, string messageId = "")
    {
        if (entryId.Length == 0) return;
        var existing = _sources.FirstOrDefault(s => s.EntryId == entryId && s.StoreId == storeId);
        if (existing != null) { if (messageId.Length > 0) existing.MessageId = messageId; return; }
        if (messageId.Length == 0) messageId = _messageIdOf(entryId, storeId);
        var source = new HistoryContextReference { EntryId = entryId, StoreId = storeId,
            OutlookProfile = _profileName(), MessageId = messageId, CapturedUtc = _capturedUtc() };
        _sources.Add(source);
        _added(source);
    }
}
