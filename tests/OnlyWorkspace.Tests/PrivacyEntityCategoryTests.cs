using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Tests;

public sealed class PrivacyEntityCategoryTests
{
    [Theory]
    [InlineData("Edilsud S.r.l.", "AZIENDA")]
    [InlineData("Tecnoservice SpA", "AZIENDA")]
    [InlineData("Pellegrini Forniture S.n.c.", "AZIENDA")]
    [InlineData("Rossi & C. sas", "AZIENDA")]
    [InlineData("Studio Associato Conti & Galli", "AZIENDA")]
    [InlineData("Banca Popolare del Lago", "AZIENDA")]
    [InlineData("Fondazione Il Girasole", "AZIENDA")]
    [InlineData("Gruppo Alfa Logistica", "AZIENDA")]
    [InlineData("Northwind Traders LLC", "AZIENDA")]
    [InlineData("Laura Verdi", "PERSONA")]
    [InlineData("Anna Maria Longo", "PERSONA")]
    [InlineData("Sara Spadoni", "PERSONA")]
    [InlineData("Luca Sasso", "PERSONA")]
    [InlineData("", "PERSONA")]
    [InlineData(null, "PERSONA")]
    public void DisplayNameIsAnOrganizationOnlyWhenItCarriesALegalFormOrInstitutionWord(string? name, string expected) =>
        Assert.Equal(expected, PrivacyEntityCategory.OfDisplayName(name));
}
