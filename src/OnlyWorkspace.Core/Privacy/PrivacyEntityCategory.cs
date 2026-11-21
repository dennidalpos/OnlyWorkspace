using System;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>
/// #292: the archive only knows display names, which were all labelled PERSONA. A name carrying a legal form or an
/// institution word is an organization, so the local entity index masks it as AZIENDA. Anything else stays PERSONA.
/// </summary>
public static class PrivacyEntityCategory
{
    private static readonly Regex Organization = new(
        @"(?<![\p{L}\p{N}])(s\s?\.?\s?r\s?\.?\s?l\s?\.?(\s?s\.?)?|s\s?\.?\s?p\s?\.?\s?a\s?\.?|s\s?\.?\s?n\s?\.?\s?c\s?\.?|s\s?\.?\s?a\s?\.?\s?s\s?\.?|s\s?\.?\s?c\s?\.?\s?a\s?\.?\s?r\s?\.?\s?l\s?\.?|" +
        @"soc(ieta|ietà)?\.?|cooperativa|coop|fondazione|associazione|banca|studio\s+associato|gruppo|ditta|ltd|llc|inc|gmbh)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static string OfDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name!.Length > 200) return "PERSONA";
        try { return Organization.IsMatch(name) ? "AZIENDA" : "PERSONA"; }
        catch (RegexMatchTimeoutException) { return "PERSONA"; }
    }
}
