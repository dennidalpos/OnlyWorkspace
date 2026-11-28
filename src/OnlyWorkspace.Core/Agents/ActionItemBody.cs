using System;
using System.Linq;
using System.Text;
using OnlyWorkspace.Core.Security;

namespace OnlyWorkspace.Core.Agents;

/// <summary>Local source material, never a prompt or a model-generated reconstruction.</summary>
public static class ActionItemBody
{
    public static string Compose(string notes, string subject, string sender, DateTime received, string body, string html)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(notes)) text.AppendLine(notes.Trim()).AppendLine();
        var links = SecurityLinks.Read(html, body)
            .Where(l => Uri.TryCreate(l.Destination, UriKind.Absolute, out var uri) &&
                (new[] { "https", "http", "tel", "msteams", "zoommtg", "sip", "sips", "skype", "mailto" }.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)))
            .GroupBy(l => l.Destination, StringComparer.Ordinal).Select(g => g.First()).ToList();
        if (links.Count > 0)
        {
            text.AppendLine("Collegamenti della mail");
            foreach (var link in links) text.AppendLine(link.Text + " — " + link.Destination);
            text.AppendLine();
        }
        text.AppendLine("Email originale").AppendLine("Oggetto: " + subject)
            .AppendLine("Da: " + sender).AppendLine("Data: " + received.ToString("dd/MM/yyyy HH:mm"))
            .AppendLine().Append(body);
        return text.ToString();
    }
}
