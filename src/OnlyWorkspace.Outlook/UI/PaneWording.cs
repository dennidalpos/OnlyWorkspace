using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// Small sentences and shapes the pane shows around an answer (#177): the sender line, the email header the model reads, the
/// top-bar badge, and the notes under an answer. No Outlook, no model: the coordinator reads and asks, this only words.
/// </summary>
internal static class PaneWording
{
    /// <summary>Only Minimo sends typed facts alone; Standard and Esteso send masked text, so raising the level unlocks the action.</summary>
    public const string FactsOnly =
        "Questa azione richiede il testo dell'email, che il livello «1 · Minimo» non invia: escono solo fatti estratti in locale. " +
        "Alzare il livello a «2 · Standard» o usare un profilo locale.";

    /// <summary>Exchange senders expose an X.500 DN ("/o=…"): show the display name only.</summary>
    public static string Sender(string name, string address) =>
        string.IsNullOrWhiteSpace(address) || address.StartsWith("/", StringComparison.Ordinal) ? name : $"{name} <{address}>";

    /// <summary>Header lines prepended to the email text so the model knows subject, sender and date.</summary>
    public static string EmailHeader(string subject, string senderName, string senderAddress, DateTime received, bool withSender)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(subject)) sb.AppendLine("Oggetto: " + subject);
        if (withSender)
        {
            sb.AppendLine("Da: " + Sender(senderName, senderAddress));
            sb.AppendLine($"Data: {received:dd/MM/yyyy HH:mm}");
        }
        return sb.Length == 0 ? string.Empty : sb.AppendLine().ToString();
    }

    /// <summary>Model and server shown under the pane's top bar: who receives the data of the next action.</summary>
    public static object Badge(LlmProfileConfig profile) => new
    {
        model = profile.Model,
        host = HostOf(profile.BaseUrl),
        region = profile.IsExternal ? ServiceRegions.Label(profile.ServiceRegion) : string.Empty,
        webHosts = WebActionLabels(profile).Count > 0 ? string.Join(", ", WebSearchService.Destinations(profile)) : string.Empty,
        webActions = WebActionLabels(profile)
    };

    /// <summary>Names of the actions that search the web with this profile (empty when Web Search is off).</summary>
    public static List<string> WebActionLabels(LlmProfileConfig profile) =>
        WebSearchAction.All.Where(a => WebSearchAction.Uses(profile, a.Key)).Select(a => a.Value).ToList();

    public static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>
    /// Lines under an answer on one email: the attachments not read and why, whatever the model says; then, with a
    /// local profile that read some, a caution (a small model can pair an amount with the wrong label on a dense form).
    /// </summary>
    public static string? AttachmentNotice(LlmProfileConfig profile, IReadOnlyList<DocumentOutcome> documents)
    {
        var notice = AnalysisReport.EmailDocuments(documents.ToList());
        if (!profile.IsExternal && documents.Any(d => d.WasRead)) notice += "\n\n*Importi e date letti dagli allegati: verificali sul documento.*";
        return notice.Length == 0 ? null : notice;
    }

    /// <summary>Line under an answer built on part of a list: a fixed context takes whole items while they fit.</summary>
    public static string? LeftOutNotice(int included, int total, string what) =>
        included >= total ? null : $"\n\n*Risposta basata su {included} {what} su {total}: gli altri non entrano nel contesto del profilo in uso.*";

    /// <summary>Why an action that sends whole emails is refused at this exposure level.</summary>
    public static string WholeEmailsRefused(LlmProfileConfig profile) =>
        ExposurePolicy.Of(profile).Level == ExposureLevel.LocalOnly
            ? "Il profilo cloud è al livello di esposizione «0 · Solo locale»: non invia nulla. Per questa azione usare un profilo locale."
            : FactsOnly;
}
