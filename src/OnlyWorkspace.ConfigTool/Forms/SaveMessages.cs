using System;
using System.Collections.Generic;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>What the ConfigTool says about saving and which values its numeric boxes accept, with no control.</summary>
internal static class SaveMessages
{
    public static string ValidationFailed(IEnumerable<string> errors) => "Validazione fallita:\n\n- " + string.Join("\n- ", errors);

    /// <summary>Status after a save: warnings (missing key or model, cards profile gone) belong here, not only in a log.</summary>
    public static (string Text, Tone Tone) Saved(IReadOnlyList<string> warnings, DateTime now)
    {
        if (warnings.Count == 0) return ($"Salvato alle {now:HH:mm}. Le modifiche sono attive per l'Add-in Outlook.", Tone.Success);
        string lead = warnings.Count == 1 ? "con un avviso: " : $"con {warnings.Count} avvisi (tutti nel log di Manutenzione e backup): ";
        return ($"Salvato alle {now:HH:mm}, " + lead + warnings[0], Tone.Warning);
    }

    public static string BlockedUntilNotice(IEnumerable<string> blocked) =>
        "Salvato. Bloccati finché non si accetta l'informativa sull'uscita dei dati (Privacy e dati): " + string.Join(", ", blocked) + ".";

    public static int MaxSearchItems(int value) => Math.Max(10, Math.Min(200, value));
    public static int RetentionDays(int value) => Math.Max(1, Math.Min(365, value));
    public static int MaxConversations(int value) => Math.Max(10, Math.Min(1000, value));
}
