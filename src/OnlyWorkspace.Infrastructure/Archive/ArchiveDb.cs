using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;

#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace OnlyWorkspace.Infrastructure.Archive;

/// <summary>The database exists but its key is missing or wrong (another Windows user, restored profile).</summary>
public sealed class ArchiveUnreadableException : Exception
{
    public ArchiveUnreadableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Per-user mail archive: headers, addresses grouped into counterparties, threads and the word index of the
/// full bodies and deduplicated attachment text, encrypted at rest; every call is serialized.
/// </summary>
#if NET5_0_OR_GREATER
[SupportedOSPlatform("windows")]
#endif
public sealed partial class ArchiveDb : IDisposable
{
    private const int SchemaVersion = 13;
    private const string MetaPass = "pass";
    private const string MetaCheckOpen = "check_open";
    private const string MetaCheckTime = "check_time";
    private const string MetaCheckAsked = "check_asked";
    // A check asked for by a missing email: not more often than this, a store that reads empty would ask forever.
    private static readonly TimeSpan MinCheckGap = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly SqliteConnection _db;
    private readonly string _path;
    private readonly FileStream _ownership;
    private readonly Func<string, long> _availableFreeSpace;
    // Number of the full check in progress or last closed; 0 until the first one.
    private long _pass;
    private bool? _indexAttachmentText;
    private bool? _attachmentOcr;
    private string? _ocrLanguage;
    private bool _pageLimitCleared;

    /// <param name="ocrLanguage">Language OCR reads on this PC ("" none), null when not known yet.</param>
    internal void ConfigureStorage(bool indexAttachmentText, bool attachmentOcr = true, string? ocrLanguage = null)
    {
        lock (_gate)
        {
            if (!_pageLimitCleared) { Exec("pragma max_page_count=4294967294"); _pageLimitCleared = true; }
            ConfigureOcr(attachmentOcr);
            ConfigureOcrLanguage(attachmentOcr, ocrLanguage);
            if (_indexAttachmentText == indexAttachmentText) return;
            var previous = GetMeta("index_attachment_text");
            // On the first disabled open, reconcile legacy saved rows even if an older build already wrote 0.
            if (indexAttachmentText && previous == "1") { _indexAttachmentText = true; return; }
            using var tx = _db.BeginTransaction();
            if (indexAttachmentText && previous == "0")
                Exec("update emails set body_state=0 where body_state=1 and id in (select id from emails where has_attach=1 union select email_id from attachments)", tx);
            else if (!indexAttachmentText)
            {
                Exec("update attachments set content_hash=null", tx);
                Exec("delete from attachment_contents", tx);
                Exec("delete from attachment_fts", tx);
                Exec("insert into attachment_fts(rowid,name,body) select rowid,name,'' from attachments", tx);
                Exec("update attachments set state='excluded',problem='disabled',text_limited=0 where state in ('indexed','facts-only')", tx);

            }
            Exec("insert into meta(key,value) values('index_attachment_text',$v) on conflict(key) do update set value=excluded.value", tx, ("$v", indexAttachmentText ? "1" : "0"));
            tx.Commit();
            _indexAttachmentText = indexAttachmentText;
        }
    }

    /// <summary>
    /// E8: OCR reads scans and images that had no reader, or stayed unread while the administrator had it off. Turning it
    /// on rereads those emails once from Outlook (also on the first opening after the upgrade), without a new reader
    /// revision: texts already read do not change. Turning it off deletes nothing. Closed mailboxes keep what they have.
    /// </summary>
    private void ConfigureOcr(bool attachmentOcr)
    {
        if (_attachmentOcr == attachmentOcr) return;
        string wanted = attachmentOcr ? "1" : "0";
        if (GetMeta("ocr_reader") != wanted)
        {
            using var tx = _db.BeginTransaction();
            if (attachmentOcr)
                Exec("update emails set body_state=0 where body_state=1 and folder_id in (select id from folders where listed=1) and id in (select email_id from attachments where " +
                     "state in ('scanned','ocr-off') or (state='limited' and problem in ('scanned','ocr-off')) or (state='unsupported' and problem='format' and (" +
                     string.Join(" or ", new[] { "png", "jpg", "jpeg", "gif", "tif", "tiff", "bmp" }.Select(e => "lower(name) like '%." + e + "'")) + ")))", tx);
            Exec("insert or replace into meta(key,value) values('ocr_reader',$v)", tx, ("$v", wanted));
            tx.Commit();
        }
        _attachmentOcr = attachmentOcr;
    }

    /// <summary>
    /// A123: scans left "ocr-unavailable" (no recognizer for the user's languages) are reread once when a recognizer
    /// appears or changes, e.g. after the administrator installs a Windows OCR language. Acquired texts are preserved.
    /// </summary>
    private void ConfigureOcrLanguage(bool attachmentOcr, string? ocrLanguage)
    {
        if (!attachmentOcr || string.IsNullOrEmpty(ocrLanguage) || ocrLanguage == _ocrLanguage) return;
        if (GetMeta("ocr_language") != ocrLanguage)
        {
            using var tx = _db.BeginTransaction();
            Exec("update emails set body_state=0 where body_state=1 and folder_id in (select id from folders where listed=1) and id in (select email_id from attachments where " +
                 "state='ocr-unavailable' or (state='limited' and problem='ocr-unavailable'))", tx);
            Exec("insert or replace into meta(key,value) values('ocr_language',$v)", tx, ("$v", ocrLanguage!));
            tx.Commit();
        }
        _ocrLanguage = ocrLanguage;
    }

    internal long AvailableFreeBytes => _availableFreeSpace(_path);
    internal ArchiveCapacityState Capacity => ArchiveCapacity.Evaluate(AvailableFreeBytes);

    private void RequireIndexCapacity()
    {
        var state = Capacity;
        if (state == ArchiveCapacityState.Full) throw new InvalidOperationException(ArchiveCapacity.Message(state));
    }

    public void OptimizeIndex()
    {
        lock (_gate)
        {
            RequireIndexCapacity();
            Exec("pragma optimize");
            foreach (var table in new[] { "body_fts", "attachment_fts", "code_body_fts", "code_attachment_fts" })
                Exec("insert into " + table + "(" + table + ") values('optimize')");
            Exec("pragma wal_checkpoint(TRUNCATE)");
        }
    }

    /// <summary>Test seam: runs <paramref name="action"/> holding the database lock, as a compaction does.</summary>
    internal void Locked(Action action)
    {
        lock (_gate) action();
    }

    public void Compact()
    {
        lock (_gate)
        {
            long needed = checked(2 * new FileInfo(_path).Length + ArchiveCapacity.ReserveBytes);
            if (AvailableFreeBytes < needed)
                throw new IOException("Spazio insufficiente per compattare: servono circa due volte la dimensione del DB oltre a 1 GiB di riserva.");
            Exec("pragma wal_checkpoint(TRUNCATE)");
            Exec("vacuum");
            Exec("pragma wal_checkpoint(TRUNCATE)");
        }
    }

    // Loaded with the first connection, not with the type: Outlook uses ArchiveDb's paths and helpers on its UI thread
    // at startup, where loading the native SQLite would count against the add-in's startup time.
    private static readonly Lazy<bool> Native = new(() => { LoadNative(); return true; });

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string path);

    /// <summary>
    /// Inside Outlook (net48, AnyCPU) nothing picks the native build for the process bitness: load it by path,
    /// so the provider's DllImport finds the module already in memory.
    /// </summary>
    private static void LoadNative()
    {
        // Only x86 and x64 builds ship.
        string rid = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? "win-x86" : "win-x64";
        var dir = Path.GetDirectoryName(typeof(ArchiveDb).Assembly.Location) ?? string.Empty;
        var native = Path.Combine(dir, "runtimes", rid, "native", "sqlite3mc.dll");
        if (File.Exists(native)) LoadLibrary(native);
    }

    public static string DefaultDirectory =>
        OnlyWorkspace.Core.AppPaths.ArchiveDirectory;

    internal static string EngineFileOf(string directory, string name)
    {
        using var hash = SHA256.Create();
        var suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(name))).Replace("-", "");
        return Path.Combine(directory, "archive-" + suffix + ".engine.db");
    }

    /// <summary>Opens (or creates) the archive of one Outlook profile.</summary>
    public static ArchiveDb Open(string directory, string name, CancellationToken cancellationToken = default, Func<string, long>? availableFreeSpace = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var path = EngineFileOf(directory, name);
        // File sharing works across Windows sessions and has no mutex thread affinity.
        var ownership = new FileStream(path + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        SqliteConnection? connection = null;
        try
        {
            string keyPath = Path.Combine(directory, "archive.key");
            if (!File.Exists(keyPath) && (Directory.GetFiles(directory, "*.db").Length > 0 || Directory.GetFiles(directory, "*.db-wal").Length > 0))
                throw new ArchiveUnreadableException("The existing archive key is missing.");
            var key = ArchiveKey.LoadOrCreate(keyPath)
                      ?? throw new ArchiveUnreadableException("The archive key cannot be decrypted by this Windows user.");
            connection = Connect(path, key);
            connection.Open();
            var db = new ArchiveDb(connection, path, ownership, availableFreeSpace ?? (p => new DriveInfo(Path.GetPathRoot(p)!).AvailableFreeSpace));
            db.Exec("pragma secure_delete=on");
            long version = db.Scalar<long>("pragma user_version");
            cancellationToken.ThrowIfCancellationRequested();
            using (cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle))) db.InitializeIndex(version, cancellationToken);
            DeleteStagingCopies(path);
            db.Exec("pragma journal_mode=wal");
            db.Exec("pragma synchronous=full");
            db.Exec("pragma secure_delete=on");
            foreach (string table in new[] { "body_fts", "attachment_fts", "code_body_fts", "code_attachment_fts" })
                db.Exec("insert into " + table + "(" + table + ",rank) values('secure-delete',1)");
            db.Exec("pragma cache_size=-8000");
            db.PruneIdlePeers(DateTime.UtcNow);
            db.SetMeta("profile", name);
            db._pass = long.TryParse(db.GetMeta(MetaPass), out var pass) ? pass : 0;
            return db;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26)
        {
            connection?.Dispose();
            ownership.Dispose();
            throw new ArchiveUnreadableException("The archive database does not match its key.", ex);
        }
        catch
        {
            connection?.Dispose();
            ownership.Dispose();
            throw;
        }
    }

    private static SqliteConnection Connect(string path, string key, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        _ = Native.Value;
        return new(new SqliteConnectionStringBuilder { DataSource = path, Password = key, Pooling = false, Mode = mode, DefaultTimeout = 2 }.ToString());
    }

    internal static string? ReadProfileName(string path)
    {
        string keyPath = Path.Combine(Path.GetDirectoryName(path)!, "archive.key");
        if (!File.Exists(keyPath)) throw new ArchiveUnreadableException("The archive key is missing.");
        var key = ArchiveKey.LoadOrCreate(keyPath) ?? throw new ArchiveUnreadableException("The archive key cannot be read.");
        using var connection = Connect(path, key, SqliteOpenMode.ReadOnly);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select value from meta where key='profile'";
        return command.ExecuteScalar() as string;
    }

    private const string StagingMarker = ".copy-";

    /// <summary>A copy interrupted by a stopped process leaves its staging file: this archive's owner removes it.</summary>
    private static void DeleteStagingCopies(string engine)
    {
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(engine)!, Path.GetFileName(engine) + "*" + StagingMarker + "*"))
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Encrypted copy for the user's backups; the target appears only when complete.</summary>
    private static void CopyEncrypted(string source, string target, string key, CancellationToken ct)
    {
        string staging = target + StagingMarker + Guid.NewGuid().ToString("N");
        try
        {
            using (var old = Connect(source, key, SqliteOpenMode.ReadOnly))
            using (var copy = Connect(staging, key))
            {
                old.Open();
                copy.Open();
                CopyPages(old, copy, ct);
                using var check = copy.CreateCommand();
                check.CommandText = "pragma integrity_check";
                if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                    throw new InvalidDataException("Archive copy integrity check failed.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(staging, target);
        }
        finally
        {
            foreach (var file in new[] { staging, staging + "-wal", staging + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    internal static void CopyPages(SqliteConnection source, SqliteConnection destination, CancellationToken ct, Action<int>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        using var backup = SQLitePCL.raw.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main");
        if (backup == null || backup.IsInvalid) throw new SqliteException("Archive backup could not start.", SQLitePCL.raw.sqlite3_errcode(destination.Handle));
        int result;
        do
        {
            ct.ThrowIfCancellationRequested();
            result = SQLitePCL.raw.sqlite3_backup_step(backup, 64);
            if (result != SQLitePCL.raw.SQLITE_OK && result != SQLitePCL.raw.SQLITE_DONE)
                throw new SqliteException("Archive backup failed; the source was preserved.", result);
            progress?.Invoke(SQLitePCL.raw.sqlite3_backup_remaining(backup));
        } while (result != SQLitePCL.raw.SQLITE_DONE);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Encrypted, coherent backup; the destination must not already exist.</summary>
    public void Backup(string destination, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (File.Exists(destination)) throw new IOException("The archive backup destination already exists.");
            var key = ArchiveKey.LoadOrCreate(Path.Combine(Path.GetDirectoryName(_path)!, "archive.key"))
                      ?? throw new ArchiveUnreadableException("The archive key cannot be read.");
            CopyEncrypted(_path, destination, key, cancellationToken);
        }
    }

    public static bool Exists(string directory, string name) => File.Exists(EngineFileOf(directory, name));

    /// <summary>Removes the database of one Outlook profile (the key stays: other profiles share it).</summary>
    public static void Delete(string directory, string name)
    {
        string engine = EngineFileOf(directory, name);
        using var ownership = new FileStream(engine + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        DeleteWithJournal(engine);
    }

    private static void DeleteWithJournal(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private ArchiveDb(SqliteConnection db, string path, FileStream ownership, Func<string, long> availableFreeSpace)
    {
        _db = db;
        _path = path;
        _ownership = ownership;
        _availableFreeSpace = availableFreeSpace;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
            _ownership.Dispose();
        }
    }

    /// <summary>Size on disk, including the write-ahead log and shared-memory file.</summary>
    public long FileSizeBytes
    {
        get
        {
            long size = 0;
            foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            {
                var info = new FileInfo(file);
                if (info.Exists) size += info.Length;
            }
            return size;
        }
    }

    public string FilePath => _path;

    /// <summary>Schema of the current format for a new archive.</summary>
    private void CreateSchema(SqliteTransaction tx)
    {
        foreach (var sql in new[]
        {
            "create table meta(key text primary key, value text) without rowid",
            // pass: full check in which the folder was read to its end; listed 0: deleted in Outlook, kept for emails found again.
            "create table folders(id integer primary key, store_id text not null, entry_id text not null, path text, oldest integer, newest integer, done integer not null default 0, " +
            "pass integer not null default 0, listed integer not null default 1, verified integer not null default 0, absence_verified integer not null default 0, cache_store integer not null default 0, error text, verified_utc integer, " +
            "unique(store_id, entry_id))",
            // two_way: the user has written to them.
            "create table counterparties(id integer primary key, key text not null unique, name text not null, important integer not null default 0, two_way integer not null default 0)",
            "create table addresses(id integer primary key, addr text not null unique, name text, cp_id integer, is_me integer not null default 0)",
            "create index ix_addresses_cp on addresses(cp_id)",
            // seen: full check in which the email was last found; retry_count: attempts of a retryable body or attachment.
            "create table emails(id integer primary key, folder_id integer not null, entry_id text not null, message_id text, conv_id text, in_reply_to text, thread_id integer not null default 0, " +
            "subject text, subject_norm text, sender_id integer, sender_name text, received integer not null, size integer, has_attach integer, preview text, " +
            "body_state integer not null default 0, seen integer not null default 0, body_version text, text_limited integer not null default 0, " +
            "recipients_limited integer not null default 0, attachments_limited integer not null default 0, retry_utc integer, retry_count integer not null default 0, full_body text, source_version text, modified_utc integer, present integer not null default 1, unique(folder_id, entry_id))",
            "create index ix_emails_message on emails(message_id)",
            "create index ix_emails_conv on emails(conv_id)",
            "create index ix_emails_reply on emails(in_reply_to)",
            "create index ix_emails_thread on emails(thread_id)",
            "create index ix_emails_received on emails(received)",
            "create index ix_emails_body on emails(body_state, received)",
            // role: 0 sender, 1 to, 2 cc
            "create table email_addr(email_id integer not null, addr_id integer not null, role integer not null, primary key(email_id, addr_id, role)) without rowid",
            "create index ix_email_addr_addr on email_addr(addr_id)",
            "create table attachments(email_id integer not null, name text not null, source_id text, version text, state text not null default 'pending', problem text, " +
            "text_limited integer not null default 0, content_hash text)",
            "create table attachment_contents(hash text primary key, text text not null) without rowid",
            "create index ix_attachments_email on attachments(email_id)",
            // Contentless: the words are searchable, the text cannot be read back.
            "create virtual table body_fts using fts5(subject, body, content='', contentless_delete=1, tokenize='unicode61 remove_diacritics 2')",
            "create virtual table attachment_fts using fts5(name,body,content='',contentless_delete=1,tokenize='unicode61 remove_diacritics 2')",
            "create trigger attachments_deleted after delete on attachments begin delete from attachment_fts where rowid=old.rowid; end",
            "create table rpc_batches(id text primary key, operation text not null, digest text not null, result text) without rowid"
        })
        {
            Exec(sql.Replace("create table ", "create table if not exists ").Replace("create index ", "create index if not exists "), tx);
        }
    }

    public string? GetMeta(string key)
    {
        lock (_gate) return Scalar<string?>("select value from meta where key=$k", null, ("$k", key));
    }

    public void SetMeta(string key, string? value)
    {
        lock (_gate)
        {
            if (value == null) Exec("delete from meta where key=$k", null, ("$k", key));
            else Exec("insert into meta(key,value) values($k,$v) on conflict(key) do update set value=excluded.value", null, ("$k", key), ("$v", value));
        }
    }

    public ArchiveStats GetStats()
    {
        lock (_gate)
        {
            return new ArchiveStats
            {
                PendingDeletions = (int)Scalar<long>("select count(*) from archive_deletions"),
                Emails = (int)Scalar<long>("select count(*) from emails where body_state<>3"),
                BodiesIndexed = (int)Scalar<long>("select count(*) from emails where body_state=1"),
                BodiesPending = (int)Scalar<long>("select count(*) from emails where body_state=0"),
                BodiesUnavailable = (int)Scalar<long>("select count(*) from emails where body_state=2"),
                BodiesRetryable = (int)Scalar<long>("select count(*) from emails where body_state=4"),
                TextsLimited = (int)Scalar<long>("select count(*) from emails where body_state<>3 and text_limited=1"),
                RecipientsLimited = (int)Scalar<long>("select count(*) from emails where body_state<>3 and recipients_limited=1"),
                AttachmentsLimited = (int)Scalar<long>("select count(*) from emails where body_state<>3 and attachments_limited=1"),
                AttachmentTextsIndexed = (int)Scalar<long>("select count(*) from attachments a join emails e on e.id=a.email_id where e.body_state=1 and a.state='indexed'"),
                AttachmentTextsExcluded = (int)Scalar<long>("select count(*) from attachments a join emails e on e.id=a.email_id where e.body_state<>3 and a.state not in ('indexed','pending','retryable','limited')"),
                AttachmentTextsRetryable = (int)Scalar<long>("select count(*) from attachments a join emails e on e.id=a.email_id where e.body_state<>3 and a.state='retryable'"),
                AttachmentTextsLimited = (int)Scalar<long>("select count(*) from attachments a join emails e on e.id=a.email_id where e.body_state<>3 and (a.state='limited' or a.text_limited=1)"),
                AttachmentTextsPending = (int)Scalar<long>("select count(*) from attachments a join emails e on e.id=a.email_id where e.body_state<>3 and a.state='pending'"),
                AttachmentTexts = (int)Scalar<long>("select count(*) from attachments a join emails e on e.id=a.email_id where e.body_state<>3"),
                FoldersFailed = (int)Scalar<long>("select count(*) from folders where listed=1 and error is not null"),
                Folders = (int)Scalar<long>("select count(*) from folders where listed=1"),
                FoldersDone = (int)Scalar<long>("select count(*) from folders where listed=1 and done=1 and error is null"),
                DbBytes = FileSizeBytes,
                AvailableFreeBytes = AvailableFreeBytes,
                DiskState = Capacity
            };
        }
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string UnixText(DateTime utc) => ToUnix(utc).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Row ids read from this database, inlined in SQL; -1 for an empty list matches nothing.
    /// Invariant: some cultures write the minus sign as U+2212, which SQLite rejects.</summary>
    private static string InlineIds(IEnumerable<long> ids)
    {
        var text = new List<string>();
        foreach (long id in ids) text.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return text.Count == 0 ? "-1" : string.Join(",", text);
    }

    private static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    private SqliteCommand Command(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, tx, args);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, tx, args);
        var value = cmd.ExecuteScalar();
        if (value == null || value is DBNull) return default!;
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, type);
    }
}
