using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using OnlyWorkspace.Core;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Infrastructure.Security;

/// <summary>Per-user metadata, encrypted with DPAPI. Only this ledger expires, after 30 days.</summary>
public sealed class PseudonymizationAuditStore : IPseudonymizationAuditStore
{
    private readonly string _directory, _mutex;
    private readonly Func<DateTime> _utc;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OnlyWorkspace-PrivacyAudit-v1");
    public PseudonymizationAuditStore(string? directory = null, Func<DateTime>? utc = null)
    {
        _directory = Path.GetFullPath(directory ?? AppPaths.PseudonymizationAuditDirectory);
        _utc = utc ?? (() => DateTime.UtcNow);
        using var sha = SHA256.Create();
        _mutex = "Local\\OnlyWorkspace-PrivacyAudit-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(_directory.ToUpperInvariant()))).Replace("-", "");
    }
    private T Locked<T>(Func<T> action)
    {
        using var gate = new Mutex(false, _mutex); bool held = false;
        try
        {
            try { held = gate.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Privacy audit busy.");
            return action();
        }
        finally { if (held) gate.ReleaseMutex(); }
    }
    public void Write(PseudonymizationAudit record) => Locked(() =>
    {
        Validate(record); Directory.CreateDirectory(_directory); CheckDirectory();
        var records = ReadFiles();
        string path = Path.Combine(_directory, record.RequestId + ".audit");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(record), Entropy, DataProtectionScope.CurrentUser);
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(cipher, 0, cipher.Length); file.Flush(true); }
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        foreach (var item in records.Where(r => r.Record.Utc <= _utc().AddDays(-30))) File.Delete(item.Path);
        return true;
    });
    public IReadOnlyList<PseudonymizationAudit> Read() => Locked<IReadOnlyList<PseudonymizationAudit>>(() =>
    {
        if (!Directory.Exists(_directory)) return Array.Empty<PseudonymizationAudit>();
        CheckDirectory(); var records = ReadFiles();
        foreach (var item in records.Where(r => r.Record.Utc <= _utc().AddDays(-30))) File.Delete(item.Path);
        return records.Where(r => r.Record.Utc > _utc().AddDays(-30)).Select(r => r.Record).OrderByDescending(r => r.Utc).ToArray();
    });
    private void CheckDirectory()
    {
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid audit directory.");
    }
    private List<(string Path, PseudonymizationAudit Record)> ReadFiles()
    {
        var result = new List<(string, PseudonymizationAudit)>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.audit"))
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 65536 || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
                throw new IOException("Invalid audit entry.");
            var record = JsonSerializer.Deserialize<PseudonymizationAudit>(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser))
                ?? throw new IOException("Invalid audit entry.");
            Validate(record);
            if (record.RequestId != Path.GetFileNameWithoutExtension(path)) throw new IOException("Invalid audit identity.");
            result.Add((path, record));
        }
        return result;
    }
    private static void Validate(PseudonymizationAudit record)
    {
        if (!Guid.TryParseExact(record.RequestId, "N", out _) || record.Utc.Kind != DateTimeKind.Utc ||
            record.Outcome is not ("prepared" or "dispatching" or "completed" or "failed" or "blocked" or "cancelled") ||
            record.Counts == null || record.Revisions == null || record.Counts.Count > 64 || record.Revisions.Count > 64 ||
            record.Counts.Any(p => p.Value < 0 || !Regex.IsMatch(p.Key, @"\A[A-Z][A-Z0-9]{1,15}\z")) ||
            record.Revisions.Any(p => !Regex.IsMatch(p.Key, @"\A[a-z][a-z0-9-]{0,63}\z") || !Regex.IsMatch(p.Value, @"\A[0-9A-Za-z.-]{1,64}\z")))
            throw new IOException("Invalid privacy audit metadata.");
    }
}
