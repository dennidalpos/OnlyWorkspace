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
    [InlineData("Rossi & Figli", "AZIENDA")]
    [InlineData("Studio 21", "AZIENDA")]
    [InlineData("Microsoft", "AZIENDA")]
    [InlineData("AreaJob", "AZIENDA")]
    [InlineData("Coca-Cola", "AZIENDA")]
    [InlineData("Lumen Digitale", "AZIENDA")]
    [InlineData("Harbor Lane Consulting", "AZIENDA")]
    [InlineData("Ortofrutta Delle Colline", "AZIENDA")]
    [InlineData("Laura Verdi", "PERSONA")]
    [InlineData("Anna Maria Longo", "PERSONA")]
    [InlineData("Sara Spadoni", "PERSONA")]
    [InlineData("Luca Sasso", "PERSONA")]
    [InlineData("Chiara De Luca", "PERSONA")]
    [InlineData("sara conti", "PERSONA")]
    [InlineData("Gian Luca Bruni", "PERSONA")]
    [InlineData("M. Rossi", "PERSONA")]
    [InlineData("Rossi, Mario", "PERSONA")]
    [InlineData("mario.rossi@esempio.example", "PERSONA")]
    [InlineData("Mo", "PERSONA")]
    [InlineData("", "PERSONA")]
    [InlineData(null, "PERSONA")]
    public void DisplayNameIsAnOrganizationWhenItHasALegalFormAnAmpersandADigitASingleWordOrABusinessNoun(string? name, string expected) =>
        Assert.Equal(expected, PrivacyEntityCategory.OfDisplayName(name));
}
