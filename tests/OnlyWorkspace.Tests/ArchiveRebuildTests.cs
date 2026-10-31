using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveRebuildTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") ?? Path.GetTempPath(), "ow-migration-" + Guid.NewGuid().ToString("N"));
    private string PathOf => ArchiveDb.EngineFileOf(_directory, "p");
    private string Backups => Path.Combine(_directory, "migration-backups");
    public void Dispose() { if (Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") == null && Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private SqliteConnection Raw(string? path = null)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path ?? PathOf, Password = ArchiveKey.LoadOrCreate(Path.Combine(_directory, "archive.key")), Pooling = false
        }.ToString());
        db.Open(); return db;
    }
    private void Sql(string sql) { using var db = Raw(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private long Value(string sql, string? path = null)
    { using var db = Raw(path); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }

    // A v11 archive is built with SQL, independently of the removed runtime APIs.
    private void Legacy(int sourceVersion = 11)
    {
        using (var db = ArchiveDb.Open(_directory, "p"))
        {
            var folder = db.GetOrAddFolder("store", "inbox", "Synthetic/Inbox");
            db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "one", MessageId = "<one@example.test>", References = "<parent@example.test>", ThreadMetadataComplete = true, Subject = "Original subject", SenderAddress = "sender@example.test", ReceivedUtc = new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc), HasAttachments = true } }, true, true);
            var body = new ArchiveBody { Text = "Ordine ORD-98765. Importo EUR 1.200,00. Scadenza: 2026-11-30" };
            body.Attachments.Add(new ArchiveAttachment { Name = "invoice.txt", SourceId = "1", Type = "txt", Size = 42, State = "indexed", Text = "attachmentuniqueterm", Version = "synthetic-sha" });
            db.SaveBody(db.NextPendingBodies(1).Single().Id, body);
            db.SetMeta("stores", "[\"store\"]");
            while (db.NextFactWork() is { } work) Assert.True(db.CommitFacts(work, ArchiveDb.ExtractFacts(work, default)));
            while (db.NextSearchWork() is { } work) Assert.True(db.CommitSearch(work, ArchiveSearchText.CodeTerms(work.Body), ArchiveSearchText.CodeTerms(work.Attachments)));
        }
        Sql("create table party_choice(source text primary key, target text not null) without rowid; insert into party_choice values('addr:synthetic@example.test','domain:example.test'); create table contact_rejects(store_id text not null, addr text not null, rejected_utc integer not null, primary key(store_id, addr)) without rowid;" +
            "insert into contact_rejects values('contacts','refused@example.test',1234);insert into meta values('contacts_scope','{}');" +
            "create table archive_case_closures(member text primary key,closed_utc integer not null) without rowid;" +
            "insert into archive_case_closures values('m:<one@example.test>',1234);" +
            "insert into rpc_batches values('case-retired','SetCaseClosed','digest','old-result'),('retained','SaveBody','digest','retained-result');" +
            "pragma user_version=12;");
        if (sourceVersion == 12) return;
        Sql("drop table archive_source_checks; alter table emails add column card_state integer not null default 1; create index ix_emails_card on emails(card_state,received);" +
            "create table cards(email_id integer primary key,summary text,json text); insert into cards values(1,'paidonlyuniqueterm','{\"summary\":\"paidonlyuniqueterm\"}');" +
            "create virtual table cards_fts using fts5(summary,content='',contentless_delete=1); insert into cards_fts(rowid,summary) values(1,'paidonlyuniqueterm');" +
            "create virtual table code_cards_fts using fts5(code,content='',contentless_delete=1,tokenize='trigram'); insert into code_cards_fts(rowid,code) values(1,'PAID-54321');" +
            "create table archive_saved_cards(store_id text not null,entry_id text not null,message_id text not null,json text); insert into archive_saved_cards values('store','one','<one@example.test>','{\"summary\":\"paidonlyuniqueterm\"}');" +
            "create table archive_saved_sources(id integer primary key,store_id text,entry_id text,message_id text,checked_pass integer not null default -1); insert into archive_saved_sources values(1,'store','one','<one@example.test>',-1);" +
            "create table usage(day text,profile_id text,purpose text,prompt_tokens integer); insert into usage values('2026-10-29','p','cards',120);" +
            "insert into meta values('cards_consents','synthetic-consent'),('cards_budget','1000'),('cards_cloud_attempt:20261029:p','pending'),('card_save_sequence','7'),('saved_source_sequence','3');" +
            "insert into rpc_batches values('retired','SaveCard','digest','old-result');" +
            "insert into archive_deletions values(20,'p','old-store','old-entry','<old@example.test>',1234);" +
            "create trigger emails_saved_cards_deleted after delete on emails begin delete from archive_saved_cards; end;" +
            "drop trigger search_email_deleted; create trigger search_email_deleted after delete on emails begin delete from code_cards_fts where rowid=old.id; delete from archive_search_jobs where email_id=old.id; end;" +
            "pragma user_version=11;");
    }

    [Fact]
    public void CurrentFormat_DropsTheAddressBookDataOfTheRemovedFlow()
    {
        using (ArchiveDb.Open(_directory, "p")) { }
        Sql("create table contact_rejects(store_id text not null, addr text not null, rejected_utc integer not null, primary key(store_id, addr)) without rowid;" +
            "insert into contact_rejects values('s','refused@example.test',1); insert into meta values('contacts_scope','{}');");

        using (ArchiveDb.Open(_directory, "p")) { }

        Assert.Equal(0, Value("select count(*) from sqlite_master where name='contact_rejects'"));
        Assert.Equal(0, Value("select count(*) from meta where key='contacts_scope'"));
        Assert.Equal(13, Value("pragma user_version"));
    }

    [Fact]
    public void CurrentFormat_RegroupsTheAddressesOfTheRemovedMergeAndSplitChoices()
    {
        var day = new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);
        ArchiveHeader Header(string id, string sender) => new() { EntryId = id, MessageId = "<" + id + "@example.test>", Subject = id, SenderAddress = sender, ReceivedUtc = day };
        using (var db = ArchiveDb.Open(_directory, "p"))
        {
            db.AddHeaders(db.GetOrAddFolder("store", "inbox", "Inbox"), new[] { Header("a", "mario@acme.it"), Header("b", "john@acme.com"), Header("c", "fatture@acme.it") }, true);
            Assert.NotEqual(db.CounterpartyOf("mario@acme.it"), db.CounterpartyOf("john@acme.com"));
        }
        // What the user had merged (acme.com under acme.it) and split (the invoices address on its own).
        Sql("create table party_choice(source text primary key, target text not null) without rowid; insert into party_choice values('acme.com','acme.it'),('fatture@acme.it','fatture@acme.it');" +
            "update addresses set cp_id=(select cp_id from addresses where addr='mario@acme.it') where addr='john@acme.com';" +
            "update counterparties set important=1;");

        using (var db = ArchiveDb.Open(_directory, "p"))
        {
            Assert.NotEqual(db.CounterpartyOf("mario@acme.it"), db.CounterpartyOf("john@acme.com"));
            Assert.Equal(db.CounterpartyOf("mario@acme.it"), db.CounterpartyOf("fatture@acme.it"));
        }
        Assert.Equal(0, Value("select count(*) from sqlite_master where name='party_choice'"));
        Assert.Equal(0, Value("select count(*) from counterparties where important<>0"));
        Assert.Equal(2, Value("select count(*) from counterparties"));
        Assert.Equal(13, Value("pragma user_version"));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void MigrationPreservesOriginalsFactsIndexesChoicesAndLedger_AndRemovesRetiredSurfaces(int sourceVersion)
    {
        Legacy(sourceVersion); string original;
        using (var raw = Raw()) { using var q = raw.CreateCommand(); q.CommandText = "select full_body from emails where id=1"; original = (string)q.ExecuteScalar()!; }
        using (var db = ArchiveDb.Open(_directory, "p"))
        {
            Assert.Equal(original, db.GetOriginal("one", "store")!.Text);
            Assert.Single(db.Search(new ArchiveQuery { Text = "attachmentuniqueterm" }));
            Assert.Single(db.Search(new ArchiveQuery { Text = "98765" }));
            Assert.NotEmpty(db.GetFacts(1, true));
            Assert.Equal("[\"store\"]", db.GetMeta("stores"));
            Assert.Equal(0, Value("select count(*) from sqlite_master where name='party_choice'"));
            Assert.Equal(0, Value("select count(*) from sqlite_master where name='contact_rejects'"));
            Assert.Equal(0, Value("select count(*) from meta where key='contacts_scope'"));
            if (sourceVersion == 11) Assert.Equal(20, Assert.Single(db.PendingDeletions()).Id);
            Assert.Empty(db.Search(new ArchiveQuery { Text = "paidonlyuniqueterm" }));
            Assert.Empty(db.Search(new ArchiveQuery { Text = "54321" }));
        }
        Assert.Equal(13, Value("pragma user_version"));
        Assert.Equal(0, Value("select count(*) from sqlite_master where name like '%cards%' or name='usage' or name='ix_emails_card'"));
        Assert.Equal(0, Value("select count(*) from pragma_table_info('emails') where name='card_state'"));
        Assert.Equal(0, Value("select count(*) from meta where key glob 'cards_*' or key in ('card_save_sequence','saved_source_sequence')"));
        Assert.Equal(0, Value("select count(*) from sqlite_master where name='archive_case_closures'"));
        Assert.Equal(1, Value("select count(*) from rpc_batches where id='retained'"));
        Assert.Equal(0, Value("select count(*) from rpc_batches where id='retired'"));
        string backup = Directory.GetFiles(Backups, "*.engine.db", SearchOption.AllDirectories).Single();
        Assert.Equal(sourceVersion, Value("pragma user_version", backup));
        Assert.Equal(1, Value("select count(*) from archive_case_closures", backup));
        Assert.Equal(0, Value("select count(*) from rpc_batches where id='case-retired'"));
        if (sourceVersion == 11) Assert.Equal(1, Value("select count(*) from cards", backup));
        Assert.DoesNotContain("paidonlyuniqueterm", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(backup)!, "manifest.json")));
        Assert.Equal(sourceVersion, manifest.RootElement.GetProperty("sourceVersion").GetInt32());
        Assert.Equal(13, manifest.RootElement.GetProperty("targetVersion").GetInt32());
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string path = Path.Combine(Path.GetDirectoryName(backup)!, file.GetProperty("name").GetString()!);
            using var sha = SHA256.Create();
            Assert.Equal(file.GetProperty("sha256").GetString(), BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""));
        }
        using (var reopened = ArchiveDb.Open(_directory, "p")) Assert.Equal(original, reopened.GetOriginal("one", "store")!.Text);
        Assert.Single(Directory.GetFiles(Backups, "*.engine.db", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(11, "backup-error")]
    [InlineData(12, "backup-error")]
    [InlineData(11, "corrupt")]
    [InlineData(12, "corrupt")]
    [InlineData(11, "changed")]
    [InlineData(12, "changed")]
    [InlineData(11, "changed-retired-data")]
    [InlineData(12, "changed-retired-data")]
    [InlineData(11, "cancel")]
    [InlineData(12, "cancel")]
    [InlineData(11, "commit-error")]
    [InlineData(12, "commit-error")]
    [InlineData(11, "ddl-error")]
    [InlineData(11, "destination-error")]
    [InlineData(12, "destination-error")]
    [InlineData(11, "cancel-at-commit")]
    [InlineData(12, "cancel-at-commit")]
    public void FailureNeverConfirmsMigration_AndRetryUsesTheRetainedOriginal(int sourceVersion, string failure)
    {
        Legacy(sourceVersion); using var cancel = new CancellationTokenSource();
        var options = new ArchiveMigrationOptions
        {
            BackupCreated = path =>
            {
                if (failure == "backup-error") throw new IOException("synthetic backup failure");
                if (failure == "corrupt") File.WriteAllBytes(path, new byte[20]);
                if (failure == "changed") { using var copy = Raw(path); using var q = copy.CreateCommand(); q.CommandText = "update emails set full_body='wrong'"; q.ExecuteNonQuery(); }
                if (failure == "changed-retired-data") { using var copy = Raw(path); using var q = copy.CreateCommand(); q.CommandText = sourceVersion == 11 ? "delete from archive_saved_cards" : "delete from archive_case_closures"; q.ExecuteNonQuery(); }
                if (failure == "cancel") cancel.Cancel();
            },
            BeforeCommit = () =>
            {
                if (failure == "commit-error") throw new IOException("synthetic commit failure");
                if (failure == "cancel-at-commit") cancel.Cancel();
            }
        };
        if (failure == "destination-error")
        {
            options.BackupRoot = Path.Combine(_directory, "destination-file");
            File.WriteAllText(options.BackupRoot, "not a directory");
        }
        if (failure == "ddl-error") Sql("create trigger fail_migration before delete on meta begin select raise(abort,'synthetic failure'); end;");
        Assert.ThrowsAny<Exception>(() => ArchiveDb.OpenForMigration(_directory, "p", options, cancel.Token));
        Assert.Equal(sourceVersion, Value("pragma user_version"));
        Assert.Equal(1, Value("select count(*) from archive_case_closures"));
        if (sourceVersion == 11) Assert.Equal(1, Value("select count(*) from cards"));
        Assert.Equal(1, Value("select count(*) from emails where full_body like 'Ordine ORD-98765%'"));
        if (failure == "ddl-error") Sql("drop trigger fail_migration");
        using var retry = ArchiveDb.Open(_directory, "p");
        Assert.Single(retry.Search(new ArchiveQuery { Text = "98765" }));
    }

    [Fact]
    public void InsufficientSpaceDoesNotBeginBackupOrDeleteData()
    {
        Legacy();
        Assert.Throws<IOException>(() => ArchiveDb.Open(_directory, "p", availableFreeSpace: _ => 0));
        Assert.False(Directory.Exists(Backups));
        Assert.Equal(11, Value("pragma user_version"));
        Assert.Equal(1, Value("select count(*) from cards"));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void VerifiedBackupCanBeRestoredAndMigratedAgainInIsolation(int sourceVersion)
    {
        Legacy(sourceVersion); using (ArchiveDb.Open(_directory, "p")) { }
        string backup = Directory.GetFiles(Backups, "*.engine.db", SearchOption.AllDirectories).Single();
        string restored = Path.Combine(_directory, "restored"); Directory.CreateDirectory(restored);
        File.Copy(backup, ArchiveDb.EngineFileOf(restored, "p"));
        File.Copy(Path.Combine(Path.GetDirectoryName(backup)!, "archive.key"), Path.Combine(restored, "archive.key"));
        using var db = ArchiveDb.Open(restored, "p");
        Assert.Contains("ORD-98765", db.GetOriginal("one", "store")!.Text);
        Assert.Single(db.Search(new ArchiveQuery { Text = "attachmentuniqueterm" }));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(999)]
    public void UnknownFormatsAreRefusedWithoutDeletingAnyData(int version)
    {
        Legacy(); Sql("pragma user_version=" + version);
        Assert.Throws<InvalidDataException>(() => ArchiveDb.Open(_directory, "p"));
        Assert.Equal(version, Value("pragma user_version")); Assert.Equal(1, Value("select count(*) from cards"));
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void MissingDerivedIndexesAreRebuiltFromPreservedOriginalsWithoutOutlook()
    {
        Legacy(); using (ArchiveDb.Open(_directory, "p")) { }
        Sql("drop table body_fts; drop table attachment_fts; drop table code_body_fts; drop table code_attachment_fts; drop table archive_facts; drop table archive_message_refs;");
        using var db = ArchiveDb.Open(_directory, "p");
        Assert.Single(db.Search(new ArchiveQuery { Text = "attachmentuniqueterm" }));
        Assert.Single(db.Search(new ArchiveQuery { Text = "98765" }));
        while (db.NextFactWork() is { } fact) Assert.True(db.CommitFacts(fact, ArchiveDb.ExtractFacts(fact, default)));
        while (db.NextSearchWork() is { } work) Assert.True(db.CommitSearch(work, ArchiveSearchText.CodeTerms(work.Body), ArchiveSearchText.CodeTerms(work.Attachments)));
        Assert.NotEmpty(db.GetFacts(1, true)); Assert.Contains("ORD-98765", db.GetOriginal("one", "store")!.Text);
        Assert.Equal(0, Value("select count(*) from sqlite_master where name='archive_case_closures'"));
        Assert.Equal(1, Value("select count(*) from archive_message_refs where value='<parent@example.test>'"));
    }

    [Fact]
    public void MissingKeyRefusesMigrationWithoutReplacingTheExistingArchive()
    {
        Legacy(); File.Move(Path.Combine(_directory, "archive.key"), Path.Combine(_directory, "saved.key"));
        byte[] original = File.ReadAllBytes(PathOf);
        Assert.Throws<ArchiveUnreadableException>(() => ArchiveDb.Open(_directory, "p"));
        Assert.Equal(original, File.ReadAllBytes(PathOf)); Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void CommittedWalIsInTheBackup_AndUncommittedWorkIsRolledBack()
    {
        Legacy();
        using var keeper = Raw(); using var command = keeper.CreateCommand();
        command.CommandText = "pragma journal_mode=wal; insert into meta values('wal-committed','kept')"; command.ExecuteNonQuery();
        using (var writer = Raw())
        {
            using var tx = writer.BeginTransaction(); using var pending = writer.CreateCommand(); pending.Transaction = tx;
            pending.CommandText = "insert into meta values('wal-incomplete','discarded')"; pending.ExecuteNonQuery();
        }
        using var db = ArchiveDb.Open(_directory, "p");
        Assert.Equal("kept", db.GetMeta("wal-committed")); Assert.Null(db.GetMeta("wal-incomplete"));
        string backup = Directory.GetFiles(Backups, "*.engine.db", SearchOption.AllDirectories).Single();
        Assert.Equal(1, Value("select count(*) from meta where key='wal-committed'", backup));
        Assert.Equal(0, Value("select count(*) from meta where key='wal-incomplete'", backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MigratedOrphanSourcesRequireOutlookProof_AndAnotherCopyKeepsHistory(bool anotherCopy)
    {
        Legacy();
        Sql("insert into archive_saved_sources values(2,'store','orphan','<orphan@example.test>',-1)");
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("store", "inbox", "Synthetic/Inbox");
        if (anotherCopy)
        {
            db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "copy", MessageId = "<orphan@example.test>", Subject = "Copy", ReceivedUtc = DateTime.UtcNow } }, true);
        }
        db.BeginRescan(new[] { folder.Id });
        void ListCurrent()
        {
            var headers = new List<ArchiveHeader> { new() { EntryId = "one", MessageId = "<one@example.test>", Subject = "Original subject", ReceivedUtc = DateTime.UtcNow } };
            if (anotherCopy) headers.Add(new ArchiveHeader { EntryId = "copy", MessageId = "<orphan@example.test>", Subject = "Copy", ReceivedUtc = DateTime.UtcNow });
            db.AddHeaders(db.GetOrAddFolder("store", "inbox", "Synthetic/Inbox"), headers, true, true);
        }
        ListCurrent();
        var source = Assert.Single(db.UnseenCacheCopies(10)); Assert.Equal(-2, source.Id);
        Assert.Null(db.FinishRescan());
        db.ConfirmCacheCopies(new[] { source.Id }, Array.Empty<long>());
        Assert.DoesNotContain(db.PendingDeletions(), d => d.EntryId == "orphan");
        db.BeginRescan(new[] { folder.Id });
        ListCurrent();
        db.ConfirmCacheCopies(Array.Empty<long>(), new[] { source.Id });
        Assert.Equal(!anotherCopy, db.PendingDeletions().Any(d => d.EntryId == "orphan"));
    }

    [Fact]
    public void LazyLegacySourceListDoesNotLoseHistoryDependenciesDuringRetirement()
    {
        Legacy();
        Sql("insert into archive_saved_cards values('store','orphan-not-queued','<orphan-lazy@example.test>','{}')");
        using var db = ArchiveDb.Open(_directory, "p");
        var folder = db.GetOrAddFolder("store", "inbox", "Synthetic/Inbox");
        db.BeginRescan(new[] { folder.Id });
        db.AddHeaders(db.GetOrAddFolder("store", "inbox", "Synthetic/Inbox"), new[] { new ArchiveHeader { EntryId = "one", MessageId = "<one@example.test>", Subject = "Original subject", ReceivedUtc = DateTime.UtcNow } }, true, true);
        var source = Assert.Single(db.UnseenCacheCopies(10));
        Assert.Equal("orphan-not-queued", source.EntryId); Assert.Equal(-2, source.Id);
        Assert.Null(db.FinishRescan());
        db.ConfirmCacheCopies(Array.Empty<long>(), new[] { source.Id });
        Assert.Contains(db.PendingDeletions(), d => d.EntryId == "orphan-not-queued");
    }
}
