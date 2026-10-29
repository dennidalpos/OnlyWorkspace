using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>The PC the report describes, read by the caller (registry, environment).</summary>
public sealed class DataEgressWorkstation
{
    public string Machine { get; set; } = string.Empty;
    /// <summary>Windows user who exports the report.</summary>
    public string ExportedBy { get; set; } = string.Empty;
    public string Windows { get; set; } = string.Empty;
    public string ProductVersion { get; set; } = string.Empty;
    public string Outlook { get; set; } = string.Empty;
    public string WebView2 { get; set; } = string.Empty;
    /// <summary>The config.json the report was read from.</summary>
    public string ConfigPath { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
}

/// <summary>
/// The data egress notice as a printable A4 page: workstation, AI profiles, authorizations, who enabled the egress,
/// history and the accepted text. Pure HTML with inline CSS and no scripts or external resources.
/// </summary>
public static class DataEgressReport
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public const string Title = "Informativa sull'uscita dei dati — OnlyWorkspace";

    /// <summary>One line of the state: authorized, to renew, or not authorized.</summary>
    public static string State(WorkspaceConfig config)
    {
        var consent = config.DataEgress?.Consent;
        if (consent == null) return "Non autorizzata: nessun dato di Outlook esce verso servizi esterni.";
        if (!DataEgress.Current(consent)) return $"Da rinnovare: accettata su un testo precedente (versione {consent.NoticeVersion}); l'uscita dei dati è bloccata.";
        var missing = DataEgress.Missing(config);
        return missing.Count == 0 ? "Autorizzata per tutti i server configurati."
            : "Autorizzata, ma bloccata per server o livelli non coperti: " + string.Join(", ", missing) + ".";
    }

    public static string RenderHtml(WorkspaceConfig config, DataEgressWorkstation workstation)
    {
        var consent = config.DataEgress?.Consent;
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"it\"><head><meta charset=\"utf-8\"><title>").Append(E(Title)).Append("</title><style>")
            .Append(Css).Append("</style></head><body>");

        html.Append("<header><div class=\"brand\">OnlyWorkspace</div><h1>").Append(E(DataEgressNotice.Title)).Append("</h1>")
            .Append("<p class=\"state ").Append(consent != null && DataEgress.Current(consent) && DataEgress.Missing(config).Count == 0 ? "ok" : "warn").Append("\">")
            .Append(E(State(config))).Append("</p><p class=\"muted\">Generato il ").Append(E(When(workstation.GeneratedUtc)))
            .Append(" da ").Append(E(workstation.ExportedBy)).Append("</p></header>");

        Section(html, "Postazione", Table(new[]
        {
            Row("Computer", workstation.Machine), Row("Esportato dall'utente Windows", workstation.ExportedBy), Row("Windows", workstation.Windows),
            Row("OnlyWorkspace", workstation.ProductVersion), Row("Outlook", workstation.Outlook), Row("Runtime WebView2", workstation.WebView2),
            Row("Configurazione", workstation.ConfigPath)
        }));

        if (consent != null)
        {
            Section(html, "Chi ha abilitato l'uscita dei dati", Table(new[]
            {
                Row("Azienda (titolare del trattamento)", consent.Company), Row("Responsabile", $"{consent.FirstName} {consent.LastName}"),
                Row("Ruolo", consent.Role), Row("Email", consent.Email), Row("Data indicata", Day(consent.Date)),
                Row("Registrata il", When(consent.AcceptedUtc)), Row("Utente Windows", consent.WindowsUser), Row("Computer", consent.Machine),
                Row("Testo accettato", $"versione {consent.NoticeVersion}, SHA-256 {consent.NoticeHash}")
            }));
            Section(html, "Dichiarazioni dell'azienda", Table(new[]
            {
                Row("Referente privacy", consent.PrivacyContact.Length == 0 ? "non indicato" : consent.PrivacyContact),
                Row("Base giuridica (GDPR art. 6)", Blank(consent.LegalBasis)), Row("Valutazione d'impatto (art. 35)", Blank(consent.Dpia)),
                Row("Lavoratori informati (L. 132/2025 art. 11)", YesNo(consent.WorkersInformed)),
                Row("Alfabetizzazione sull'IA (AI Act art. 4)", YesNo(consent.AiLiteracy))
            }));
            html.Append("<section><h2>Server autorizzati</h2>");
            if (consent.Providers.Count == 0) html.Append("<p>Nessun server: la configurazione al momento dell'accettazione non inviava dati.</p>");
            else
            {
                html.Append("<table class=\"grid\"><thead><tr><th>Server</th><th>Fornitore</th><th>Livello massimo</th><th>Nomina art. 28</th>")
                    .Append("<th>Dove</th><th>Trasferimento</th><th>Addestramento escluso</th><th>Conservazione</th></tr></thead><tbody>");
                foreach (var p in consent.Providers)
                    html.Append("<tr><td>").Append(E(p.Host)).Append("</td><td>").Append(E(p.Provider)).Append("</td><td>").Append(E(ExposurePolicy.Label(p.Level)))
                        .Append("</td><td>").Append(YesNo(p.ProcessingAgreement)).Append("</td><td>").Append(E(p.Region)).Append("</td><td>").Append(E(p.TransferBasis))
                        .Append("</td><td>").Append(YesNo(p.NoTraining)).Append("</td><td>").Append(E(p.Retention)).Append("</td></tr>");
                html.Append("</tbody></table>");
            }
            html.Append("</section>");
        }

        html.Append("<section><h2>Profili IA (modelli coinvolti)</h2><table class=\"grid\"><thead><tr><th>Profilo</th><th>Servizio e modello</th><th>Server</th>")
            .Append("<th>Destinazione</th><th>Livello</th><th>Allegati</th><th>Ricerca web</th><th>Uso</th><th>Stato</th></tr></thead><tbody>");
        foreach (var p in config.Profiles ?? new List<LlmProfileConfig>())
        {
            var uses = new List<string>();
            if (string.Equals(p.Id, config.DefaultProfile, StringComparison.OrdinalIgnoreCase)) uses.Add("predefinito");
            var web = WebSearchAction.All.Where(a => WebSearchAction.Uses(p, a.Key)).Select(a => a.Value).ToList();
            bool allowed = DataEgress.AllowsModel(config, p) && DataEgress.AllowsWeb(config, p);
            string server = Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var url) ? url.Host : p.BaseUrl;
            html.Append("<tr><td>").Append(E(p.DisplayName.Length == 0 ? p.Id : p.DisplayName)).Append("</td><td>")
                .Append(E(ProviderCatalog.Get(p.Type).DisplayName)).Append("<br>").Append(E(p.Model)).Append("</td><td>").Append(E(server))
                .Append("</td><td>").Append(p.IsExternal ? "cloud" : "locale").Append("</td><td>").Append(E(p.IsExternal ? ExposurePolicy.Label(p.Exposure) : "—"))
                .Append("</td><td>").Append(p.IsExternal ? YesNo(p.SendsAttachmentText) : "in locale").Append("</td><td>")
                .Append(E(web.Count == 0 ? "no" : string.Join(", ", web) + (DataEgress.WebHosts(p).Count > 0 ? " (" + string.Join(", ", DataEgress.WebHosts(p)) + ")" : "")))
                .Append("</td><td>").Append(E(uses.Count == 0 ? "—" : string.Join(", ", uses))).Append("</td><td>")
                .Append(allowed ? "utilizzabile" : "<strong>bloccato</strong>").Append("</td></tr>");
        }
        html.Append("</tbody></table></section>");

        var archive = config.Archive ?? new WorkspaceArchiveConfig();
        var archiveRows = new List<string>
        {
            Row("Archivio locale cifrato", archive.Enabled ? "attivo" : "spento"),
            Row("Testo degli allegati nell'archivio", archive.IndexAttachmentText ? "sì" : "no"),
        };
        Section(html, "Archivio locale", Table(archiveRows));

        var history = config.DataEgress?.History ?? new List<DataEgressEvent>();
        if (history.Count > 0)
        {
            html.Append("<section><h2>Storico delle autorizzazioni</h2><table class=\"grid\"><thead><tr><th>Quando</th><th>Azione</th><th>Responsabile</th>")
                .Append("<th>Utente e computer</th><th>Versione</th><th>Server e livelli</th></tr></thead><tbody>");
            foreach (var e in history.AsEnumerable().Reverse())
                html.Append("<tr><td>").Append(E(When(e.AtUtc))).Append("</td><td>").Append(E(e.Action)).Append("</td><td>").Append(E(e.Responsible))
                    .Append("</td><td>").Append(E($"{e.WindowsUser} su {e.Machine}")).Append("</td><td>").Append(e.NoticeVersion)
                    .Append("</td><td>").Append(E(string.Join(", ", e.Levels.Count > 0 ? e.Levels : e.Hosts))).Append("</td></tr>");
            html.Append("</tbody></table></section>");
        }

        string text = consent != null && consent.NoticeText.Length > 0 ? consent.NoticeText : DataEgressNotice.Text;
        string version = consent != null && consent.NoticeText.Length > 0
            ? $"Testo accettato, versione {consent.NoticeVersion}, SHA-256 {DataEgressNotice.HashOf(text)}"
            : $"Testo corrente, versione {DataEgressNotice.Version}, SHA-256 {DataEgressNotice.Hash}" + (consent == null ? " (non ancora accettato)" : "");
        html.Append("<section class=\"notice\"><h2>Testo dell'informativa</h2><p class=\"muted\">").Append(E(version)).Append("</p>");
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
            html.Append(paragraph.Length == 0 ? "" : paragraph.StartsWith("• ", StringComparison.Ordinal) ? "<p class=\"item\">" + E(paragraph) + "</p>"
                : paragraph.Length < 40 ? "<h3>" + E(paragraph) + "</h3>" : "<p>" + E(paragraph) + "</p>");
        html.Append("</section>");

        html.Append("<section class=\"sign\"><div><span>Luogo e data</span></div><div><span>Firma del responsabile</span></div></section>");
        html.Append("<footer>Documento generato da OnlyWorkspace sul computer ").Append(E(workstation.Machine))
            .Append(". Non sostituisce la valutazione del titolare né una consulenza legale.</footer></body></html>");
        return html.ToString();
    }

    private const string Css =
        "@page{size:A4;margin:16mm 14mm 18mm}" +
        "*{box-sizing:border-box}body{font-family:'Segoe UI',Arial,sans-serif;font-size:9.5pt;color:#1d1d1f;line-height:1.4;margin:0}" +
        "header{border-bottom:3px solid #1A5FB4;padding-bottom:8px;margin-bottom:12px}" +
        ".brand{color:#1A5FB4;font-weight:600;font-size:10pt;letter-spacing:.04em;text-transform:uppercase}" +
        "h1{font-size:15pt;margin:4px 0 6px}h2{font-size:11.5pt;color:#1A5FB4;margin:16px 0 6px;break-after:avoid}h3{font-size:10pt;margin:10px 0 4px;break-after:avoid}" +
        "p{margin:0 0 6px}.item{padding-left:12px;text-indent:-10px}.muted{color:#5f6368;font-size:8.5pt}" +
        ".state{display:inline-block;padding:3px 8px;border-radius:4px;font-weight:600}.ok{background:#e6f4ea;color:#137333}.warn{background:#fef7e0;color:#8a5300}" +
        "table{width:100%;border-collapse:collapse;margin-bottom:4px}tr{break-inside:avoid}" +
        "table.kv th{width:38%;text-align:left;font-weight:600;color:#3c4043;vertical-align:top;padding:3px 8px 3px 0;border-bottom:1px solid #e0e0e0}" +
        "table.kv td{padding:3px 0;border-bottom:1px solid #e0e0e0;overflow-wrap:anywhere}" +
        "table.grid{font-size:8.3pt}table.grid th{background:#eef3fb;text-align:left;padding:4px;border:1px solid #c9d7ee}" +
        "table.grid td{padding:4px;border:1px solid #dadce0;vertical-align:top;overflow-wrap:break-word}" +
        ".notice{break-before:page}.sign{display:flex;gap:24px;margin-top:28px;break-inside:avoid}" +
        ".sign div{flex:1;border-top:1px solid #5f6368;padding-top:4px;margin-top:40px;color:#5f6368;font-size:8.5pt}" +
        "footer{margin-top:18px;color:#5f6368;font-size:8pt;border-top:1px solid #e0e0e0;padding-top:6px}";

    private static void Section(StringBuilder html, string title, string body) =>
        html.Append("<section><h2>").Append(E(title)).Append("</h2>").Append(body).Append("</section>");

    private static string Table(IEnumerable<string> rows) => "<table class=\"kv\"><tbody>" + string.Concat(rows) + "</tbody></table>";

    private static string Row(string name, string? value) => "<tr><th>" + E(name) + "</th><td>" + E(Blank(value)) + "</td></tr>";

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value!;

    private static string YesNo(bool value) => value ? "sì" : "no";

    private static string When(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm", Italian);

    private static string Day(string date) =>
        DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy", Italian) : date;

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
