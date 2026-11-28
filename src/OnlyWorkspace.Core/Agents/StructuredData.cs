using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OnlyWorkspace.Core.Agents;

/// <summary>Business data from an email (order, invoice, quote...): header fields plus optional line items.</summary>
public sealed class ExtractedData
{
    public static readonly string[] LineHeaders = { "Descrizione", "Quantità", "Prezzo unitario", "Importo" };

    public string DocumentType { get; set; } = "altro";
    public List<KeyValuePair<string, string>> Fields { get; } = new();
    public List<string[]> Lines { get; } = new();
    public ExtractionDiagnostics Diagnostics { get; } = new();

    public bool IsEmpty => Fields.Count == 0 && Lines.Count == 0;

    /// <summary>Tab-separated: pastes into Excel cells.</summary>
    public string ToTsv() => Build('\t', quoteAll: false);

    /// <summary>CSV with the given separator (';' for Italian Excel), RFC 4180 quoting.</summary>
    public string ToCsv(char separator) => Build(separator, quoteAll: true);

    private string Build(char separator, bool quoteAll)
    {
        var sb = new StringBuilder();
        void Row(IEnumerable<string> cells) => sb.AppendLine(string.Join(separator.ToString(), cells.Select(c => Cell(c, separator, quoteAll))));

        if (Diagnostics.IsPartial) Row(new[] { "Copertura", Diagnostics.Describe() });
        Row(new[] { "Campo", "Valore" });
        foreach (var f in Fields) Row(new[] { f.Key, f.Value });
        if (Lines.Count > 0)
        {
            sb.AppendLine();
            Row(LineHeaders);
            foreach (var line in Lines) Row(line);
        }
        return sb.ToString();
    }

    // A leading + or - is left alone on amounts and phone numbers.
    private static readonly System.Text.RegularExpressions.Regex SignedNumber = new(@"\A[+\-][\d\s.,'/()%€$£\-]*\z");

    /// <summary>Values come from an untrusted email: Excel runs a cell starting with = @ + - as a formula.</summary>
    private static string NeutralizeFormula(string v)
    {
        if (v.Length == 0) return v;
        bool formula = v[0] == '=' || v[0] == '@' || ((v[0] == '+' || v[0] == '-') && !SignedNumber.IsMatch(v));
        return formula ? "'" + v : v;
    }

    /// <summary>One delimited cell, also for the analysis table export.</summary>
    internal static string Cell(string value, char separator, bool quote)
    {
        var v = NeutralizeFormula((value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").TrimStart());
        if (!quote) return v.Replace('\t', ' ');
        return v.IndexOfAny(new[] { separator, '"' }) >= 0 || v.Length != v.Trim().Length ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}

public static class StructuredDataParser
{

    public static ExtractedData Parse(string json)
    {
        var result = new ExtractedData();
        using var doc = ModelOutput.ParseObject(json, "fields");
        var root = doc.RootElement;
        var type = Str(root, "type").ToLowerInvariant();
        result.DocumentType = type.Length > 0 && type.Length <= 30 ? type : "altro";

        foreach (var f in root.GetProperty("fields").EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) { result.Diagnostics.Discard("campi non oggetto"); continue; }
            var name = Str(f, "name");
            var value = Str(f, "value");
            if (name.Length > 0 && value.Length > 0) result.Fields.Add(new KeyValuePair<string, string>(name, value));
            else result.Diagnostics.Discard("campi senza nome o valore valido");
        }

        if (root.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in lines.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.Object) { result.Diagnostics.Discard("righe non oggetto"); continue; }
                var row = new[] { Str(l, "description"), Str(l, "quantity"), Str(l, "unit_price"), Str(l, "amount") };
                if (row.Any(c => c.Length > 0)) result.Lines.Add(row);
                else result.Diagnostics.Discard("righe senza valori validi");
            }
        }
        else if (root.TryGetProperty("lines", out _)) throw new InvalidModelOutputException("Risposta IA non valida: lines deve essere un elenco.");
        return result;
    }

    /// <summary>Strings as-is; numbers keep their JSON text (no culture-dependent reformatting).</summary>
    private static string Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.String => (v.GetString() ?? string.Empty).Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => string.Empty
        };
    }
}
