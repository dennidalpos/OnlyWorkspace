using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Storage;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>What the «Privacy e dati» page says about data leaving the PC: text and tone, with no control.</summary>
internal sealed class EgressStatus
{
    public string Text { get; set; } = string.Empty;
    public Tone Tone { get; set; }
    /// <summary>There is at least one profile that sends data outside: «Leggi e autorizza» makes sense.</summary>
    public bool CanAccept { get; set; }
    /// <summary>A consent exists: «Revoca» makes sense.</summary>
    public bool CanRevoke { get; set; }

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static EgressStatus Build(WorkspaceConfig config, Func<string, string?> resolveSecret)
    {
        static string When(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Italian);

        var consent = config.DataEgress?.Consent;
        var hosts = DataEgress.Hosts(config);
        var missing = DataEgress.Missing(config, resolveSecret);
        var lines = new List<string>();
        Tone tone;
        if (consent == null)
        {
            lines.Add("Non accettata: profili cloud e ricerca web esterna sono bloccati.");
            lines.Add(hosts.Count == 0 ? "La configurazione attuale non invia dati a server esterni." : "Server configurati: " + string.Join(", ", hosts) + ".");
            tone = hosts.Count == 0 ? Tone.Muted : Tone.Warning;
        }
        else
        {
            lines.Add($"Autorizzazione tecnica; " +
                $"registrata il {When(consent.AcceptedUtc)} dall'utente {consent.WindowsUser} su {consent.Machine}.");
            lines.Add("Destinazioni autorizzate: " + (consent.Providers.Count == 0 ? "nessuno"
                : string.Join(", ", consent.Providers.Select(p => $"{p.Host} fino a «{ExposurePolicy.Label(p.Level)}»"))) + ".");
            if (!DataEgress.Current(consent))
            {
                lines.Add($"Da rinnovare: il testo dell'informativa è cambiato (versione {DataEgressNotice.Version}). Fino ad allora l'uscita dei dati è bloccata.");
                tone = Tone.Warning;
            }
            else if (missing.Count > 0)
            {
                lines.Add("Server da autorizzare (servizio, livello, permessi o chiavi mai autorizzati): " + string.Join(", ", missing) + ".");
                tone = Tone.Warning;
            }
            else tone = Tone.Success;
        }
        var history = config.DataEgress?.History ?? new List<DataEgressEvent>();
        var egressProfiles = DataEgress.EgressProfiles(config).ToList();
        // One line per service: the authorization covers every profile and model of the service with the same permissions.
        foreach (var service in DataEgress.ByService(egressProfiles))
        {
            var blocked = service.Where(p => !DataEgress.HasProfileGrant(config, p, resolveSecret)).Select(DataEgress.Name).ToList();
            string names = string.Join(", ", service.Select(DataEgress.Name));
            lines.Add($"• {DataEgress.Endpoint(service.First().BaseUrl)} ({names}): " + (blocked.Count == 0 ? "autorizzato"
                : "invii bloccati per " + string.Join(", ", blocked) + ", permessi o chiavi mai autorizzati"));
        }
        if (history.Count > 0)
        {
            lines.Add("Storico:");
            lines.AddRange(history.AsEnumerable().Reverse().Take(5).Select(e => $"• {When(e.AtUtc)} {e.Action}: {e.Responsible} (utente {e.WindowsUser})"));
        }
        return new EgressStatus
        {
            Text = string.Join("\r\n", lines),
            Tone = tone,
            CanAccept = egressProfiles.Any(),
            CanRevoke = consent != null
        };
    }
}
