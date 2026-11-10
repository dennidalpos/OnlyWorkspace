using System.Linq;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>Whether Ricostruisci pratica has an email to start from; COM-free. Null means available.</summary>
internal static class FlowSeedAvailability
{
    public const string NoSelection = "Seleziona o apri un'email per ricostruire la pratica.";
    public const string SeedGone = "L'email di partenza non è più disponibile. Seleziona un'email e riprova.";

    /// <summary>Neither a selected email nor one named by the pane.</summary>
    public static string? BeforeRead(ArchiveFlowSeed? seed) => seed == null ? NoSelection : null;

    /// <summary>A seed not open in Outlook must be in the read flow, as a mail or one of its copies.</summary>
    public static string? AfterRead(ArchiveFlowSeed seed, bool visibleInOutlook, ArchiveFlow flow) =>
        visibleInOutlook || flow.Timeline.Any(m => m.StoreId == seed.StoreId && m.EntryId == seed.EntryId ||
            m.Copies.Any(c => c.StoreId == seed.StoreId && c.EntryId == seed.EntryId))
            ? null
            : SeedGone;
}
