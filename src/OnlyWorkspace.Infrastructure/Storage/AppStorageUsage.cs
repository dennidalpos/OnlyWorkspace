using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using OnlyWorkspace.Core;

namespace OnlyWorkspace.Infrastructure.Storage;

/// <summary>One folder or file the app writes, measured without opening it.</summary>
public sealed class StorageItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public int Files { get; set; }
    /// <summary>Files or folders that could not be measured (access denied, removed meanwhile).</summary>
    public int Unreadable { get; set; }
    /// <summary>Breakdown by kind of file, empty when there is a single kind.</summary>
    public string Detail { get; set; } = string.Empty;
}

public sealed class StorageReport
{
    public IReadOnlyList<StorageItem> Items { get; set; } = Array.Empty<StorageItem>();
    public long TotalBytes => Items.Sum(i => i.Bytes);
    public int TotalFiles => Items.Sum(i => i.Files);
    /// <summary>Free space on the archive's volume; null when it cannot be read.</summary>
    public long? FreeBytes { get; set; }
}

/// <summary>Disk space taken by the files OnlyWorkspace generates for the current Windows user and in ProgramData.</summary>
public static class AppStorageUsage
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static StorageReport Measure(CancellationToken ct = default, string? configDir = null)
    {
        var items = new List<StorageItem>
        {
            Archive(AppPaths.ArchiveDirectory, ct),
            Folder("Log", AppPaths.LogDirectory, $"Eventi tecnici dell'add-in, senza contenuto delle email né chiavi. Un file al giorno, al massimo {Logging.OnlyWorkspaceLog.FilesKept} da {Logging.OnlyWorkspaceLog.FileLimitBytes / (1024 * 1024)} MB: i più vecchi si cancellano da soli.", ct),
            Folder("Storico", AppPaths.HistoryDirectory, "Chat e risultati delle azioni, cifrati per utente; giorni e numero di conversazioni si impostano in Generale.", ct),
            Folder("Registro pseudonimizzazione", AppPaths.PseudonymizationAuditDirectory, "Metadati cifrati per utente: conteggi, revisioni ed esiti delle richieste esterne, conservati per 30 giorni. Nessun testo originale o mappa dei segnaposto.", ct),
            Folder("Dati del riquadro", AppPaths.WebViewDirectory, "Cache del componente WebView2 che disegna il riquadro; si ricrea da sola se viene tolta.", ct),
            File("Preferenze utente", Path.Combine(AppPaths.PreferencesDirectory, "preferences.json"), "Profilo scelto e schermate di primo avvio già viste."),
            Folder("Copie temporanee", AppPaths.TempDirectory, "Allegati copiati per la lettura; eliminati al termine, i residui oltre un'ora all'avvio di Outlook.", ct),
            Folder("Configurazioni non leggibili", AppPaths.UnreadableConfigDirectory, "Copie di config.json o secrets.bin che il ConfigTool non è riuscito a leggere, conservate per l'amministratore.", ct),
            Folder("Configurazione e chiavi", configDir ?? AppPaths.ConfigDirectory, "config.json, chiavi API cifrate (secrets.bin) e password amministrativa, comuni a tutti gli utenti.", ct)
        };
        long? free = null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(AppPaths.ArchiveDirectory));
            if (!string.IsNullOrEmpty(root)) free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { }
        return new StorageReport { Items = items, FreeBytes = free };
    }

    /// <summary>The archive split by kind: databases, their journals, rollback copies, key and locks.</summary>
    internal static StorageItem Archive(string directory, CancellationToken ct)
    {
        var item = Folder("Archivio delle email", directory,
            "Database cifrato per utente: testi integrali delle email, testi degli allegati deduplicati, indice e fatti locali. Include i giornali SQLite.", ct, out var files);
        long Sum(Func<string, bool> kind) => files.Where(f => kind(f.Key)).Sum(f => f.Value);
        static bool Rollback(string name) => name.EndsWith(".rollback.db", StringComparison.OrdinalIgnoreCase);
        static bool Database(string name) => name.EndsWith(".engine.db", StringComparison.OrdinalIgnoreCase) && !Rollback(name);
        static bool Journal(string name) => name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);
        long databases = Sum(Database), journals = Sum(Journal), rollbacks = Sum(Rollback);
        long other = item.Bytes - databases - journals - rollbacks;
        var parts = new List<string> { "database " + Text(databases), "giornali " + Text(journals) };
        if (rollbacks > 0) parts.Add("copie di ripristino " + Text(rollbacks));
        parts.Add("chiave e blocchi " + Text(other));
        item.Detail = string.Join(" · ", parts);
        return item;
    }

    internal static StorageItem Folder(string name, string path, string note, CancellationToken ct) => Folder(name, path, note, ct, out _);

    private static StorageItem Folder(string name, string path, string note, CancellationToken ct, out Dictionary<string, long> files)
    {
        var item = new StorageItem { Name = name, Path = path, Note = note };
        files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(path)) return item;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    // A junction or symbolic link points elsewhere: its target is not the app's.
                    try { if ((System.IO.File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) pending.Push(sub); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { item.Unreadable++; }
                }
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        long length = new FileInfo(file).Length;
                        item.Bytes += length;
                        item.Files++;
                        files[file] = length;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { item.Unreadable++; }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { item.Unreadable++; }
        }
        return item;
    }

    private static StorageItem File(string name, string path, string note)
    {
        var item = new StorageItem { Name = name, Path = path, Note = note };
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) { item.Bytes = info.Length; item.Files = 1; }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { item.Unreadable++; }
        return item;
    }

    /// <summary>Byte size in Italian notation (it-IT whatever the Windows language): «0 KB», «812 KB», «4,7 MB», «1,2 GB».</summary>
    public static string Text(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        if (bytes >= Gb) return (bytes / Gb).ToString("N1", Italian) + " GB";
        if (bytes >= Mb) return (bytes / Mb).ToString("N1", Italian) + " MB";
        return Math.Ceiling(bytes / Kb).ToString("N0", Italian) + " KB";
    }
}
