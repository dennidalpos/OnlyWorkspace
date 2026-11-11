using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Storage;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>Texts of the storage table of «Privacy e dati».</summary>
internal static class StorageText
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Key of the rows shown: the table is rebuilt only when it changes.</summary>
    public static string Signature(StorageReport report) =>
        string.Join("\n", report.Items.Select(i => i.Name + "|" + i.Bytes + "|" + i.Files + "|" + i.Unreadable + "|" + i.Detail + "|" + i.Path));

    public static string Size(StorageItem item)
    {
        string size = AppStorageUsage.Text(item.Bytes) + (item.Files == 1 ? " · 1 file" : $" · {item.Files.ToString("N0", Italian)} file");
        if (item.Unreadable > 0) size += $" · {item.Unreadable} non leggibili";
        if (item.Detail.Length > 0) size += "\r\n" + item.Detail;
        return size;
    }

    public static string SizeTip(StorageItem item) =>
        "Dimensione dei file su disco" + (item.Detail.Length > 0 ? ", divisa per tipo." : ".") +
        (item.Unreadable > 0 ? " Alcuni file non sono leggibili con i diritti del tuo utente e non sono contati." : string.Empty);

    public static string Total(StorageReport report, DateTime now) =>
        $"Totale {AppStorageUsage.Text(report.TotalBytes)} in {report.TotalFiles.ToString("N0", Italian)} file" +
        (report.FreeBytes is { } free ? $"; liberi sul disco dell'archivio {AppStorageUsage.Text(free)}." : ".") +
        $" Calcolato alle {now:HH:mm}.";
}
