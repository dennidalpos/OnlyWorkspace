using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>
/// The person who authorized, for the company, data leaving Outlook towards external services, and the company's
/// statements the notice asks for (GDPR art. 6 and 35, AI Act art. 4, L. 132/2025 art. 11).
/// </summary>
public class DataEgressSigner
{
    public string Company { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    /// <summary>Date written by the responsible person, yyyy-MM-dd.</summary>
    public string Date { get; set; } = string.Empty;
    /// <summary>Data protection officer or privacy contact; optional.</summary>
    public string PrivacyContact { get; set; } = string.Empty;
    /// <summary>One of DataEgress.LegalBases.</summary>
    public string LegalBasis { get; set; } = string.Empty;
    /// <summary>Reference of the impact assessment, or why it is not needed.</summary>
    public string Dpia { get; set; } = string.Empty;
    /// <summary>The workers were informed of the use of AI (L. 132/2025 art. 11).</summary>
    public bool WorkersInformed { get; set; }
    /// <summary>Measures for the AI literacy of whoever uses the add-in were taken (AI Act art. 4).</summary>
    public bool AiLiteracy { get; set; }

    public string Describe() => string.IsNullOrEmpty(FirstName) && string.IsNullOrEmpty(Company) ? "Autorizzazione tecnica" : $"{FirstName} {LastName}, {Role}, {Company} <{Email}>";
}

/// <summary>What the company declares about one server that receives data: contract, location, use and level.</summary>
public sealed class DataEgressProvider
{
    public string Host { get; set; } = string.Empty;
    /// <summary>The company that runs the server (the processor).</summary>
    public string Provider { get; set; } = string.Empty;
    public ExposureLevel Level { get; set; } = ExposureLevel.Minimal;
    /// <summary>A data processing agreement (GDPR art. 28) is signed with the provider.</summary>
    public bool ProcessingAgreement { get; set; }
    /// <summary>Where the data is processed, as the contract states.</summary>
    public string Region { get; set; } = string.Empty;
    /// <summary>One of DataEgress.TransferBases.</summary>
    public string TransferBasis { get; set; } = string.Empty;
    /// <summary>The contract excludes the use of the data for training.</summary>
    public bool NoTraining { get; set; }
    /// <summary>Retention the contract states.</summary>
    public string Retention { get; set; } = string.Empty;
}

/// <summary>The current authorization: who, when, on which text and for which servers.</summary>
public sealed class DataEgressConsent : DataEgressSigner
{
    public DateTime AcceptedUtc { get; set; }
    public string WindowsUser { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public int NoticeVersion { get; set; }
    public string NoticeHash { get; set; } = string.Empty;
    /// <summary>The accepted text itself, so an export reproduces what was signed after the notice changes.</summary>
    public string NoticeText { get; set; } = string.Empty;
    /// <summary>Servers the authorization covers; a server added later needs a new acceptance.</summary>
    public List<string> Hosts { get; set; } = new();
    /// <summary>One entry per host: what the company declared and the highest level authorized.</summary>
    public List<DataEgressProvider> Providers { get; set; } = new();
    public List<DataEgressProfileGrant> Profiles { get; set; } = new();
}

/// <summary>One acceptance or revocation, kept in config.json so it travels with every backup.</summary>
public sealed class DataEgressEvent
{
    public const string Accepted = "accettata";
    public const string Revoked = "revocata";

    public string Action { get; set; } = string.Empty;
    public DateTime AtUtc { get; set; }
    public string WindowsUser { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public string Responsible { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public int NoticeVersion { get; set; }
    public string NoticeHash { get; set; } = string.Empty;
    public List<string> Hosts { get; set; } = new();
    /// <summary>"host: level" for each covered server.</summary>
    public List<string> Levels { get; set; } = new();
    public List<string> Profiles { get; set; } = new();
    /// <summary>Profile IDs explicitly confirmed despite their unspecified service region.</summary>
    public List<string>? ConfirmedUnspecifiedRegionProfiles { get; set; }
    /// <summary>Preserves the accepted declarations/text, including host-only authorizations superseded by renewal.</summary>
    public DataEgressConsent? Authorization { get; set; }
}

public sealed class WorkspaceDataEgressConfig
{
    public DataEgressConsent? Consent { get; set; }
    public List<DataEgressEvent> History { get; set; } = new();
}

/// <summary>Text the responsible person accepts. A new version voids every earlier acceptance.</summary>
public static class DataEgressNotice
{
    public const int Version = 6;

    public const string Title = "Informativa sull'uscita dei dati verso servizi di intelligenza artificiale esterni";

    public static readonly string Text = string.Join("\r\n\r\n", new[]
    {
        "OnlyWorkspace conserva posta e storico cifrati sul PC. Un profilo locale elabora i contenuti sul PC.",
        "Prima di ogni invio il testo passa da una pseudonimizzazione meccanica, anche per i modelli locali: nomi, indirizzi, recapiti, IBAN e codici riconosciuti sono sostituiti con segnaposto; importi e date sempre verso il cloud, in locale solo se il profilo lo prevede. Con il livello Minimo escono solo fatti strutturati estratti in locale. Con Standard ed Esteso escono anche email e testo libero, già mascherati; gli allegati solo con Esteso e con il permesso del profilo. Se la pseudonimizzazione non riesce, nulla viene inviato.",
        "I valori originali vengono ripristinati solo sul PC. La pseudonimizzazione è reversibile e non rende anonimi i dati. Identificativi, importi e date sono sostituiti da riferimenti, ma ciò che la procedura non riconosce, comprese le categorie particolari di dati (art. 9 GDPR, per esempio la salute), resta nel testo e viene inviato al servizio. Nessun file binario viene caricato. La ricerca web con contenuti non verificabili è bloccata, anche quando la query nasce da un modello locale.",
        "Per ogni profilo cloud l'amministratore dichiara dove il servizio elabora i dati (UE, non UE o non specificata); un'area non specificata richiede una conferma esplicita.",
        "Le prove di connessione e capacità usano richieste sintetiche fisse, senza contenuti di Outlook. Le credenziali necessarie all'autenticazione sono trasmesse al servizio configurato.",
        "Autorizzi esclusivamente i profili e le destinazioni riepilogati, con i permessi e le chiavi attuali. L'autorizzazione si può revocare; le richieste già ricevute dal servizio non si possono ritirare.",
        "L'autorizzazione tecnica non certifica adempimenti, contratti o conformità normativa. L'organizzazione verifica le condizioni del fornitore e gli obblighi applicabili al proprio utilizzo. Rileggi sempre i risultati dell'IA."
    });

    public static string Hash { get; } = HashOf(Text);

    public static string HashOf(string text)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }
}

/// <summary>
/// Which servers receive data from Outlook and whether the accepted notice covers them. The single rule used by the
/// pane, the archive cards and the ConfigTool; the ConfigTool's own probes send no Outlook data and are not gated.
/// </summary>
public static partial class DataEgress
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant);

    /// <summary>GDPR art. 6(1) bases the company can declare.</summary>
    public static IReadOnlyList<string> LegalBases { get; } = new[]
    {
        "Esecuzione di un contratto (art. 6.1.b)", "Obbligo legale (art. 6.1.c)", "Legittimo interesse (art. 6.1.f)", "Consenso (art. 6.1.a)"
    };

    /// <summary>GDPR chapter V: where the data goes and on which basis.</summary>
    public static IReadOnlyList<string> TransferBases { get; } = new[]
    {
        "Server nello Spazio economico europeo", "Decisione di adeguatezza (art. 45)", "EU-US Data Privacy Framework (art. 45)",
        "Clausole contrattuali tipo (art. 46)"
    };

    /// <summary>Server that receives the profile's model requests when it is a cloud profile.</summary>
    public static string? ModelHost(LlmProfileConfig profile) =>
        profile.IsExternal && Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var url) ? url.Host.ToLowerInvariant() : null;

    /// <summary>Servers outside the PC reached by the profile's web search.</summary>
    public static IReadOnlyList<string> WebHosts(LlmProfileConfig profile)
    {
        var hosts = new List<string>();
        if (!WebSearchAction.All.Any(a => WebSearchAction.Uses(profile, a.Key))) return hosts;
        var mode = ProviderCatalog.Get(profile.Type).WebSearch;
        if (mode == WebSearchMode.OllamaCom) hosts.Add("ollama.com");
        else if (mode != WebSearchMode.None && ModelHost(profile) is { } host) hosts.Add(host);
        return hosts;
    }

    /// <summary>Exposure a profile asks of its servers; a local profile's web search sends a query only.</summary>
    private static ExposureLevel LevelOf(LlmProfileConfig profile) => profile.IsExternal ? profile.Exposure : ExposureLevel.Minimal;

    private static IEnumerable<(string Host, ExposureLevel Level)> Destinations(WorkspaceConfig config) =>
        (config.Profiles ?? new List<LlmProfileConfig>())
            .Where(p => !p.IsExternal || p.Exposure != ExposureLevel.LocalOnly)
            .SelectMany(p => new[] { ModelHost(p) }.Concat(WebHosts(p)).Where(h => !string.IsNullOrEmpty(h)).Select(h => (h!, LevelOf(p))));

    /// <summary>Every server that would receive Outlook data with this configuration, sorted.</summary>
    public static IReadOnlyList<string> Hosts(WorkspaceConfig config) =>
        Destinations(config).Select(d => d.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Highest level any profile asks of the server.</summary>
    public static ExposureLevel RequiredLevel(WorkspaceConfig config, string host) =>
        Destinations(config).Where(d => string.Equals(d.Host, host, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Level).DefaultIfEmpty(ExposureLevel.Minimal).Max();

    /// <summary>One entry per server of the configuration at its required level, prefilled from the current acceptance.</summary>
    public static List<DataEgressProvider> ProvidersFor(WorkspaceConfig config)
    {
        var previous = config.DataEgress?.Consent?.Providers ?? new List<DataEgressProvider>();
        return Hosts(config).Select(host =>
        {
            var old = previous.FirstOrDefault(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase));
            return new DataEgressProvider
            {
                Host = host, Level = RequiredLevel(config, host), Provider = old?.Provider ?? string.Empty,
                ProcessingAgreement = old?.ProcessingAgreement ?? false, Region = old?.Region ?? string.Empty,
                TransferBasis = old?.TransferBasis ?? string.Empty, NoTraining = old?.NoTraining ?? false, Retention = old?.Retention ?? string.Empty
            };
        }).ToList();
    }

    /// <summary>The acceptance is on the current text of the notice.</summary>
    public static bool Current(DataEgressConsent? consent) =>
        consent != null && consent.NoticeVersion == DataEgressNotice.Version && consent.NoticeHash == DataEgressNotice.Hash;

    /// <summary>The current acceptance covers the server up to the level.</summary>
    public static bool Covers(WorkspaceConfig? config, string host, ExposureLevel level)
    {
        var consent = config?.DataEgress?.Consent;
        return Current(consent) && consent!.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase) &&
            consent.Providers.Any(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase) && p.Level >= level);
    }

    /// <summary>Servers of this configuration the acceptance does not cover at their level (all of them without one).</summary>
    public static IReadOnlyList<string> Missing(WorkspaceConfig config, Func<string, string?>? resolveSecret = null) =>
        EgressProfiles(config).Where(p => !HasProfileGrant(config, p, resolveSecret))
            .SelectMany(p => ProfileHosts(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A local profile always; a cloud profile only above Solo locale and when its server is covered at its level.</summary>
    public static bool AllowsModel(WorkspaceConfig? config, LlmProfileConfig profile, Func<string, string?>? resolveSecret = null) =>
        !profile.IsExternal || (ModelHost(profile) != null && ExposurePolicy.Of(profile).ModelAllowed && HasProfileGrant(config, profile, resolveSecret));

    public static bool AllowsWeb(WorkspaceConfig? config, LlmProfileConfig profile, Func<string, string?>? resolveSecret = null) =>
        WebHosts(profile).Count == 0 || HasProfileGrant(config, profile, resolveSecret);

    /// <summary>What the pane says when a cloud profile is blocked.</summary>
    public const string BlockedMessage =
        "Invio a servizi esterni non autorizzato: l'amministratore deve leggere e accettare l'informativa sull'uscita dei dati nel ConfigTool " +
        "(Privacy e dati) per questo profilo, destinazione, livello e chiavi. Si può salvare la configurazione senza autorizzare l'invio.";

    public const string LocalOnlyMessage =
        "Il profilo cloud è impostato su «Solo locale» e non invia dati: usare un profilo locale o alzare il livello nel ConfigTool.";

    public static string BlockedMessageFor(LlmProfileConfig profile) =>
        profile.IsExternal && !ExposurePolicy.Of(profile).ModelAllowed ? LocalOnlyMessage : BlockedMessage;

    /// <summary>
    /// An advisory only: saving a blocked default profile is allowed. Dispatch remains gated.
    /// </summary>
    public static string? SelectionProblem(WorkspaceConfig config, Func<string, string?>? resolveSecret = null)
    {
        var profiles = config.Profiles ?? new List<LlmProfileConfig>();
        var selected = profiles.FirstOrDefault(p => string.Equals(p.Id, config.DefaultProfile, StringComparison.OrdinalIgnoreCase));
        if (selected != null && resolveSecret == null &&
            (!string.IsNullOrEmpty(selected.SecretRef) || !string.IsNullOrEmpty(selected.WebSearchSecretRef)) && ProfileHosts(selected).Count > 0)
            return $"Il profilo predefinito «{selected.DisplayName}»: autorizzazione e chiavi da verificare con il vault prima dell'invio.";
        if (selected != null && (!AllowsModel(config, selected, resolveSecret) || !AllowsWeb(config, selected, resolveSecret)))
            return $"Il profilo predefinito «{selected.DisplayName}» è salvabile, ma gli invii esterni restano bloccati: " +
                "autorizzare il profilo in Privacy e dati.";
        return null;
    }

    /// <summary>Why a stored acceptance is not complete, or null; an acceptance of an older notice is not checked (it no longer counts).</summary>
    public static string? Problem(DataEgressConsent consent, DateTime today) =>
        !Current(consent) ? null : string.IsNullOrWhiteSpace(consent!.WindowsUser) || string.IsNullOrWhiteSpace(consent.Machine) ? "Autorizzazione priva di utente o postazione." :
            consent.Hosts.Where(h => !consent.Providers.Any(p => string.Equals(p.Host, h, StringComparison.OrdinalIgnoreCase)))
                .Select(h => $"{h}: mancano le dichiarazioni sul server.").FirstOrDefault();

    public static bool RequiresRegionConfirmation(LlmProfileConfig profile) =>
        profile.IsExternal && profile.ServiceRegion is not (ServiceRegion.Eu or ServiceRegion.NonEu);

    /// <summary>Technical authorization only; never creates company or provider declarations.</summary>
    public static DataEgressConsent Accept(WorkspaceConfig config, string windowsUser, string machine,
        DateTime nowUtc, Func<string, string?> resolveSecret, IReadOnlyCollection<string>? confirmedUnspecifiedRegionProfiles = null)
    {
        if (string.IsNullOrWhiteSpace(windowsUser) || string.IsNullOrWhiteSpace(machine))
            throw new ArgumentException("Utente e postazione sono necessari.");
        var profiles = EgressProfiles(config).ToList();
        if (profiles.Count == 0) throw new InvalidOperationException("Nessun profilo esterno da autorizzare.");
        var unspecified = profiles.Where(RequiresRegionConfirmation).Select(p => p.Id).ToList();
        var confirmed = new HashSet<string>(confirmedUnspecifiedRegionProfiles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (unspecified.Any(id => !confirmed.Contains(id)))
            throw new InvalidOperationException("Confermare esplicitamente tutti i profili con area del servizio non specificata.");
        var consent = new DataEgressConsent
        {
            AcceptedUtc = nowUtc, WindowsUser = windowsUser, Machine = machine,
            NoticeVersion = DataEgressNotice.Version, NoticeHash = DataEgressNotice.Hash, NoticeText = DataEgressNotice.Text,
            Date = nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Hosts = Hosts(config).ToList(),
            Providers = Hosts(config).Select(h => new DataEgressProvider { Host = h, Level = RequiredLevel(config, h) }).ToList(),
            Profiles = profiles.Select(p => CreateGrant(p, resolveSecret)).ToList()
        };
        config.DataEgress ??= new WorkspaceDataEgressConfig();
        ScrubHistory(config.DataEgress);
        var previous = config.DataEgress.Consent;
        if (previous != null && !config.DataEgress.History.Any(e => e.Authorization?.AcceptedUtc == previous.AcceptedUtc && e.Authorization.NoticeVersion == previous.NoticeVersion && e.Authorization.NoticeHash == previous.NoticeHash && e.Authorization.NoticeText == previous.NoticeText))
            config.DataEgress.History.Add(Event(DataEgressEvent.Accepted, previous, previous.WindowsUser, previous.Machine, previous.AcceptedUtc));
        config.DataEgress.Consent = consent;
        var accepted = Event(DataEgressEvent.Accepted, consent, windowsUser, machine, nowUtc);
        if (unspecified.Count > 0) accepted.ConfirmedUnspecifiedRegionProfiles = unspecified;
        config.DataEgress.History.Add(accepted);
        ScrubHistory(config.DataEgress);
        BindProfiles(config, resolveSecret);
        return consent;
    }

    public static void Revoke(WorkspaceConfig config, string windowsUser, string machine, DateTime nowUtc)
    {
        var consent = config.DataEgress?.Consent;
        if (consent == null) return;
        ScrubHistory(config.DataEgress!);
        config.DataEgress!.Consent = null;
        config.DataEgress.History.Add(Event(DataEgressEvent.Revoked, consent, windowsUser, machine, nowUtc));
        ScrubHistory(config.DataEgress);
        BindProfiles(config, null);
    }

    private static DataEgressEvent Event(string action, DataEgressConsent consent, string windowsUser, string machine, DateTime nowUtc) => new()
    {
        Action = action, AtUtc = nowUtc, WindowsUser = windowsUser, Machine = machine, Responsible = consent.Describe(),
        Date = consent.Date, NoticeVersion = consent.NoticeVersion, NoticeHash = consent.NoticeHash, Hosts = consent.Hosts.ToList(),
        Levels = consent.Providers.Select(p => $"{p.Host}: {ExposurePolicy.Label(p.Level)}").ToList(),
        Profiles = consent.Profiles.Select(p => p.ProfileId).ToList(),
        Authorization = WithoutCredentialProofs(JsonSerializer.Deserialize<DataEgressConsent>(JsonSerializer.Serialize(consent))!)
    };

    /// <summary>A past authorization is never verified again, so its history copy keeps what was accepted (text, declarations,
    /// profile and policy key) but not the salts and fingerprints that tie a grant to the credentials: they would sit in
    /// config.json for good and help a guess at a weak key.</summary>
    internal static DataEgressConsent WithoutCredentialProofs(DataEgressConsent consent)
    {
        foreach (var grant in consent.Profiles ?? new List<DataEgressProfileGrant>())
        {
            grant.CredentialSalt = string.Empty;
            grant.CredentialFingerprint = string.Empty;
        }
        return consent;
    }

    /// <summary>Copies written before this rule; the current consent is the only place that keeps the proofs. Called on every
    /// Accept, Revoke and save, so a config.json rewritten for any reason drops them (backups already written keep theirs).</summary>
    public static void ScrubHistory(WorkspaceConfig config)
    {
        if (config.DataEgress != null) ScrubHistory(config.DataEgress);
    }

    private static void ScrubHistory(WorkspaceDataEgressConfig egress)
    {
        // A hand-edited config may hold a null list or entry: cleanup must never stop Accept or Revoke before BindProfiles.
        egress.History ??= new List<DataEgressEvent>();
        egress.History.RemoveAll(entry => entry == null);
        foreach (var entry in egress.History)
            if (entry.Authorization != null) WithoutCredentialProofs(entry.Authorization);
    }
}

/// <summary>A request that would send Outlook data to a server the accepted notice does not cover.</summary>
public sealed class DataEgressBlockedException : InvalidOperationException
{
    public DataEgressBlockedException(string message) : base(message) { }
}
