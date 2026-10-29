using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Infrastructure.Security;

public sealed partial class DpapiHistoryStorageService
{
    private string LedgerPath => Path.Combine(_storageDirectory, "deleted-sources.dat");
    private static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The mutex shared by every process writing this history folder.</summary>
    internal string HistoryMutexName =>
        @"Global\OnlyWorkspace_History_" + Hash(Path.GetFullPath(_storageDirectory).ToUpperInvariant()).Replace('/', '_').Replace('+', '-');

    private T WithHistoryLock<T>(Func<T> action) =>
        TryWithHistoryLock(action, TimeSpan.FromSeconds(30), out var result) ? result : throw new TimeoutException("Lo storico è occupato: riprovare.");

    private bool TryWithHistoryLock<T>(Func<T> action, TimeSpan wait, out T result)
    {
        using var gate = new Mutex(false, HistoryMutexName);
        bool held;
        try { held = gate.WaitOne(wait); }
        catch (AbandonedMutexException) { held = true; }
        result = default!;
        if (!held) return false;
        try { result = action(); }
        finally { gate.ReleaseMutex(); }
        return true;
    }

    private Dictionary<string, long> ReadLedger()
    {
        if (!File.Exists(LedgerPath)) return new Dictionary<string, long>(StringComparer.Ordinal);
        byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(LedgerPath), Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<Dictionary<string, long>>(bytes) ?? throw new InvalidDataException("Invalid history deletion ledger.");
    }

    private static IEnumerable<string> Keys(string profile, string store, string entry, string message)
    {
        if (entry.Length > 0) yield return Hash("entry\0" + store + "\0" + entry);
        if (message.Length > 0 && profile.Length > 0) yield return Hash("message\0" + profile + "\0" + message.Trim());
    }

    private static IEnumerable<HistoryContextReference> Sources(ConversationHistory conversation) =>
        conversation.ContextReferences.Concat(conversation.Messages.SelectMany(m => m.MailSources))
            .Concat(conversation.Messages.Where(m => m.Email != null).Select(m => m.Email!));

    /// <summary>The email sources of a conversation, copied on the calling thread: the pane keeps changing the conversation.</summary>
    private static List<HistoryContextReference> EmailSources(ConversationHistory conversation) =>
        Sources(conversation).Where(s => s.ItemType == "email").ToList();

    private static bool IsValidCore(IReadOnlyList<HistoryContextReference> sources, Dictionary<string, long> ledger) =>
        !sources.Any(source => Keys(source.OutlookProfile, source.StoreId, source.EntryId, source.MessageId)
            .Any(key => ledger.TryGetValue(key, out long deleted) && source.CapturedUtc.Ticks <= deleted));

    /// <summary>
    /// Checked every 2 seconds for the open chat: the ledger is read on the thread pool, never on Outlook's UI thread.
    /// A history held by a save or a deletion sweep is not waited for: true, and the next check reads it.
    /// </summary>
    public Task<bool> IsValidAsync(ConversationHistory conversation)
    {
        var sources = EmailSources(conversation);
        return Task.Run(() => !TryWithHistoryLock(() => IsValidCore(sources, ReadLedger()), TimeSpan.Zero, out bool valid) || valid);
    }

    /// <summary>
    /// Ledger first, then files: a crash or stale concurrent save cannot resurrect the conversation. The history lock is
    /// held to write the ledger and to delete each file, not while every conversation is decrypted (Outlook waits on it).
    /// </summary>
    public Task InvalidateSourcesAsync(IReadOnlyList<ArchiveDeletedSource> sources)
    {
        var ledger = WithHistoryLock(() =>
        {
            Directory.CreateDirectory(_storageDirectory);
            var current = ReadLedger();
            foreach (var source in sources)
                foreach (string key in Keys(source.OutlookProfile, source.StoreId, source.EntryId, source.MessageId))
                    current[key] = Math.Max(current.TryGetValue(key, out long previous) ? previous : 0, source.DeletedUtc.Ticks);
            byte[] cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(current), Entropy, DataProtectionScope.CurrentUser);
            Configuration.AtomicFile.WriteAllBytes(LedgerPath, cipher);
            return current;
        });
        var failures = new List<string>();
        foreach (string file in Directory.GetFiles(_storageDirectory, "*.bin"))
        {
            try
            {
                var stamp = Stamp(file);
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
                var conversation = JsonSerializer.Deserialize<ConversationHistory>(plain) ?? throw new InvalidDataException("Invalid conversation.");
                if (IsValidCore(EmailSources(conversation), ledger)) continue;
                // Saved again meanwhile: read it once more next time rather than delete what was not verified.
                if (!WithHistoryLock(() => Stamp(file) == stamp && Delete(file))) failures.Add(Path.GetFileName(file) + " (changed)");
            }
            catch (Exception ex)
            {
                // Never delete an unverified file. Finish the readable files, then keep the queue pending.
                failures.Add(Path.GetFileName(file) + " (" + ex.GetType().Name + ")");
            }
        }
        if (failures.Count > 0) throw new HistoryCleanupIncompleteException(failures);
        return Task.CompletedTask;

        static bool Delete(string path)
        {
            File.Delete(path);
            return true;
        }
    }
}

/// <summary>Only basenames and error kinds: no decrypted conversation data in diagnostics.</summary>
internal sealed class HistoryCleanupIncompleteException : IOException
{
    internal IReadOnlyList<string> Files { get; }
    internal HistoryCleanupIncompleteException(IReadOnlyList<string> files)
        : base("Bonifica storico incompleta; file conservati, da verificare o ripristinare: " + string.Join(", ", files)) => Files = files;
}
