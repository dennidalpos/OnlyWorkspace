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
    /// <summary>There is at least one profile that sends data outside: «Accetta» makes sense.</summary>
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
                lines.Add("Profili da autorizzare o rinnovare (destinazione, livello, permessi o chiavi cambiati): " + string.Join(", ", missing) + ".");
                tone = Tone.Warning;
            }
            else tone = Tone.Success;
        }
        var history = config.DataEgress?.History ?? new List<DataEgressEvent>();
        var egressProfiles = DataEgress.EgressProfiles(config);
        foreach (var profile in egressProfiles)
            lines.Add($"• {profile.DisplayName}: " + (DataEgress.HasProfileGrant(config, profile, resolveSecret) ? "autorizzato" : "invii bloccati, da autorizzare o rinnovare"));
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

/// <summary>Texts of the storage table of «Privacy e dati».</summary>
internal static class StorageText
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Key of the rows shown: the table is rebuilt only when it changes.</summary>
    public static string Signature(StorageReport report) =>
        string.Join("\n", report.Items.Select(i => i.Name + "|" + i.Bytes + "|" + i.Files + "|" + i.Unreadable + "|" + i.Detail + "|" + i.Path));

    public static string Size(StorageItem item)
    {
        string size = AppStorageUsage.Text(item.Bytes) + (item.Files == 1 ? " · 1 file" : $" · {item.Files.ToString("N0", Italian)} file");
        if (item.Unreadable > 0) size += $" · {item.Unreadable} non leggibili";
        if (item.Detail.Length > 0) size += "\r\n" + item.Detail;
        return size;
    }

    public static string SizeTip(StorageItem item) =>
        "Dimensione dei file su disco" + (item.Detail.Length > 0 ? ", divisa per tipo." : ".") +
        (item.Unreadable > 0 ? " Alcuni file non sono leggibili con i diritti del tuo utente e non sono contati." : string.Empty);

    public static string Total(StorageReport report, DateTime now) =>
        $"Totale {AppStorageUsage.Text(report.TotalBytes)} in {report.TotalFiles.ToString("N0", Italian)} file" +
        (report.FreeBytes is { } free ? $"; liberi sul disco dell'archivio {AppStorageUsage.Text(free)}." : ".") +
        $" Calcolato alle {now:HH:mm}.";
}
