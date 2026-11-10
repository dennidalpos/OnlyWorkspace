using System;
using System.Globalization;
using System.IO;
using Nager.PublicSuffix;
using Nager.PublicSuffix.Exceptions;
using Nager.PublicSuffix.Models;
using Nager.PublicSuffix.RuleParsers;
using Nager.PublicSuffix.RuleProviders;

namespace OnlyWorkspace.Core.Security;

internal static class SecurityDomains
{
    private static readonly Lazy<DomainParser> Parser = new(() =>
    {
        using var stream = typeof(SecurityDomains).Assembly.GetManifestResourceStream("OnlyWorkspace.Core.Security.public_suffix_list.dat")
            ?? throw new InvalidOperationException("Public Suffix List resource missing.");
        using var reader = new StreamReader(stream);
        return new DomainParser(new StaticRuleProvider(new TldRuleParser(TldRuleDivisionFilter.All).ParseRules(reader.ReadToEnd())));
    });

    internal static string Normalize(string host)
    {
        try { return new IdnMapping().GetAscii(host.Trim().TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { return string.Empty; }
    }

    internal static string Registrable(string host)
    {
        host = Normalize(host);
        if (host.Length == 0) return string.Empty;
        try { return Parser.Value.Parse(host)?.RegistrableDomain ?? string.Empty; }
        catch (ParseException) { return string.Empty; }
    }

    internal static bool SameDomain(string left, string right)
    {
        left = Normalize(left);
        right = Normalize(right);
        if (left.Length == 0 || right.Length == 0) return false;
        if (left == right) return true;
        var registrable = Registrable(left);
        return registrable.Length > 0 && registrable == Registrable(right);
    }
}
