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
            using var sha = SHA256.Create();
            using var stream = System.IO.File.OpenRead(package.Path);
            string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new DataEgressBlockedException("Modello del riconoscimento locale con hash diverso: nessun dato inviato.");
            return package;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new DataEgressBlockedException("Modello del riconoscimento locale non leggibile: nessun dato inviato.");
        }
    }
}
