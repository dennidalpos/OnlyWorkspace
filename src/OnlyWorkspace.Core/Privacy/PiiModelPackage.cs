using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OnlyWorkspace.Core.Privacy;

/// <summary>
/// The model installed next to the recognizer: <c>model.json</c> names the file and its SHA-256, and nothing is used
/// whose hash differs (#247). The setup does not ship a model yet; without one every check here blocks.
/// </summary>
public sealed class PiiModelPackage
{
    public const string ManifestFileName = "model.json";
    public string Revision { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>Other files the model needs (tokenizer, config, external weights): file name to SHA-256. Each one is verified like the model.</summary>
    public System.Collections.Generic.Dictionary<string, string> Companions { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public string Path { get; private set; } = string.Empty;

    /// <exception cref="DataEgressBlockedException">The manifest or the model is missing or does not match its hash.</exception>
    public static PiiModelPackage Verify(string directory)
    {
        try
        {
            string manifestPath = System.IO.Path.Combine(directory, ManifestFileName);
            if (!System.IO.File.Exists(manifestPath)) throw new DataEgressBlockedException("Modello del riconoscimento locale assente: nessun dato inviato.");
            var package = JsonSerializer.Deserialize<PiiModelPackage>(System.IO.File.ReadAllText(manifestPath))
                ?? throw new DataEgressBlockedException("Modello del riconoscimento locale non valido: nessun dato inviato.");
            if (string.IsNullOrWhiteSpace(package.Revision) || string.IsNullOrWhiteSpace(package.File) || package.File != System.IO.Path.GetFileName(package.File) ||
                package.Sha256.Length != 64)
                throw new DataEgressBlockedException("Modello del riconoscimento locale non valido: nessun dato inviato.");
            package.Path = System.IO.Path.Combine(directory, package.File);
            if (!System.IO.File.Exists(package.Path)) throw new DataEgressBlockedException("Modello del riconoscimento locale assente: nessun dato inviato.");
            VerifyHash(package.Path, package.Sha256);
            foreach (var companion in package.Companions ?? new())
            {
                if (companion.Key != System.IO.Path.GetFileName(companion.Key) || companion.Value is not { Length: 64 })
                    throw new DataEgressBlockedException("Modello del riconoscimento locale non valido: nessun dato inviato.");
                string companionPath = System.IO.Path.Combine(directory, companion.Key);
                if (!System.IO.File.Exists(companionPath)) throw new DataEgressBlockedException("Modello del riconoscimento locale assente: nessun dato inviato.");
                VerifyHash(companionPath, companion.Value);
            }
            return package;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new DataEgressBlockedException("Modello del riconoscimento locale non leggibile: nessun dato inviato.");
        }
    }

    private static void VerifyHash(string path, string expected)
    {
        using var sha = SHA256.Create();
        using var stream = System.IO.File.OpenRead(path);
        string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new DataEgressBlockedException("Modello del riconoscimento locale con hash diverso: nessun dato inviato.");
    }
}
