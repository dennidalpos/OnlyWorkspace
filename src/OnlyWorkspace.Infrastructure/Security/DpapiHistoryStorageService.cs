using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Logging;

#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace OnlyWorkspace.Infrastructure.Security;

#if NET5_0_OR_GREATER
[SupportedOSPlatform("windows")]
#endif
public sealed partial class DpapiHistoryStorageService : IHistoryStorageService, IHistorySourceInvalidation
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OnlyWorkspace-UserHistory-Entropy-v1");
    private readonly string _storageDirectory;

    public DpapiHistoryStorageService(string? storageDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
        {
            _storageDirectory = OnlyWorkspace.Core.AppPaths.HistoryDirectory;
        }
        else
        {
            _storageDirectory = storageDirectory!;
        }
    }

    /// <summary>
    /// The conversation is serialized on the calling thread (the pane keeps changing it); encryption, the history mutex
    /// and the file run on the thread pool, not on Outlook's UI thread.
    /// </summary>
    public Task SaveConversationAsync(ConversationHistory conversation)
    {
        if (conversation == null) throw new ArgumentNullException(nameof(conversation));
        if (string.IsNullOrWhiteSpace(conversation.Id)) throw new ArgumentException("Conversation ID must not be empty.", nameof(conversation));

        if (conversation.UpdatedAt == default)
        {
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
        }
        // A reopened conversation keeps its load-time UpdatedAt: the newest message is its last activity.
        foreach (var message in conversation.Messages)
        {
            if (message.Timestamp > conversation.UpdatedAt) conversation.UpdatedAt = message.Timestamp;
        }
        var filePath = GetFilePath(conversation.Id) ?? throw new ArgumentException("Conversation ID is not a valid file name.", nameof(conversation));
        var conversationId = conversation.Id;
        var sources = EmailSources(conversation);
        var plainBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(conversation));

        // Saves run one after another in the order asked: an older copy never overwrites a newer one.
        lock (_saveGate)
        {
            var save = _saves.ContinueWith(_ => WithHistoryLock(() =>
            {
                if (ReadConversationRevocations().Contains(conversationId))
                    throw new InvalidOperationException("La conversazione è stata eliminata dallo storico.");
                if (!IsValidCore(sources, ReadLedger())) throw new InvalidOperationException("La conversazione usa un'email eliminata dall'archivio.");
                Directory.CreateDirectory(_storageDirectory);
                var cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                Configuration.AtomicFile.WriteAllBytes(filePath, cipherBytes);
                return true;
            }), System.Threading.CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _saves = save;
            return save;
        }
    }

    private readonly object _saveGate = new();
    private Task _saves = Task.CompletedTask;

    public Task<ConversationHistory?> LoadConversationAsync(string id)
        => Task.Run(() => WithHistoryLock(() => LoadConversationCore(id, ReadLedger())));

    private ConversationHistory? LoadConversationCore(string id, Dictionary<string, long> ledger)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var filePath = GetFilePath(id);
        if (filePath == null || !File.Exists(filePath)) return null;

        try
        {
            var cipherBytes = File.ReadAllBytes(filePath);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            var result = JsonSerializer.Deserialize<ConversationHistory>(Encoding.UTF8.GetString(plainBytes));
            return result != null && !IsValidCore(EmailSources(result), ledger) ? null : result;
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to load/decrypt conversation {ConversationId}", id);
            return null;
        }
    }

    // Storico opened on the pane: each summary costs a DPAPI decryption of a whole conversation; a file unchanged
    // since the last listing (and an unchanged deletion ledger) is not decrypted again.
    private readonly object _summaryGate = new();
    private Dictionary<string, (long Length, DateTime WrittenUtc, ConversationSummary? Summary)> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private (long Length, DateTime WrittenUtc) _ledgerStamp;

    private static (long Length, DateTime WrittenUtc) Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
    }

    public Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync() => Task.Run(() =>
    {
        if (!Directory.Exists(_storageDirectory)) return (IReadOnlyList<ConversationSummary>)new List<ConversationSummary>();
        return WithHistoryLock(() =>
        {
            lock (_summaryGate)
            {
                var ledgerStamp = Stamp(LedgerPath);
                if (ledgerStamp != _ledgerStamp)
                {
                    _summaries.Clear();
                    _ledgerStamp = ledgerStamp;
                }
                Dictionary<string, long>? ledger = null;
                var next = new Dictionary<string, (long, DateTime, ConversationSummary?)>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(_storageDirectory, "*.bin"))
                {
                    var (length, written) = Stamp(file);
                    if (_summaries.TryGetValue(file, out var known) && known.Length == length && known.WrittenUtc == written)
                    {
                        next[file] = known;
                        continue;
                    }
                    ledger ??= ReadLedger();
                    next[file] = (length, written, LoadConversationCore(Path.GetFileNameWithoutExtension(file), ledger)?.ToSummary());
                }
                _summaries = next;
                return (IReadOnlyList<ConversationSummary>)next.Values.Where(v => v.Item3 != null).Select(v => v.Item3!)
                    .OrderByDescending(s => s.UpdatedAt).ToList();
            }
        });
    });

    public Task<bool> DeleteConversationAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(false);
        var filePath = GetFilePath(id);
        if (filePath == null) return Task.FromResult(false);
        return Task.Run(() => WithHistoryLock(() =>
        {
            if (!File.Exists(filePath)) return false;
            RevokeConversations(new[] { id });
            File.Delete(filePath);
            return true;
        }));
    }

    public Task DeleteAllConversationsAsync() => Task.Run(() => WithHistoryLock(() =>
    {
        var failures = new List<Exception>();
        if (Directory.Exists(_storageDirectory))
        {
            var files = Directory.GetFiles(_storageDirectory, "*.bin");
            // Snapshot and revoke together: pending writers cannot recreate these identities.
            // A new conversation not yet persisted is deliberately outside this deletion.
            RevokeConversations(files.Select(file => Path.GetFileNameWithoutExtension(file)!));
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    OnlyWorkspaceLog.Logger.Warning(ex, "Failed to delete conversation file {FilePath}", file);
                    failures.Add(ex);
                }
            }
        }

        if (failures.Count != 0) throw new AggregateException("Some conversations could not be deleted.", failures);
        return true;
    }));

    public async Task<int> CleanupExpiredConversationsAsync(int retentionDays, int maxConversations)
    {
        var summaries = await ListConversationsAsync().ConfigureAwait(false);
        if (summaries.Count == 0) return 0;

        int deletedCount = 0;
        var remaining = new List<ConversationSummary>();

        // Retention days
        if (retentionDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
            foreach (var summary in summaries)
            {
                if (summary.UpdatedAt < cutoff)
                {
                    if (await DeleteConversationAsync(summary.Id).ConfigureAwait(false))
                    {
                        deletedCount++;
                    }
                }
                else
                {
                    remaining.Add(summary);
                }
            }
        }
        else
        {
            remaining.AddRange(summaries);
        }

        // Max conversations cap
        if (maxConversations > 0 && remaining.Count > maxConversations)
        {
            var excess = remaining.OrderByDescending(s => s.UpdatedAt).Skip(maxConversations).ToList();
            foreach (var item in excess)
            {
                if (await DeleteConversationAsync(item.Id).ConfigureAwait(false))
                {
                    deletedCount++;
                }
            }
        }

        return deletedCount;
    }

    private static readonly System.Text.RegularExpressions.Regex ValidId = new(@"\A[A-Za-z0-9_-]{1,64}\z");

    /// <summary>The id arrives from the pane: null unless it is a plain file name (no path parts).</summary>
    private string? GetFilePath(string id) => ValidId.IsMatch(id) ? Path.Combine(_storageDirectory, $"{id}.bin") : null;
}
