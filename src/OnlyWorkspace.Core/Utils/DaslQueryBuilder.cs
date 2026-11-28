using System;
using System.Collections.Generic;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Utils;

public static class DaslQueryBuilder
{
    public const string PropInternetMessageId = "http://schemas.microsoft.com/mapi/proptag/0x1035001E";
    public const string PropSenderSmtpAddress = "http://schemas.microsoft.com/mapi/proptag/0x5D01001F";
    public const string PropDateReceived = "urn:schemas:httpmail:datereceived";
    public const string PropSubject = "urn:schemas:httpmail:subject";
    public const string PropTextDescription = "urn:schemas:httpmail:textdescription";
    public const string PropSenderEmail = "urn:schemas:httpmail:senderemail";
    public const string PropSenderName = "urn:schemas:httpmail:sendername";
    public const string PropFromEmail = "urn:schemas:httpmail:fromemail";
    public const string PropFromName = "urn:schemas:httpmail:fromname";
    public const string PropDisplayTo = "urn:schemas:httpmail:displayto";
    public const string PropDisplayCc = "urn:schemas:httpmail:displaycc";

    public static string BuildArchiveRecentFilter(DateTime receivedSinceUtc, DateTime modifiedSinceUtc, DateTime modifiedUntilUtc)
    {
        if (modifiedSinceUtc > modifiedUntilUtc) throw new ArgumentException("The modification window is reversed.");
        string Date(DateTime value) => value.ToUniversalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        return $"@SQL=(\"{PropDateReceived}\" >= '{Date(receivedSinceUtc)}') OR " +
            $"(\"DAV:getlastmodified\" >= '{Date(modifiedSinceUtc)}' AND \"DAV:getlastmodified\" <= '{Date(modifiedUntilUtc)}')";
    }

    public static string BuildEmailSearchFilter(EmailSearchQuery query)
    {
        if (query == null) return string.Empty;

        var clauses = new List<string>();

        if (query.StartDate.HasValue)
        {
            var startUtc = query.StartDate.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            clauses.Add($"\"{PropDateReceived}\" >= '{startUtc}'");
        }

        if (query.EndDate.HasValue)
        {
            var endUtc = query.EndDate.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            clauses.Add($"\"{PropDateReceived}\" <= '{endUtc}'");
        }

        // senderemail alone never matches in Outlook filters (checked on IMAP stores): fromemail carries the address.
        var sender = AnyPersonLike(query.Sender, PropFromEmail, PropSenderEmail, PropSenderName, PropFromName);
        var recipient = AnyPersonLike(query.Recipient, PropDisplayTo, PropDisplayCc);
        if (sender != null && recipient != null && query.EitherPerson)
        {
            clauses.Add($"({sender} OR {recipient})");
        }
        else
        {
            if (sender != null) clauses.Add($"({sender})");
            if (recipient != null) clauses.Add($"({recipient})");
        }

        var subject = AnyLike(query.Subject, PropSubject);
        if (subject != null) clauses.Add(subject);

        if (!string.IsNullOrWhiteSpace(query.Keywords))
        {
            if (query.KeywordsAny)
            {
                var terms = MailSearchTerms.Terms(query.Keywords!);
                var alternatives = new List<string>();
                foreach (var term in terms)
                {
                    var safe = EscapeString(term);
                    alternatives.Add($"\"{PropSubject}\" LIKE '%{safe}%'");
                    alternatives.Add($"\"{PropTextDescription}\" LIKE '%{safe}%'");
                }
                if (alternatives.Count > 0) clauses.Add("(" + string.Join(" OR ", alternatives) + ")");
            }
            else
            {
                var kwSafe = EscapeString(query.Keywords!.Trim());
                clauses.Add($"(\"{PropSubject}\" LIKE '%{kwSafe}%' OR \"{PropTextDescription}\" LIKE '%{kwSafe}%')");
            }
        }

        if (clauses.Count == 0)
        {
            return string.Empty;
        }

        return "@SQL=" + string.Join(" AND ", clauses.ToArray());
    }

    /// <summary>
    /// Substring match on any of the properties; null when the text leaves nothing to search.
    /// With wildcards only the longest plain fragment is sent: EmailRowFilter does the exact check on the rows.
    /// </summary>
    /// <summary>
    /// A person filter may list alternatives ("Acme, acmespa.com", when a caller supplies multiple sender clues):
    /// each one is matched on its own. A subject keeps its commas.
    /// </summary>
    private static string? AnyPersonLike(string? text, params string[] properties)
    {
        var parts = new List<string>();
        foreach (var alternative in (text ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (AnyLike(alternative.Trim(), properties) is { } like) parts.Add(like);
        return parts.Count == 0 ? null : string.Join(" OR ", parts);
    }

    private static string? AnyLike(string? text, params string[] properties)
    {
        var pattern = WildcardPattern.Parse(text);
        if (pattern == null || pattern.LongestLiteral.Length == 0) return null;
        var safe = EscapeString(pattern.LongestLiteral);
        return string.Join(" OR ", Array.ConvertAll(properties, p => $"\"{p}\" LIKE '%{safe}%'"));
    }

    public static string EscapeString(string value)
    {
        return value.Replace("'", "''");
    }
}
