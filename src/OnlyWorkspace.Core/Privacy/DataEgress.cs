using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

    public string Describe() => $"{FirstName} {LastName}, {Role}, {Company} <{Email}>";
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
}

public sealed class WorkspaceDataEgressConfig
{
    public DataEgressConsent? Consent { get; set; }
    public List<DataEgressEvent> History { get; set; } = new();
}

/// <summary>Text the responsible person accepts. A new version voids every earlier acceptance.</summary>
public static class DataEgressNotice
{
    public const int Version = 3;

    public const string Title = "Informativa sull'uscita dei dati verso servizi di intelligenza artificiale esterni";

    public static readonly string Text = string.Join("\r\n", new[]
    {
        "OnlyWorkspace legge posta, calendario e contatti dagli archivi aperti in Outlook su questo PC e li conserva cifrati in un archivio locale. " +
        "Con i profili IA locali nessun dato lascia il PC. Con un profilo IA «cloud», o con la ricerca web di Ollama su ollama.com, " +
        "una parte dei dati esce dal PC e raggiunge i server del fornitore elencati sotto, nei limiti del livello di esposizione scelto per ciascun profilo.",
        "",
        "Livelli di esposizione",
        "• 0 Solo locale: il profilo cloud non invia nulla.",
        "• 1 Minimo: escono solo fatti estratti in locale; nomi, indirizzi email, telefoni, IBAN, partite IVA e codici fiscali sono sostituiti da segnaposto e ripristinati solo sul PC. Niente testo integrale, allegati o ricerca web.",
        "• 2 Standard: come il livello 1, piu il testo originale dell'email ripulito dalle citazioni; ricerca web solo per la verifica di sicurezza, con i soli nomi di dominio. Nessun testo degli allegati viene inviato.",
        "Le schede IA in background e il livello 3 Completo sono rimossi. I profili esterni configurati al precedente livello 3 passano a Standard; la nuova informativa richiede una nuova accettazione.",
        "I segnaposto riducono i dati personali inviati ma non li rendono anonimi: restano dati personali ai sensi del GDPR.",
        "",
        "Cosa non esce",
        "• Le chiavi API restano cifrate su questo PC; archivio locale, storico e log non vengono inviati.",
        "• Le prove del ConfigTool (Test connessione, Rileva modelli, Verifica Web Search) inviano solo la chiave e richieste di prova, senza dati di Outlook.",
        "",
        "Cosa comporta",
        "• L'azienda resta titolare del trattamento (GDPR art. 24): indica la base giuridica (art. 6), nomina il fornitore responsabile del trattamento " +
        "(art. 28), valuta l'impatto (art. 35), informa gli interessati, cioè mittenti e destinatari delle email (art. 13-14), e verifica la base " +
        "degli eventuali trasferimenti fuori dallo Spazio economico europeo (capo V).",
        "• Il fornitore tratta i dati secondo il proprio contratto (conservazione, uso per l'addestramento, paese dei server): le dichiarazioni " +
        "richieste per ogni server vanno verificate sul contratto prima di accettare.",
        "• I lavoratori che usano OnlyWorkspace vanno informati dell'uso dell'intelligenza artificiale (L. 132/2025 art. 11) e messi in condizione " +
        "di usarla consapevolmente (Regolamento UE 2024/1689, art. 4). I testi proposti dall'IA vanno sempre riletti: OnlyWorkspace non invia email da solo.",
        "• Una richiesta già ricevuta dal fornitore non si può ritirare.",
        "",
        "Accettando, il responsabile indicato dichiara di aver letto questa informativa e che le dichiarazioni rese sono veritiere, e autorizza, " +
        "per conto dell'azienda, l'uscita dei dati descritta verso i server elencati fino al livello indicato per ciascuno. " +
        "Senza questa autorizzazione i profili cloud e la ricerca web esterna restano bloccati per tutti gli utenti di questo PC. " +
        "Ogni nuovo server o livello più alto richiede una nuova accettazione; l'autorizzazione si può revocare in qualsiasi momento dal ConfigTool."
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
public static class DataEgress
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
        if (!profile.WebSearchEnabled || !ExposurePolicy.Of(profile).AnyWeb) return hosts;
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
    public static IReadOnlyList<string> Missing(WorkspaceConfig config) =>
        Hosts(config).Where(h => !Covers(config, h, RequiredLevel(config, h))).ToList();

    /// <summary>A local profile always; a cloud profile only above Solo locale and when its server is covered at its level.</summary>
    public static bool AllowsModel(WorkspaceConfig? config, LlmProfileConfig profile) =>
        ModelHost(profile) is not { } host || (ExposurePolicy.Of(profile).ModelAllowed && Covers(config, host, profile.Exposure));

    public static bool AllowsWeb(WorkspaceConfig? config, LlmProfileConfig profile) =>
        WebHosts(profile).All(h => Covers(config, h, LevelOf(profile)));

    /// <summary>What the pane says when a cloud profile is blocked.</summary>
    public const string BlockedMessage =
        "Invio a servizi esterni non autorizzato: l'amministratore deve compilare e accettare l'informativa sull'uscita dei dati nel ConfigTool " +
        "(Privacy e dati) per il server e il livello di esposizione del profilo. Nel frattempo si possono usare i profili locali.";

    public const string LocalOnlyMessage =
        "Il profilo cloud è impostato su «Solo locale» e non invia dati: usare un profilo locale o alzare il livello nel ConfigTool.";

    public static string BlockedMessageFor(LlmProfileConfig profile) =>
        profile.IsExternal && !ExposurePolicy.Of(profile).ModelAllowed ? LocalOnlyMessage : BlockedMessage;

    /// <summary>
    /// Why the configuration cannot be saved as it is: the default profile would send data the notice
    /// does not cover. Other cloud profiles can be saved; they stay unusable until covered.
    /// </summary>
    public static string? SelectionProblem(WorkspaceConfig config)
    {
        var profiles = config.Profiles ?? new List<LlmProfileConfig>();
        var selected = profiles.FirstOrDefault(p => string.Equals(p.Id, config.DefaultProfile, StringComparison.OrdinalIgnoreCase));
        if (selected != null && selected.IsExternal && !AllowsModel(config, selected))
            return $"Il profilo predefinito «{selected.DisplayName}» è cloud e l'informativa non copre il suo server al livello scelto: " +
                "compilare l'informativa (Privacy e dati) o scegliere un profilo locale.";
        return null;
    }

    /// <summary>Why the signer's data cannot be accepted, or null when complete.</summary>
    public static string? Problem(DataEgressSigner signer, DateTime today)
    {
        if (string.IsNullOrWhiteSpace(signer.Company)) return "Indicare l'azienda.";
        if (string.IsNullOrWhiteSpace(signer.FirstName)) return "Indicare il nome del responsabile.";
        if (string.IsNullOrWhiteSpace(signer.LastName)) return "Indicare il cognome del responsabile.";
        if (string.IsNullOrWhiteSpace(signer.Role)) return "Indicare il ruolo del responsabile.";
        if (!EmailPattern.IsMatch(signer.Email.Trim())) return "Indicare un indirizzo email valido del responsabile.";
        if (!DateTime.TryParseExact(signer.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return "Indicare la data.";
        if (date.Date > today.Date) return "La data non può essere futura.";
        if (!LegalBases.Contains(signer.LegalBasis)) return "Indicare la base giuridica del trattamento.";
        if (string.IsNullOrWhiteSpace(signer.Dpia)) return "Indicare il riferimento della valutazione d'impatto, o perché non è necessaria.";
        if (!signer.WorkersInformed) return "Confermare che i lavoratori sono stati informati dell'uso dell'IA (L. 132/2025 art. 11).";
        if (!signer.AiLiteracy) return "Confermare le misure di alfabetizzazione sull'IA per chi usa OnlyWorkspace (AI Act art. 4).";
        return null;
    }

    /// <summary>Why a server's declarations cannot be accepted, or null when complete.</summary>
    public static string? Problem(DataEgressProvider provider)
    {
        string host = provider.Host;
        if (string.IsNullOrWhiteSpace(provider.Provider)) return $"{host}: indicare il fornitore.";
        if (provider.Level == ExposureLevel.LocalOnly) return $"{host}: il livello «Solo locale» non autorizza alcun invio.";
        if (!provider.ProcessingAgreement) return $"{host}: senza nomina a responsabile del trattamento (GDPR art. 28) il server non può ricevere dati.";
        if (string.IsNullOrWhiteSpace(provider.Region)) return $"{host}: indicare dove sono trattati i dati.";
        if (!TransferBases.Contains(provider.TransferBasis)) return $"{host}: indicare la base del trasferimento.";
        if (!provider.NoTraining) return $"{host}: il contratto deve escludere l'uso dei dati per l'addestramento.";
        if (string.IsNullOrWhiteSpace(provider.Retention)) return $"{host}: indicare la conservazione prevista dal contratto.";
        return null;
    }

    /// <summary>Why a stored acceptance is not complete, or null; an acceptance of an older notice is not checked (it no longer counts).</summary>
    public static string? Problem(DataEgressConsent consent, DateTime today) =>
        !Current(consent) ? null : Problem((DataEgressSigner)consent, today) ?? consent.Providers.Select(Problem).FirstOrDefault(p => p != null) ??
            consent.Hosts.Where(h => !consent.Providers.Any(p => string.Equals(p.Host, h, StringComparison.OrdinalIgnoreCase)))
                .Select(h => $"{h}: mancano le dichiarazioni sul server.").FirstOrDefault();

    /// <summary>
    /// Records an acceptance covering the servers of the configuration at the declared levels, and its event.
    /// Every server needs complete declarations at least at the level its profiles ask for.
    /// </summary>
    public static DataEgressConsent Accept(WorkspaceConfig config, DataEgressSigner signer, IReadOnlyList<DataEgressProvider> providers,
        string windowsUser, string machine, DateTime nowUtc)
    {
        if (Problem(signer, nowUtc.ToLocalTime()) is { } problem) throw new ArgumentException(problem, nameof(signer));
        var hosts = Hosts(config).ToList();
        var declared = new List<DataEgressProvider>();
        foreach (string host in hosts)
        {
            var provider = providers.FirstOrDefault(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{host}: mancano le dichiarazioni sul server.", nameof(providers));
            if (Problem(provider) is { } providerProblem) throw new ArgumentException(providerProblem, nameof(providers));
            var required = RequiredLevel(config, host);
            if (provider.Level < required)
                throw new ArgumentException($"{host}: un profilo chiede il livello «{ExposurePolicy.Label(required)}».", nameof(providers));
            declared.Add(new DataEgressProvider
            {
                Host = host, Provider = provider.Provider.Trim(), Level = provider.Level, ProcessingAgreement = true,
                Region = provider.Region.Trim(), TransferBasis = provider.TransferBasis, NoTraining = true, Retention = provider.Retention.Trim()
            });
        }
        var consent = new DataEgressConsent
        {
            Company = signer.Company.Trim(), FirstName = signer.FirstName.Trim(), LastName = signer.LastName.Trim(), Role = signer.Role.Trim(),
            Email = signer.Email.Trim(), Date = signer.Date, PrivacyContact = signer.PrivacyContact.Trim(), LegalBasis = signer.LegalBasis,
            Dpia = signer.Dpia.Trim(), WorkersInformed = true, AiLiteracy = true,
            AcceptedUtc = nowUtc, WindowsUser = windowsUser, Machine = machine,
            NoticeVersion = DataEgressNotice.Version, NoticeHash = DataEgressNotice.Hash, NoticeText = DataEgressNotice.Text,
            Hosts = hosts, Providers = declared
        };
        config.DataEgress ??= new WorkspaceDataEgressConfig();
        config.DataEgress.Consent = consent;
        config.DataEgress.History.Add(Event(DataEgressEvent.Accepted, consent, windowsUser, machine, nowUtc));
        return consent;
    }

    public static void Revoke(WorkspaceConfig config, string windowsUser, string machine, DateTime nowUtc)
    {
        var consent = config.DataEgress?.Consent;
        if (consent == null) return;
        config.DataEgress!.Consent = null;
        config.DataEgress.History.Add(Event(DataEgressEvent.Revoked, consent, windowsUser, machine, nowUtc));
    }

    private static DataEgressEvent Event(string action, DataEgressConsent consent, string windowsUser, string machine, DateTime nowUtc) => new()
    {
        Action = action, AtUtc = nowUtc, WindowsUser = windowsUser, Machine = machine, Responsible = consent.Describe(),
        Date = consent.Date, NoticeVersion = consent.NoticeVersion, NoticeHash = consent.NoticeHash, Hosts = consent.Hosts.ToList(),
        Levels = consent.Providers.Select(p => $"{p.Host}: {ExposurePolicy.Label(p.Level)}").ToList()
    };
}

/// <summary>A request that would send Outlook data to a server the accepted notice does not cover.</summary>
public sealed class DataEgressBlockedException : InvalidOperationException
{
    public DataEgressBlockedException(string message) : base(message) { }
}
