using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MimeKit;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace OnlyWorkspace.Core.Security;

/// <summary>Local inputs for the deterministic checks.</summary>
public sealed class EmailSecurityInput
{
    public string Subject { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    /// <summary>SMTP address; X.500 (Exchange internal) addresses are ignored for domain checks.</summary>
    public string FromAddress { get; set; } = string.Empty;
    /// <summary>PR_TRANSPORT_MESSAGE_HEADERS; empty for internal/locally created items.</summary>
    public string TransportHeaders { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public List<string> ReadLimitations { get; } = new();
    public IReadOnlyList<AttachmentFacts> Attachments { get; set; } = Array.Empty<AttachmentFacts>();
    /// <summary>PR_HASATTACH says at least one attachment exists; it does not count hidden attachments.</summary>
    public bool HasAttachmentFlag { get; set; }
    /// <summary>False when enumeration or metadata reads omitted an attachment.</summary>
    public bool AttachmentListComplete { get; set; } = true;
    /// <summary>SMTP domains of the user's own accounts, for lookalike detection.</summary>
    public IReadOnlyList<string> OwnDomains { get; set; } = Array.Empty<string>();
    /// <summary>SMTP addresses of the user's own accounts, for self-impersonation detection.</summary>
    public IReadOnlyList<string> OwnAddresses { get; set; } = Array.Empty<string>();
    /// <summary>Names the user's accounts send as ("Mario Rossi").</summary>
    public IReadOnlyList<string> OwnNames { get; set; } = Array.Empty<string>();
}

/// <summary>What the local checks observed, with the limits of what they could read. The verdict is SecurityVerdict's.</summary>
public sealed class EmailSecurityReport
{
    public List<SecurityObservation> Observations { get; } = new();
    public List<SecurityLimit> Limits { get; } = new();
    /// <summary>Microsoft Defender's results, kept apart: they never decide the app's badge.</summary>
    public List<DefenderEntry> Defender { get; } = new();
    public IReadOnlyList<AttachmentFacts> Attachments { get; internal set; } = Array.Empty<AttachmentFacts>();
    public List<SecurityLink> Links { get; } = new();
    /// <summary>The email has something the checks could read (body, links or an inspected attachment).</summary>
    public bool ContentAnalyzable { get; set; }
    /// <summary>The envelope a PEC provider puts around the original message.</summary>
    public bool PecEnvelope { get; internal set; }
    public bool Automatic { get; internal set; }

    public void Observe(string code, ObservationKind kind, ObservationOrigin origin, ObservationSubject subject, string text,
        string? factKey = null, string? source = null, string? quote = null) =>
        Observations.Add(new SecurityObservation(code, kind, origin, subject, text, factKey) { Source = source, Quote = quote });

    public void Limit(string text, bool partial)
    {
        if (!Limits.Any(l => l.Text == text)) Limits.Add(new SecurityLimit(text, partial));
    }

    /// <summary>Local facts for the model's context: never a source it may cite.</summary>
    public string ContextText()
    {
        var sb = new StringBuilder();
        foreach (var o in Observations) sb.Append("- ").Append(o.Origin == ObservationOrigin.Declared ? "dichiarato dal server: " : "").AppendLine(o.Text);
        foreach (var d in Defender) sb.Append("- ").AppendLine(d.Text);
        foreach (var l in Limits) sb.Append("- limite: ").AppendLine(l.Text);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Local, contextual checks on what the email shows: sender and envelope (PEC, on behalf of, automatic mail),
/// authentication results the servers declare (RFC 8601 / Microsoft 365), links in the body and in attachments,
/// attachments and their static reading. Each observation says what it is; the badge comes from SecurityVerdict.
/// Missing data is a limit, not an alarm; no domain, provider or known contact is exempt from concrete evidence.
/// </summary>
public static class EmailSecurityAnalyzer
{
    private const int MaxLinks = 200;

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "scr", "com", "pif", "bat", "cmd", "js", "jse", "vbs", "vbe", "wsf", "wsh", "hta", "ps1", "psm1",
        "msi", "msix", "msp", "appx", "jar", "lnk", "cpl", "reg", "dll", "iso", "img", "vhd", "vhdx",
        "application", "gadget", "xll", "chm", "url", "scf", "inf"
    };

    private static readonly HashSet<string> MacroExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "docm", "dotm", "xlsm", "xltm", "xlam", "pptm", "potm", "ppam", "ppsm", "sldm", "one"
    };

    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase) { "html", "htm", "shtml", "xhtml", "svg" };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "rar", "7z", "gz", "tgz", "ace", "cab", "arj", "xz", "bz2"
    };

    private static readonly Regex UrlLikeText = new(@"^(https?://|www\.)?((?:[\p{L}\p{N}-]+\.)+([\p{L}]{2,}))(?:[/:?#]\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Bare "name.tld" link text counts as a URL only with a common TLD ("fattura.pdf" is not a domain).
    private static readonly HashSet<string> CommonTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "it", "eu", "io", "co", "uk", "de", "fr", "es", "ch", "at", "nl", "be", "info", "biz",
        "gov", "edu", "app", "dev", "me", "us", "online", "site", "shop", "xyz", "top"
    };
    private static readonly Regex EmailInText = new(@"[\w.+-]+@([\w-]+\.)+[\w-]{2,}", RegexOptions.Compiled);
    private static readonly Regex ForwardSubject = new(@"^\s*(fwd?|fw|i|inoltro|tr|wg)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static EmailSecurityReport Analyze(EmailSecurityInput input)
    {
        var report = new EmailSecurityReport();
        foreach (var limitation in input.ReadLimitations) report.Limit(limitation, partial: true);
        List<KeyValuePair<string, string>> headers;
        try { headers = ParseHeaders(input.TransportHeaders); }
        catch (FormatException)
        {
            headers = new();
            report.Limit("Intestazioni Internet non leggibili: autenticazione non valutata.", partial: true);
        }
        string fromAddress = ResolveFromAddress(input, headers);
        string fromDomain = DomainOf(fromAddress);

        CheckContext(input, headers, fromAddress, report);
        CheckAuthentication(headers, report);
        CheckMicrosoftVerdict(headers, report);
        CheckSender(input, fromAddress, fromDomain, report);
        CheckReplyTo(headers, fromDomain, report);
        CheckLinks(input, report);
        CheckAttachments(input.Attachments, input.OwnDomains, report);
        if (input.HasAttachmentFlag && input.AttachmentListComplete && input.Attachments.Count == 0)
        {
            // Outlook blocks only types that run code; the files themselves never reach the checks.
            report.Observe("att.blocked", ObservationKind.Attention, ObservationOrigin.Local, ObservationSubject.Attachment,
                "Outlook ha bloccato l’accesso ad allegati di tipo potenzialmente pericoloso (programmi o script).", "att.blocked");
            report.Limit("Allegati bloccati da Outlook: non letti dai controlli né da Defender.", partial: true);
        }
        report.Attachments = input.Attachments;
        report.ContentAnalyzable = !string.IsNullOrWhiteSpace(input.Body) || !string.IsNullOrWhiteSpace(input.HtmlBody) ||
                                   report.Links.Count > 0 || input.Attachments.Any(a => a.Inspection is { Inspected: > 0 });
        return report;
    }

    // ---------- Context ----------

    /// <summary>Envelope and kind of message: they explain other observations, they never clear evidence.</summary>
    private static void CheckContext(EmailSecurityInput input, List<KeyValuePair<string, string>> headers, string fromAddress, EmailSecurityReport report)
    {
        // A PEC envelope: provider subject plus its transport header or its own two attachments.
        bool pecSubject = Agents.PecMessage.IsEnvelope(input.Subject ?? string.Empty);
        bool pecParts = FirstHeader(headers, "X-Trasporto") != null ||
                        (input.Attachments.Any(a => a.IsPecReceiptData) && input.Attachments.Any(a => a.Name.Equals("postacert.eml", StringComparison.OrdinalIgnoreCase)));
        report.PecEnvelope = pecSubject && pecParts;
        if (report.PecEnvelope)
            report.Observe("context.pec", ObservationKind.Context, ObservationOrigin.Local, ObservationSubject.Sender,
                $"Busta PEC: il gestore ({fromAddress}) consegna il messaggio originale allegato; il mittente tecnico è quello del gestore.");

        var autoSubmitted = FirstHeader(headers, "Auto-Submitted");
        var precedence = FirstHeader(headers, "Precedence");
        report.Automatic = (autoSubmitted != null && !autoSubmitted.Trim().StartsWith("no", StringComparison.OrdinalIgnoreCase)) ||
                           FirstHeader(headers, "List-Id") != null || FirstHeader(headers, "List-Unsubscribe") != null ||
                           (precedence != null && Regex.IsMatch(precedence, @"\b(bulk|list|junk)\b", RegexOptions.IgnoreCase));
        if (report.Automatic)
            report.Observe("context.automatic", ObservationKind.Context, ObservationOrigin.Local, ObservationSubject.Sender,
                "Email automatica o da lista di distribuzione: indirizzi di risposta diversi sono frequenti.");

        var sender = FirstHeader(headers, "Sender");
        var onBehalf = sender != null ? ExtractAddresses(sender).FirstOrDefault() : null;
        if (onBehalf != null && fromAddress.Length > 0 && !onBehalf.Equals(fromAddress, StringComparison.OrdinalIgnoreCase))
            report.Observe("context.onbehalf", ObservationKind.Context, ObservationOrigin.Local, ObservationSubject.Sender,
                $"Inviata da {onBehalf} per conto di {fromAddress}.");

        if (ForwardSubject.IsMatch(input.Subject ?? string.Empty))
            report.Observe("context.forward", ObservationKind.Context, ObservationOrigin.Local, ObservationSubject.Sender,
                "Email inoltrata: il mittente è chi inoltra; il messaggio inoltrato è nel corpo.");
    }

    // ---------- Headers ----------

    /// <summary>Preserve header order without assuming it proves origin.</summary>
    internal static List<KeyValuePair<string, string>> ParseHeaders(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw.TrimEnd() + "\r\n\r\n"));
        return HeaderList.Load(stream).Select(h => new KeyValuePair<string, string>(h.Field, h.Value)).ToList();
    }

    private static string? FirstHeader(List<KeyValuePair<string, string>> headers, string name) =>
        headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void CheckAuthentication(List<KeyValuePair<string, string>> headers, EmailSecurityReport report)
    {
        if (headers.Count == 0)
        {
            report.Limit("Intestazioni Internet assenti (tipico di email interne o create localmente): autenticazione non valutabile.", partial: false);
            return;
        }

        var reported = headers.Where(h => h.Key.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        if (reported.Count == 0)
        {
            report.Limit("Nessun risultato SPF/DKIM/DMARC registrato dal server: autenticazione non valutabile.", partial: false);
            return;
        }

        report.Limit("SPF, DKIM, DMARC e compauth sono dichiarati dai server nelle intestazioni: origine e firme non verificate dall’app.", partial: false);
        var auth = reported[0];
        if (reported.Skip(1).Any(other => new[] { "spf", "dkim", "dmarc", "compauth" }.Any(method =>
                AuthResult(other, method) != null && AuthResult(auth, method) != null && AuthResult(other, method) != AuthResult(auth, method))))
            report.Limit("Risultati di autenticazione contraddittori: nessuna intestazione è considerata attendibile per la sua posizione.", partial: false);
        foreach (var declaration in reported) CheckAuthenticationDeclaration(declaration, report);
    }

    private static void CheckAuthenticationDeclaration(string auth, EmailSecurityReport report)
    {
        string? spf = AuthResult(auth, "spf");
        string? dkim = AuthResult(auth, "dkim");
        string? dmarc = AuthResult(auth, "dmarc");
        string? compauth = AuthResult(auth, "compauth");
        string? reason = Regex.Match(auth, @"\breason=(\d{3})", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

        void Declared(string code, ObservationKind kind, string text) =>
            report.Observe(code, kind, ObservationOrigin.Declared, ObservationSubject.Server, text, "auth:" + code);

        switch (dmarc)
        {
            case "fail": Declared("dmarc.fail", ObservationKind.Attention, "Il server dichiara DMARC fallito: il dominio del mittente non conferma l’invio (può dipendere anche da un inoltro)."); break;
            case "pass": Declared("dmarc.pass", ObservationKind.Context, "Il server dichiara DMARC superato; non dice nulla sul contenuto."); break;
            case "none": Declared("dmarc.none", ObservationKind.Context, "Il server dichiara DMARC none: il dominio non pubblica una politica."); break;
        }
        if (compauth == "fail")
            Declared("compauth.fail", ObservationKind.Attention, "Il server Microsoft 365 dichiara fallita l’autenticazione composita" + (reason != null ? $" (codice {reason})" : "") + ".");
        else if (compauth == "softpass")
            Declared("compauth.softpass", ObservationKind.Context, "Il server Microsoft 365 dichiara un’autenticazione parziale (softpass).");
        if (spf is "fail" or "softfail")
            Declared("spf." + spf, ObservationKind.Context, $"Il server dichiara SPF {spf}: da solo non dimostra una falsificazione (gli inoltri lo producono).");
        if (dkim == "fail")
            Declared("dkim.fail", ObservationKind.Context, "Il server dichiara una firma DKIM non valida: da sola non dimostra una falsificazione.");
    }

    /// <summary>Read reported method results; only DKIM can have multiple independent signatures.</summary>
    private static string? AuthResult(string header, string method)
    {
        string? first = null;
        foreach (Match m in Regex.Matches(header, $@"(?:^|;)\s*{method}\s*=\s*([a-z]+)", RegexOptions.IgnoreCase))
        {
            var v = m.Groups[1].Value.ToLowerInvariant();
            if (method == "dkim" && v == "pass") return v;
            first ??= v;
        }
        return first;
    }

    private static void CheckMicrosoftVerdict(List<KeyValuePair<string, string>> headers, EmailSecurityReport report)
    {
        var forefront = FirstHeader(headers, "X-Forefront-Antispam-Report");
        if (forefront == null) return;

        var cat = Regex.Match(forefront, @"\bCAT:([A-Z]+)", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
        switch (cat)
        {
            case "PHSH" or "HPHSH" or "HPHISH" or "SPOOF" or "MALW" or "UIMP" or "DIMP" or "GIMP" or "BIMP" or "INTOS":
                report.Observe("ms.cat", ObservationKind.Attention, ObservationOrigin.Declared, ObservationSubject.Server,
                    $"Il filtro Microsoft del server ha classificato il messaggio come minaccia (CAT:{cat}).");
                break;
            case "SPM" or "HSPM":
                report.Observe("ms.spam", ObservationKind.Context, ObservationOrigin.Declared, ObservationSubject.Server,
                    $"Il filtro Microsoft del server ha classificato il messaggio come spam (CAT:{cat}).");
                break;
        }

        var sfty = Regex.Match(forefront, @"\bSFTY:([\d.]+)").Groups[1].Value;
        if (sfty is "9.19" or "9.20")
            report.Observe("ms.impersonation", ObservationKind.Attention, ObservationOrigin.Declared, ObservationSubject.Server,
                "Il filtro Microsoft del server ha segnalato un tentativo di impersonificazione.");
        else if (sfty == "9.25")
            report.Observe("ms.firstcontact", ObservationKind.Context, ObservationOrigin.Declared, ObservationSubject.Server,
                "Il filtro Microsoft del server segnala un primo contatto con questo mittente.");
    }

    // ---------- Sender ----------

    private static string ResolveFromAddress(EmailSecurityInput input, List<KeyValuePair<string, string>> headers)
    {
        if (input.FromAddress.Contains("@")) return input.FromAddress.Trim();
        var from = FirstHeader(headers, "From");
        return from != null ? ExtractAddresses(from).FirstOrDefault() ?? string.Empty : string.Empty;
    }

    private static void CheckSender(EmailSecurityInput input, string fromAddress, string fromDomain, EmailSecurityReport report)
    {
        if (fromDomain.Length == 0) return;
        bool fromOwn = input.OwnAddresses.Contains(fromAddress, StringComparer.OrdinalIgnoreCase);

        // An address written in the display name: "avvisi@legalmail.it" <posta-certificata@legalmail.it>.
        var shown = EmailInText.Match(input.FromName ?? string.Empty);
        if (shown.Success && !shown.Value.Equals(fromAddress, StringComparison.OrdinalIgnoreCase))
        {
            string shownAddress = shown.Value, shownDomain = DomainOf(shownAddress);
            string key = "sender.shown:" + shownAddress.ToLowerInvariant();
            // Your domain counts only when it is yours alone (not gmail.com or legalmail.it) and the sender is outside it.
            bool showsOwnAddress = !fromOwn && input.OwnAddresses.Contains(shownAddress, StringComparer.OrdinalIgnoreCase);
            bool showsOwnDomain = !fromOwn && !showsOwnAddress && !Archive.CounterpartyResolver.IsMailboxProvider(SecurityDomains.Registrable(shownDomain)) &&
                                  !SecurityDomains.SameDomain(shownDomain, fromDomain) && input.OwnDomains.Any(d => SecurityDomains.SameDomain(d, shownDomain));
            if ((showsOwnAddress || showsOwnDomain) && !report.PecEnvelope)
                report.Observe("from.shownown", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Sender,
                    $"Il nome visualizzato mostra {(showsOwnAddress ? "un tuo indirizzo" : "un indirizzo del tuo dominio")} ({shownAddress}), ma il mittente tecnico è {fromAddress}.", key);
            else if (report.PecEnvelope)
                report.Observe("from.pecshown", ObservationKind.Context, ObservationOrigin.Local, ObservationSubject.Sender,
                    $"Il nome visualizzato riporta il mittente del messaggio originale ({shownAddress}); la busta PEC è inviata da {fromAddress}.", key);
            else if (SecurityDomains.SameDomain(shownDomain, fromDomain))
                report.Observe("from.shownsame", ObservationKind.Context, ObservationOrigin.Local, ObservationSubject.Sender,
                    $"Il nome visualizzato mostra {shownAddress}; il mittente tecnico {fromAddress} è dello stesso dominio.", key);
            else
                report.Observe("from.shownother", ObservationKind.Attention, ObservationOrigin.Local, ObservationSubject.Sender,
                    $"Il nome visualizzato mostra {shownAddress}, ma il mittente tecnico è {fromAddress}, di un altro dominio.", key);
            // The address someone shows is checked too: a lookalike of your domain stays one inside a PEC envelope.
            if (shownDomain.Length > 0 && !SecurityDomains.SameDomain(shownDomain, fromDomain))
                CheckDomain(ObservationSubject.Sender, shownDomain, "Il dominio mostrato nel nome", input.OwnDomains, report);
        }

        if (!fromOwn) CheckSelfImpersonation(input, fromAddress, report);
        CheckDomain(ObservationSubject.Sender, fromDomain, "Il dominio del mittente", input.OwnDomains, report);
    }

    /// <summary>
    /// Sender named like the recipient ("mario.rossi" or "Mario Rossi") while writing from someone else's address.
    /// Names that simply mirror the sender's own address ("Info" &lt;info@...&gt;) are not flagged.
    /// </summary>
    private static void CheckSelfImpersonation(EmailSecurityInput input, string fromAddress, EmailSecurityReport report)
    {
        var name = (input.FromName ?? string.Empty).Trim().Trim('"', '\'').Trim();
        var key = NameKey(name);
        if (key.Length < 4 || key == NameKey(LocalPartOf(fromAddress))) return;

        foreach (var own in input.OwnAddresses)
        {
            var local = LocalPartOf(own);
            // Typed names have no dots or digits: the raw local part as a name is machine-made.
            if (name.Equals(local, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(local, @"[._\-\d]"))
            {
                report.Observe("from.selfaddress", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Sender,
                    $"Il mittente si presenta con il tuo indirizzo («{name}») ma scrive da {fromAddress}.", "sender.self");
                return;
            }
        }

        if (input.OwnAddresses.Select(LocalPartOf).Concat(input.OwnNames).Any(own => NameKey(own) == key))
            report.Observe("from.selfname", ObservationKind.Attention, ObservationOrigin.Local, ObservationSubject.Sender,
                $"Il mittente usa il tuo nome («{name}») ma scrive da un altro indirizzo ({fromAddress}).", "sender.self");
    }

    private static string LocalPartOf(string address)
    {
        int at = address.LastIndexOf('@');
        return at > 0 ? address.Substring(0, at).Trim() : string.Empty;
    }

    /// <summary>Lowercase letters and digits only, so "Mario Rossi" equals "mario.rossi".</summary>
    private static string NameKey(string text) =>
        Regex.Replace((text ?? string.Empty).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();

    private static void CheckReplyTo(List<KeyValuePair<string, string>> headers, string fromDomain, EmailSecurityReport report)
    {
        var replyTo = FirstHeader(headers, "Reply-To");
        if (replyTo == null || fromDomain.Length == 0) return;

        foreach (var address in ExtractAddresses(replyTo))
        {
            var domain = DomainOf(address);
            if (domain.Length > 0 && !SecurityDomains.SameDomain(domain, fromDomain))
            {
                // Lists, notifications and PEC envelopes answer elsewhere by design.
                report.Observe("replyto.mismatch", report.Automatic || report.PecEnvelope ? ObservationKind.Context : ObservationKind.Attention,
                    ObservationOrigin.Local, ObservationSubject.Sender,
                    $"Le risposte andrebbero a {address}, dominio diverso dal mittente ({fromDomain}).", "replyto:" + domain);
                return;
            }
        }
    }

    /// <summary>Punycode/IDN and lookalikes of the user's own domains.</summary>
    private static void CheckDomain(ObservationSubject subject, string domain, string label, IReadOnlyList<string> ownDomains, EmailSecurityReport report)
    {
        if (domain.Split('.').Any(l => l.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)) || domain.Any(c => c > 127))
            report.Observe("domain.idn", ObservationKind.Context, ObservationOrigin.Local, subject,
                $"{label} ({domain}) usa caratteri internazionali: da solo non è un indizio.", "idn:" + domain);

        var registrable = RegistrableDomain(domain);
        if (registrable.Length == 0)
        {
            report.Limit("Dominio non riconosciuto nella Public Suffix List: " + domain, partial: false);
            return;
        }
        foreach (var own in ownDomains)
        {
            var ownRegistrable = RegistrableDomain(own);
            if (ownRegistrable.Length == 0 || registrable.Equals(ownRegistrable, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsLookalike(registrable, ownRegistrable))
            {
                report.Observe("domain.lookalike", ObservationKind.Attention, ObservationOrigin.Local, subject,
                    $"{label} ({domain}) assomiglia al tuo dominio {ownRegistrable}.", "lookalike:" + registrable);
                return;
            }
        }
    }

    internal static bool IsLookalike(string candidate, string reference)
    {
        string a = SkeletonOf(candidate), b = SkeletonOf(reference);
        if (a == b) return true;

        // Compare the name part only (contoso.com vs contoso-it.com is not flagged, cont0so/contosso is).
        string an = a.Split('.')[0], bn = b.Split('.')[0];
        return bn.Length >= 5 && Levenshtein(an, bn) <= 1;
    }

    /// <summary>Lowercase, accents stripped, common visual confusables folded (rn→m, 0→o, 1→l...).</summary>
    private static string SkeletonOf(string domain)
    {
        try { domain = new IdnMapping().GetUnicode(domain); }
        catch (ArgumentException) { return domain.ToLowerInvariant(); }
        var decomposed = domain.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch
            {
                '0' => 'o',
                '1' => 'l',
                'i' => 'l',
                '3' => 'e',
                '5' => 's',
                'а' => 'a', // Cyrillic
                'е' => 'e',
                'о' => 'o',
                'р' => 'p',
                'с' => 'c',
                'х' => 'x',
                _ => c
            });
        }
        return sb.ToString().Replace("rn", "m").Replace("vv", "w");
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ---------- Links ----------

    private static void CheckLinks(EmailSecurityInput input, EmailSecurityReport report)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var link in SecurityLinks.Read(input.HtmlBody, input.Body))
        {
            if (!seen.Add(link.Destination.Trim() + "|" + link.Text.Trim())) continue;
            if (++count > MaxLinks)
            {
                report.Limit("Controllati solo i primi " + MaxLinks + " link distinti del messaggio.", partial: true);
                break;
            }
            var href = UnwrapSafeLink(link.Destination.Trim());
            report.Links.Add(new SecurityLink(link.Text.Trim(), href));
            CheckLink(link.Text.Trim(), href, "Email", input.OwnDomains, report);
        }
    }

    /// <summary>One link of the body or of an attachment: shown text against destination, schemes, hosts.</summary>
    private static void CheckLink(string text, string href, string source, IReadOnlyList<string> ownDomains, EmailSecurityReport report)
    {
        if (href.Length == 0 || href.StartsWith("#") || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return;

        var scheme = Regex.Match(href, @"^\s*(javascript|vbscript|data):", RegexOptions.IgnoreCase);
        if (scheme.Success)
        {
            report.Observe("link.script", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Link,
                $"Un link esegue codice o dati incorporati ({scheme.Groups[1].Value.ToLowerInvariant()}:) invece di aprire una pagina.",
                "link.script:" + source, source, Clip(href));
            return;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        string host = uri.IdnHost.TrimEnd('.');
        string displayHost = uri.Host;

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            report.Observe("link.ip", ObservationKind.Attention, ObservationOrigin.Local, ObservationSubject.Link,
                $"Un link porta a un indirizzo IP ({host}) invece che a un dominio.", "link.ip:" + host, source, Clip(href));
            return;
        }

        // https://www.banca.it@evil.tk/ goes to evil.tk: everything before '@' is a user name.
        if (uri.UserInfo.Length > 0)
            report.Observe("link.userinfo", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Link,
                $"Un link nasconde la destinazione dietro un nome utente: porta a {displayHost}.", "link.userinfo:" + host, source, Clip(href));

        string? shownHost = ShownHost(text);
        if (shownHost != null && !SecurityDomains.SameDomain(shownHost, displayHost) && !SecurityDomains.SameDomain(shownHost, host))
            report.Observe("link.mismatch", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Link,
                $"Un link mostra «{shownHost}» ma porta a {displayHost}.", "link.mismatch:" + shownHost + ">" + host, source, Clip(text));

        CheckDomain(ObservationSubject.Link, displayHost, "Il dominio di un link", ownDomains, report);
    }

    private static string Clip(string text) => text.Length > 200 ? text.Substring(0, 200) + "…" : text;

    private static readonly char[] LinkTextPunctuation = "()[]{}<>«»\"'“”‘’.,;:!?".ToCharArray();

    /// <summary>The domain a link text shows: the whole text as an address, or else a word in it written with
    /// http(s):// or www. ("Accedi a www.banca.it."); a bare "name.tld" word in a sentence is not enough.</summary>
    internal static string? ShownHost(string text)
    {
        static string? Host(string candidate, bool needsPrefix)
        {
            var shown = UrlLikeText.Match(candidate.Trim().Trim(LinkTextPunctuation));
            if (!shown.Success || !(shown.Groups[1].Success || (!needsPrefix && CommonTlds.Contains(shown.Groups[3].Value)))) return null;
            string host = shown.Groups[2].Value.TrimEnd('.');
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host.Substring(4) : host;
        }
        return Host(text, needsPrefix: false) ??
               text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => Host(word, needsPrefix: true)).FirstOrDefault(h => h != null);
    }

    /// <summary>Microsoft Defender Safe Links wrap the real URL in the "url" query parameter.</summary>
    internal static string UnwrapSafeLink(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) ||
            !(uri.Host.Equals("safelinks.protection.outlook.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".safelinks.protection.outlook.com", StringComparison.OrdinalIgnoreCase)))
        {
            return href;
        }

        var destination = HttpUtility.ParseQueryString(uri.Query)["url"];
        return string.IsNullOrWhiteSpace(destination) ? href : destination;
    }

    // ---------- Attachments ----------

    private static void CheckAttachments(IReadOnlyList<AttachmentFacts> attachments, IReadOnlyList<string> ownDomains, EmailSecurityReport report)
    {
        foreach (var attachment in attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.Name)) continue;
            var name = attachment.Name.Trim();
            string source = "allegato «" + name + "»";

            if (!attachment.IsInline || attachment.ScanStatus != AttachmentScanStatus.NotScanned)
                report.Defender.Add(new DefenderEntry(name, attachment.ScanStatus, attachment.ScanDetail));
            switch (attachment.ScanStatus)
            {
                case AttachmentScanStatus.Threat:
                    report.Limit($"«{name}» non letto: Defender ha segnalato una minaccia.", partial: true);
                    break;
                case AttachmentScanStatus.Removed:
                    report.Limit($"«{name}» non letto: la copia non è più disponibile (causa non confermata).", partial: true);
                    break;
                case AttachmentScanStatus.Failed:
                case AttachmentScanStatus.NotScanned when !attachment.IsInline:
                    report.Limit($"«{name}»: scansione Defender non completata" + (attachment.ScanDetail == null ? "." : " (" + attachment.ScanDetail + ")."), partial: true);
                    break;
            }
            if (attachment.InspectionDetail != null) report.Limit($"«{name}»: {attachment.InspectionDetail}", partial: true);

            if (name.IndexOf('‮') >= 0)
            {
                report.Observe("att.rtlo", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Attachment,
                    $"Il nome dell’allegato «{name}» usa un carattere che inverte il testo e maschera l’estensione.", "att.mask:" + name, source);
                continue;
            }

            var parts = name.Split('.').Select(p => p.Trim()).ToArray();
            string ext = parts.Length > 1 ? parts[parts.Length - 1] : string.Empty;
            string inner = parts.Length > 2 ? parts[parts.Length - 2] : string.Empty;
            bool masked = CheckSignature(attachment, name, source, report);

            if (ExecutableExtensions.Contains(ext))
            {
                bool disguised = inner.Length is > 1 and <= 4 && !ExecutableExtensions.Contains(inner);
                if (disguised)
                    report.Observe("att.double", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Attachment,
                        $"L’allegato «{name}» sembra un file .{inner}, ma è un .{ext} eseguibile.", "att.mask:" + name, source);
                else
                    report.Observe("att.exec", ObservationKind.Attention, ObservationOrigin.Local, ObservationSubject.Attachment,
                        $"L’allegato «{name}» è un programma o uno script (.{ext}): aprirlo lo esegue.", "att.exec:" + name, source);
            }
            else if (!masked && ArchiveExtensions.Contains(ext) && attachment.Inspection == null)
            {
                report.Observe("att.archive", ObservationKind.Context, ObservationOrigin.Local, ObservationSubject.Attachment,
                    $"«{name}» è un archivio compresso non letto dai controlli.", "att.archive:" + name, source);
            }

            if (attachment.HasMacros)
                report.Observe("att.macro", ObservationKind.Attention, ObservationOrigin.Local, ObservationSubject.Attachment,
                    $"«{name}» contiene macro VBA: si attivano solo se le abiliti; il loro codice non è stato letto.", "att.macro:" + name, source);
            else if (MacroExtensions.Contains(ext) && attachment.Inspection != null && attachment.Inspection.Error == null)
                report.Observe("att.macroformat", ObservationKind.Context, ObservationOrigin.Local, ObservationSubject.Attachment,
                    $"«{name}» è in un formato che ammette macro, ma non ne contiene.", "att.macroformat:" + name, source);
            else if (MacroExtensions.Contains(ext))
                report.Observe("att.macroformat", ObservationKind.Attention, ObservationOrigin.Local, ObservationSubject.Attachment,
                    $"«{name}» è in un formato che ammette macro e non è stato possibile controllarlo.", "att.macroformat:" + name, source);

            if (PageExtensions.Contains(ext) && attachment.Inspection is not { Inspected: > 0 })
                report.Observe("att.page", ObservationKind.Attention, ObservationOrigin.Local, ObservationSubject.Attachment,
                    $"«{name}» è una pagina web non letta dai controlli: può contenere script o moduli.", "att.page:" + name, source);

            if (attachment.Inspection != null) CheckInspection(attachment, name, ownDomains, report);
        }
    }

    /// <summary>Content type (magic bytes) must match what the extension promises.</summary>
    /// <returns>true when an executable hides behind another extension.</returns>
    private static bool CheckSignature(AttachmentFacts attachment, string name, string source, EmailSecurityReport report)
    {
        if (attachment.DetectedType == null) return false;
        var ext = attachment.Extension;
        var expected = FileSignature.ExpectedFor(ext);

        if (attachment.DetectedType == "exe" && expected != "exe" && !ExecutableExtensions.Contains(ext))
        {
            report.Observe("att.disguised", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Attachment,
                $"Il file «{name}» sembra un .{ext}, ma contiene un programma eseguibile.", "att.mask:" + name, source);
            return true;
        }
        if (expected != null && expected != attachment.DetectedType)
            report.Observe("att.mismatch", ObservationKind.Attention, ObservationOrigin.Local, ObservationSubject.Attachment,
                $"Il contenuto di «{name}» ({attachment.DetectedType.ToUpperInvariant()}) non corrisponde all’estensione .{ext}.", "att.mismatch:" + name, source);
        return false;
    }

    /// <summary>The static reading of one attachment: rules, links and limits, attributed to the file inside it.</summary>
    private static void CheckInspection(AttachmentFacts attachment, string name, IReadOnlyList<string> ownDomains, EmailSecurityReport report)
    {
        var inspection = attachment.Inspection!;
        string Where(string file) => "allegato «" + name + "»" + (file.Length > 0 ? " › " + file : "");

        foreach (var finding in inspection.Findings)
        {
            if (InspectionRules.Describe(finding.Rule) is not { } rule) continue;
            var subject = finding.Rule.StartsWith("script.", StringComparison.Ordinal) ? ObservationSubject.Script : ObservationSubject.Attachment;
            report.Observe(finding.Rule, rule.Kind, ObservationOrigin.Local, subject, rule.Text,
                finding.Rule + ":" + name + "/" + finding.File, Where(finding.File) + (finding.Line > 0 ? ", riga " + finding.Line : ""), finding.Snippet);
        }
        foreach (var entry in inspection.Entries)
            CheckEntryName(entry, name, Where(entry.File), report);
        foreach (var limit in inspection.Limits)
            report.Limit(Where(limit.File) + ": " + InspectionRules.LimitText(limit.Reason) + ".", partial: true);
        if (inspection.Truncated)
            report.Limit(Where(string.Empty) + ": letto in parte (limiti di dimensione, profondità o testo).", partial: true);
        if (inspection.Error != null)
            report.Limit(Where(string.Empty) + ": lettura statica non riuscita.", partial: true);
        else if (inspection.Problem is { } problem && !(problem == "unsupported" && (inspection.Inspected > 0 || attachment.DetectedType is "png" or "jpeg" or "gif")))
            report.Limit(Where(string.Empty) + ": " + ProblemText(problem) + ".", partial: problem != "unsupported");

        int count = 0;
        foreach (var link in inspection.Links)
        {
            if (++count > MaxLinks)
            {
                report.Limit(Where(string.Empty) + ": controllati solo i primi " + MaxLinks + " link.", partial: true);
                break;
            }
            var href = UnwrapSafeLink(link.Destination);
            report.Links.Add(new SecurityLink(link.Text, href));
            CheckLink(link.Text, href, Where(link.File), ownDomains, report);
        }
    }

    /// <summary>A file inside a container: the same name and type checks as a top-level attachment.</summary>
    private static void CheckEntryName(InspectionEntry entry, string attachment, string source, EmailSecurityReport report)
    {
        int cut = entry.File.LastIndexOf(" › ", StringComparison.Ordinal);
        string file = (cut >= 0 ? entry.File.Substring(cut + 3) : entry.File).Trim();
        if (file.Length == 0) return;
        string fact = attachment + "/" + entry.File;
        if (file.IndexOf('‮') >= 0)
        {
            report.Observe("att.rtlo", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Attachment,
                $"Il nome del file «{file}» usa un carattere che inverte il testo e maschera l’estensione.", "att.mask:" + fact, source);
            return;
        }
        var parts = file.Split('.').Select(p => p.Trim()).ToArray();
        string ext = parts.Length > 1 ? parts[parts.Length - 1] : string.Empty;
        string inner = parts.Length > 2 ? parts[parts.Length - 2] : string.Empty;
        if (ExecutableExtensions.Contains(ext))
        {
            if (inner.Length is > 1 and <= 4 && !ExecutableExtensions.Contains(inner))
                report.Observe("att.double", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Attachment,
                    $"Il file «{file}» sembra un file .{inner}, ma è un .{ext} eseguibile.", "att.mask:" + fact, source);
            else
                report.Observe("att.exec", ObservationKind.Attention, ObservationOrigin.Local, ObservationSubject.Attachment,
                    $"Il file «{file}» è un programma o uno script (.{ext}): aprirlo lo esegue.", "att.exec:" + fact, source);
        }
        else if (entry.DetectedType == "exe")
        {
            report.Observe("att.disguised", ObservationKind.Deception, ObservationOrigin.Local, ObservationSubject.Attachment,
                $"Il file «{file}» sembra un .{ext}, ma contiene un programma eseguibile.", "att.mask:" + fact, source);
        }
    }

    private static string ProblemText(string problem) => problem switch
    {
        "protected" => "protetto da password, contenuto non letto",
        "unsupported" => "formato non letto dai controlli",
        "depth-limit" or "container-limit" => "contenitori annidati oltre il limite, non letti",
        "expanded-size-limit" or "size-limit" => "contenuto oltre il limite di dimensione",
        "text-limit" => "contenuto oltre il limite di lettura",
        "scanned" or "ocr-off" or "ocr-unavailable" => "immagini o scansioni senza testo letto",
        _ => "contenuto letto in parte"
    };

    // ---------- Address helpers ----------

    internal static IEnumerable<string> ExtractAddresses(string headerValue)
    {
        if (InternetAddressList.TryParse(headerValue, out var addresses))
            foreach (var address in addresses.Mailboxes) yield return address.Address;
    }

    internal static string DomainOf(string address)
    {
        int at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address.Substring(at + 1).Trim().TrimEnd('>', '.').ToLowerInvariant() : string.Empty;
    }

    internal static string RegistrableDomain(string host) => SecurityDomains.Registrable(host);
}
