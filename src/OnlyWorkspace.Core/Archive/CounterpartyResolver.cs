using System;
using System.Collections.Generic;

namespace OnlyWorkspace.Core.Archive;

/// <summary>
/// Groups addresses into counterparties: one per company domain (mario@acme.it, fatture@pec.acme.it → acme.it),
/// one per address on mailbox providers, where the domain says nothing about who writes.
/// </summary>
public static class CounterpartyResolver
{
    private static readonly HashSet<string> MailboxProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "outlook.it", "hotmail.com", "hotmail.it", "live.com", "live.it", "msn.com",
        "yahoo.com", "yahoo.it", "ymail.com", "icloud.com", "me.com", "mac.com", "aol.com", "proton.me", "protonmail.com", "gmx.com", "gmx.de",
        "libero.it", "virgilio.it", "alice.it", "tin.it", "tim.it", "tiscali.it", "fastwebnet.it", "email.it", "inwind.it", "iol.it", "teletu.it", "vodafone.it", "aruba.it",
        // PEC providers
        "pec.it", "legalmail.it", "arubapec.it", "postacert.it", "pec.libero.it", "pec.aruba.it", "postecert.it", "pec.poste.it", "cert.legalmail.it",
        "registerpec.it", "pecimprese.it", "mypec.eu", "pec.net", "sicurezzapostale.it", "gigapec.it", "pec.buffetti.it", "pec.cgn.it", "pecprofessionisti.it", "lamiapec.it", "casellapec.com", "pec.tim.it"
    };

    // Second-level labels that are part of the public suffix (acme.co.uk, acme.com.br).
    private static readonly HashSet<string> SuffixLabels = new(StringComparer.OrdinalIgnoreCase) { "co", "com", "org", "net", "gov", "edu", "ac" };

    /// <summary>Mailbox or PEC provider: its domain says nothing about who writes.</summary>
    public static bool IsMailboxProvider(string? domain) => MailboxProviders.Contains((domain ?? string.Empty).Trim());

    public static string Normalize(string? address) => (address ?? string.Empty).Trim().Trim('<', '>', '\'', '"').ToLowerInvariant();

    /// <summary>Grouping key of an address; the address itself when it has no usable domain (Exchange DN) or belongs to a mailbox provider.</summary>
    public static string KeyOf(string? address)
    {
        var addr = Normalize(address);
        int at = addr.LastIndexOf('@');
        if (at <= 0 || at == addr.Length - 1) return addr;

        var domain = addr.Substring(at + 1);
        if (MailboxProviders.Contains(domain)) return addr;

        var labels = domain.Split('.');
        if (labels.Length <= 2) return domain;
        int keep = labels[labels.Length - 1].Length == 2 && SuffixLabels.Contains(labels[labels.Length - 2]) ? 3 : 2;
        var company = string.Join(".", labels, labels.Length - keep, keep);
        // legalmail.it is a provider, cert.legalmail.it too.
        return MailboxProviders.Contains(company) ? addr : company;
    }

    /// <summary>Name shown until the user renames it: the domain, or the person behind a single address.</summary>
    public static string DefaultName(string key, string? displayName)
    {
        if (key.IndexOf('@') < 0 && key.IndexOf('/') < 0) return key;
        var name = (displayName ?? string.Empty).Trim().Trim('\'', '"');
        return name.Length > 0 && name.IndexOf('@') < 0 ? name : key;
    }
}
