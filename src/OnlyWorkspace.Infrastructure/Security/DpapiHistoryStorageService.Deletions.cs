using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OnlyWorkspace.Infrastructure.Security;

public sealed partial class DpapiHistoryStorageService
{
    private string ConversationRevocationsPath => Path.Combine(_storageDirectory, "deleted-conversations.dat");

    // Called only while holding the history mutex. Missing means an unmodified older history;
    // unreadable or malformed data must fail saves/deletions rather than discard revocations.
    private HashSet<string> ReadConversationRevocations()
    {
        if (Directory.Exists(ConversationRevocationsPath)) throw new InvalidDataException("Invalid conversation deletion ledger path.");
        if (!File.Exists(ConversationRevocationsPath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(ConversationRevocationsPath), Entropy, DataProtectionScope.CurrentUser);
        var ids = JsonSerializer.Deserialize<string[]>(bytes) ?? throw new InvalidDataException("Invalid conversation deletion ledger.");
        foreach (var id in ids)
            if (id == null || !ValidId.IsMatch(id)) throw new InvalidDataException("Invalid revoked conversation ID.");
        return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
    }

    private void RevokeConversations(IEnumerable<string> ids)
    {
        var revoked = ReadConversationRevocations();
        bool changed = false;
        foreach (var id in ids)
        {
            if (!ValidId.IsMatch(id)) throw new InvalidDataException("Invalid conversation file name.");
            changed |= revoked.Add(id.ToUpperInvariant());
        }
        if (!changed) return;
        var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(revoked), Entropy, DataProtectionScope.CurrentUser);
        Configuration.AtomicFile.WriteAllBytes(ConversationRevocationsPath, cipher);
    }
}
