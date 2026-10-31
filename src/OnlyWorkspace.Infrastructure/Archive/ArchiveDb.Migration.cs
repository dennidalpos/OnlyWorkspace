using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace OnlyWorkspace.Infrastructure.Archive;

// Fault injection stays internal; production always uses the verified encrypted copy path.
internal sealed class ArchiveMigrationOptions
{
    internal string? BackupRoot;
    internal Action<string>? BackupCreated;
    internal Action? BeforeCommit;
}

public sealed partial class ArchiveDb
{
    private const string RetiredMeta = "key glob 'cards_*' or key in ('card_save_sequence','saved_source_sequence','contacts_scope')";
    private static readonly string[] RetiredOperations =
    {
        "SaveCard", "SkipCard", "RegenerateCard", "RetrySkippedCardsForStore", "RetryTooLongCards",
        "BeginCloudCardAttempt", "CompleteCloudCardAttempt", "ApplyCardsBudgetReset", "SetCaseClosed"
    };

    private static bool RetiredTable(string name) => name == "cards" || name == "archive_saved_cards" ||
        name == "usage" || name == "archive_case_closures" || name == "contact_rejects" || name == "party_choice" || name == "cards_fts" ||
        name.StartsWith("cards_fts_", StringComparison.Ordinal) || name == "code_cards_fts" ||
        name.StartsWith("code_cards_fts_", StringComparison.Ordinal);

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    // Compare all retained values, including FTS row identities, without exposing mail in the manifest/log.
    private static string PreservedDigest(SqliteConnection connection, CancellationToken ct, SqliteTransaction? tx = null, bool retainedOnly = true)
    {
        var tables = new List<(string Name, string Sql)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "select name,sql from sqlite_master where type='table' and name not like 'sqlite_%' order by name";
            using var rows = cmd.ExecuteReader();
            while (rows.Read()) if (!retainedOnly || !RetiredTable(rows.GetString(0))) tables.Add((rows.GetString(0), rows.GetString(1)));
        }
        using var hash = SHA256.Create();
        using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            string Canonical(string name) => retainedOnly && name == "archive_saved_sources" ? "archive_source_checks" : name;
            foreach (var table in tables.OrderBy(t => Canonical(t.Name), StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var columns = new List<string>();
                using (var info = connection.CreateCommand())
                {
                    info.Transaction = tx; info.CommandText = "pragma table_info(" + QuoteIdentifier(table.Name) + ")";
                    using var rows = info.ExecuteReader();
                    while (rows.Read()) if (!retainedOnly || table.Name != "emails" || rows.GetString(1) != "card_state") columns.Add(rows.GetString(1));
                }
                writer.Write(Canonical(table.Name));
                bool rowid = table.Sql.IndexOf("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) < 0;
                string fields = (rowid ? "rowid," : "") + string.Join(",", columns.Select(QuoteIdentifier));
                string filter = !retainedOnly ? "" : table.Name == "meta" ? " where not (" + RetiredMeta + ")" : table.Name == "rpc_batches" ?
                    " where operation not in (" + string.Join(",", RetiredOperations.Select(x => "'" + x + "'")) + ")" : "";
                using var command = connection.CreateCommand(); command.Transaction = tx;
                command.CommandText = "select " + fields + " from " + QuoteIdentifier(table.Name) + filter +
                    " order by " + string.Join(",", Enumerable.Range(1, columns.Count + (rowid ? 1 : 0)));
                using var data = command.ExecuteReader();
                while (data.Read())
                {
                    ct.ThrowIfCancellationRequested(); writer.Write(true);
                    for (int i = 0; i < data.FieldCount; i++)
                    {
                        object value = data.GetValue(i);
                        writer.Write(value.GetType().FullName ?? "");
                        if (value is byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
                        else writer.Write(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                    }
                }
                writer.Write(false);
            }
        }
        return BitConverter.ToString(hash.Hash!).Replace("-", "");
    }

    private string CreateMigrationBackup(long sourceVersion, CancellationToken ct, ArchiveMigrationOptions? options)
    {
        long required = checked(FileSizeBytes + FileSizeBytes + Core.Archive.ArchiveCapacity.ReserveBytes);
        if (AvailableFreeBytes < required) throw new IOException("Spazio insufficiente per migrare l'archivio con un backup verificato. Libera spazio e riprova.");
        string directory = Path.GetDirectoryName(_path)!;
        string root = options?.BackupRoot ?? (string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(DefaultDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Core.AppPaths.LocalDirectory, "archive-migration-backups") : Path.Combine(directory, "migration-backups"));
        string backupDirectory = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDirectory);
        string target = Path.Combine(backupDirectory, Path.GetFileName(_path));
        string keyPath = Path.Combine(directory, "archive.key");
        string key = ArchiveKey.LoadOrCreate(keyPath) ?? throw new ArchiveUnreadableException("La chiave dell'archivio non è leggibile.");
        string expected = PreservedDigest(_db, ct);
        string fullExpected = PreservedDigest(_db, ct, retainedOnly: false);
        CopyEncrypted(_path, target, key, ct);
        File.Copy(keyPath, Path.Combine(backupDirectory, "archive.key"));
        if (ArchiveKey.LoadOrCreate(Path.Combine(backupDirectory, "archive.key")) != key)
            throw new InvalidDataException("La chiave protetta del backup non corrisponde all'archivio.");
        options?.BackupCreated?.Invoke(target);
        using (var copy = Connect(target, key, SqliteOpenMode.ReadOnly))
        {
            copy.Open();
            using var check = copy.CreateCommand(); check.CommandText = "pragma integrity_check";
            using (var rows = check.ExecuteReader())
            {
                if (!rows.Read() || rows.GetString(0) != "ok" || rows.Read()) throw new InvalidDataException("Il backup dell'archivio non supera il controllo di integrità.");
            }
            check.CommandText = "pragma user_version";
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != sourceVersion || PreservedDigest(copy, ct) != expected ||
                PreservedDigest(copy, ct, retainedOnly: false) != fullExpected)
                throw new InvalidDataException("Il backup dell'archivio non corrisponde ai dati da preservare.");
        }
        string HashFile(string path) { using var sha = SHA256.Create(); using var file = File.OpenRead(path); return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""); }
        File.WriteAllText(Path.Combine(backupDirectory, "manifest.json"), JsonSerializer.Serialize(new
        {
            sourceVersion, targetVersion = SchemaVersion, createdUtc = DateTime.UtcNow, preservedSha256 = expected, fullDataSha256 = fullExpected,
            files = new[] { target, Path.Combine(backupDirectory, "archive.key") }.Select(p => new { name = Path.GetFileName(p), bytes = new FileInfo(p).Length, sha256 = HashFile(p) })
        }), new UTF8Encoding(false));
        ct.ThrowIfCancellationRequested();
        return expected;
    }

    private void RetireStoredCards(SqliteTransaction tx)
    {
        var triggers = new List<string>();
        using (var cmd = Command("select name from sqlite_master where type='trigger' and (sql like '%cards%' or sql like '%card_state%')", tx))
        using (var rows = cmd.ExecuteReader()) while (rows.Read()) triggers.Add(rows.GetString(0));
        foreach (var trigger in triggers) Exec("drop trigger " + QuoteIdentifier(trigger), tx);
        // These identities can still own history after an earlier index rebuild. They are absence
        // checks, not generated content: keep them until Outlook proves absence of the last copy.
        if (HasTable("archive_saved_sources", tx)) Exec("alter table archive_saved_sources rename to archive_source_checks", tx);
        Exec("drop index if exists ix_emails_card", tx);
        foreach (string table in new[] { "cards_fts", "code_cards_fts", "cards", "usage" })
            Exec("drop table if exists " + QuoteIdentifier(table), tx);
        if (Scalar<long>("select count(*) from pragma_table_info('emails') where name='card_state'", tx) != 0)
            Exec("alter table emails drop column card_state", tx);
        Exec("delete from meta where " + RetiredMeta, tx);
        Exec("delete from rpc_batches where operation in (" + string.Join(",", RetiredOperations.Select(x => "'" + x + "'")) + ")", tx);
    }

    private void FinishStoredCardRetirement(SqliteTransaction tx)
    {
        // Older builds populated their source list lazily. Capture identities absent from the index
        // before deleting the last durable evidence that history can still depend on those sources.
        Exec("create table if not exists archive_source_checks(id integer primary key,store_id text not null,entry_id text not null,message_id text not null,checked_pass integer not null default -1,unique(store_id,entry_id,message_id))", tx);
        if (HasTable("archive_saved_cards", tx))
        {
            Exec("insert into archive_source_checks(id,store_id,entry_id,message_id) select " +
                 "(select coalesce(max(id),0) from archive_source_checks)+row_number() over(order by s.store_id,s.entry_id,s.message_id)," +
                 "s.store_id,s.entry_id,s.message_id from (select distinct store_id,entry_id,message_id from archive_saved_cards) s " +
                 "where not exists(select 1 from emails e join folders f on f.id=e.folder_id where f.store_id=s.store_id and e.entry_id=s.entry_id and coalesce(e.message_id,'')=s.message_id) " +
                 "and not exists(select 1 from archive_source_checks c where c.store_id=s.store_id and c.entry_id=s.entry_id and c.message_id=s.message_id)", tx);
            Exec("drop table archive_saved_cards", tx);
        }
    }
}
