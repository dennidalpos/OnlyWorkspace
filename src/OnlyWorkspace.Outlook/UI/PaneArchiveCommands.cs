using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// Archive commands sent by the pane, decoded and applied to the indexer. COM-free so tests can drive the
/// real indexer. Mailbox choice and cloud consent are set in the ConfigTool, not here.
/// </summary>
internal static class PaneArchiveCommands
{
    /// <returns>Message for the user, or null when nothing needs saying.</returns>
    public static async Task<string?> ApplyAsync(IArchiveIndexer indexer, string type, JsonElement message)
    {
        switch (type)
        {
            case "archive_pause":
                await indexer.SetPausedAsync(IsTrue(message, "paused"));
                return null;

            case "archive_index_now":
                await indexer.IndexNowAsync();
                return "Indicizzazione avviata: prosegue anche mentre usi Outlook, a piccoli passi, finché non resta nulla da leggere.";

            case "archive_delete":
                await indexer.DeleteArchiveAsync();
                OnlyWorkspaceLog.Logger.Information("Archive deleted by the user");
                return "Archivio eliminato e spento: si riaccende in Configurazione → Archivio email → Le mie cassette.";
        }
        return null;
    }

    private static bool IsTrue(JsonElement message, string property) =>
        message.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
