using System;
using System.Text.Json;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Agents;

/// <summary>
/// Model output that cannot be used as a result: invalid JSON, missing structure or cut at the output limit.
/// Reported as a failure so that it never reads as "nothing found".
/// </summary>
public sealed class InvalidModelOutputException : Exception
{
    public InvalidModelOutputException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class ModelOutput
{
    public const string TruncatedMessage = "Risposta IA incompleta: limite di output del modello raggiunto.";

    public static void EnsureComplete(LlmCompletion? completion)
    {
        if (completion?.IsTruncated == true) throw new InvalidModelOutputException(TruncatedMessage);
    }

    /// <summary>Model strings are trimmed; missing fields and other JSON kinds are not converted to text.</summary>
    internal static string ReadTrimmedString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim() : string.Empty;

    /// <summary>The JSON object in the answer (a code fence or text around it is tolerated) with the array <paramref name="required"/>.</summary>
    public static JsonDocument ParseObject(string? text, string required)
    {
        var json = text ?? string.Empty;
        int start = json.IndexOf('{'), end = json.LastIndexOf('}');
        if (start < 0 || end <= start) throw Invalid("nessun oggetto JSON");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json.Substring(start, end - start + 1)); }
        catch (JsonException ex) { throw Invalid("JSON non valido", ex); }
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty(required, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            doc.Dispose();
            throw Invalid($"manca l'elenco «{required}»");
        }
        return doc;
    }

    private static InvalidModelOutputException Invalid(string detail, Exception? inner = null) =>
        new("Risposta IA non valida: " + detail + ".", inner);
}
