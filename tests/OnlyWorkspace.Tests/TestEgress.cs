using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Tests;

/// <summary>Tests that send to a cloud profile need the administrator's notice accepted, as a real installation does.</summary>
internal static class TestEgress
{
    public static string? ResolveSecret(string reference) => "fixture-secret";

    public static DataEgressSigner Signer(string? date = null) => new()
    {
        Company = "Prova Srl", FirstName = "Test", LastName = "Prova", Role = "Amministratore", Email = "test@prova.example",
        Date = date ?? DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        LegalBasis = DataEgress.LegalBases[2], Dpia = "DPIA-2026-01", WorkersInformed = true, AiLiteracy = true
    };

    /// <summary>Complete declarations for every server of the configuration, at the level its profiles ask for.</summary>
    public static List<DataEgressProvider> Providers(WorkspaceConfig config) => DataEgress.ProvidersFor(config).Select(p =>
    {
        p.Provider = "Fornitore di prova"; p.ProcessingAgreement = true; p.Region = "UE"; p.TransferBasis = DataEgress.TransferBases[0];
        p.NoTraining = true; p.Retention = "nessuna";
        return p;
    }).ToList();

    /// <summary>
    /// Accepts the notice for every profile. Cloud profiles still at the default level are raised to Standard first,
    /// so tests written for whole emails keep that behaviour.
    /// </summary>
    public static WorkspaceConfig Accepted(WorkspaceConfig config, bool fullExposure = true)
    {
        if (fullExposure)
            foreach (var profile in config.Profiles.Where(p => p.IsExternal && p.Exposure == ExposureLevel.Minimal)) profile.Exposure = ExposureLevel.Standard;
        DataEgress.Accept(config, Signer(), Providers(config), "test", "pc", DateTime.UtcNow, ResolveSecret);
        return config;
    }
}
