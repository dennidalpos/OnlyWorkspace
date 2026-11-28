using OnlyWorkspace.ConfigTool.Forms;

namespace OnlyWorkspace.Tests;

/// <summary>#177: save messages and numeric limits of the ConfigTool, checked without a form.</summary>
public sealed class SaveMessagesTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 9, 5, 0);

    [Fact]
    public void Saved_WithoutWarnings_IsSuccess()
    {
        var (text, tone) = SaveMessages.Saved(new List<string>(), Now);
        Assert.Equal("Salvato alle 09:05. Le modifiche sono attive per l'Add-in Outlook.", text);
        Assert.Equal(Tone.Success, tone);
    }

    [Fact]
    public void Saved_WithWarnings_ShowsTheFirstAndCountsTheRest()
    {
        var one = SaveMessages.Saved(new List<string> { "manca la chiave" }, Now);
        Assert.Equal("Salvato alle 09:05, con un avviso: manca la chiave", one.Text);
        Assert.Equal(Tone.Warning, one.Tone);

        var two = SaveMessages.Saved(new List<string> { "primo", "secondo" }, Now);
        Assert.Equal("Salvato alle 09:05, con 2 avvisi (tutti nel log di Manutenzione e backup): primo", two.Text);
    }

    [Fact]
    public void ValidationAndBlockedTexts_ListEveryItem()
    {
        Assert.Equal("Validazione fallita:\n\n- a\n- b", SaveMessages.ValidationFailed(new[] { "a", "b" }));
        string blocked = SaveMessages.BlockedUntilNotice(new[] { "host1", "host2" });
        Assert.StartsWith("Salvato. Bloccati finché non si accetta l'informativa", blocked);
        Assert.EndsWith("host1, host2.", blocked);
    }

    [Fact]
    public void NumericLimits_ClampToTheBoxRange()
    {
        Assert.Equal(new[] { 10, 50, 200 }, new[] { -5, 50, 9999 }.Select(SaveMessages.MaxSearchItems));
        Assert.Equal(new[] { 1, 30, 365 }, new[] { -5, 30, 9999 }.Select(SaveMessages.RetentionDays));
        Assert.Equal(new[] { 10, 500, 1000 }, new[] { -5, 500, 9999 }.Select(SaveMessages.MaxConversations));
    }
}
