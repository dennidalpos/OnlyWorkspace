using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>
/// Excluding a mailbox from the archive removes its emails and data from this PC (#207): which mailboxes a change
/// excludes, and the question asked before it is saved. Pure, so tests drive it without a window.
/// </summary>
internal static class MailboxExclusion
{
    /// <summary>Mailboxes archived before the change and not after it.</summary>
    public static IReadOnlyList<string> Excluded(StoreScope persisted, IReadOnlyCollection<string> storeIds,
        bool allChecked, IReadOnlyCollection<string> archivedNow)
    {
        if (allChecked) return Array.Empty<string>();
        var before = storeIds.Where(persisted.Allows).ToList();
        return before.Where(id => !archivedNow.Contains(id, StringComparer.Ordinal)).ToList();
    }

    /// <param name="emails">Emails in the archive per excluded mailbox; null when the count could not be read.</param>
    public static string Question(IReadOnlyList<(string Name, int? Emails)> emails)
    {
        var it = CultureInfo.GetCultureInfo("it-IT");
        string list = string.Join("\n", emails.Select(e => "• «" + e.Name + "»: " +
            (e.Emails is { } n ? n.ToString("N0", it) + " email" : "numero di email non leggibile")));
        return "Escludere queste cassette dall'archivio?\n\n" + list +
            "\n\nLe email, i fatti, gli allegati letti e le scelte di queste cassette saranno eliminati dall'archivio di questo PC. " +
            "Non si possono recuperare; le email restano in Outlook e, se riattivi la cassetta, vengono archiviate di nuovo.";
    }
}
