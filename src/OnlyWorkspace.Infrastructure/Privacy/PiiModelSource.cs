using System;
using System.Collections.Generic;
using System.IO;

namespace OnlyWorkspace.Infrastructure.Privacy;

/// <summary>One file of the model: where it is on the host, its flat name in the model folder, its size and SHA-256.</summary>
public sealed class PiiModelFile
{
    public string RemotePath { get; set; } = string.Empty;
    public string LocalName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// The one place the model may be downloaded from (#319). Repository, revision and every hash are fixed in the product:
/// nothing the host answers can change what is installed. <see cref="Default"/> was null until the data licence of the
/// model is decided (#312); since 25/11 it is the MIT release of the producer.
/// </summary>
public sealed class PiiModelSource
{
    public string Host { get; set; } = "huggingface.co";
    public string Repository { get; set; } = string.Empty;
    /// <summary>Commit of the repository, never a branch name.</summary>
    public string Revision { get; set; } = string.Empty;
    /// <summary>Revision written to model.json.</summary>
    public string DisplayName { get; set; } = string.Empty;
    public string LicenseNote { get; set; } = string.Empty;
    public List<PiiModelFile> Files { get; set; } = new();
    /// <summary>The file the engine loads; every other file is listed as a companion in model.json.</summary>
    public string ModelFile { get; set; } = "model.onnx";

    public long TotalSize { get { long total = 0; foreach (var f in Files) total += f.Size; return total; } }

    /// <summary>
    /// The source shipped with the product (#312, owner decision 25/11): rizzo-pii-0.3B of Simone Rizzo (Rizzo AI Academy), MIT, fixed at one commit.
    /// Files and hashes are those of the official fp32 ONNX export measured by NerBench; the model is the same one the bench validated.
    /// </summary>
    public static PiiModelSource? Default { get; } = new PiiModelSource
    {
        Repository = "rizzoaiacademy/rizzo-pii-0.3B",
        Revision = "c21b36ac2784f837737a408be9f5273ea48847e4",
        // Becomes the NER revision of every privacy audit record, which only accepts [0-9A-Za-z.-] (#36): no "@".
        DisplayName = "rizzo-pii-0.3B-c21b36a",
        LicenseNote = "Licenza del modello: MIT, © 2026 Simone Rizzo (Rizzo AI Academy). I termini dei dati di addestramento non sono dichiarati dal produttore.",
        Files =
        {
            new PiiModelFile { RemotePath = "onnx/model.onnx", LocalName = "model.onnx", Size = 568606, Sha256 = "9d0fcfa86ec0751ecb737bb51d882be723c9998d4872137570bb4b5c5a02effe" },
            new PiiModelFile { RemotePath = "onnx/model.onnx_data", LocalName = "model.onnx_data", Size = 1230259200, Sha256 = "dc7dbe401863292ebf1e7e396fd438424891b83fc6f2b877247b2550a7418b24" },
            new PiiModelFile { RemotePath = "tokenizer.json", LocalName = "tokenizer.json", Size = 34363286, Sha256 = "a0ab5772a8c5450f45a66c95985d6df2205bf09449ba7ff7bb46de034eaa118a" },
            new PiiModelFile { RemotePath = "config.json", LocalName = "config.json", Size = 3137, Sha256 = "6b9d5bdd4a194d40fb40c53fdc302e225514cd7940d97f657c4082454ee7e91b" },
        }
    };

    public Uri UrlOf(PiiModelFile file) => new Uri($"https://{Host}/{Repository}/resolve/{Revision}/{file.RemotePath}");

    /// <exception cref="InvalidDataException">The description is incomplete or lets a file leave the model folder.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Repository) || Revision.Length != 40 || string.IsNullOrWhiteSpace(DisplayName) || Files.Count == 0)
            throw new InvalidDataException("Fonte del modello incompleta.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasModel = false;
        foreach (var file in Files)
        {
            if (string.IsNullOrWhiteSpace(file.RemotePath) || file.LocalName != Path.GetFileName(file.LocalName) || string.IsNullOrWhiteSpace(file.LocalName) ||
                file.LocalName == "model.json" || file.Size <= 0 || file.Sha256.Length != 64 || !names.Add(file.LocalName))
                throw new InvalidDataException("Fonte del modello non valida.");
            hasModel |= string.Equals(file.LocalName, ModelFile, StringComparison.OrdinalIgnoreCase);
        }
        if (!hasModel) throw new InvalidDataException("Fonte del modello senza il file del modello.");
    }
}
