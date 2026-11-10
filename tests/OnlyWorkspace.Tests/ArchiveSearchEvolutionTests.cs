using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveSearchEvolutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") ?? Path.GetTempPath(), "OnlyWorkspace-E5-" + Guid.NewGuid().ToString("N"));
    private ArchiveDb Open() => ArchiveDb.Open(_directory, "p");
    private static readonly DateTime Day = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static long Add(ArchiveDb db, string entry, string body = "", string message = "", string reply = "", string refs = "", string store = "s", DateTime? sent = null, DateTime? received = null, string subject = "Synthetic")
    {
        var folder = db.GetOrAddFolder(store, "inbox", "\\Mailbox\\Inbox");
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = entry, MessageId = message, InReplyTo = reply, References = refs, SentUtc = sent,
            ReceivedUtc = received ?? Day, Subject = subject, ThreadMetadataComplete = true } }, true);
        long id = db.Search(new ArchiveQuery { EmailIds = null, Max = 500 }).Single(h => h.EntryId == entry && h.StoreId == store).Id;
        if (body.Length > 0) db.SaveBody(id, new ArchiveBody { Text = body, MessageId = message });
        return id;
    }
    private static void Drain(ArchiveDb db)
    {
        while (db.NextFactWork() is { } fact) Assert.True(db.CommitFacts(fact, ArchiveDb.ExtractFacts(fact, CancellationToken.None)));
        while (db.NextSearchWork() is { } search)
            Assert.True(db.CommitSearch(search, ArchiveSearchText.CodeTerms(search.Body), ArchiveSearchText.CodeTerms(search.Attachments)));
    }

    [Theory]
    [InlineData("2026/118")]
    [InlineData("D-2026/118")]
    [InlineData("ord-2026/118")]
    public void LiteralCodeFragments_FindTheWholeCode_WithoutBroadNumericMatches(string query)
    {
        using var db = Open();
        long expected = Add(db, "yes", "Ordine ORD-2026/118"); Add(db, "no", "Ordine ORD-2026/119");
        // Recovery has not run yet: acquired originals still give the correct result.
        Assert.Equal(expected, Assert.Single(db.Search(new ArchiveQuery { Text = query })).Id);
        Drain(db);
        Assert.Equal(expected, Assert.Single(db.Search(new ArchiveQuery { Text = query })).Id);
    }

    [Fact]
    public void DifferentSourcesCanSatisfyDifferentCodeTerms_AndFtsOperatorsAreData()
    {
        using var db = Open(); long id = Add(db, "one", "REF-12345", subject: "Ticket XYZ-98765"); Drain(db);
        Assert.Equal(id, Assert.Single(db.Search(new ArchiveQuery { Text = "12345 98765" })).Id);
        Assert.Empty(db.Search(new ArchiveQuery { Text = "77777\" OR NOT NEAR(" }));
        Assert.Single(db.Search(new ArchiveQuery { Text = "12345\" OR NOT NEAR(" }));
    }

    [Fact]
    public void CompleteCodeIndexDoesNotReinspectEveryOriginal()
    {
        using var db = Open(); Add(db, "one", "Ordine ORD-2026/118"); Drain(db);
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(ArchiveDb).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(db)!;
        int inspections = 0;
        connection.CreateFunction<string?, string?, long>("ow_code_contains", (text, fragment) => { inspections++; return 0; });
        Assert.Single(db.Search(new ArchiveQuery { Text = "2026/118" }));
        Assert.Equal(0, inspections);
    }

    [Fact]
    public void ShortCodesUseExactReferenceFacts_AndNeverMergeDistinctNumbers()
    {
        using var db = Open(); long id = Add(db, "one", "Ordine 12"); Add(db, "other", "Ordine 123"); Drain(db);
        Assert.Equal(id, Assert.Single(db.Search(new ArchiveQuery { Text = "12" })).Id);
    }

    [Fact]
    public void RecoveryResumesAndRejectsStaleWork_WithoutChangingOriginals()
    {
        long id; string original;
        using (var db = Open())
        {
            id = Add(db, "one", "Ordine ORD-2026/118");
            original = db.GetOriginal("one", "s")!.Text;
            var work = db.NextSearchWork()!; db.SetMeta("paused", "1");
            Assert.False(db.CommitSearch(work, "OLD-11111", "")); Assert.Null(db.NextSearchWork()); db.SetMeta("paused", null);
        }
        using (var db = Open())
        {
            Drain(db); Assert.Null(db.NextSearchWork());
            Assert.Equal(original, db.GetOriginal("one", "s")!.Text);
            db.SaveBody(id, new ArchiveBody { Text = "Ordine NEW-22222" }); var old = db.NextSearchWork()!;
            db.SaveBody(id, new ArchiveBody { Text = "Ordine NEW-33333" }); Assert.False(db.CommitSearch(old, "NEW-22222", ""));
            Drain(db); Assert.Empty(db.Search(new ArchiveQuery { Text = "22222" })); Assert.Single(db.Search(new ArchiveQuery { Text = "33333" }));
            db.SetBodyState(id, ArchiveBodyState.Gone); Assert.Empty(db.Search(new ArchiveQuery { Text = "33333" })); Drain(db);
        }
    }

    [Fact]
    public void AttachmentTermsAndSharedFactsRespectPolicy_AndPecIsNeverSearchable()
    {
        using var db = Open(); long id = Add(db, "file", "Plain body");
        var body = new ArchiveBody { Text = "Plain body" };
        body.Attachments.Add(new ArchiveAttachment { SourceId = "a", Name = "order.txt", Version = "hash", State = "indexed", Text = "Ordine FILE-99999" });
        body.Attachments.Add(new ArchiveAttachment { SourceId = "pec", Name = "daticert.xml", Version = "pec-hash", State = "facts-only", Text = "<postacert>PEC-88888</postacert>" });
        db.SaveBody(id, body); Drain(db);
        Assert.Single(db.Search(new ArchiveQuery { Text = "99999" })); Assert.Empty(db.Search(new ArchiveQuery { Text = "99999", IncludeAttachmentText = false }));
        Assert.Empty(db.Search(new ArchiveQuery { Text = "88888" }));
        db.ConfigureStorage(false); Assert.Empty(db.Search(new ArchiveQuery { Text = "99999" })); Drain(db); Assert.Empty(db.Search(new ArchiveQuery { Text = "99999" }));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("")]
    public void FactsOnlyReceiptCannotExpandAChainOrSupplyPromptLinkEvidence(string sourceId)
    {
        using var db = Open(); long a = Add(db, "a", "Plain body", "<a@test>");
        var body = new ArchiveBody { Text = "Plain body", MessageId = "<a@test>" };
        body.Attachments.Add(new ArchiveAttachment { SourceId = sourceId, Name = "daticert.xml", State = "facts-only", Version = "receipt", Text = "ordine LOCAL-12345" });
        db.SaveBody(a, body); Add(db, "b", "ordine LOCAL-12345", "<b@test>"); Drain(db);
        Assert.Contains(db.GetFacts(a, true), f => f.SourceName == "daticert.xml" && f.Kind == ArchiveFactKind.Order);
        var flow = db.GetFlow(new ArchiveFlowSeed { Id = a });
        Assert.Single(flow.Timeline); Assert.Empty(flow.Timeline[0].LinkFacts);
    }

    [Fact]
    public void ReceiptOccurrenceCannotLeakThroughAnEqualBodyReference()
    {
        using var db = Open(); long a = Add(db, "a", "Ordine LOCAL-12345", "<a@test>");
        var body = new ArchiveBody { Text = "Ordine LOCAL-12345", MessageId = "<a@test>" };
        body.Attachments.Add(new ArchiveAttachment { Name = "daticert.xml", State = "facts-only", Version = "receipt", Text = "LOCAL RECEIPT ONLY: ordine LOCAL-12345" });
        db.SaveBody(a, body); Add(db, "b", "ordine LOCAL-12345", "<b@test>"); Drain(db);
        var flow = db.GetFlow(new ArchiveFlowSeed { Id = a }); Assert.Equal(2, flow.Timeline.Count);
        Assert.All(flow.Timeline.SelectMany(m => m.LinkFacts), fact => Assert.DoesNotContain("LOCAL RECEIPT ONLY", fact.Context));
        Assert.DoesNotContain(flow.Timeline.SelectMany(m => m.LinkFacts), fact => fact.SourceName == "daticert.xml");
    }

    [Fact]
    public void SharedCachedTextCorrectionInvalidatesEveryDependentCodeIndex()
    {
        using var db = Open();
        foreach (string entry in new[] { "a", "b" })
        {
            long id = Add(db, entry, "Body");
            var body = new ArchiveBody { Text = "Body" };
            body.Attachments.Add(new ArchiveAttachment { SourceId = "file", Name = "order.txt", Version = "same-file-hash", State = "indexed", Text = "ORDER-12345" });
            db.SaveBody(id, body);
        }
        Drain(db); Assert.Equal(2, db.Search(new ArchiveQuery { Text = "12345" }).Count);
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(ArchiveDb).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(db)!;
        using (var command = connection.CreateCommand()) { command.CommandText = "update attachment_contents set text='ORDER-98765'"; command.ExecuteNonQuery(); }
        Assert.Empty(db.Search(new ArchiveQuery { Text = "12345" })); Assert.Equal(2, db.Search(new ArchiveQuery { Text = "98765" }).Count);
        Drain(db); Assert.Empty(db.Search(new ArchiveQuery { Text = "12345" })); Assert.Equal(2, db.Search(new ArchiveQuery { Text = "98765" }).Count);
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(true, 2, false)]
    [InlineData(true, 2, true)]
    public void SavingSharedTextThroughUpsertKeepsOneSearchJobPerEmail(bool pending, int copies, bool upgrade)
    {
        using var initial = Open();
        var db = initial;
        long a = Add(db, "a", "Original A"), b = Add(db, "b", "Original B");
        ArchiveBody Body(string text, string attachment, int count)
        {
            var body = new ArchiveBody { Text = text };
            body.Attachments.AddRange(Enumerable.Range(0, count).Select(i => new ArchiveAttachment
            {
                SourceId = "file" + i,
                Name = "order.txt",
                Version = "same-file-hash",
                State = "indexed",
                Text = attachment
            }));
            return body;
        }
        db.SaveBody(a, Body("Original A", "Ordine OLD-12345", 1));
        db.SaveBody(b, Body("Original B", "Ordine OLD-12345", copies));
        if (!pending) Drain(db);
        if (upgrade)
        {
            // Emulate an existing archive with the previously persisted content trigger and no revision.
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(ArchiveDb).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(db)!;
            using var command = connection.CreateCommand();
            command.CommandText = "drop trigger search_content_update; create trigger search_content_update after update on attachment_contents begin " +
                "delete from code_attachment_fts where rowid in(select email_id from attachments where content_hash=old.hash); " +
                "insert or ignore into archive_search_jobs select email_id from attachments where content_hash=old.hash; end; " +
                "delete from meta where key='search_trigger_revision'";
            command.ExecuteNonQuery();
            initial.Dispose();
            db = Open();
        }
        using var reopened = upgrade ? db : null;
        Assert.Equal("Original A", db.GetOriginal("a", "s")!.Text);
        Assert.All(db.GetOriginal("b", "s")!.Attachments, file => Assert.Equal("Ordine OLD-12345", file.Text));
        // SaveBody deletes A's attachment rows, then UPSERT updates the content still shared by B.
        // B may already have a queued job or reference that content more than once.
        var corrected = Body("Original A", "Ordine CORRECTED-98765, importo EUR 42,00", 1);
        db.SaveBody(a, corrected, "shared-correction");
        db.SaveBody(a, corrected, "shared-correction"); // A retried pipe request must remain idempotent.
        Assert.Equal("Original A", db.GetOriginal("a", "s")!.Text);
        var original = db.GetOriginal("b", "s")!;
        Assert.Equal("Original B", original.Text);
        Assert.Equal(copies, original.Attachments.Count);
        Assert.All(original.Attachments, file => Assert.Equal(corrected.Attachments[0].Text, file.Text));
        Assert.Empty(db.Search(new ArchiveQuery { Text = "12345" }));
        Assert.Equal(2, db.Search(new ArchiveQuery { Text = "98765" }).Count);
        var jobs = new List<long>();
        while (db.NextSearchWork() is { } work)
        {
            jobs.Add(work.Id);
            Assert.True(db.CommitSearch(work, ArchiveSearchText.CodeTerms(work.Body), ArchiveSearchText.CodeTerms(work.Attachments)));
        }
        Assert.Equal(new[] { a, b }.OrderBy(id => id), jobs.OrderBy(id => id));
        Drain(db);
        Assert.All(new[] { a, b }, id => Assert.Contains(db.GetFacts(id, true), fact => fact.Kind == ArchiveFactKind.Amount && fact.Currency == "EUR"));
        Assert.Empty(db.Search(new ArchiveQuery { Text = "12345" }));
        Assert.Equal(2, db.Search(new ArchiveQuery { Text = "98765" }).Count);
    }

    [Fact]
    public void HeaderMetadataRefreshKeepsOriginals_AndConnectsPreviouslyMissingReferences()
    {
        using var db = Open(); long root = Add(db, "root", "Original body", "<root@test>");
        long child = Add(db, "child", "Unchanged child", "<child@test>"); Drain(db);
        string version = db.GetBodyVersion(child)!;
        var folder = db.GetOrAddFolder("s", "inbox", "");
        db.AddHeaders(folder, new[] { new ArchiveHeader { EntryId = "child", MessageId = "<child@test>", References = "<root@test>", Subject = "Synthetic", ReceivedUtc = Day, SentUtc = Day.AddHours(1), ThreadMetadataComplete = true } }, true);
        Assert.Equal(2, db.GetFlow(new ArchiveFlowSeed { Id = root }).Timeline.Count);
    }

    [Fact]
    public void ReferencesLinkTransitiveBranches_EvenWithMissingParents_AndAllCopiesStayAccessible()
    {
        using var db = Open(); long seed = Add(db, "a", "A", "<a@test>", refs: "<missing@test>");
        Add(db, "b", "B", "<b@test>", refs: "<missing@test> <a@test>");
        Add(db, "c", "C", "<c@test>", refs: "<b@test>"); Add(db, "copy-b", "B", "<b@test>", store: "other");
        var flow = db.GetFlow(new ArchiveFlowSeed { Id = seed });
        Assert.Equal(3, flow.Timeline.Count); var b = Assert.Single(flow.Timeline, m => m.MessageId == "<b@test>"); Assert.Equal(2, b.Copies.Count);
        Assert.Equal("a", b.ParentEntryId); Assert.Equal(b.EntryId, Assert.Single(flow.Timeline, m => m.MessageId == "<c@test>").ParentEntryId);
        Assert.Equal(4, flow.Timeline.Sum(m => m.Copies.Count));
    }

    [Fact]
    public void SharedReferencesExpandAcrossThreads_WithGroundedReason_ButAmountsAndSubjectDoNot()
    {
        using var db = Open(); long a = Add(db, "a", "Ordine ORD-2026/118. Importo 100 EUR.", "<a@test>", subject: "First");
        Add(db, "b", "Ordine ORD-2026/118 confermato.", "<b@test>", subject: "Different");
        Add(db, "c", "Fattura ORD-2026/118. Importo 100 EUR.", "<c@test>", subject: "First"); Drain(db);
        var flow = db.GetFlow(new ArchiveFlowSeed { Id = a }); Assert.Equal(2, flow.Timeline.Count); Assert.DoesNotContain(flow.Timeline, m => m.EntryId == "c");
        Assert.All(flow.Timeline, m => Assert.Contains(m.LinkFacts, f => f.Value == "ORD-2026/118" && f.Context.Contains("ORD-2026/118")));
    }

    [Fact]
    public void AllowedStoresAreAppliedDuringTraversal_NotOnlyAfterIt()
    {
        using var db = Open(); long a = Add(db, "a", "Ordine REF-12345", "<a@test>");
        Add(db, "bridge", "Ordine REF-12345; ordine REF-98765", "<bridge@test>", store: "excluded");
        Add(db, "c", "Ordine REF-98765", "<c@test>"); Drain(db);
        Assert.Equal(3, db.GetFlow(new ArchiveFlowSeed { Id = a }).Timeline.Count);
        Assert.Single(db.GetFlow(new ArchiveFlowSeed { Id = a, AllowedStoreIds = new List<string> { "s" } }).Timeline);
        Assert.Empty(db.GetFlow(new ArchiveFlowSeed { Id = a, AllowedStoreIds = new List<string>() }).Timeline);
    }

    [Fact]
    public void SearchSeedsExpandOutsideDateAndSenderFilters_AndKeepChronologyAndDistinctMissingIds()
    {
        using var db = Open(); long a = Add(db, "a", "Ordine REF-12345", "<a@test>", sent: Day, received: Day);
        Add(db, "older", "Ordine REF-12345", "<b@test>", sent: Day.AddDays(-2), received: Day.AddDays(1));
        Add(db, "forward", "Ordine REF-12345. Forwarded message dated 2001-01-01.", "<f@test>", sent: Day.AddDays(2), received: Day.AddDays(2));
        Add(db, "none1", "Ordine REF-12345"); Add(db, "none2", "Ordine REF-12345"); Drain(db);
        var result = db.ExpandRelated(new[] { a }, new ArchiveFlowSeed { AllowedStoreIds = new List<string> { "s" } });
        Assert.Equal(5, result.Hits.Count); Assert.Equal("older", result.Hits[0].EntryId); Assert.Equal("forward", result.Hits[^1].EntryId);
        Assert.Equal(4, result.Hits.Count(h => h.OutsideFilters)); Assert.False(result.Hits.Single(h => h.Id == a).OutsideFilters);
    }

    [Fact]
    public void ChatMergeDeduplicatesArchiveAndOutlookCopies_AndKeepsUnidentifiedMessages()
    {
        var items = new[] {
            new ChatMailItem { MessageId = "<a@test>", StoreId = "archive", EntryId = "a" },
            new ChatMailItem { MessageId = "<a@test>", StoreId = "outlook", EntryId = "copy" },
            new ChatMailItem { StoreId = "s", EntryId = "one" }, new ChatMailItem { StoreId = "s", EntryId = "two" },
            new ChatMailItem(), new ChatMailItem()
        };
        var merged = ChatMailDigest.OnePerMessage(items);
        Assert.Equal(5, merged.Count); Assert.Equal(2, merged[0].Copies.Count);
    }

    [Fact]
    public void ExplicitFactParsingKeepsExactDecimalsAndAmbiguity_AndFiltersSurviveJson()
    {
        var filters = new List<ArchiveFactFilter>();
        Assert.Empty(ArchiveSearchText.Parse("ordine ORD-2026/118; importo >=1000,01 EUR; scadenza entro 2026-11-30", filters));
        Assert.Equal(3, filters.Count); Assert.Equal(1000.01m, filters[1].AmountMin); Assert.Null(filters[1].AmountMax); Assert.Equal("2026-11-30", filters[2].DateTo);
        var q = new ArchiveQuery { FactFilters = filters }; var decoded = JsonSerializer.Deserialize<ArchiveQuery>(JsonSerializer.Serialize(q))!;
        Assert.Equal(3, decoded.FactFilters.Count); Assert.Equal(1000.01m, decoded.FactFilters[1].AmountMin);
        Assert.NotEmpty(ArchiveSearchText.Parse("scadenza venerdì; importo 1.000 EUR", new List<ArchiveFactFilter>()));
        using var valid = JsonDocument.Parse("{\"scope\":{\"kind\":\"archive\",\"factText\":\"importo >=1000,01 EUR\"}}");
        Assert.Single(AnalysisRequest.Parse(valid.RootElement, out _)!.FactFilters);
        using var invalid = JsonDocument.Parse("{\"scope\":{\"kind\":\"archive\",\"factText\":\"scadenza venerdì\"}}"); Assert.Null(AnalysisRequest.Parse(invalid.RootElement, out _));
    }

    [Theory]
    [InlineData("Riassumi le email sull'ordine 4471", "Ordine 4471", ArchiveFactKind.Order)]
    [InlineData("Cosa chiede l'email sull'ordine 4471 e chi la manda?", "Ordine 4471", ArchiveFactKind.Order)]
    [InlineData("Riassumi le email sulla fattura INV-4471", "Fattura INV-4471", ArchiveFactKind.Invoice)]
    [InlineData("Elenca i messaggi sul protocollo PR/4471", "Protocollo PR/4471", ArchiveFactKind.Protocol)]
    public void ChatReferenceSearch_FindsTheReferenceWithoutRequestWords(string question, string reference, ArchiveFactKind kind)
    {
        using var db = Open();
        long expected = Add(db, "match", reference + ". Conferma ricevuta.");
        Add(db, "other-code", reference.Replace("4471", "4472") + ". Riassumi cosa chiede e chi la manda.");
        Add(db, "other-store", reference, store: "excluded");
        Drain(db);
        var filters = new List<ArchiveFactFilter>();
        string words = ArchiveSearchText.ParseChat(question, filters);
        Assert.Equal(kind, Assert.Single(filters).Kind);
        Assert.Equal(expected, Assert.Single(db.Search(new ArchiveQuery
        {
            Text = words,
            FactFilters = filters,
            AllowedStoreIds = new List<string> { "s" }
        })).Id);
        // The explicit search parser must not start interpreting natural-language instructions.
        Assert.NotEmpty(ArchiveSearchText.Parse(question, new List<ArchiveFactFilter>()));
    }

    [Fact]
    public void ChatReferenceSearch_ContentStillNarrowsTheReference_AndAttachmentPolicyStillApplies()
    {
        using var db = Open();
        long expected = Add(db, "delay", "Ordine 4471. Ritardo consegna.");
        Add(db, "no-delay", "Ordine 4471. Conferma ricevuta.");
        long attachment = Add(db, "attachment", "Ordine 4471.");
        db.SaveBody(attachment, new ArchiveBody
        {
            Text = "Ordine 4471.",
            Attachments =
        {
            new ArchiveAttachment { SourceId = "file", Name = "note.txt", Version = "synthetic", State = "indexed", Text = "Ritardo consegna" }
        }
        });
        Add(db, "wrong-reference", "Ordine 4472. Ritardo consegna.");
        Drain(db);
        var filters = new List<ArchiveFactFilter>();
        var query = new ArchiveQuery
        {
            Text = ArchiveSearchText.ParseChat("Riassumi le email sull'ordine 4471 con ritardo consegna", filters),
            FactFilters = filters,
            IncludeAttachmentText = false
        };
        Assert.Equal(expected, Assert.Single(db.Search(query)).Id);
        query.IncludeAttachmentText = true;
        Assert.Equal(new[] { expected, attachment }.OrderBy(id => id), db.Search(query).Select(h => h.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task PipePreservesFactFiltersScopeCopiesAndGroundedLinks()
    {
        long a;
        using (var db = Open()) { a = Add(db, "a", "Ordine REF-12345. EUR 1000,01", "<a@test>"); Add(db, "other", "Ordine REF-12345", "<b@test>"); Add(db, "nonmatch", "Importo 20 EUR", "<c@test>"); Drain(db); }
        using var engine = new ArchiveEngine(_directory); await engine.Ready;
        var client = new ArchiveClient("p", _directory); await client.OpenAsync();
        var query = new ArchiveQuery { IncludeFacts = true, AllowedStoreIds = new List<string> { "s" }, Max = 1 };
        query.FactFilters.Add(new ArchiveFactFilter { Kind = ArchiveFactKind.Amount, AmountMin = 1000.01m, AmountMax = 1000.01m, Currency = "EUR" });
        Assert.Equal(a, Assert.Single(await client.SearchAsync(query)).Id);
        var result = await client.ExpandRelatedAsync(new[] { a }, new ArchiveFlowSeed { AllowedStoreIds = new List<string> { "s" }, IncludeAttachmentText = false });
        Assert.Equal(2, result.Hits.Count); Assert.Contains(result.Hits, h => h.OutsideFilters && h.LinkFacts.Count > 0);
        var flow = await client.GetFlowAsync(new ArchiveFlowSeed { Id = a }); Assert.All(flow.Timeline, m => Assert.Single(m.Copies));
    }

    public void Dispose() { if (Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") == null && Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true); }
}
