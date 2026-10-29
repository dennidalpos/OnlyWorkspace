using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Context;

public sealed class ContextBuilder : IContextBuilder
{
    // Tags only: <mario@example.com> and <https://…> in a plain-text body are addresses and links to keep.
    // A tag never spans another '<': [^>]+ rescanned to the end from every '<' (quadratic on crafted bodies).
    private static readonly Regex HtmlTagRegex = new(@"<(?![^\s<>]*(?:@|://)[^\s<>]*>)[^<>]+>", RegexOptions.Compiled);
    private static readonly Regex MultipleNewlinesRegex = new(@"(\r?\n\s*){3,}", RegexOptions.Compiled);
    private static readonly Regex QuoteFromRegex = new(@"^(Da|From)\s*:\s*\S", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuoteFieldRegex = new(@"^(Inviato|Sent|Data|Date|A|To|Cc|Oggetto|Subject)\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuoteWroteRegex = new(@"(ha scritto|wrote)\s*:?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ForwardMarkerRegex = new(@"^[-\s]*(Inizio messaggio inoltrato|Messaggio inoltrato|Forwarded message|Begin forwarded message)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ForwardSubjectRegex = new(@"^\s*(fwd?|i|inoltro|tr|wg|rv)\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Body without HTML, quoted replies and disclaimers. A forward keeps the forwarded message (marker line or
    /// forward subject): it is the content, not history. <paramref name="senderTextOnly"/> cuts it as well.
    /// </summary>
    public string CleanEmailBody(string rawBody, string? subject = null, bool senderTextOnly = false)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return string.Empty;

        string text = rawBody;
        if (text.Contains("<") && text.Contains(">"))
        {
            text = HtmlTagRegex.Replace(text, " ");
        }

        var lines = new List<string>();
        using (var reader = new StringReader(text))
        {
            string? l;
            while ((l = reader.ReadLine()) != null) lines.Add(l);
        }

        bool keepForward = !senderTextOnly;
        bool forwardSubject = subject != null && ForwardSubjectRegex.IsMatch(subject);
        var sb = new StringBuilder();
        string previous = string.Empty;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Quote markers
            if (trimmed.StartsWith(">") || trimmed.StartsWith("&gt;"))
            {
                continue;
            }

            // Reply headers
            if (trimmed.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("-----Messaggio originale-----", StringComparison.OrdinalIgnoreCase) ||
                IsQuotedHeaderBlock(lines, i) ||
                IsQuoteIntro(lines, i))
            {
                // Only the first header block is the forwarded message; the history inside it is still cut.
                if (keepForward && (forwardSubject || ForwardMarkerRegex.IsMatch(previous)))
                {
                    keepForward = false;
                    sb.AppendLine(line);
                    previous = trimmed;
                    continue;
                }
                break;
            }
            if (trimmed.Length > 0) previous = trimmed;

            // Disclaimers
            if (trimmed.StartsWith("Le informazioni contenute in questo messaggio", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("This message and any attachments are confidential", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Ai sensi del Regolamento UE 2016/679", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            sb.AppendLine(line);
        }

        var cleaned = sb.ToString().Trim();
        cleaned = MultipleNewlinesRegex.Replace(cleaned, "\n\n");
        return cleaned;
    }

    // "Da: X" counts as a quoted header only if followed by Inviato/A/Oggetto…: plain "Da: domani…" is body text.
    private static bool IsQuotedHeaderBlock(List<string> lines, int i)
    {
        if (!QuoteFromRegex.IsMatch(lines[i].Trim())) return false;
        for (int j = i + 1, seen = 0; j < lines.Count && seen < 3; j++)
        {
            var next = lines[j].Trim();
            if (next.Length == 0) continue;
            if (QuoteFieldRegex.IsMatch(next)) return true;
            seen++;
        }
        return false;
    }

    // "Il giorno … ha scritto:" / "On … wrote:", possibly wrapped on the next line.
    private static bool IsQuoteIntro(List<string> lines, int i)
    {
        var t = lines[i].Trim();
        if (!t.StartsWith("Il giorno ", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("On ", StringComparison.OrdinalIgnoreCase))
            return false;
        return QuoteWroteRegex.IsMatch(t) || (i + 1 < lines.Count && QuoteWroteRegex.IsMatch(lines[i + 1].Trim()));
    }

    /// <summary>Exact prefix before a recognized reply/forward separator. Without one, keep every character.
    /// Unlike CleanEmailBody this does not remove disclaimers, HTML-looking data or isolated quoted lines.</summary>
    public static string NewEmailText(string body)
    {
        var lines = new List<string>();
        var starts = new List<int>();
        int at = 0;
        using (var reader = new StringReader(body))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                starts.Add(at);
                lines.Add(line);
                at += line.Length;
                if (at < body.Length && body[at] == '\r') at++;
                if (at < body.Length && body[at] == '\n') at++;
            }
        }
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("-----Messaggio originale-----", StringComparison.OrdinalIgnoreCase) ||
                ForwardMarkerRegex.IsMatch(line) || IsQuotedHeaderBlock(lines, i) || IsQuoteIntro(lines, i))
                return body.Substring(0, starts[i]);
        }
        return body;
    }

    /// <summary>Estimate used wherever a text is measured against a token budget.</summary>
    public const double CharsPerToken = 3.5;

    /// <summary>Characters a budget allows; 0 tokens = no client-side limit.</summary>
    public static int MaxChars(int budgetTokens) =>
        budgetTokens <= 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, budgetTokens * CharsPerToken);

    public bool Fits(string text, int budgetTokens) => (text?.Length ?? 0) <= MaxChars(budgetTokens);

    public string EnsureFits(string text, int budgetTokens)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length > MaxChars(budgetTokens))
        {
            throw new ContextTooLongException((int)Math.Ceiling(text.Length / CharsPerToken), budgetTokens);
        }
        return text;
    }

    /// <summary>Whole items while they fit: an item is never cut. Not even the first one fitting is an error.</summary>
    private static string JoinWhole(IEnumerable<string> items, int budgetTokens, out int included)
    {
        int room = MaxChars(budgetTokens);
        var sb = new StringBuilder();
        included = 0;
        foreach (var item in items)
        {
            if (sb.Length + item.Length > room)
            {
                if (included == 0) throw new ContextTooLongException((int)Math.Ceiling(item.Length / CharsPerToken), budgetTokens);
                break;
            }
            sb.Append(item);
            included++;
        }
        return sb.ToString().Trim();
    }

    public string FormatEmailsContext(IEnumerable<EmailSearchResult> emails, int budgetTokens, out int included)
    {
        included = 0;
        return emails == null ? string.Empty : JoinWhole(EmailBlocks(emails), budgetTokens, out included);
    }

    private IEnumerable<string> EmailBlocks(IEnumerable<EmailSearchResult> emails)
    {
        int index = 1;
        foreach (var email in emails)
        {
            if (email == null) continue;

            var sb = new StringBuilder();
            sb.AppendLine($"--- EMAIL #{index} ---");
            sb.AppendLine($"Da: {email.SenderName} <{email.SenderEmail}>");
            sb.AppendLine($"Data: {email.ReceivedTime:dd/MM/yyyy HH:mm}");
            sb.AppendLine($"Oggetto: {email.Subject}");
            sb.AppendLine($"Cartella/Account: {email.FolderName} ({email.StoreDisplayName})");
            if (email.IsEncrypted)
            {
                sb.AppendLine("Stato: [Crittografato S/MIME o protetto]");
            }
            if (email.HasOnlyHeaders)
            {
                sb.AppendLine("Stato: [Solo intestazioni, corpo non scaricato]");
            }

            var clean = CleanEmailBody(email.Body, email.Subject);
            if (!string.IsNullOrWhiteSpace(clean))
            {
                sb.AppendLine("Corpo:");
                sb.AppendLine(clean);
            }
            sb.AppendLine();
            index++;
            yield return sb.ToString();
        }
    }

    public string FormatCalendarContext(IEnumerable<AppointmentInfo> appointments, int budgetTokens, out int included)
    {
        included = 0;
        return appointments == null ? string.Empty : JoinWhole(AppointmentBlocks(appointments), budgetTokens, out included);
    }

    private IEnumerable<string> AppointmentBlocks(IEnumerable<AppointmentInfo> appointments)
    {
        int index = 1;
        foreach (var appt in appointments)
        {
            if (appt == null) continue;

            var sb = new StringBuilder();
            sb.AppendLine($"--- IMPEGNO #{index} ---");
            sb.AppendLine($"Titolo: {appt.Subject}");
            sb.AppendLine($"Inizio: {appt.Start:dd/MM/yyyy HH:mm} | Fine: {appt.End:dd/MM/yyyy HH:mm}");
            if (!string.IsNullOrWhiteSpace(appt.Location)) sb.AppendLine($"Luogo: {appt.Location}");
            if (!string.IsNullOrWhiteSpace(appt.Organizer)) sb.AppendLine($"Organizzatore: {appt.Organizer}");
            if (appt.IsAllDayEvent) sb.AppendLine("Tipo: Giornata intera");
            if (appt.IsRecurring) sb.AppendLine("Ricorrenza: Sì (serie ricorrente)");
            if (!string.IsNullOrWhiteSpace(appt.StoreDisplayName)) sb.AppendLine($"Calendario: {appt.StoreDisplayName}");

            var clean = CleanEmailBody(appt.Body);
            if (!string.IsNullOrWhiteSpace(clean))
            {
                sb.AppendLine($"Dettagli: {clean}");
            }
            sb.AppendLine();
            index++;
            yield return sb.ToString();
        }
    }

    public string FormatContactsContext(IEnumerable<ContactInfo> contacts, int budgetTokens, out int included)
    {
        included = 0;
        return contacts == null ? string.Empty : JoinWhole(ContactBlocks(contacts), budgetTokens, out included);
    }

    private static IEnumerable<string> ContactBlocks(IEnumerable<ContactInfo> contacts)
    {
        int index = 1;
        foreach (var c in contacts)
        {
            if (c == null) continue;

            var sb = new StringBuilder();
            sb.AppendLine($"--- CONTATTO #{index} ---");
            sb.AppendLine($"Nome: {c.FullName}");
            if (!string.IsNullOrWhiteSpace(c.Email)) sb.AppendLine($"Email: {c.Email}");
            if (!string.IsNullOrWhiteSpace(c.MobilePhone)) sb.AppendLine($"Cellulare: {c.MobilePhone}");
            if (!string.IsNullOrWhiteSpace(c.BusinessPhone)) sb.AppendLine($"Telefono Ufficio: {c.BusinessPhone}");
            if (!string.IsNullOrWhiteSpace(c.CompanyName)) sb.AppendLine($"Azienda: {c.CompanyName}");
            if (!string.IsNullOrWhiteSpace(c.JobTitle)) sb.AppendLine($"Ruolo: {c.JobTitle}");
            if (!string.IsNullOrWhiteSpace(c.StoreDisplayName)) sb.AppendLine($"Rubrica: {c.StoreDisplayName}");
            sb.AppendLine();
            index++;
            yield return sb.ToString();
        }
    }
}
