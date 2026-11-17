using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OnlyWorkspace.Core.Agents;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>The email a structured result was read from: what its cards and notes cite, without any Outlook type.</summary>
internal sealed class EmailSource
{
    public string Subject { get; set; } = string.Empty;
    /// <summary>Display name, with the address when it is not an Exchange X.500 one.</summary>
    public string Sender { get; set; } = string.Empty;
    public DateTime Received { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public string StoreId { get; set; } = string.Empty;
}

/// <summary>
/// What «Attività e appuntamenti» and «Estrazione dati» show after the model answered (#177): the bubble text, the cards or the
/// table, and the copy/CSV export. No Outlook, no model: the coordinator reads and asks, this only words and shapes the result.
/// </summary>
internal static class StructuredEmailReport
{
    /// <summary>Items of one email as the bubble text and the cards the pane offers as unsaved Outlook drafts.</summary>
    public static (string Text, object? Extra) ActionItems(string json, EmailSource email)
    {
        var items = ActionItemParser.Parse(json);
        var source = $"Da email «{email.Subject}» di {email.Sender} del {email.Received:dd/MM/yyyy}.";
        var sb = new StringBuilder();
        if (items.Diagnostics.IsPartial) sb.AppendLine(items.Diagnostics.Describe());
        if (items.Count == 0)
        {
            sb.Append("Nessuna attività o appuntamento individuato nell'email.");
        }
        else
        {
            sb.Append(items.Count == 1 ? "Trovata 1 voce" : $"Trovate {items.Count} voci").AppendLine(": apri la bozza, verifica i dati e salvala in Outlook.");
            foreach (var item in items)
            {
                sb.Append("- **").Append(item.IsAppointment ? "Appuntamento" : "Attività").Append("** — ").Append(item.Title).Append(" (").Append(item.WhenLabel).AppendLine(")");
            }
        }

        var payload = items.Select(i => new
        {
            kind = i.Kind,
            title = i.Title,
            when = i.WhenLabel,
            due = i.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            start = i.Start?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            end = i.End?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            allDay = i.AllDay,
            location = i.Location,
            notes = string.IsNullOrWhiteSpace(i.Notes) ? source : i.Notes + "\n\n" + source,
            source = new { entryId = email.EntryId, storeId = email.StoreId, label = email.Subject }
        }).ToList();
        var export = ListExport(new[] { "Tipo", "Titolo", "Quando", "Luogo", "Note" },
            items.Select(i => new[] { i.Kind, i.Title, i.WhenLabel, i.Location, i.Notes }), items.Diagnostics);
        return (sb.ToString(), new { items = payload, export });
    }

    /// <summary>Order or invoice data as the bubble text and a table the pane can copy (TSV) or save as CSV.</summary>
    /// <param name="listSeparator">The locale's list separator; null reads the current culture.</param>
    public static (string Text, object? Extra) StructuredData(string json, string? listSeparator = null)
    {
        var data = StructuredDataParser.Parse(json);
        if (data.IsEmpty) return ((data.Diagnostics.IsPartial ? data.Diagnostics.Describe() + "\n" : "") + "Nessun dato commerciale strutturato valido individuato nell'email.", null);

        var text = $"Dati estratti ({data.DocumentType}): {data.Fields.Count} campi" + (data.Lines.Count > 0 ? $", {data.Lines.Count} righe" : "") +
                   ". Verifica i valori con l'email originale prima di usarli.";
        if (data.Diagnostics.IsPartial) text = data.Diagnostics.Describe() + "\n" + text;
        var table = new
        {
            documentType = data.DocumentType,
            fields = data.Fields.Select(f => new[] { f.Key, f.Value }).ToList(),
            lineHeaders = ExtractedData.LineHeaders,
            lines = data.Lines,
            tsv = data.ToTsv(),
            csv = data.ToCsv(Separator(listSeparator))
        };
        return (text, new { table });
    }

    /// <summary>Keep every extracted row in history too, with a fence email data cannot terminate.</summary>
    public static string HistoryText(string text, object? extra)
    {
        if (extra == null) return text;
        var table = extra.GetType().GetProperty("table")?.GetValue(extra) ?? extra.GetType().GetProperty("export")?.GetValue(extra);
        var tsv = table?.GetType().GetProperty("tsv")?.GetValue(table) as string;
        if (string.IsNullOrEmpty(tsv)) return text;
        int longest = 0, run = 0;
        foreach (char c in tsv!)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string fence = new('`', Math.Max(3, longest + 1));
        return text + "\n\n" + fence + "text\n" + tsv + "\n" + fence;
    }

    /// <summary>A result list as clipboard text (TSV) and CSV, for the same Copia/Salva CSV buttons of the tables; null when empty.</summary>
    /// <param name="listSeparator">The locale's list separator; null reads the current culture.</param>
    public static object? ListExport(string[] headers, IEnumerable<string[]> rows, ExtractionDiagnostics? diagnostics = null, string? listSeparator = null)
    {
        var list = rows.ToList();
        if (list.Count == 0 && diagnostics?.IsPartial != true) return null;
        var separator = Separator(listSeparator);
        string warning = diagnostics?.IsPartial == true ? diagnostics.Describe() : string.Empty;
        return new
        {
            tsv = (warning.Length > 0 ? AnalysisTable.ToDelimited(new[] { "Copertura", warning }, Array.Empty<string[]>(), '\t', quote: false) : "") + AnalysisTable.ToDelimited(headers, list, '\t', quote: false),
            csv = (warning.Length > 0 ? AnalysisTable.ToDelimited(new[] { "Copertura", warning }, Array.Empty<string[]>(), separator, quote: true) : "") + AnalysisTable.ToDelimited(headers, list, separator, quote: true)
        };
    }

    private static char Separator(string? listSeparator) =>
        (listSeparator ?? CultureInfo.CurrentCulture.TextInfo.ListSeparator) == ";" ? ';' : ',';
}
