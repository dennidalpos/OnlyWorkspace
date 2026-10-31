using System;
using System.Collections.Generic;

namespace OnlyWorkspace.Core.Archive;

/// <summary>One line of the archive status: counts and a percentage, never a sentence.</summary>
public sealed class ArchiveMetric
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>"count", "bytes" or "tokens".</summary>
    public string Unit { get; set; } = "count";
    public long Done { get; set; }
    /// <summary>Null for a plain figure (space, tokens).</summary>
    public long? Total { get; set; }
    public long Remaining { get; set; }
    /// <summary>Excluded or failed: not counted in the percentage.</summary>
    public long Excluded { get; set; }
    public int? Percent { get; set; }
}

/// <summary>One mailbox of the archive status.</summary>
public sealed class ArchiveStoreMetric
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Fixed label: "chiusa", "esclusa", "errore", "in attesa", "inventario"...</summary>
    public string State { get; set; } = string.Empty;
    public StatusLight Light { get; set; }
    public long Emails { get; set; }
    public long BodiesRemaining { get; set; }
    public int? BodiesPercent { get; set; }
    public long AttachmentsDone { get; set; }
    public long AttachmentsTotal { get; set; }
    public long FoldersDone { get; set; }
    public long Folders { get; set; }
    public DateTime? LastInventoryUtc { get; set; }
    public string Error { get; set; } = string.Empty;
}

/// <summary>Same figures in the Outlook pane and in the tray window.</summary>
public static class ArchiveMetrics
{
    /// <summary>Rounded down: 100% only when nothing is left.</summary>
    public static int? Percent(long done, long total) => total > 0 ? (int)Math.Min(100, Math.Floor(100.0 * Math.Max(0, done) / total)) : null;

    public static string PhaseLabel(string? phase) => phase switch
    {
        "off" => "spento",
        "starting" => "avvio",
        "cleaning" => "rimozione cassette",
        "headers" => "intestazioni",
        "bodies" => "testi",
        "waiting" => "attesa inattività",
        "paused" => "in pausa",
        "limit" => "limite di spazio",
        "error" => "errore",
        "sync" => "sincronizzazione",
        "backfill" => "storico",
        "idle" => "aggiornato",
        _ => "attesa"
    };

    public static List<ArchiveMetric> Build(ArchiveStats s)
    {
        long bodiesLeft = s.BodiesPending + s.BodiesRetryable;
        long attachmentsLeft = s.AttachmentTextsPending + s.AttachmentTextsRetryable;
        long attachmentsDone = Math.Max(0, s.AttachmentTexts - attachmentsLeft - s.AttachmentTextsExcluded);
        var list = new List<ArchiveMetric>
        {
            Progress("bodies", "Testi email", s.BodiesIndexed, s.Emails, bodiesLeft, s.BodiesUnavailable),
            Progress("attachments", "Allegati", attachmentsDone, s.AttachmentTexts, attachmentsLeft, s.AttachmentTextsExcluded),
            Progress("folders", "Cartelle", s.FoldersDone, s.Folders, Math.Max(0, s.Folders - s.FoldersDone - s.FoldersFailed), s.FoldersFailed)
        };
        if (s.PendingDeletions > 0) list.Add(new ArchiveMetric { Key = "deletions", Name = "Pulizie in attesa", Done = s.PendingDeletions });
        list.Add(new ArchiveMetric { Key = "db", Name = "Database", Unit = "bytes", Done = s.DbBytes });
        list.Add(new ArchiveMetric { Key = "free", Name = "Disco libero", Unit = "bytes", Done = s.AvailableFreeBytes });
        return list;
    }

    private static ArchiveMetric Progress(string key, string name, long done, long total, long remaining, long excluded) => new()
    {
        Key = key, Name = name, Done = done, Total = total, Remaining = remaining, Excluded = excluded,
        Percent = Percent(done, total - excluded)
    };

    /// <summary>coverage null = never inventoried.</summary>
    public static ArchiveStoreMetric Store(string name, bool open, bool selected, ArchiveStoreCoverage? c)
    {
        var m = new ArchiveStoreMetric { Name = string.IsNullOrWhiteSpace(name) ? c?.DisplayName ?? string.Empty : name };
        if (c != null)
        {
            m.Emails = c.Emails;
            m.BodiesRemaining = c.BodiesPending;
            m.BodiesPercent = Percent(c.BodiesIndexed, c.BodiesIndexed + c.BodiesPending);
            m.AttachmentsDone = c.AttachmentsIndexed;
            m.AttachmentsTotal = c.AttachmentsTotal;
            m.FoldersDone = c.FoldersDone;
            m.Folders = c.Folders;
            m.LastInventoryUtc = c.LastInventoryUtc;
            m.Error = c.Error;
        }
        m.State = !open ? "chiusa" : !selected ? "esclusa" : c == null ? "in attesa" : c.Error.Length > 0 ? "errore"
            : c.FoldersDone < c.Folders ? "inventario" : c.AbsenceVerified ? "verificata" : c.CacheChecked ? "cache verificata" : "cache da controllare";
        // A coverage snapshot cannot prove that this mailbox is being processed right now.
        m.Light = !open || !selected ? StatusLight.Off : c == null ? StatusLight.Waiting : c.Error.Length > 0 ? StatusLight.Error
            : c.FoldersDone < c.Folders || c.BodiesPending > 0 ? StatusLight.Waiting
            : c.AbsenceVerified || c.CacheChecked ? StatusLight.Ok : StatusLight.Waiting;
        return m;
    }
}
