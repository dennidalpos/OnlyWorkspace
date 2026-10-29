using System.Collections.Generic;
using System.Text;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Core.Context;

/// <summary>Whole originals, not search snippets, are the input of the unread-mail triage.</summary>
public static class BriefingContext
{
    public static string FormatAgenda(IEnumerable<AppointmentInfo> appointments)
    {
        var agenda = new StringBuilder();
        foreach (var a in appointments)
        {
            agenda.Append("- ").Append(a.IsAllDayEvent ? "Tutto il giorno" : $"{a.Start:HH:mm}–{a.End:HH:mm}").Append(' ').Append(a.Subject);
            if (!string.IsNullOrWhiteSpace(a.Location)) agenda.Append(" (").Append(a.Location).Append(')');
            agenda.AppendLine();
        }
        return agenda.Length > 0 ? agenda.ToString() : "Nessun impegno in calendario oggi.\n";
    }

    /// <summary>The deterministic agenda remains on the PC at Minimal exposure, including on subsequent chat turns.</summary>
    public static string AgendaForModel(LlmProfileConfig profile, string agenda) =>
        ExposurePolicy.Of(profile).WholeEmails ? agenda : "Agenda non inclusa: disponibile solo sul PC dell'utente.";

    public static string Email(int index, EmailSearchResult email, string body, string attachments, bool facts = false) =>
        $"[{index}] Da: {email.SenderName} <{email.SenderEmail}> | Oggetto: {email.Subject} | Ricevuta: {email.ReceivedTime:dd/MM/yyyy HH:mm}\n" +
        (facts ? "<fatti_locali>\n" : "<originale>\n") + body + attachments + (facts ? "\n</fatti_locali>\n" : "\n</originale>\n");
}
