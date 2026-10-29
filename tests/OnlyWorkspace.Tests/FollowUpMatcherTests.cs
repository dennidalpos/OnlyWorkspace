using OnlyWorkspace.Core.Agents;
using Xunit;

namespace OnlyWorkspace.Tests;

public sealed class FollowUpMatcherTests
{
    [Fact]
    public void SubjectAndReference_AreRecognizedAcrossReplies()
    {
        Assert.Equal("Proposta commerciale", FollowUpMatcher.NormalizeSubject("Re: Fw: Proposta commerciale"));
        var refs = FollowUpMatcher.References("Rif. ABC-1234; ordine 778899");
        Assert.Contains("ABC-1234", refs);
        Assert.Contains("778899", refs);
        Assert.Equal("Stessa controparte e riferimento",
            FollowUpMatcher.Relation("Ordine 778899", "Aggiornamento", "Confermo ordine 778899", refs, true));
    }

    [Fact]
    public void CommonWords_AreNotReferences_AndItalianPrefixesAreRemoved()
    {
        Assert.Empty(FollowUpMatcher.References("Con riferimento alla vostra, ho inviato quanto ordinato. Ordine della settimana, possibile?"));
        var refs = FollowUpMatcher.References("RIF 12345, ORD-2024/77, INV2024001");
        Assert.Equal(new[] { "12345", "2024/77", "2024001" }, refs);
        Assert.Equal("Ordine 123", FollowUpMatcher.NormalizeSubject("R: I: Fwd: Ordine 123"));
    }

    [Fact]
    public void UnrelatedMessage_IsExcluded_AndWeakSubjectIsMarkedUncertain()
    {
        var refs = FollowUpMatcher.References("Ticket QWER1234");
        Assert.Null(FollowUpMatcher.Relation("Richiesta assistenza", "Fattura marzo", "Non pertinente", refs, false));
        Assert.Equal("Stesso oggetto (da verificare)",
            FollowUpMatcher.Relation("Richiesta assistenza", "Re: Richiesta assistenza", "Non pertinente", refs, false));
    }
}
