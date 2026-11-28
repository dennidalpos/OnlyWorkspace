using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using Xunit;

namespace OnlyWorkspace.Tests;

public sealed class ArchiveFlowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspaceFlowTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BranchesDeduplicateMessageIdsAndKeepCopyLocations_AndSubjectMatchesAreOnlyPossible()
    {
        using var db = ArchiveDb.Open(_directory, "synthetic");
        var now = DateTime.UtcNow;
        Add(db, "pst", "root", "m1", "", "conversation", "Progetto", now);
        Add(db, "ost", "answer-a", "m2", "m1", "conversation", "RE: Progetto", now.AddHours(1));
        Add(db, "imap-store", "answer-b", "m3", "m1", "conversation", "RE: Progetto", now.AddHours(2));
        Add(db, "pst", "copy", "m2", "m1", "conversation", "RE: Progetto", now.AddHours(3));
        Add(db, "pst", "similar", "", "", "", "Progetto", now.AddDays(1));

        var flow = db.GetFlow(new ArchiveFlowSeed { EntryId = "root", StoreId = "pst" });
        Assert.Equal(3, flow.Timeline.Count);
        Assert.Contains(flow.Timeline, e => e.EntryId == "answer-a" && e.ParentEntryId == "root");
        Assert.Contains(flow.Timeline, e => e.EntryId == "answer-b" && e.ParentEntryId == "root");
        Assert.Contains(flow.Timeline.Single(e => e.MessageId == "m2").Copies, e => e.EntryId == "copy" && e.StoreId == "pst");
        Assert.Contains(flow.PossibleLinks, e => e.EntryId == "similar");
        Assert.DoesNotContain(flow.Timeline, e => e.EntryId == "similar");
    }

    [Fact]
    public void NewestFirstRepliesMergeTransitiveBranchesWithoutSubjectHeuristics()
    {
        using var db = ArchiveDb.Open(_directory, "synthetic");
        var now = DateTime.UtcNow;
        Add(db, "ost", "grandchild", "m3", "m2", "", "Unrelated title", now.AddHours(2));
        Add(db, "imap", "sibling", "m4", "m1", "", "Another title", now.AddHours(1));
        Add(db, "ost", "child", "m2", "m1", "", "Different title", now.AddHours(1));
        Add(db, "pst", "root", "m1", "", "", "Original title", now);

        var flow = db.GetFlow(new ArchiveFlowSeed { EntryId = "root", StoreId = "pst" });
        Assert.Equal(4, flow.Timeline.Count);
        Assert.Contains(flow.Timeline, e => e.EntryId == "grandchild" && e.ParentEntryId == "child");
        Assert.Contains(flow.Timeline, e => e.EntryId == "sibling" && e.ParentEntryId == "root");
        Assert.Empty(flow.PossibleLinks);
    }

    [Fact]
    public void MissingIdentifiersDoNotCreateAConfirmedFlow_AndLimitIsReported()
    {
        using var db = ArchiveDb.Open(_directory, "synthetic");
        var now = DateTime.UtcNow;
        Add(db, "a", "one", "", "", "", "Offerta", now);
        Add(db, "b", "two", "", "", "", "RE: Offerta", now.AddHours(1));
        var noIds = db.GetFlow(new ArchiveFlowSeed { EntryId = "one", StoreId = "a" });
        Assert.Single(noIds.Timeline);
        Assert.Single(noIds.PossibleLinks);

        Add(db, "a", "three", "m3", "", "conv", "Altro", now);
        Add(db, "b", "four", "m4", "m3", "conv", "RE: Altro", now.AddHours(1));
        var limited = db.GetFlow(new ArchiveFlowSeed { EntryId = "three", StoreId = "a" }, max: 1);
        Assert.True(limited.LimitReached);
        Assert.Single(limited.Timeline);
    }

    /// <summary>#124: the view limit is a parameter, so the walk past it uses a small one; the default is pinned apart (it took 8 s with 20,001 rows).</summary>
    [Fact]
    public void FullAnalysisCanReadPastTheViewLimit()
    {
        Assert.Equal(20000, ArchiveDb.MaxFlowEmails);
        const int limit = 400;
        using var db = ArchiveDb.Open(_directory, "synthetic");
        var folders = new[]
        {
            db.GetOrAddFolder("pst-a", "inbox", "Inbox"),
            db.GetOrAddFolder("pst-b", "projects", "Projects")
        };
        var now = DateTime.UtcNow;
        const int total = limit + 1;
        for (int start = 0; start < total; start += 100)
        {
            var batch = Enumerable.Range(start, Math.Min(100, total - start))
                .Select(i => Header("mail-" + i, "m-" + i, "", "large-flow", "Progetto", now.AddSeconds(i))).ToArray();
            db.AddHeaders(folders[start / 100 % folders.Length], batch, folderDone: start + batch.Length == total);
        }
        var seed = new ArchiveFlowSeed { EntryId = "mail-0", StoreId = "pst-a" };
        var limited = db.GetFlow(seed, limit);
        Assert.True(limited.LimitReached);
        Assert.Equal(limit, limited.Timeline.Count);
        var complete = db.GetFlow(seed, int.MaxValue - 1);
        Assert.False(complete.LimitReached);
        Assert.Equal(total, complete.Timeline.Count);
        Assert.Equal("mail-" + limit, complete.Timeline[^1].EntryId);
    }

    [Fact]
    public void SameSenderWithinThirtyDaysIsOnlyAPossibleLink()
    {
        using var db = ArchiveDb.Open(_directory, "synthetic");
        var folder = db.GetOrAddFolder("pst", "inbox", "Inbox");
        var first = Header("one", "", "", "", "Preventivo", DateTime.UtcNow);
        first.SenderAddress = "alice@example.com";
        var second = Header("two", "", "", "", "Fattura", DateTime.UtcNow.AddDays(2));
        second.SenderAddress = "alice@example.com";
        db.AddHeaders(folder, new[] { first, second }, true);
        var flow = db.GetFlow(new ArchiveFlowSeed { EntryId = "one", StoreId = "pst" });
        Assert.Single(flow.Timeline);
        Assert.Equal("two", Assert.Single(flow.PossibleLinks).EntryId);
    }

    [Fact]
    public void Coverage_NamesTheMailboxFromItsFolderPath()
    {
        using var db = ArchiveDb.Open(_directory, "synthetic");
        db.GetOrAddFolder("pst", "inbox", @"Archivio 2024\Posta in arrivo");
        db.GetOrAddFolder("pst", "sent", @"Archivio 2024\Posta inviata");
        db.GetOrAddFolder("ost", "root", "\\mario@example.com");

        var coverage = db.GetStoreCoverage();
        Assert.Equal("Archivio 2024", Assert.Single(coverage, c => c.StoreId == "pst").DisplayName);
        Assert.Equal("mario@example.com", Assert.Single(coverage, c => c.StoreId == "ost").DisplayName);
    }

    [Fact]
    public void CoverageShowsLocalInventoryAndIndexedContentPerStore()
    {
        using var db = ArchiveDb.Open(_directory, "synthetic");
        var folder = db.GetOrAddFolder("pst", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { Header("one", "m1", "", "", "Progetto", DateTime.UtcNow) }, true, true);
        long id = db.NextPendingBodies(1).Single().Id;
        var body = new ArchiveBody { Text = "indexed words" };
        body.Attachments.Add(new ArchiveAttachment { SourceId = "a1", Name = "documento.pdf", State = "indexed", Text = "attachment words" });
        db.SaveBody(id, body);
        var other = db.GetOrAddFolder("ost", "inbox", "Inbox");
        db.AddHeaders(other, new[] { Header("two", "m2", "m1", "", "RE: Progetto", DateTime.UtcNow) }, true, true, canDeleteAbsent: false);

        var coverage = db.GetStoreCoverage();
        var pst = Assert.Single(coverage, c => c.StoreId == "pst");
        Assert.Equal(1, pst.Emails);
        Assert.Equal(1, pst.BodiesIndexed);
        Assert.Equal(1, pst.AttachmentsIndexed);
        Assert.Equal(1, pst.AttachmentsTotal);
        Assert.Equal(0, pst.BodiesPending);
        var ost = Assert.Single(coverage, c => c.StoreId == "ost");
        Assert.Equal(1, ost.BodiesPending);
        Assert.True(pst.AbsenceVerified);
        Assert.Equal("inventario verificato", pst.State);
        Assert.NotNull(pst.LastInventoryUtc);
        Assert.False(Assert.Single(coverage, c => c.StoreId == "ost").AbsenceVerified);
        var mail = Assert.Single(db.GetFlow(new ArchiveFlowSeed { EntryId = "one", StoreId = "pst" }).Timeline, e => e.EntryId == "one");
        Assert.Equal("documento.pdf", Assert.Single(mail.Attachments));
    }

    [Fact]
    public void FlowAndAttachmentNamesSurviveArchivePipeSerialization()
    {
        var flow = new ArchiveFlow();
        var mail = new ArchiveFlowMail { EntryId = "e", StoreId = "s" };
        mail.Attachments.Add("documento.pdf");
        flow.Timeline.Add(mail);
        var decoded = JsonSerializer.Deserialize<ArchiveFlow>(JsonSerializer.Serialize(flow))!;
        Assert.Equal("documento.pdf", Assert.Single(Assert.Single(decoded.Timeline).Attachments));
    }

    /// <summary>A cache store is not "partial" once a closed full check has asked Outlook about the emails it did not list (A12).</summary>
    [Fact]
    public void CacheStore_IsCheckedOnlyAfterAClosedFullCheck()
    {
        using var db = ArchiveDb.Open(_directory, "synthetic");
        var folder = db.GetOrAddFolder("ost", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { Header("a", "m1", "", "", "Uno", DateTime.UtcNow), Header("b", "m2", "", "", "Due", DateTime.UtcNow) }, true, true, canDeleteAbsent: false);
        // First complete inventory: every archived email was just listed, nothing to ask.
        Assert.Equal("cache verificata", Assert.Single(db.GetStoreCoverage()).State);

        db.BeginRescan(new[] { folder.Id });
        db.AddHeaders(db.GetOrAddFolder("ost", "inbox", "Inbox"), new[] { Header("a", "m1", "", "", "Uno", DateTime.UtcNow) }, true, true, canDeleteAbsent: false);
        // Inventory complete, Outlook not yet asked about "b": still to check.
        Assert.False(Assert.Single(db.GetStoreCoverage()).CacheChecked);
        db.ConfirmCacheCopies(Array.Empty<long>(), db.UnseenCacheCopies(10).Select(e => e.Id).ToArray());
        Assert.Equal(1, db.FinishRescan());

        var state = Assert.Single(db.GetStoreCoverage());
        Assert.True(state.CacheChecked);
        Assert.False(state.AbsenceVerified);
        Assert.Equal("cache verificata", state.State);

        // The next check reopens the question until it closes again.
        db.BeginRescan(new[] { folder.Id });
        Assert.False(Assert.Single(db.GetStoreCoverage()).CacheChecked);
        db.AddHeaders(db.GetOrAddFolder("ost", "inbox", "Inbox"), new[] { Header("a", "m1", "", "", "Uno", DateTime.UtcNow) }, true, verified: false, canDeleteAbsent: false);
        Assert.Equal("cache da controllare", Assert.Single(db.GetStoreCoverage()).State);
    }

    [Fact]
    public void FailedOrInterruptedInventoryExposesUncertaintyWithoutRemovingMail()
    {
        using var db = ArchiveDb.Open(_directory, "synthetic");
        var folder = db.GetOrAddFolder("ost", "inbox", "Inbox");
        db.AddHeaders(folder, new[] { Header("cached", "m1", "", "", "Progetto", DateTime.UtcNow) }, true, true, canDeleteAbsent: false);
        db.BeginRescan(new[] { folder.Id });
        db.SetFolderError(folder.Id, "cartella non leggibile");
        Assert.Null(db.FinishRescan());
        var state = Assert.Single(db.GetStoreCoverage());
        Assert.Contains("cartella non leggibile", state.Error);
        Assert.Equal("errore", state.State);
        Assert.False(state.AbsenceVerified);
        Assert.Equal(1, state.Emails);
        Assert.Single(db.GetFlow(new ArchiveFlowSeed { EntryId = "cached", StoreId = "ost" }).Timeline);
    }

    [Fact]
    public void FactsIncomplete_CountsOnlyTheFlowsOwnJobs()
    {
        using var db = ArchiveDb.Open(_directory, "synthetic");
        var now = DateTime.UtcNow;
        Add(db, "pst", "flow", "m1", "", "", "Ordine forniture", now);
        Add(db, "pst", "flow-reply", "m2", "m1", "", "RE: Ordine forniture", now.AddHours(1));
        Add(db, "pst", "elsewhere", "m9", "", "", "Fattura diversa", now.AddDays(-60));
        foreach (var pending in db.NextPendingBodies(10)) db.SaveBody(pending.Id, new ArchiveBody { Text = "Importo 120,00 EUR entro il 2026-11-30." });
        long elsewhere = db.Search(new ArchiveQuery { Text = "Fattura" }).Single().Id;

        // Every job done, the one of an unrelated email failed: the flow is complete.
        while (db.NextFactWork() is { } work) Assert.True(db.CommitFacts(work, ArchiveDb.ExtractFacts(work, default), work.EmailId == elsewhere ? "synthetic failure" : null));
        Assert.Equal(1, db.GetFactProgress().Failed);
        Assert.False(db.GetFlow(new ArchiveFlowSeed { EntryId = "flow", StoreId = "pst" }).FactsIncomplete);
        Assert.True(db.GetFlow(new ArchiveFlowSeed { EntryId = "elsewhere", StoreId = "pst" }).FactsIncomplete);
        Assert.False(db.ExpandRelated(new[] { db.Search(new ArchiveQuery { Text = "forniture" }).First().Id }, new ArchiveFlowSeed()).FactsIncomplete);
    }

    private static void Add(ArchiveDb db, string store, string entry, string message, string reply, string conversation, string subject, DateTime when)
    {
        var folder = db.GetOrAddFolder(store, "inbox", "Inbox");
        db.AddHeaders(folder, new[] { Header(entry, message, reply, conversation, subject, when) }, true);
    }

    private static ArchiveHeader Header(string entry, string message, string reply, string conversation, string subject, DateTime when) => new()
    {
        EntryId = entry,
        MessageId = message,
        InReplyTo = reply,
        ConversationId = conversation,
        Subject = subject,
        SenderName = "Sender",
        ReceivedUtc = when
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) TestArtifacts.DeleteDirectory(_directory, true);
    }
}
