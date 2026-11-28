using System;
using System.IO;
using System.Linq;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Infrastructure.Privacy;

public enum PiiModelState
{
    /// <summary>No model folder or manifest: nothing installed.</summary>
    Missing,
    /// <summary>Installed, every file matches its hash and the manifest matches the model fixed in the product.</summary>
    Ready,
    /// <summary>A file is missing, unreadable or has a different hash, or the manifest is not valid.</summary>
    Corrupt,
    /// <summary>Intact, but not the model fixed in this product version: it can be replaced.</summary>
    Different
}

/// <summary>Result of checking the installed model, so the download is skipped when it is already usable and offered again only when needed.</summary>
public sealed class PiiModelStatus
{
    public PiiModelStatus(PiiModelState state, string detail) { State = state; Detail = detail; }
    public PiiModelState State { get; }
    public string Detail { get; }
    public bool NeedsInstall => State != PiiModelState.Ready;

    /// <summary>Hashes every installed file (about 1.2 GB: run off the UI thread).</summary>
    public static PiiModelStatus Check(string directory, PiiModelSource? source)
    {
        if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, PiiModelPackage.ManifestFileName)))
            return new PiiModelStatus(PiiModelState.Missing, "modello non installato");
        PiiModelPackage package;
        try { package = PiiModelPackage.Verify(directory); }
        catch (DataEgressBlockedException ex) { return new PiiModelStatus(PiiModelState.Corrupt, Reason(ex.Message)); }
        if (source != null && !Matches(package, source))
            return new PiiModelStatus(PiiModelState.Different, $"installato «{package.Revision}», atteso «{source.DisplayName}»");
        return new PiiModelStatus(PiiModelState.Ready, package.Revision);
    }

    private static bool Matches(PiiModelPackage package, PiiModelSource source)
    {
        var model = source.Files.Find(f => string.Equals(f.LocalName, source.ModelFile, StringComparison.OrdinalIgnoreCase));
        if (model == null || !string.Equals(package.File, model.LocalName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.Sha256, model.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        var expected = source.Files.Where(f => f != model).ToList();
        return package.Companions.Count == expected.Count && expected.All(f =>
            package.Companions.TryGetValue(f.LocalName, out var hash) && string.Equals(hash, f.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static string Reason(string message) =>
        message.IndexOf("hash diverso", StringComparison.Ordinal) >= 0 ? "un file non corrisponde all'impronta attesa"
        : message.IndexOf("assente", StringComparison.Ordinal) >= 0 ? "manca un file del modello"
        : message.IndexOf("non leggibile", StringComparison.Ordinal) >= 0 ? "un file del modello non è leggibile"
        : "manifesto del modello non valido";
}
