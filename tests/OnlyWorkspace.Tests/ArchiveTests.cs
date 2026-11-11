using System;
using System.IO;
using System.Linq;
using System.Text;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using Xunit;

namespace OnlyWorkspace.Tests;

public class CounterpartyResolverTests
{
    [Theory]
    [InlineData("Mario.Rossi@Acme.it", "acme.it")]
    [InlineData("fatture@pec.acme.it", "acme.it")]
    [InlineData("info@sales.acme.co.uk", "acme.co.uk")]
    [InlineData("mario.rossi@gmail.com", "mario.rossi@gmail.com")]
    [InlineData("studio.bianchi@legalmail.it", "studio.bianchi@legalmail.it")]
    [InlineData("ditta@cert.legalmail.it", "ditta@cert.legalmail.it")]
    [InlineData("/o=exchangelabs/ou=x/cn=recipients/cn=abc", "/o=exchangelabs/ou=x/cn=recipients/cn=abc")]
    public void KeyOf_GroupsByCompanyDomain_ButNotOnMailboxProviders(string address, string expected)
    {
        Assert.Equal(expected, CounterpartyResolver.KeyOf(address));
    }

    [Fact]
    public void DefaultName_IsTheDomain_OrThePersonBehindASingleAddress()
    {
        Assert.Equal("acme.it", CounterpartyResolver.DefaultName("acme.it", "Mario Rossi"));
        Assert.Equal("Mario Rossi", CounterpartyResolver.DefaultName("mario.rossi@gmail.com", "Mario Rossi"));
        Assert.Equal("mario.rossi@gmail.com", CounterpartyResolver.DefaultName("mario.rossi@gmail.com", "mario.rossi@gmail.com"));
    }
}

public class ArchiveTextTests
{
    [Theory]
    [InlineData("RE: R: I: Offerta 12/2020", "offerta 12/2020")]
    [InlineData("Fwd: [EXT] RE:  Specifiche   posa", "specifiche posa")]
    [InlineData("Relazione tecnica", "relazione tecnica")]
    [InlineData("Re[2]: Ordine", "ordine")]
    public void NormalizeSubject_DropsReplyPrefixes(string subject, string expected)
    {
        Assert.Equal(expected, ArchiveText.NormalizeSubject(subject));
    }

    [Fact]
    public void BuildFtsQuery_KeepsSignificantWords_AndCutsEndings()
    {
        Assert.Equal("\"specific\"* OR \"posa\" OR \"paviment\"*", ArchiveText.BuildFtsQuery("Quali sono le specifiche di posa del pavimento?"));
        Assert.Equal(string.Empty, ArchiveText.BuildFtsQuery("che cosa con le"));
        // Operators typed by the user are data, not syntax.
        Assert.Equal("\"acme\" OR \"near\" OR \"ordin\"*", ArchiveText.BuildFtsQuery("acme\" NEAR( ordine"));
    }

    [Fact]
    public void IncompleteIndex_CanBeCheckedAgainstTheWholeOriginal()
    {
        string original = new string('x', 60001) + " riferimento ZQ8743";
        Assert.False(MailSearchTerms.Matches("ZQ8743", original.Substring(0, 60000)));
        Assert.True(MailSearchTerms.Matches("ZQ8743", original));
    }

    [Fact]
    public void SearchTerms_KeepARelevantWordAfterTheSixteenth()
    {
        var question = string.Join(" ", Enumerable.Range(1, 20).Select(i => "term" + i));
        Assert.Contains("\"term20\"", ArchiveText.BuildFtsQuery(question));
        Assert.True(MailSearchTerms.Matches(question, "riferimento term20"));
    }
}

public class ArchiveDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_Archive_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { TestArtifacts.DeleteDirectory(_dir, true); }
        catch { /* temp folder */ }
    }

    private static ArchiveHeader Header(string entryId, string subject, string sender, DateTime received, string messageId = "", string inReplyTo = "", string conversation = "", string preview = "") => new()
    {
        EntryId = entryId,
        Subject = subject,
        SenderAddress = sender,
        SenderName = sender.Split('@')[0],
        ReceivedUtc = received,
        MessageId = messageId,
        InReplyTo = inReplyTo,
        ConversationId = conversation,
        Preview = preview
    };

    [Fact]
    public void File_IsEncrypted_AndUnreadableWithAnotherKey()
    {
        using (var db = ArchiveDb.Open(_dir, "Outlook"))
        {
            var folder = db.GetOrAddFolder("store", "inbox", "Posta in arrivo");
            db.AddHeaders(folder, new[] { Header("e1", "Capitolato riservato", "mario@acme.it", DateTime.UtcNow) }, folderDone: true);
        }

        var bytes = File.ReadAllBytes(ArchiveDb.EngineFileOf(_dir, "Outlook"));
        Assert.False(Encoding.ASCII.GetString(bytes, 0, 15).StartsWith("SQLite format 3"));
        Assert.DoesNotContain("Capitolato", Encoding.UTF8.GetString(bytes));

        File.Delete(Path.Combine(_dir, "archive.key"));
        Assert.Throws<ArchiveUnreadableException>(() => ArchiveDb.Open(_dir, "Outlook"));
    }

    [Fact]
    public void Headers_AreIdempotentByLocation_CopiesStayDistinct_AndTheBookmarkSurvivesReopening()
    {
        var day = new DateTime(2021, 3, 10, 9, 0, 0, DateTimeKind.Utc);
        using (var db = ArchiveDb.Open(_dir, "p"))
        {
            var inbox = db.GetOrAddFolder("s", "inbox", "Inbox");
            Assert.Equal(2, db.AddHeaders(inbox, new[]
            {
                Header("e1", "Ordine", "a@acme.it", day, "<m1>"),
                Header("e2", "Fattura", "b@acme.it", day.AddDays(-1), "<m2>")
            }, folderDone: false));
            // Same batch again (restart) and the same message filed under a Gmail label.
            Assert.Equal(0, db.AddHeaders(inbox, new[] { Header("e1", "Ordine", "a@acme.it", day, "<m1>") }, folderDone: false));
            var label = db.GetOrAddFolder("s", "label", "Etichetta");
            Assert.Equal(1, db.AddHeaders(label, new[] { Header("x9", "Ordine", "a@acme.it", day, "<m1>") }, folderDone: true));
            var copies = db.Search(new ArchiveQuery { Text = "ordine" });
            Assert.Equal(2, copies.Count);
            Assert.Equal(copies[0].ThreadId, copies[1].ThreadId);
            // An analysis reads the message once; a hit without a Message-ID is never merged.
            Assert.All(copies, c => Assert.Equal("<m1>", c.MessageId));
            var once = ArchiveHit.OnePerMessage(copies.Concat(new[] { new ArchiveHit { Id = 7 }, new ArchiveHit { Id = 8 } }), out int skipped);
            Assert.Equal(new[] { copies[0].Id, 7L, 8L }, once.Select(h => h.Id));
            Assert.Equal(1, skipped);
        }

        using var again = ArchiveDb.Open(_dir, "p");
        var folder = again.GetOrAddFolder("s", "inbox", "Inbox");
        Assert.Equal(day.AddDays(-1), folder.OldestUtc);
        Assert.Equal(day, folder.NewestUtc);
        Assert.False(folder.Done);
        Assert.Equal(3, again.GetStats().Emails);
    }

    [Fact]
    public void PagedLocalSearch_ReachesOlderMailPastFiftyThreeHundredAndTwoThousand_AcrossStores()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        var now = DateTime.UtcNow;
        foreach (var store in new[] { "first", "second" })
        {
            var folder = db.GetOrAddFolder(store, "inbox", "Inbox");
            var headers = Enumerable.Range(0, 1051).Select(i =>
                Header(store + "-" + i, i == 1050 && store == "second" ? "Riferimento remoto ZQ8743" : "Messaggio",
                    "sender@example.com", now.AddMinutes(-(store == "first" ? i : 1051 + i)), "<" + store + i + ">")).ToArray();
            db.AddHeaders(folder, headers, folderDone: true);
        }
        var ids = new System.Collections.Generic.List<string>();
        for (int offset = 0; ; offset += 500)
        {
            var page = db.Search(new ArchiveQuery { Max = 500, Offset = offset });
            ids.AddRange(page.Select(h => h.EntryId));
            if (page.Count < 500) break;
        }
        Assert.Equal(2102, ids.Count);
        Assert.Equal("second-1050", ids[^1]);
        Assert.Contains(db.Search(new ArchiveQuery { Text = "ZQ8743" }), h => h.EntryId == "second-1050");
    }

    [Fact]
    public void Search_FindsWordsWithoutAccentsAndEndings_AcrossTheAddressesOfACounterparty()
    {
        var day = new DateTime(2019, 5, 2, 8, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        db.SetOwnAddresses(new[] { "Danny@miaditta.it" });
        var inbox = db.GetOrAddFolder("s", "inbox", "Inbox");
        var sent = db.GetOrAddFolder("s", "sent", "Sent");
        db.AddHeaders(inbox, new[]
        {
            Header("e1", "Cantiere via Roma", "mario@acme.it", day, "<m1>", preview: "In allegato le modalità operative"),
            Header("e2", "Auguri", "info@altri.it", day.AddDays(1), "<m2>"),
            Header("e3", "Fattura 12", "amministrazione@pec.acme.it", day.AddYears(2), "<m3>")
        }, folderDone: true);
        db.AddHeaders(sent, new[] { Header("e4", "Specifiche di posa", "danny@miaditta.it", day.AddDays(-3), "<m4>") }, folderDone: true);

        // Sent email: the counterparty comes from the recipients, known once the email has been opened.
        var pending = db.NextPendingBodies(10);
        Assert.Equal(4, pending.Count);
        var body = new ArchiveBody { Text = "La posa va eseguita a colla con fuga di 3 mm, come da capitolato." };
        body.Recipients.Add(new ArchiveRecipient { Address = "mario@acme.it", Name = "Mario Rossi" });
        body.AttachmentNames.Add("capitolato.pdf");
        db.SaveBody(pending.Single(p => p.EntryId == "e4").Id, body);

        var acme = db.CounterpartyOf("MARIO@acme.it");
        Assert.NotNull(acme);
        Assert.Null(db.CounterpartyOf("danny@miaditta.it"));

        var all = db.Search(new ArchiveQuery { CounterpartyId = acme });
        Assert.Equal(new[] { "e3", "e1", "e4" }, all.Select(h => h.EntryId));

        Assert.Equal("e4", db.Search(new ArchiveQuery { Text = "specifica della posa", CounterpartyId = acme }).First().EntryId);
        Assert.Equal("e1", db.Search(new ArchiveQuery { Text = "modalita operativa" }).Single().EntryId);
        Assert.Empty(db.Search(new ArchiveQuery { Text = "capitolato", ToUtc = day.AddDays(-10) }));

        Assert.Equal(2, db.FindAddresses("acme").Count);
        Assert.Equal(3, db.GetStats().BodiesPending);
    }

    [Fact]
    public void FindAddresses_CoversAPersonOrAWholeCompany_AndTheRoleNarrowsTheSearch()
    {
        var day = new DateTime(2020, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        db.SetOwnAddresses(new[] { "danny@miaditta.it" });
        var inbox = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.AddHeaders(inbox, new[]
        {
            Header("e1", "Preventivo 44", "mario@acme.it", day, "<m1>"),
            Header("e2", "Fattura 9", "amministrazione@acme.it", day.AddDays(1), "<m2>"),
            Header("e3", "Richiesta preventivo", "danny@miaditta.it", day.AddDays(2), "<m3>"),
            Header("e4", "Newsletter", "news@altri.it", day.AddDays(3), "<m4>")
        }, folderDone: true);
        var sent = new ArchiveBody { Text = "Buongiorno, chiedo un preventivo." };
        sent.Recipients.Add(new ArchiveRecipient { Address = "mario@acme.it", Name = "Mario Rossi" });
        db.SaveBody(db.NextPendingBodies(10).Single(p => p.EntryId == "e3").Id, sent);

        // A person: only that address. The company (domain or name): every address.
        var mario = db.FindAddresses("mario");
        Assert.Single(mario);
        Assert.Equal(new[] { "e3", "e1" }, db.Search(new ArchiveQuery { AddressIds = mario }).Select(h => h.EntryId));
        Assert.Equal(new[] { "e1" }, db.Search(new ArchiveQuery { AddressIds = mario, Role = ArchivePartyRole.Sender }).Select(h => h.EntryId));
        Assert.Equal(new[] { "e3" }, db.Search(new ArchiveQuery { AddressIds = mario, Role = ArchivePartyRole.Recipient }).Select(h => h.EntryId));

        var acme = db.FindAddresses("*@acme.it");
        Assert.Equal(2, acme.Count);
        Assert.Equal(new[] { "e3", "e2", "e1" }, db.Search(new ArchiveQuery { AddressIds = acme }).Select(h => h.EntryId));
        Assert.Equal(new[] { "e3", "e1" }, db.Search(new ArchiveQuery { AddressIds = acme, Subject = "preventiv*" }).Select(h => h.EntryId));

        // Own addresses are never a counterparty; an unknown person finds nothing instead of everything.
        Assert.Empty(db.FindAddresses("miaditta"));
        Assert.Empty(db.Search(new ArchiveQuery { AddressIds = db.FindAddresses("sconosciuto") }));
    }

    [Fact]
    public void Body_ReplacesThePreview_AndAnUnavailableOneKeepsTheSubjectSearchable()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        var inbox = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.AddHeaders(inbox, new[]
        {
            Header("e1", "Collaudo impianto", "a@acme.it", DateTime.UtcNow, "<m1>"),
            Header("e2", "Verbale riservato", "a@acme.it", DateTime.UtcNow, "<m2>")
        }, folderDone: true);
        var pending = db.NextPendingBodies(10);
        db.SaveBody(pending.Single(p => p.EntryId == "e1").Id, new ArchiveBody { Text = new string('x', 300) + " garanzia" });
        db.SaveBody(pending.Single(p => p.EntryId == "e2").Id, new ArchiveBody { Unavailable = true });

        var hit = db.Search(new ArchiveQuery { Text = "garanzia" }).Single();
        Assert.Equal("e1", hit.EntryId);
        Assert.Equal(255, hit.Preview.Length);
        Assert.Equal("e2", db.Search(new ArchiveQuery { Text = "verbale" }).Single().EntryId);
        Assert.Empty(db.NextPendingBodies(10));
        Assert.Equal(0, db.GetStats().BodiesPending);
        Assert.Equal(1, db.GetStats().BodiesIndexed);
        Assert.Equal(2, db.GetStats().Emails);

        db.SetBodyState(hit.Id, ArchiveBodyState.Gone);
        Assert.Empty(db.Search(new ArchiveQuery { Text = "garanzia" }));
        Assert.Equal(0, db.GetStats().BodiesIndexed);
        Assert.Equal(1, db.GetStats().Emails);
    }



    [Fact]
    public void Meta_And_Delete()
    {
        using (var db = ArchiveDb.Open(_dir, "Profilo di prova/1"))
        {
            db.SetMeta("paused", "1");
            Assert.Equal("1", db.GetMeta("paused"));
            db.SetMeta("paused", null);
            Assert.Null(db.GetMeta("paused"));
            Assert.True(db.FileSizeBytes > 0);
        }
        Assert.True(ArchiveDb.Exists(_dir, "Profilo di prova/1"));
        ArchiveDb.Delete(_dir, "Profilo di prova/1");
        Assert.False(ArchiveDb.Exists(_dir, "Profilo di prova/1"));
        // The key survives: another profile's archive stays readable.
        Assert.True(File.Exists(Path.Combine(_dir, "archive.key")));
    }

    [Fact]
    public void RetryableSources_BackOff_AndAreGivenUpAfterTheLimit()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        var inbox = db.GetOrAddFolder("s", "inbox", "Inbox");
        db.AddHeaders(inbox, new[] { Header("b", "Corpo", "a@acme.it", DateTime.UtcNow), Header("a", "Allegato", "a@acme.it", DateTime.UtcNow) }, folderDone: true);
        var ids = db.NextPendingBodies(10).ToDictionary(p => p.EntryId, p => p.Id);
        var failing = new ArchiveBody { Text = "testo", Attachments = { new ArchiveAttachment { SourceId = "1", Name = "x.pdf", State = "retryable", Problem = "estrattore non installato" } } };

        for (int attempt = 0; attempt < ArchiveDb.MaxRetries; attempt++)
        {
            db.SetBodyState(ids["b"], ArchiveBodyState.Retryable);
            db.SaveBody(ids["a"], failing);
        }
        Assert.Equal(1, db.GetStats().BodiesRetryable);
        Assert.Equal(1, db.GetStats().AttachmentTextsRetryable);

        db.SetBodyState(ids["b"], ArchiveBodyState.Retryable);
        db.SaveBody(ids["a"], failing);
        var stats = db.GetStats();
        Assert.Equal(0, stats.BodiesRetryable);
        Assert.Equal(1, stats.BodiesUnavailable);
        Assert.Equal(0, stats.AttachmentTextsRetryable);
    }

    [Fact]
    public void ReceiptsOfAnIdlePeer_ArePruned_RecentOnesKept()
    {
        using var db = ArchiveDb.Open(_dir, "p");
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "Inbox"), new[] { Header("e", "Oggetto", "a@acme.it", DateTime.UtcNow) }, folderDone: true);
        long id = db.NextPendingBodies(10).Single().Id;
        string peer = Guid.NewGuid().ToString("N");
        db.SaveBody(id, new ArchiveBody { Text = "uno" }, peer + ":1");

        db.PruneIdlePeers(DateTime.UtcNow);
        Assert.Throws<InvalidDataException>(() => db.SaveBody(id, new ArchiveBody { Text = "due" }, peer + ":1"));

        db.PruneIdlePeers(DateTime.UtcNow + ArchiveDb.PeerIdleLimit + TimeSpan.FromHours(1));
        db.SaveBody(id, new ArchiveBody { Text = "due" }, peer + ":1");
        Assert.Null(db.GetMeta("rpc_ack:" + peer));
    }

    [Fact]
    public void InlinedIds_IgnoreACultureWithAUnicodeMinusSign()
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.GetCultureInfo("sv-SE").Clone();
        culture.NumberFormat.NegativeSign = "−";
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        try
        {
            using var db = ArchiveDb.Open(_dir, "p");
            db.GetOrAddFolder("s", "inbox", "Inbox");
            db.BeginRescan(Array.Empty<long>());
            db.SetListedFolders(Array.Empty<long>(), new[] { "s" });
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Threads_CopyOfAReplyWithoutItsParent_StaysInTheFirstCopysThread()
    {
        var day = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        var inbox = db.GetOrAddFolder("s", "inbox", "Inbox");
        var label = db.GetOrAddFolder("s", "label", "Etichetta");
        db.AddHeaders(inbox, new[] { Header("m2", "RE: Offerta", "a@acme.it", day.AddDays(1), "<m2>", inReplyTo: "<missing>") }, folderDone: true);
        db.AddHeaders(label, new[] { Header("m2-copy", "RE: Offerta", "a@acme.it", day.AddDays(1), "<m2>", inReplyTo: "<missing>") }, folderDone: true);

        var hits = db.Search(new ArchiveQuery { Max = 20 }).ToDictionary(h => h.EntryId, h => h.ThreadId);
        Assert.Equal(hits["m2"], hits["m2-copy"]);
    }

    [Fact]
    public void Threads_JoinRepliesIndexedBeforeTheirOriginal()
    {
        var day = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        var inbox = db.GetOrAddFolder("s", "inbox", "Inbox");
        // Newest first, as the indexer reads them.
        db.AddHeaders(inbox, new[]
        {
            Header("e3", "RE: Offerta", "a@acme.it", day.AddDays(2), "<m3>", inReplyTo: "<m2>"),
            Header("e2", "RE: Offerta", "b@acme.it", day.AddDays(1), "<m2>", inReplyTo: "<m1>"),
            Header("e1", "Offerta", "a@acme.it", day, "<m1>"),
            Header("e9", "Altro", "a@acme.it", day, "<m9>"),
            Header("c2", "R: Sopralluogo", "a@acme.it", day, "", conversation: "AB12"),
            Header("c1", "Sopralluogo", "a@acme.it", day.AddDays(-1), "", conversation: "AB12")
        }, folderDone: true);

        var hits = db.Search(new ArchiveQuery { Max = 20 }).ToDictionary(h => h.EntryId, h => h.ThreadId);
        Assert.Equal(hits["e1"], hits["e2"]);
        Assert.Equal(hits["e1"], hits["e3"]);
        Assert.NotEqual(hits["e1"], hits["e9"]);
        Assert.Equal(hits["c1"], hits["c2"]);
    }

    [Fact]
    public void Rescan_FollowsMovedEmails_GivesUpDeletedOnes_AndSparesAFolderThatReadsEmpty()
    {
        var day = new DateTime(2024, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        var order = Header("e1", "Ordine", "a@acme.it", day, "<m1>");
        var invoice = Header("e2", "Fattura", "a@acme.it", day.AddDays(-1), "<m2>");
        var greetings = Header("e3", "Auguri", "b@altri.it", day.AddDays(-2), "<m3>");
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "Inbox"), new[] { order, invoice, greetings }, folderDone: true);
        db.AddHeaders(db.GetOrAddFolder("s", "clients", "Clienti"), Array.Empty<ArchiveHeader>(), folderDone: true);
        db.AddHeaders(db.GetOrAddFolder("s2", "old", "Vecchie"), new[] { Header("o1", "Storico", "c@altri.it", day.AddYears(-3), "<m9>") }, folderDone: true);
        var ids = new[] { "inbox", "clients" }.Select(f => db.GetOrAddFolder("s", f, f).Id).Append(db.GetOrAddFolder("s2", "old", "Vecchie").Id).ToArray();

        Assert.False(db.RescanDue(TimeSpan.FromDays(7)));
        Assert.True(db.RescanDue(TimeSpan.Zero));
        Assert.Null(db.FinishRescan());

        db.BeginRescan(ids);
        Assert.False(db.RescanDue(TimeSpan.Zero));
        var inbox = db.GetOrAddFolder("s", "inbox", "Inbox");
        Assert.False(inbox.Done);
        Assert.Null(inbox.OldestUtc);
        Assert.Equal(day, inbox.NewestUtc);

        // The order was filed under Clienti (read before the Inbox), the invoice deleted; the other store is offline and reads empty.
        Assert.Equal(1, db.AddHeaders(db.GetOrAddFolder("s", "clients", "Clienti"), new[] { Header("k1", "Ordine", "a@acme.it", day, "<m1>") }, folderDone: true));
        db.AddHeaders(inbox, new[] { greetings }, folderDone: true, verified: true);
        db.AddHeaders(db.GetOrAddFolder("s2", "old", "Vecchie"), Array.Empty<ArchiveHeader>(), folderDone: true);
        Assert.Equal(2, db.FinishRescan());

        var hits = db.Search(new ArchiveQuery()).ToDictionary(h => h.Subject, h => h.EntryId);
        Assert.Equal("k1", hits["Ordine"]);
        Assert.Equal("e3", hits["Auguri"]);
        Assert.Equal("o1", hits["Storico"]);
        Assert.False(hits.ContainsKey("Fattura"));

        // An email not found in Outlook brings the next check forward, but not within the hour of the last one.
        db.RequestRescan();
        Assert.False(db.RescanDue(TimeSpan.FromDays(7)));
        db.SetMeta("check_time", DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds().ToString());
        Assert.True(db.RescanDue(TimeSpan.FromDays(7)));

        // Next check: the invoice is back from the trash, with the EntryID it had.
        db.BeginRescan(ids);
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "Inbox"), new[] { invoice, greetings }, folderDone: true);
        db.AddHeaders(db.GetOrAddFolder("s", "clients", "Clienti"), new[] { Header("k1", "Ordine", "a@acme.it", day, "<m1>") }, folderDone: true);
        Assert.Null(db.FinishRescan()); // the third folder still has no completed inventory in this pass
        db.AddHeaders(db.GetOrAddFolder("s2", "old", "Vecchie"), Array.Empty<ArchiveHeader>(), folderDone: true);
        Assert.Equal(0, db.FinishRescan());
        Assert.Equal(4, db.Search(new ArchiveQuery()).Count);
        Assert.False(db.RescanDue(TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Rescan_WithoutMessageId_KeepsIdenticalCopiesDistinctAndReindexesMovedLocation()
    {
        var day = new DateTime(2024, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        var inbox = db.GetOrAddFolder("s", "inbox", "Inbox");
        var copy = db.GetOrAddFolder("s", "copy", "Copy");
        var destination = db.GetOrAddFolder("s", "destination", "Destination");
        var original = Header("old", "Ordine", "a@acme.it", day);
        var identicalCopy = Header("copy", "Ordine", "a@acme.it", day);
        db.AddHeaders(inbox, new[] { original }, folderDone: true);
        db.AddHeaders(copy, new[] { identicalCopy }, folderDone: true);
        db.AddHeaders(destination, Array.Empty<ArchiveHeader>(), folderDone: true);
        foreach (var pending in db.NextPendingBodies(10)) db.SaveBody(pending.Id, new ArchiveBody { Text = "Original text" });

        db.BeginRescan(new[] { inbox.Id, copy.Id, destination.Id });
        db.AddHeaders(db.GetOrAddFolder("s", "destination", "Destination"),
            new[] { Header("new", "Ordine", "a@acme.it", day) }, folderDone: true, verified: true);
        db.AddHeaders(db.GetOrAddFolder("s", "copy", "Copy"), new[] { identicalCopy }, folderDone: true, verified: true);
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "Inbox"), Array.Empty<ArchiveHeader>(), folderDone: true, verified: true);

        Assert.Equal(1, db.FinishRescan());
        Assert.Equal(new[] { "copy", "new" }, db.Search(new ArchiveQuery { Subject = "Ordine" }).Select(hit => hit.EntryId).OrderBy(id => id));
        Assert.Equal("new", db.NextPendingBodies(10).Single().EntryId);
    }



    [Fact]
    public void Rescan_GivesUpTheEmailsOfAFolderDeletedInOutlook_UnlessItsMailboxIsNotListedAtAll()
    {
        var day = new DateTime(2024, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        var inbox = db.GetOrAddFolder("s", "inbox", "Inbox");
        var project = db.GetOrAddFolder("s", "project", "Progetto chiuso");
        var other = db.GetOrAddFolder("s2", "old", "Vecchie");
        db.AddHeaders(inbox, new[] { Header("e1", "Ordine", "a@acme.it", day, "<m1>") }, folderDone: true);
        db.AddHeaders(project, new[] { Header("p1", "Verbale", "a@acme.it", day, "<m2>"), Header("p2", "Preventivo", "a@acme.it", day, "<m3>") }, folderDone: true);
        db.AddHeaders(other, new[] { Header("o1", "Storico", "c@altri.it", day, "<m9>") }, folderDone: true);

        // The project folder was deleted (one email filed in the Inbox first); the other mailbox is offline: no folder listed.
        db.BeginRescan(new[] { inbox.Id });
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "Inbox"), new[] { Header("e1", "Ordine", "a@acme.it", day, "<m1>"), Header("e9", "Preventivo", "a@acme.it", day, "<m3>") }, folderDone: true);
        db.SetListedFolders(new[] { inbox.Id }, new[] { "s" });
        Assert.Equal(2, db.FinishRescan());

        var hits = db.Search(new ArchiveQuery()).ToDictionary(h => h.Subject, h => h.EntryId);
        Assert.Equal(new[] { "Ordine", "Preventivo", "Storico" }, hits.Keys.OrderBy(k => k));
        Assert.Equal("e9", hits["Preventivo"]);

        // The deleted folder is no longer counted; listed again (restored), it is.
        Assert.Equal((2, 2), (db.GetStats().Folders, db.GetStats().FoldersDone));
        db.SetListedFolders(new[] { inbox.Id, project.Id });
        Assert.Equal(3, db.GetStats().Folders);
    }

    [Fact]
    public void RemoveOtherStores_DropsTheMailboxTakenOut_AndHasTheOthersReadAgain()
    {
        var day = new DateTime(2024, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        var order = Header("g1", "Ordine", "a@acme.it", day, "<m1>");
        var greetings = Header("g2", "Auguri", "b@altri.it", day.AddDays(-2), "<m3>");
        // The order reached both mailboxes: recorded once, under the one read first.
        db.AddHeaders(db.GetOrAddFolder("pec", "inbox", "Inbox"), new[]
        {
            Header("p1", "Ordine", "a@acme.it", day, "<m1>"),
            Header("p2", "Ricevuta di consegna", "posta@legalmail.it", day.AddDays(-1), "<m2>")
        }, folderDone: true);
        db.AddHeaders(db.GetOrAddFolder("GMAIL", "inbox", "Inbox"), new[] { order, greetings }, folderDone: true);
        foreach (var p in db.NextPendingBodies(10))
        {
            db.SaveBody(p.Id, new ArchiveBody { Text = "testo" });
        }
        Assert.Equal(4, db.GetStats().Emails);

        Assert.Equal(0, db.RemoveOtherStores(StoreScope.All(), 10)); // all = every mailbox
        Assert.Equal(0, db.RemoveOtherStores(StoreScope.None(), 10)); // none never empties the archive by itself
        var keep = StoreScope.Only(new[] { "gmail" });
        Assert.Equal(1, db.RemoveOtherStores(keep, 1));
        Assert.Equal(1, db.RemoveOtherStores(keep, 10));
        Assert.Equal(1, db.RemoveOtherStores(keep, 10)); // the folder, once empty
        Assert.Equal(0, db.RemoveOtherStores(keep, 10));

        var stats = db.GetStats();
        Assert.Equal(2, stats.Emails);
        Assert.Equal(1, stats.Folders);
        Assert.Empty(db.Search(new ArchiveQuery { Text = "ricevuta" }));
        Assert.Equal(new[] { "g1", "g2" }, db.Search(new ArchiveQuery()).Select(h => h.EntryId).OrderBy(x => x));

        // The mailbox that stays keeps its own copy and card; rereading does not duplicate it.
        var gmail = db.GetOrAddFolder("GMAIL", "inbox", "Inbox");
        Assert.False(gmail.Done);
        Assert.Null(gmail.OldestUtc);
        Assert.Equal(0, db.AddHeaders(gmail, new[] { order, greetings }, folderDone: true));
        Assert.Equal("g1", db.Search(new ArchiveQuery { Text = "ordine" }).Single().EntryId);
    }

    /// <summary>A24: a closed mailbox leaves the archive with its choices, also under «every mailbox»; an open one cannot.</summary>
    [Fact]
    public void RemoveClosedStore_DropsItsEmailsAndChoices_KeepsTheOthersAndEveryMailbox()
    {
        var day = new DateTime(2024, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        var gmail = db.GetOrAddFolder("gmail", "inbox", @"\\Gmail\Inbox");
        db.AddHeaders(gmail, new[] { Header("g1", "Ordine", "a@acme.it", day, "<m1>") }, folderDone: true);
        db.AddHeaders(db.GetOrAddFolder("pst", "inbox", @"\\OW test\Inbox"), new[]
        {
            Header("p1", "Prova", "x@test.example", day, "<m2>"),
            Header("p2", "Altra prova", "y@test.example", day.AddDays(-1), "<m3>")
        }, folderDone: true);
        db.SetMeta(ArchiveScope.StoresKey, StoreScope.All().ToJson());
        db.SetMeta(StoreOwnAddresses.MetaKey, StoreOwnAddresses.WithGuess(StoreOwnAddresses.WithGuess(null, "pst", new StoreOwnEntry { Guessed = { "me@test.example" } }),
            "other", new StoreOwnEntry { Guessed = { "me@old.example" } }));

        Assert.Throws<InvalidOperationException>(() => db.RemoveClosedStore("pst", 10));
        db.SetListedFolders(new[] { gmail.Id }, null, new[] { "gmail" }); // the PST was taken out of the profile
        Assert.Throws<InvalidOperationException>(() => db.RemoveClosedStore("gmail", 10));

        Assert.Equal(1, db.RemoveClosedStore("PST", 1));
        Assert.Equal(1, db.RemoveClosedStore("PST", 10));
        Assert.Equal(1, db.RemoveClosedStore("PST", 10)); // the folder, once empty
        Assert.Equal(0, db.RemoveClosedStore("PST", 10));

        Assert.Equal("g1", db.Search(new ArchiveQuery()).Single().EntryId);
        Assert.Equal("gmail", db.GetStoreCoverage().Single().StoreId);
        Assert.Equal(StoreScope.All().ToJson(), db.GetMeta(ArchiveScope.StoresKey));
        Assert.Equal(new[] { "other" }, StoreOwnAddresses.Parse(db.GetMeta(StoreOwnAddresses.MetaKey)).Keys);
    }

    [Fact]
    public void Bookmark_MovesOnRowsThatAreNotEmails_AndIgnoresARowWithoutDate()
    {
        var day = new DateTime(2024, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        var folder = db.GetOrAddFolder("s", "invites", "Inviti");

        Assert.Equal(1, db.AddHeaders(folder, new[]
        {
            new ArchiveHeader { EntryId = "r1", ReceivedUtc = day, NotEmail = true },
            new ArchiveHeader { EntryId = "r2", ReceivedUtc = day.AddDays(-5), NotEmail = true },
            Header("e1", "Senza data", "a@acme.it", new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), "<m1>")
        }, folderDone: false));

        var again = db.GetOrAddFolder("s", "invites", "Inviti");
        Assert.Equal(day.AddDays(-5), again.OldestUtc);
        Assert.Equal(day, again.NewestUtc);
        Assert.Equal(1, db.GetStats().Emails);
    }




    [Fact]
    public void CounterpartyOfEmail_IsTheSenderOrTheRecipientOfAnEmailTheUserSent()
    {
        var day = new DateTime(2025, 3, 4, 9, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        db.SetOwnAddresses(new[] { "danny@miaditta.it" });
        db.AddHeaders(db.GetOrAddFolder("s", "inbox", "Inbox"), new[]
        {
            Header("e3", "RE: Offerta 12", "mario@acme.it", day.AddDays(2), "<m3>", inReplyTo: "<m2>"),
            Header("e0", "Fattura 2023/77", "fatture@pec.acme.it", day.AddYears(-2), "<m0>")
        }, folderDone: true);
        db.AddHeaders(db.GetOrAddFolder("s", "sent", "Sent"), new[] { Header("e2", "RE: Offerta 12", "danny@miaditta.it", day.AddDays(1), "<m2>", inReplyTo: "<m1>") }, folderDone: true);
        foreach (var p in db.NextPendingBodies(10))
        {
            var body = new ArchiveBody { Text = "testo" };
            if (p.EntryId == "e2") body.Recipients.Add(new ArchiveRecipient { Address = "mario@acme.it", Name = "Mario Rossi" });
            db.SaveBody(p.Id, body);
        }

        long acme = db.CounterpartyOf("mario@acme.it")!.Value;
        Assert.Equal(acme, db.CounterpartyOf("fatture@pec.acme.it")); // one counterparty per company domain
        Assert.Equal(acme, db.CounterpartyOfEmail("e3"));
        Assert.Equal(acme, db.CounterpartyOfEmail("e2")); // sent by the user: the recipient's
        Assert.Null(db.CounterpartyOfEmail("sconosciuta"));
    }
}
