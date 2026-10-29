using System;
using System.IO;
using System.Linq;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Infrastructure.Archive;
using Xunit;

namespace OnlyWorkspace.Tests;

public class StoreOwnAddressesTests
{
    [Fact]
    public void Guess_KeepsAliasesAboveTheShare_AndSkipsExchangeDns()
    {
        var rows = Enumerable.Repeat(new SentRow(null, "Danny@Vecchia.it", "Danny Perondi"), 6)
            .Concat(Enumerable.Repeat(new SentRow("info@vecchia.it", "/o=org/cn=recipients/cn=danny", "Ufficio"), 3))
            .Concat(new[] { new SentRow(null, "collega@vecchia.it", "Collega") })
            .Concat(Enumerable.Repeat(new SentRow(null, "/o=org/cn=recipients/cn=x", "X"), 20));

        var guess = StoreOwnAddresses.Guess(rows);

        // 10 rows with an address: 6 and 3 pass the 20% share, the delegate's single email does not.
        Assert.Equal(new[] { "danny@vecchia.it", "info@vecchia.it" }, guess.Guessed);
        Assert.Equal(new[] { "Danny Perondi", "Ufficio" }, guess.Names);
        Assert.Empty(StoreOwnAddresses.Guess(Array.Empty<SentRow>()).Guessed);
    }

    [Fact]
    public void UserChoices_WinOverANewGuess()
    {
        var json = StoreOwnAddresses.WithGuess(null, "PST1", new StoreOwnEntry { Guessed = { "a@vecchia.it", "b@vecchia.it" } });
        json = StoreOwnAddresses.WithChoice(json, "pst1", new[] { "A@vecchia.it", "c@altro.it" });

        var entry = StoreOwnAddresses.Parse(json)["PST1"];
        Assert.Equal(new[] { "c@altro.it" }, entry.Added);
        Assert.Equal(new[] { "b@vecchia.it" }, entry.Removed);

        // Outlook guesses again: b stays removed, c stays added.
        json = StoreOwnAddresses.WithGuess(json, "PST1", new StoreOwnEntry { Guessed = { "a@vecchia.it", "b@vecchia.it", "d@vecchia.it" } });
        json = StoreOwnAddresses.WithGuess(json, "PST2", new StoreOwnEntry { Guessed = { "e@x.it" } });
        Assert.Equal(new[] { "a@vecchia.it", "d@vecchia.it", "c@altro.it" }, StoreOwnAddresses.Parse(json)["PST1"].Effective);
        Assert.Equal(new[] { "a@vecchia.it", "d@vecchia.it", "c@altro.it", "e@x.it" }, StoreOwnAddresses.All(json));
    }

    [Fact]
    public void UserChoice_AppliesToTheOtherStoresToo()
    {
        // The same PST opened from two paths: the old entry stays in the meta, the ConfigTool shows only the open one.
        var json = StoreOwnAddresses.WithGuess(null, "old", new StoreOwnEntry { Guessed = { "danny@vecchia.it", "info@vecchia.it" } });
        json = StoreOwnAddresses.WithGuess(json, "new", new StoreOwnEntry { Guessed = { "danny@vecchia.it" } });
        json = StoreOwnAddresses.WithGuess(json, "other", new StoreOwnEntry { Guessed = { "x@altro.it" } });

        json = StoreOwnAddresses.WithChoice(json, "new", Array.Empty<string>());
        Assert.Equal(new[] { "info@vecchia.it", "x@altro.it" }, StoreOwnAddresses.All(json));
        Assert.Equal(new[] { "danny@vecchia.it" }, StoreOwnAddresses.Parse(json)["old"].Removed);
        Assert.Empty(StoreOwnAddresses.Parse(json)["other"].Removed);

        // A meta saved before this rule (removed only here) is put right by saving again.
        var stale = StoreOwnAddresses.WithGuess(StoreOwnAddresses.WithGuess(null, "old", new StoreOwnEntry { Guessed = { "danny@vecchia.it" } }),
            "new", new StoreOwnEntry { Guessed = { "danny@vecchia.it" } });
        var entries = StoreOwnAddresses.Parse(stale);
        entries["new"].Removed.Add("danny@vecchia.it");
        Assert.Equal(new[] { "danny@vecchia.it" }, StoreOwnAddresses.All(StoreOwnAddresses.Serialize(entries)));
        Assert.Empty(StoreOwnAddresses.All(StoreOwnAddresses.WithChoice(StoreOwnAddresses.Serialize(entries), "new", Array.Empty<string>())));

        // Chosen again: no longer removed anywhere.
        json = StoreOwnAddresses.WithChoice(json, "new", new[] { "danny@vecchia.it" });
        Assert.Equal(new[] { "danny@vecchia.it", "info@vecchia.it", "x@altro.it" }, StoreOwnAddresses.All(json));
        Assert.Empty(StoreOwnAddresses.Parse(json)["old"].Removed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Parse_UnreadableMeta_IsEmpty(string? json)
    {
        Assert.Empty(StoreOwnAddresses.Parse(json));
        Assert.Empty(StoreOwnAddresses.All(json));
    }

    [Fact]
    public void Parse_ReadsTheDocumentedShape()
    {
        var entry = StoreOwnAddresses.Parse("{\"S\":{\"guessed\":[\"a@x.it\"],\"added\":[\"b@x.it\"],\"removed\":[\"a@x.it\"]}}")["s"];
        Assert.Equal(new[] { "b@x.it" }, entry.Effective);
    }

    [Fact]
    public void MailboxProviders_AreNotOwnDomains()
    {
        Assert.True(CounterpartyResolver.IsMailboxProvider("gmail.com"));
        Assert.False(CounterpartyResolver.IsMailboxProvider("vecchia.it"));
    }
}

public class ArchiveOwnAddressesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_OwnAddr_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch { /* temp folder */ }
    }

    private static ArchiveHeader Header(string entryId, string sender, DateTime received) => new()
    {
        EntryId = entryId, Subject = "Oggetto " + entryId, SenderAddress = sender, SenderName = sender.Split('@')[0], ReceivedUtc = received
    };

    [Fact]
    public void PstAddress_BecomesOwn_AndGoesBackToItsCounterparty()
    {
        var day = new DateTime(2018, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        using var db = ArchiveDb.Open(_dir, "p");
        db.SetOwnAddresses(new[] { "danny@miaditta.it" });
        var inbox = db.GetOrAddFolder("pst", "inbox", "Posta in arrivo");
        var sent = db.GetOrAddFolder("pst", "sent", "Posta inviata");
        db.AddHeaders(inbox, new[] { Header("in1", "mario@acme.it", day) }, folderDone: true);
        db.AddHeaders(sent, new[] { Header("out1", "danny@vecchiaditta.it", day.AddDays(1)) }, folderDone: true);
        foreach (var pending in db.NextPendingBodies(10))
        {
            var body = new ArchiveBody { Text = "Testo " + pending.EntryId };
            body.Recipients.Add(new ArchiveRecipient { Address = pending.EntryId == "in1" ? "danny@vecchiaditta.it" : "mario@acme.it" });
            db.SaveBody(pending.Id, body);
        }

        // Without the PST's addresses the old address is a counterparty the user "wrote to".
        var old = db.CounterpartyOf("danny@vecchiaditta.it");
        Assert.NotNull(old);
        Assert.True(db.IsTwoWay(old!.Value));
        var acme = db.CounterpartyOf("mario@acme.it")!.Value;
        Assert.True(db.IsTwoWay(acme));

        db.SetOwnAddresses(new[] { "danny@miaditta.it", "Danny@VecchiaDitta.it" });
        Assert.Null(db.CounterpartyOf("danny@vecchiaditta.it"));
        Assert.False(db.IsTwoWay(old.Value));
        Assert.True(db.IsTwoWay(acme));

        // An empty set (Outlook could not say) changes nothing.
        db.SetOwnAddresses(Array.Empty<string>());
        Assert.Null(db.CounterpartyOf("danny@vecchiaditta.it"));

        // The user takes it out: external again, under its own counterparty, two_way recomputed.
        db.SetOwnAddresses(new[] { "danny@miaditta.it" });
        Assert.Equal(old, db.CounterpartyOf("danny@vecchiaditta.it"));
        Assert.True(db.IsTwoWay(old.Value));
        Assert.Null(db.CounterpartyOf("danny@miaditta.it"));
    }
}
