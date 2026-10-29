using System;
using System.Collections.Generic;
using System.Linq;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Core.Security;

/// <summary>
/// What the security check may send to a web search: the sender's domain and the domains of the links, never an
/// address, a subject or text. Nothing for an email the user sent (Danny, 17/10/2026: the address and subject of his
/// own sent email brought back people-search pages about him).
/// </summary>
public static class SecurityWebQuery
{
    private const int MaxDomains = 8;

    /// <summary>The query, or null when there is no domain to look up or the email is the user's own.</summary>
    public static string? For(EmailSecurityInput input, EmailSecurityReport report)
    {
        string from = CounterpartyResolver.Normalize(input.FromAddress);
        if (from.Length == 0 || input.OwnAddresses.Contains(from, StringComparer.OrdinalIgnoreCase)) return null;

        var domains = new List<string>();
        void Add(string host)
        {
            var domain = SecurityDomains.Registrable(host);
            if (domain.Length > 0 && domains.Count < MaxDomains && !domains.Contains(domain, StringComparer.OrdinalIgnoreCase)) domains.Add(domain);
        }

        // A mailbox provider (gmail.com, a PEC provider) says nothing about who wrote.
        int at = from.LastIndexOf('@');
        if (at > 0 && CounterpartyResolver.KeyOf(from).IndexOf('@') < 0) Add(from.Substring(at + 1));
        foreach (var link in report.Links)
            if (Uri.TryCreate(link.Destination.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + link.Destination : link.Destination,
                    UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                Add(uri.Host);

        return domains.Count == 0 ? null : "Segnalazioni tecniche di phishing, frode o compromissione dei domini (escludere recensioni e reputazione commerciale): " + string.Join(", ", domains);
    }

    /// <summary>
    /// What a scan of several emails (meetings and deadlines) may search: the senders' domains, comma separated, never
    /// names, subjects or text (Danny, 18/10/2026: the subjects of the first emails brought back profiles of him).
    /// Senders are "Name &lt;address&gt;" or an address; the user's own and mailbox providers add nothing. Null when none is left.
    /// </summary>
    public static string? SenderDomains(IEnumerable<string> senders, IEnumerable<string> ownAddresses)
    {
        var own = new HashSet<string>(ownAddresses.Select(CounterpartyResolver.Normalize), StringComparer.OrdinalIgnoreCase);
        var domains = new List<string>();
        foreach (var sender in senders)
        {
            int open = sender.LastIndexOf('<');
            string from = CounterpartyResolver.Normalize(open >= 0 ? sender.Substring(open) : sender);
            int at = from.LastIndexOf('@');
            if (at <= 0 || own.Contains(from) || CounterpartyResolver.KeyOf(from).IndexOf('@') >= 0) continue;
            var domain = SecurityDomains.Registrable(from.Substring(at + 1));
            if (domain.Length > 0 && !domains.Contains(domain, StringComparer.OrdinalIgnoreCase)) domains.Add(domain);
            if (domains.Count == MaxDomains) break;
        }
        return domains.Count == 0 ? null : string.Join(", ", domains);
    }
}
