using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveRebuildTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") ?? Path.GetTempPath(), "ow-reopen-" + Guid.NewGuid().ToString("N"));
    private string PathOf => ArchiveDb.EngineFileOf(_directory, "p");
    private string Backups => Path.Combine(_directory, "migration-backups");
    public void Dispose() { if (Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") == null && Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true); }

    private SqliteConnection Raw()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = PathOf,
            Password = ArchiveKey.LoadOrCreate(Path.Combine(_directory, "archive.key")),
            Pooling = false
        }.ToString());
        db.Open(); return db;
    }
    private void Sql(string sql) { using var db = Raw(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private long Value(string sql)
    { using var db = Raw(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }

    private string? StringValue(string sql)
    { using var db = Raw(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar() as string; }

    private void SeedCurrent()
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

    [Fact]
    public void CurrentFormatIsCreatedDirectlyAndReopensWithRetainedData()
    {
        SeedCurrent();
        Assert.Equal(13, Value("pragma user_version"));
        Assert.Equal("content_hash", StringValue("select name from pragma_index_info('ix_attachments_hash')"));
        using var db = ArchiveDb.Open(_directory, "p");
        Assert.Contains("ORD-98765", db.GetOriginal("one", "store")!.Text);
        Assert.Single(db.Search(new ArchiveQuery { Text = "attachmentuniqueterm" }));
        Assert.NotEmpty(db.GetFacts(1, true));
        Assert.False(Directory.Exists(Backups));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(999)]
    public void UnsupportedFormatsAreRefusedWithoutChangingData(int version)
    {
        SeedCurrent();
        Sql("create table retired_fixture(value text); insert into retired_fixture values('preserve'); pragma user_version=" + version);
        string key = Path.Combine(_directory, "archive.key");
        byte[] original = File.ReadAllBytes(PathOf);
        byte[] originalKey = File.ReadAllBytes(key);

        var error = Assert.Throws<InvalidDataException>(() => ArchiveDb.Open(_directory, "p"));

        Assert.Contains($"({version})", error.Message);
        Assert.Equal(original, File.ReadAllBytes(PathOf));
        Assert.Equal(originalKey, File.ReadAllBytes(key));
        Assert.Equal(version, Value("pragma user_version"));
        Assert.Equal(1, Value("select count(*) from retired_fixture"));
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void EmptyDatabaseWithUnsupportedVersionIsNotReinitialized()
    {
        Directory.CreateDirectory(_directory);
        Sql("pragma user_version=12");
        byte[] original = File.ReadAllBytes(PathOf);

        Assert.Throws<InvalidDataException>(() => ArchiveDb.Open(_directory, "p"));

        Assert.Equal(original, File.ReadAllBytes(PathOf));
        Assert.Equal(12, Value("pragma user_version"));
        Assert.Equal(0, Value("select count(*) from sqlite_master where type='table'"));
    }

    [Fact]
    public void MissingDerivedIndexesAreRebuiltFromPreservedOriginalsWithoutOutlook()
    {
        SeedCurrent();
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
    public void MissingKeyLeavesTheExistingArchiveUntouched()
    {
        SeedCurrent(); File.Move(Path.Combine(_directory, "archive.key"), Path.Combine(_directory, "saved.key"));
        byte[] original = File.ReadAllBytes(PathOf);
        Assert.Throws<ArchiveUnreadableException>(() => ArchiveDb.Open(_directory, "p"));
        Assert.Equal(original, File.ReadAllBytes(PathOf)); Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void ReopeningRetainsCommittedWalAndIgnoresRolledBackWork()
    {
        SeedCurrent();
        using var keeper = Raw(); using var command = keeper.CreateCommand();
        command.CommandText = "pragma journal_mode=wal; insert into meta values('wal-committed','kept')"; command.ExecuteNonQuery();
        using (var writer = Raw())
        {
            using var tx = writer.BeginTransaction(); using var pending = writer.CreateCommand(); pending.Transaction = tx;
            pending.CommandText = "insert into meta values('wal-incomplete','discarded')"; pending.ExecuteNonQuery();
        }
        using var db = ArchiveDb.Open(_directory, "p");
        Assert.Equal("kept", db.GetMeta("wal-committed")); Assert.Null(db.GetMeta("wal-incomplete"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrphanSourcesRequireOutlookProof_AndAnotherCopyKeepsHistory(bool anotherCopy)
    {
        SeedCurrent();
        Sql("insert into archive_source_checks values(2,'store','orphan','<orphan@example.test>',-1)");
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
}
