using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using Xunit.Abstractions;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveFactStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") ?? Path.GetTempPath(), "ow-facts-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    public ArchiveFactStorageTests(ITestOutputHelper output) => _output = output;
    public void Dispose() { if (Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") == null && Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private ArchiveDb Open() => ArchiveDb.Open(_directory, "p");
    private static long Add(ArchiveDb db, string entry, string text = "Importo EUR 1.000,00\nScadenza: 2026-11-30", ArchiveAttachment? attachment = null)
    {
        var folder = db.GetOrAddFolder("store", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = entry, MessageId = "<" + entry + "@example.test>", Subject = "Synthetic invoice " + entry, ReceivedUtc = new DateTime(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc), HasAttachments = attachment != null } }, true, true);
        long id = db.NextPendingBodies(500).Single(e => e.EntryId == entry).Id;
        var body = new ArchiveBody { Text = text }; if (attachment != null) body.Attachments.Add(attachment);
        db.SaveBody(id, body); return id;
    }
    private static void Drain(ArchiveDb db)
    {
        while (db.NextFactWork() is { } work) Assert.True(db.CommitFacts(work, ArchiveDb.ExtractFacts(work, CancellationToken.None)));
    }
    private static ArchiveQuery Query(ArchiveFactFilter filter, bool attachments = true, int max = 50, int offset = 0)
    {
        var q = new ArchiveQuery { IncludeAttachmentText = attachments, IncludeFacts = true, Max = max, Offset = offset }; q.FactFilters.Add(filter); return q;
    }
    private void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ArchiveDb.EngineFileOf(_directory, "p"), Pooling = false,
            Password = ArchiveKey.LoadOrCreate(Path.Combine(_directory, "archive.key")) }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private static string DurableDigest(ArchiveDb db, long id)
    {
        var text = JsonSerializer.Serialize(new { Original = db.GetOriginal("one", "store"), Preference = db.GetMeta("stores"), Version = db.GetBodyVersion(id) });
        using var hash = SHA256.Create(); return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void ExactCopies_CombineWithStorePolicyAndPreserveTheFolderNameOnErrors()
    {
        using var db = Open();
        long id = Add(db, "one", "Scadenza pagamento: 15/11/2026");
        Drain(db);
        var query = new ArchiveQuery { EmailRefs = new() { new() { EntryId = "one", StoreId = "store" } },
            AllowedStoreIds = new() { "store" }, IncludeFacts = true };
        Assert.Equal(id, Assert.Single(db.Search(query)).Id);
        query.AllowedStoreIds = new() { "another" }; Assert.Empty(db.Search(query));
        query.AllowedStoreIds = new(); Assert.Empty(db.Search(query));
        query.AllowedStoreIds = null;
        query.EmailRefs![0].StoreId = "another"; Assert.Empty(db.Search(query));
        query.EmailRefs.Clear(); Assert.Empty(db.Search(query));
        var folder = db.GetOrAddFolder("store", "folder", "Synthetic\\Inbox");
        var reopened = db.GetOrAddFolder("store", "folder", "");
        Assert.Equal(folder.Path, reopened.Path);
        db.SetFolderError(folder.Id, reopened.UnavailableMessage());
        Assert.Contains("Synthetic\\Inbox", db.GetOrAddFolder("store", "folder", "").Error);
        Assert.Contains("nessuna eliminazione dedotta", reopened.UnavailableMessage());
        db.SetListedFolders(new[] { folder.Id });
        db.SetFolderError(folder.Id, "Cartella non disponibile; nessuna eliminazione dedotta.");
        var coverage = Assert.Single(db.GetStoreCoverage());
        Assert.Contains("Synthetic\\Inbox", coverage.Error);
        Assert.Contains("Controlla in Outlook", coverage.Error);
        db.SetFolderError(folder.Id, reopened.UnavailableMessage());
        Assert.Equal(reopened.UnavailableMessage(), Assert.Single(db.GetStoreCoverage()).Error);
        Assert.Equal(1, db.GetStats().Emails);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderCounts_PartitionErrorsIncludingPreviouslyCompletedFolders_WithoutChangingSavedErrors(bool previouslyDone)
    {
        const string savedError = "Cartella «Synthetic\\Cestino\\Prova» non disponibile; nessuna eliminazione dedotta. " +
            "Riaprila in Outlook e attendi il prossimo controllo completo.";
        using (var db = Open())
        {
            for (int i = 0; i < 27; i++)
            {
                var folder = db.GetOrAddFolder("store", "done-" + i, "Synthetic\\Cartella " + i);
                db.AddHeaders(folder, Array.Empty<ArchiveHeader>(), true, true);
            }
            var failed = db.GetOrAddFolder("store", "failed", "Synthetic\\Cestino\\Prova");
            if (previouslyDone) db.AddHeaders(failed, Array.Empty<ArchiveHeader>(), true, true);
            db.SetFolderError(failed.Id, savedError);
        }
        using (var db = Open())
        {
            var stats = db.GetStats();
            Assert.Equal((28, 27, 1), (stats.Folders, stats.FoldersDone, stats.FoldersFailed));
            var metric = ArchiveMetrics.Build(stats).Single(m => m.Key == "folders");
            Assert.Equal((0L, 100), (metric.Remaining, metric.Percent));
            var store = Assert.Single(db.GetStoreCoverage());
            Assert.Equal((28, 27), (store.Folders, store.FoldersDone));
            Assert.Contains("senza ricrearla se eliminata", store.Error);
            Assert.DoesNotContain("Riaprila", store.Error);
            var failed = db.GetOrAddFolder("store", "failed", "");
            Assert.Equal(savedError, failed.Error); // Projection does not rewrite the recorded failure.
            Assert.Equal(previouslyDone, failed.Done); // Nor the resume bookmark.
            db.GetOrAddFolder("store", "pending", "Synthetic\\Da leggere");
            metric = ArchiveMetrics.Build(db.GetStats()).Single(m => m.Key == "folders");
            Assert.Equal((29L, 27L, 1L, 1L, 96), (metric.Total, metric.Done, metric.Remaining, metric.Excluded, metric.Percent));
            db.AddHeaders(failed, Array.Empty<ArchiveHeader>(), true, true);
            stats = db.GetStats();
            Assert.Equal((29, 28, 0), (stats.Folders, stats.FoldersDone, stats.FoldersFailed));
            Assert.Empty(Assert.Single(db.GetStoreCoverage()).Error);
        }
    }

    [Fact]
    public void RevisionTwoDeadlines_AreRecoveredWithoutChangingDurableData()
    {
        long id; string digest;
        using (var db = Open())
        {
            id = Add(db, "one", "Importo 1000,01 EUR\nScadenza pagamento: 15/11/2026");
            db.SetMeta("stores", "[]");
            Drain(db); digest = DurableDigest(db, id);
        }
        Sql("update archive_fact_jobs set revision=2; update archive_facts set revision=2,kind=case when kind=1 then 0 else kind end;");
        using (var db = Open())
        {
            Assert.Empty(db.GetFacts(id, true));
            db.SetMeta("paused", "1"); Assert.Null(db.NextFactWork());
        }
        using (var db = Open())
        {
            db.SetMeta("paused", null); Drain(db);
            Assert.Contains(db.GetFacts(id, true), f => f.Kind == ArchiveFactKind.Deadline && f.Date == "2026-11-15");
            Assert.Equal(digest, DurableDigest(db, id));
            Assert.Null(db.NextFactWork());
        }
    }

    [Fact]
    public void ExtractorRevisionUpgrade_RecoversOnlyFactsAcrossPauseAndReopen()
    {
        long id; string digest;
        using (var db = Open())
        {
            id = Add(db, "one", "Importo 1000,01 EUR", new ArchiveAttachment { Name = "invoice.txt", SourceId = "a", State = "indexed", Text = "Amount 25.50 USD" });
            db.SetMeta("stores", "[]");
            Drain(db); digest = DurableDigest(db, id);
        }
        // Persist the actual prior-revision defect, rather than treating new extraction as old output.
        Sql("update archive_fact_jobs set revision=1; update archive_facts set revision=1,currency=null where kind=2;");
        using (var db = Open())
        {
            Assert.Equal(1, db.GetFactProgress().Pending); Assert.Empty(db.GetFacts(id, true));
            db.SetMeta("paused", "1"); Assert.Null(db.NextFactWork());
        }
        using (var db = Open())
        {
            Assert.Null(db.NextFactWork()); db.SetMeta("paused", null); Drain(db);
            Assert.Equal(1, db.GetFactProgress().Done); Assert.Equal(0, db.GetFactProgress().Pending);
            Assert.Null(db.NextFactWork());
            Assert.Equal(digest, DurableDigest(db, id));
            var amounts = db.GetFacts(id, true).Where(f => f.Kind == ArchiveFactKind.Amount).ToList();
            Assert.Equal(2, amounts.Count);
            Assert.Contains(amounts, f => f.Value == "1000.01" && f.Currency == "EUR");
            Assert.Contains(amounts, f => f.Value == "25.5" && f.Currency == "USD" && f.SourceId == "a");
        }
    }

    [Fact]
    public async Task LabelledCurrencyFilters_CrossThePipeAndRespectAttachmentPolicy()
    {
        long body, attached;
        using (var db = Open())
        {
            body = Add(db, "one", "Importo 1000,01 EUR");
            attached = Add(db, "two", "Synthetic text", new ArchiveAttachment { Name = "amount.txt", SourceId = "a", State = "indexed", Text = "Amount 1000.01 USD" });
        }
        bool attachmentsEnabled = true;
        using var engine = new ArchiveEngine(_directory, enabled: null, indexAttachmentText: () => attachmentsEnabled); await engine.Ready;
        var client = new ArchiveClient("p", _directory); await client.OpenAsync();
        var timer = Stopwatch.StartNew();
        while ((await client.GetFactProgressAsync()).Pending > 0 && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(25);
        Assert.Equal(2, (await client.GetFactProgressAsync()).Done);
        var filter = new ArchiveFactFilter { Kind = ArchiveFactKind.Amount, AmountMin = 1000.01m, AmountMax = 1000.01m, Currency = "EUR" };
        Assert.Equal(body, Assert.Single(await client.SearchAsync(Query(filter))).Id);
        filter.Currency = "USD";
        Assert.Equal(attached, Assert.Single(await client.SearchAsync(Query(filter))).Id);
        Assert.Empty(await client.SearchAsync(Query(filter, attachments: false)));
        attachmentsEnabled = false;
        Assert.Empty(await client.SearchAsync(Query(filter)));
    }

    [Fact]
    public void Backfill_ResumesAfterReopen_AndPreservesOriginalsAndChoices()
    {
        long id; string original;
        using (var db = Open())
        {
            id = Add(db, "one"); Add(db, "two");
            db.SetMeta("stores", "[]");
            original = DurableDigest(db, id);
            var work = db.NextFactWork()!; Assert.True(db.CommitFacts(work, ArchiveDb.ExtractFacts(work, CancellationToken.None)));
            Assert.Equal(1, db.GetFactProgress().Done); Assert.Equal(1, db.GetFactProgress().Pending);
        }
        using (var db = Open())
        {
            Drain(db); Assert.Equal(2, db.GetFactProgress().Done); Assert.Equal(0, db.GetFactProgress().Pending);
            Assert.Equal(original, DurableDigest(db, id));
            Assert.Contains(db.GetFacts(id, true), f => f.Kind == ArchiveFactKind.Amount && f.Value == "1000");
        }
    }

    [Fact]
    public void PecData_IsLocalOnly_AndPreservesTheOriginalBodyVersion()
    {
        using var db = Open();
        var receipt = new ArchiveAttachment { Name = "daticert.xml", SourceId = "a", State = "excluded", Problem = "inline-or-receipt" };
        long id = Add(db, "pec", "Original body", receipt);
        string version = db.GetBodyVersion(id)!;
        var body = new ArchiveBody { Text = "Original body" };
        receipt.State = "facts-only"; receipt.Problem = null; receipt.Text = "<postacert tipo=\"avvenuta-consegna\"><mittente>sender@example.test</mittente></postacert>";
        body.Attachments.Add(receipt); db.SaveBody(id, body); Drain(db);
        Assert.Equal(version, db.GetBodyVersion(id));
        Assert.Contains(db.GetFacts(id, true), f => f.Label == "postacert.tipo");
        Assert.Empty(db.GetFacts(id, false)); Assert.Empty(db.Search(new ArchiveQuery { Text = "postacert" }));
        Assert.DoesNotContain("postacert", ArchiveAttachmentReader.WholeText(db.GetOriginal("pec", "store")!));
        db.ConfigureStorage(false); Drain(db); Assert.Empty(db.GetFacts(id, true));
    }

    [Fact]
    public async Task PecReader_UsesRealHelper_ButKeepsReceiptOutOfModelText()
    {
        Directory.CreateDirectory(_directory); string path = Path.Combine(_directory, "daticert.xml");
        File.WriteAllText(path, "<postacert tipo=\"accettazione\"><mittente>sender@example.test</mittente></postacert>");
        var attachment = await ArchiveAttachmentReader.ReadAsync(new OnlyWorkspace.Core.Security.AttachmentFacts("daticert.xml", new FileInfo(path).Length) { IsInline = true }, "a", path, CancellationToken.None);
        Assert.Equal("facts-only", attachment.State); Assert.Contains("postacert", attachment.Text);
        var body = new ArchiveBody { Text = "body" }; ArchiveAttachmentReader.Add(body, attachment);
        Assert.False(body.AttachmentsLimited); Assert.Equal("body", ArchiveAttachmentReader.WholeText(body));
    }

    [Fact]
    public void SourcesChangedWhileExtracting_RejectStaleCommitAndKeepOnlyNewFacts()
    {
        using var db = Open(); long id = Add(db, "one"); var old = db.NextFactWork()!;
        db.SaveBody(id, new ArchiveBody { Text = "Importo EUR 25,00" });
        Assert.False(db.CommitFacts(old, ArchiveDb.ExtractFacts(old, CancellationToken.None)));
        Assert.Empty(db.GetFacts(id, true)); Drain(db);
        Assert.Equal("25", Assert.Single(db.GetFacts(id, true), f => f.Kind == ArchiveFactKind.Amount).Value);
        db.SetBodyState(id, ArchiveBodyState.Pending); Assert.Empty(db.GetFacts(id, true));
    }

    [Fact]
    public void AttachmentPolicyAndPartialText_AreAppliedBeforeExtractionAndBeforeCommit()
    {
        using var db = Open(); db.ConfigureStorage(true);
        long id = Add(db, "one", "Body only", new ArchiveAttachment { SourceId = "a", Name = "invoice.txt", State = "indexed", Version = "hash", Text = "EUR 1000.00" });
        var stale = db.NextFactWork()!; db.ConfigureStorage(false);
        Assert.False(db.CommitFacts(stale, ArchiveDb.ExtractFacts(stale, CancellationToken.None))); Drain(db); Assert.Empty(db.GetFacts(id, true));
        db.ConfigureStorage(true);
        var body = new ArchiveBody { Text = "Scadenza 2026-11-30" };
        body.Attachments.Add(new ArchiveAttachment { SourceId = "a", Name = "partial.txt", State = "limited", TextLimited = true, Text = "EUR 1000.00" });
        db.SaveBody(id, body); Drain(db);
        Assert.DoesNotContain(db.GetFacts(id, true), f => f.Kind == ArchiveFactKind.Amount);
        Assert.Contains(db.GetFacts(id, true), f => f.Kind == ArchiveFactKind.Deadline);
    }

    [Fact]
    public void Filters_UseExactDecimalsBeforePaging_AndCombineTextAmountCurrencyAndDueDay()
    {
        using var db = Open();
        long first = Add(db, "one", "Needle EUR 1000.00\nScadenza: 2026-11-30");
        long second = Add(db, "two", "Needle EUR 1000.01\nScadenza: 2026-11-30");
        Add(db, "three", "Other USD 1000.00\nData: 2026-11-30");
        Drain(db);
        var q = Query(new ArchiveFactFilter { AmountMin = 1000m, AmountMax = 1000m, Currency = "eur" }); q.Text = "Needle";
        q.FactFilters.Add(new ArchiveFactFilter { Kind = ArchiveFactKind.Deadline, DateFrom = "2026-11-30", DateTo = "2026-11-30" });
        Assert.Equal(first, Assert.Single(db.Search(q)).Id);
        q.FactFilters[0].AmountMax = 1000.01m; q.Max = 1;
        Assert.Equal(second, Assert.Single(db.Search(q)).Id); q.Offset = 1; Assert.Equal(first, Assert.Single(db.Search(q)).Id);
        q.FactFilters[0].AmountMin = 1001; Assert.Throws<ArgumentException>(() => db.Search(q));
        Assert.Throws<ArgumentException>(() => db.Search(Query(new ArchiveFactFilter { DateFrom = "invalid" })));
    }

    [Fact]
    public void AttachmentQuerySwitch_ExcludesAttachmentFactsAndCannotLeakByFilter()
    {
        using var db = Open();
        long id = Add(db, "one", "body", new ArchiveAttachment { Name = "amount.txt", SourceId = "one", State = "indexed", Text = "EUR 1000.00" }); Drain(db);
        var filter = new ArchiveFactFilter { Kind = ArchiveFactKind.Amount, AmountMin = 1000, AmountMax = 1000 };
        Assert.Equal(id, Assert.Single(db.Search(Query(filter))).Id);
        Assert.Empty(db.Search(Query(filter, false))); Assert.Empty(db.GetFacts(id, false));
    }

    [Fact]
    public void PauseAndFailedPass_DoNotClaimCompletionOrLoopOnTheSameSource()
    {
        using var db = Open(); Add(db, "one"); var work = db.NextFactWork()!;
        db.SetMeta("paused", "1"); Assert.Null(db.NextFactWork()); Assert.False(db.CommitFacts(work, ArchiveDb.ExtractFacts(work, CancellationToken.None)));
        db.SetMeta("paused", null); Assert.True(db.CommitFacts(work, Array.Empty<ArchiveFact>(), "RegexMatchTimeoutException"));
        Assert.Equal(1, db.GetFactProgress().Failed); Assert.Equal(0, db.GetFactProgress().Done); Assert.Null(db.NextFactWork());
        Assert.Empty(db.Search(Query(new ArchiveFactFilter { AmountMin = 1 })));
    }

    [Fact]
    public void DeletionAndRebuild_RemoveDerivedFacts_AndKeepDurableDecisions()
    {
        using (var db = Open()) { long id = Add(db, "one"); Drain(db); db.SetMeta("sentinel", "kept"); db.SetBodyState(id, ArchiveBodyState.Gone); Assert.Empty(db.GetFacts(id, true)); }
        Sql("drop table archive_facts; drop table archive_fact_jobs");
        using (var db = Open()) { Assert.Equal("kept", db.GetMeta("sentinel")); Assert.Equal(0, db.GetFactProgress().Done); Assert.Empty(db.Search(new ArchiveQuery())); }
    }

    [Fact]
    public void OcrReader_RetriesScansAndImagesOnce_WithoutTouchingReadTexts()
    {
        using (var db = Open())
        {
            Add(db, "one");
            Add(db, "pdf", "body", new ArchiveAttachment { Name = "file.pdf", SourceId = "pdf", State = "scanned", Problem = "scanned" });
            Add(db, "photo", "body", new ArchiveAttachment { Name = "foto.JPG", SourceId = "jpg", State = "unsupported", Problem = "format" });
            Add(db, "zip", "body", new ArchiveAttachment { Name = "pratica.zip", SourceId = "zip", State = "limited", Problem = "scanned" });
            Add(db, "rar", "body", new ArchiveAttachment { Name = "pratica.rar", SourceId = "rar", State = "unsupported", Problem = "format" });
            Assert.Empty(db.NextPendingBodies(100));
            db.ConfigureStorage(true, attachmentOcr: true);
            Assert.Equal(new[] { "pdf", "photo", "zip" }, db.NextPendingBodies(100).Select(e => e.EntryId).OrderBy(e => e, StringComparer.Ordinal));
        }
        using (var db = Open())
        {
            db.ConfigureStorage(true, attachmentOcr: true);
            Assert.Equal(3, db.NextPendingBodies(100).Count); // once: nothing more queued on the next opening
        }
    }

    [Fact]
    public void OcrOff_KeepsRecognizedTexts_AndTurningItOnRereadsWhatStayedUnread()
    {
        using var db = Open();
        db.ConfigureStorage(true, attachmentOcr: false);
        long read = Add(db, "read", "body", new ArchiveAttachment { Name = "scan.pdf", SourceId = "read", State = "indexed", Text = "[testo riconosciuto con OCR] Fattura ricevutaocr987" });
        Add(db, "off", "body", new ArchiveAttachment { Name = "foto.png", SourceId = "off", State = "ocr-off", Problem = "ocr-off" });
        Add(db, "inner", "body", new ArchiveAttachment { Name = "pratica.zip", SourceId = "inner", State = "limited", Problem = "ocr-off" });
        Assert.Empty(db.NextPendingBodies(100));
        Assert.Equal("0", db.GetMeta("ocr_reader"));

        db.ConfigureStorage(true, attachmentOcr: false); // still off: nothing deleted, nothing queued
        Assert.Empty(db.NextPendingBodies(100));
        Assert.Equal(read, Assert.Single(db.Search(new ArchiveQuery { Text = "ricevutaocr987", IncludeAttachmentText = true })).Id);

        db.ConfigureStorage(true, attachmentOcr: true);
        Assert.Equal(new[] { "inner", "off" }, db.NextPendingBodies(100).Select(e => e.EntryId).OrderBy(e => e, StringComparer.Ordinal));
        Assert.Equal("1", db.GetMeta("ocr_reader"));
    }

    [Fact]
    public void OcrLanguage_RereadsUnavailableScansOnce_WhenARecognizerAppears()
    {
        using (var db = Open())
        {
            db.ConfigureStorage(true, attachmentOcr: true);
            Add(db, "scan", "body", new ArchiveAttachment { Name = "scan.pdf", SourceId = "scan", State = "ocr-unavailable", Problem = "ocr-unavailable" });
            Add(db, "inner", "body", new ArchiveAttachment { Name = "pratica.zip", SourceId = "inner", State = "limited", Problem = "ocr-unavailable" });
            Add(db, "other", "body", new ArchiveAttachment { Name = "foto.png", SourceId = "other", State = "scanned", Problem = "scanned" });
            db.ConfigureStorage(true, attachmentOcr: true, ocrLanguage: null); // not known yet
            db.ConfigureStorage(true, attachmentOcr: true, ocrLanguage: "");   // no recognizer
            Assert.Empty(db.NextPendingBodies(100));

            db.ConfigureStorage(true, attachmentOcr: true, ocrLanguage: "it-IT");
            Assert.Equal(new[] { "inner", "scan" }, db.NextPendingBodies(100).Select(e => e.EntryId).OrderBy(e => e, StringComparer.Ordinal));
            Assert.Equal("it-IT", db.GetMeta("ocr_language"));
        }
        using (var db = Open())
        {
            db.ConfigureStorage(true, attachmentOcr: true, ocrLanguage: "it-IT");
            Assert.Equal(2, db.NextPendingBodies(100).Count); // once: the same language queues nothing more
        }
    }

    [Fact]
    public void AttachmentStateLabels_AreItalian_ForEveryState()
    {
        foreach (var state in ArchiveAttachment.States)
            Assert.DoesNotContain(state, ArchiveAttachment.StateLabel(state), StringComparison.Ordinal);
        Assert.Equal("non leggibile dopo ripetuti tentativi", ArchiveAttachment.StateLabel("unavailable"));
        Assert.Equal("stato sconosciuto: nuovo", ArchiveAttachment.StateLabel("nuovo"));
    }

    [Fact]
    public void AddingFactsToExistingFormat_DoesNotRebuildBodies_AndRetriesOnlyCalendarCandidates()
    {
        long id;
        using (var db = Open())
        {
            id = Add(db, "one"); Add(db, "calendar", "body", new ArchiveAttachment { Name = "invito.ics", SourceId = "ics", State = "unsupported" });
            Add(db, "pdf", "body", new ArchiveAttachment { Name = "file.pdf", SourceId = "pdf", State = "scanned" });
            db.SetMeta("facts_calendar_reader", null);
        }
        Sql("drop table archive_facts; drop table archive_fact_jobs");
        using (var db = Open())
        {
            Assert.Equal(id, Assert.Single(db.Search(new ArchiveQuery { Text = "Scadenza" })).Id);
            Assert.NotNull(db.GetOriginal("one", "store"));
            Assert.Equal("calendar", Assert.Single(db.NextPendingBodies(100)).EntryId);
            Drain(db); Assert.Equal(2, db.GetFactProgress().Done);
        }
    }

    [Fact]
    public void SyntheticBackfill_MeasuresWholeLongSourcesAndResumesWithoutDuplicateOccurrences()
    {
        using var db = Open();
        string text = new string('x', 100_000) + "\nScadenza: 2026-11-30\nEUR 1.000,00\nOrdine ORD-2026/118";
        for (int i = 0; i < 30; i++) Add(db, "mail-" + i, text);
        var sw = Stopwatch.StartNew(); Drain(db); sw.Stop();
        _output.WriteLine("Synthetic backfill: {0} emails, {1} chars each, {2} ms", 30, text.Length, sw.ElapsedMilliseconds);
        Assert.Equal(30, db.GetFactProgress().Done); Assert.Null(db.NextFactWork());
        Assert.Equal(30, db.Search(Query(new ArchiveFactFilter { Kind = ArchiveFactKind.Order, Value = "ORD-2026/118" }, max: 100)).Count);
    }

    [Fact]
    public async Task EngineWorkerAndPipe_CompleteLocalBackfill_AndSerializeFiltersAndProvenance()
    {
        long id;
        using (var db = Open()) { id = Add(db, "one"); db.SetMeta("paused", "1"); }
        using var engine = new ArchiveEngine(_directory); await engine.Ready;
        var client = new ArchiveClient("p", _directory); await client.OpenAsync();
        Assert.Equal(1, (await client.GetFactProgressAsync()).Pending);
        await client.SetMetaAsync("paused", null);
        var sw = Stopwatch.StartNew();
        while ((await client.GetFactProgressAsync()).Pending > 0 && sw.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(25);
        Assert.Equal(1, (await client.GetFactProgressAsync()).Done);
        var q = Query(new ArchiveFactFilter { Kind = ArchiveFactKind.Amount, AmountMin = 1000, AmountMax = 1000 });
        var hit = Assert.Single(await client.SearchAsync(q)); Assert.Equal(id, hit.Id); Assert.NotEmpty(hit.Facts);
        Assert.Equal(15, ArchivePipe.Version);
    }
}
