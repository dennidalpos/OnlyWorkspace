using System.Text;
using System.Text.Json;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the unread part of «Briefing di oggi» outside TaskPaneCoordinator, with no Outlook and no model.</summary>
public sealed class BriefingReportTests
{
    private static List<EmailSearchResult> Unread() => new()
    {
        new() { Subject = "Fattura", SenderName = "Alfa", EntryId = "e0", StoreId = "s0" },
        new() { Subject = "Ferie", SenderName = "Beta", EntryId = "e1", StoreId = "s1" },
        new() { Subject = "Newsletter", SenderName = "Gamma", EntryId = "e2", StoreId = "s2" }
    };

    private static TriageEntry Entry(int index, string priority, string category = "", string reason = "") =>
        new() { Index = index, Priority = priority, Category = category, Reason = reason };

    private static BriefingResult Result(string summary, params TriageEntry[] entries)
    {
        var result = new BriefingResult { Summary = summary };
        result.Emails.AddRange(entries);
        return result;
    }

    private static object? Get(object source, string name) => source.GetType().GetProperty(name)!.GetValue(source);

    [Fact]
    public void Triaged_OrdersByPriorityThenByListPosition()
    {
        var briefing = Result("", Entry(2, "bassa"), Entry(1, "media"), Entry(0, "alta"), Entry(3, "alta"));

        Assert.Equal(new[] { 0, 3, 1, 2 }, BriefingReport.Triaged(briefing).Select(t => t.Index));
    }

    [Fact]
    public void AppendUnread_ListsTriagedFirstWithPriorityAndReason_ThenTheRestPlainly()
    {
        var briefing = Result("Giornata tranquilla.", Entry(1, "alta", reason: "scade oggi"), Entry(0, "media"));
        var triaged = BriefingReport.Triaged(briefing);
        var md = new StringBuilder("Agenda");

        BriefingReport.AppendUnread(md, briefing, triaged, Unread(), null);

        string text = md.ToString().Replace("\r\n", "\n");
        Assert.StartsWith("Agenda\n**In sintesi**\n\nGiornata tranquilla.\n", text, StringComparison.Ordinal);
        Assert.Contains("**Email non lette (3)**\n", text, StringComparison.Ordinal);
        Assert.Contains("- **Alta** — Ferie (Beta): scade oggi\n- **Media** — Fattura (Alfa)\n- Newsletter (Gamma)\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendUnread_WithoutASummary_OmitsTheSummaryBlock()
    {
        var md = new StringBuilder();

        BriefingReport.AppendUnread(md, Result(""), new List<TriageEntry>(), Unread(), null);

        Assert.DoesNotContain("In sintesi", md.ToString(), StringComparison.Ordinal);
        Assert.Contains("- Fattura (Alfa)", md.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendUnread_WhenTheTriageFailed_SaysWhyBeforeTheList()
    {
        var md = new StringBuilder();

        BriefingReport.AppendUnread(md, new BriefingResult(), new List<TriageEntry>(), Unread(), "Risposta non valida. Priorità e categorie non assegnate.");

        string text = md.ToString().Replace("\r\n", "\n");
        Assert.True(text.IndexOf("Risposta non valida", StringComparison.Ordinal) < text.IndexOf("- Fattura", StringComparison.Ordinal));
    }

    [Fact]
    public void CategoryCards_AreOnlyForTriagedEmailsWithACategory()
    {
        var briefing = Result("", Entry(0, "alta", "Clienti"), Entry(1, "media"));

        var cards = BriefingReport.CategoryCards(BriefingReport.Triaged(briefing), Unread());

        var card = Assert.Single(cards);
        Assert.Equal("triage", Get(card, "kind"));
        Assert.Equal("Fattura", Get(card, "title"));
        Assert.Equal("Priorità alta · Alfa", Get(card, "when"));
        Assert.Equal("Applica categoria «Clienti»", Get(card, "buttonLabel"));
        Assert.Equal(true, Get(card, "once"));
        var message = Get(card, "message")!;
        Assert.Equal("apply_category", Get(message, "type"));
        Assert.Equal("e0", Get(message, "entryId"));
        Assert.Equal("s0", Get(message, "storeId"));
        Assert.Equal("Clienti", Get(message, "category"));
    }

    [Fact]
    public void ModelContent_KeepsOnlyTheValidatedResultWithOneBasedIds()
    {
        var briefing = Result("Sintesi", Entry(1, "alta", "Clienti", "scade oggi"));

        using var doc = JsonDocument.Parse(BriefingReport.ModelContent(briefing, BriefingReport.Triaged(briefing)));

        Assert.Equal("Sintesi", doc.RootElement.GetProperty("summary").GetString());
        var email = Assert.Single(doc.RootElement.GetProperty("emails").EnumerateArray());
        Assert.Equal(2, email.GetProperty("id").GetInt32());
        Assert.Equal("alta", email.GetProperty("priority").GetString());
        Assert.Equal("Clienti", email.GetProperty("category").GetString());
        Assert.Equal("scade oggi", email.GetProperty("reason").GetString());
        Assert.DoesNotContain("Ferie", doc.RootElement.ToString(), StringComparison.Ordinal);
    }
}
