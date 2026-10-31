using System;
using System.Linq;
using OnlyWorkspace.Core.Archive;

namespace OnlyWorkspace.Tests;

/// <summary>Figures of the archive status shared by the Outlook pane and the tray window.</summary>
public sealed class ArchiveMetricsTests
{
    [Fact]
    public void StoreSnapshot_ReportsWaitingWithoutInventingWorkOrHidingAnError()
    {
        var coverage = new ArchiveStoreCoverage { Folders = 2, FoldersDone = 1 };
        Assert.Equal(StatusLight.Waiting, ArchiveMetrics.Store("Test", true, true, coverage).Light);
        coverage.Error = "Cartella non leggibile";
        Assert.Equal(StatusLight.Error, ArchiveMetrics.Store("Test", true, true, coverage).Light);
        coverage.FoldersDone = 2;
        coverage.CacheChecked = true;
        Assert.Equal(StatusLight.Error, ArchiveMetrics.Store("Test", true, true, coverage).Light);
        coverage.Error = "";
        Assert.Equal(StatusLight.Ok, ArchiveMetrics.Store("Test", true, true, coverage).Light);
        Assert.Equal(StatusLight.Off, ArchiveMetrics.Store("Test", false, true, coverage).Light);
        Assert.Equal(StatusLight.Off, ArchiveMetrics.Store("Test", true, false, coverage).Light);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(0, 10, 0)]
    [InlineData(999, 1000, 99)]
    [InlineData(1000, 1000, 100)]
    [InlineData(5, 3, 100)]
    public void Percent_RoundsDownAndNeedsATotal(long done, long total, int? expected) =>
        Assert.Equal(expected, ArchiveMetrics.Percent(done, total));

    [Fact]
    public void Build_CountsDoneRemainingAndExcluded()
    {
        var stats = new ArchiveStats
        {
            Emails = 100, BodiesIndexed = 60, BodiesPending = 25, BodiesRetryable = 5, BodiesUnavailable = 10,
            AttachmentTexts = 20, AttachmentTextsPending = 2, AttachmentTextsRetryable = 3, AttachmentTextsExcluded = 5,
            Folders = 8, FoldersDone = 6, FoldersFailed = 1, DbBytes = 2048, AvailableFreeBytes = 4096
        };

        var metrics = ArchiveMetrics.Build(stats).ToDictionary(m => m.Key);

        var bodies = metrics["bodies"];
        Assert.Equal((60, 100L, 30, 10), (bodies.Done, bodies.Total!.Value, bodies.Remaining, bodies.Excluded));
        Assert.Equal(66, bodies.Percent); // 60 of the 90 readable
        var attachments = metrics["attachments"];
        Assert.Equal((10, 20L, 5, 5), (attachments.Done, attachments.Total!.Value, attachments.Remaining, attachments.Excluded));
        Assert.Equal(66, attachments.Percent);
        Assert.Equal((6, 1, 1), (metrics["folders"].Done, metrics["folders"].Remaining, metrics["folders"].Excluded));
        Assert.Equal("bytes", metrics["db"].Unit);
    }

    [Fact]
    public void Build_WithoutCardsOrUsage_LeavesThoseRowsOut()
    {
        var keys = ArchiveMetrics.Build(new ArchiveStats()).Select(m => m.Key).ToList();
        Assert.DoesNotContain("cards", keys);
        Assert.DoesNotContain("tokens", keys);
        Assert.Null(ArchiveMetrics.Build(new ArchiveStats()).Single(m => m.Key == "bodies").Percent);
        Assert.DoesNotContain("tokens", ArchiveMetrics.Build(new ArchiveStats()).Select(m => m.Key));
    }

    [Fact]
    public void Store_HasAFixedStateAndItsOwnFigures()
    {
        var done = new ArchiveStoreCoverage
        {
            DisplayName = "Archivio", Emails = 10, BodiesIndexed = 6, BodiesPending = 2, AttachmentsIndexed = 3, AttachmentsTotal = 4,
            Folders = 3, FoldersDone = 3, AbsenceVerified = true, LastInventoryUtc = DateTime.UtcNow
        };
        var metric = ArchiveMetrics.Store("", true, true, done);
        Assert.Equal("Archivio", metric.Name);
        Assert.Equal("verificata", metric.State);
        Assert.Equal((75, 2L), (metric.BodiesPercent!.Value, metric.BodiesRemaining));

        Assert.Equal("chiusa", ArchiveMetrics.Store("x", false, true, done).State);
        Assert.Equal("esclusa", ArchiveMetrics.Store("x", true, false, done).State);
        Assert.Equal("in attesa", ArchiveMetrics.Store("x", true, true, null).State);
        Assert.Equal("inventario", ArchiveMetrics.Store("x", true, true, new ArchiveStoreCoverage { Folders = 3, FoldersDone = 1 }).State);
        Assert.Equal("errore", ArchiveMetrics.Store("x", true, true, new ArchiveStoreCoverage { Error = "cartella non leggibile" }).State);
        Assert.Equal("cache da controllare", ArchiveMetrics.Store("x", true, true, new ArchiveStoreCoverage { Folders = 1, FoldersDone = 1 }).State);
        Assert.Equal("cache verificata", ArchiveMetrics.Store("x", true, true, new ArchiveStoreCoverage { Folders = 1, FoldersDone = 1, CacheChecked = true }).State);
    }

    [Fact]
    public void PhaseLabel_IsAFixedWord()
    {
        Assert.Equal("testi", ArchiveMetrics.PhaseLabel("bodies"));
        Assert.Equal("attesa", ArchiveMetrics.PhaseLabel("unknown"));
        Assert.Equal("attesa", ArchiveMetrics.PhaseLabel(null));
    }

    [Theory]
    [InlineData(28, 27, 1, 0, 100)]
    [InlineData(29, 27, 1, 1, 96)]
    [InlineData(1, 0, 1, 0, null)]
    [InlineData(0, 0, 0, 0, null)]
    public void FailedFolders_AreSeparateFromRemainingAndFromThePercentage(int total, int done, int failed, int remaining, int? percent)
    {
        var m = ArchiveMetrics.Build(new ArchiveStats { Folders = total, FoldersDone = done, FoldersFailed = failed })
            .Single(m => m.Key == "folders");
        Assert.Equal((done, total, remaining, failed, percent), (m.Done, m.Total, m.Remaining, m.Excluded, m.Percent));
        Assert.Equal(m.Total, m.Done + m.Remaining + m.Excluded);
    }

    [Theory]
    [InlineData("Synthetic\\Inbox")]
    [InlineData("Synthetic\\[Gmail]\\Cestino\\Prova")]
    [InlineData("")]
    public void FolderRecoveryAdvice_CoversDeletedAndTrashFoldersWithoutAssumingTheirState(string path)
    {
        var folder = new ArchiveFolder { Path = path };
        var message = folder.UnavailableMessage();
        Assert.Contains(string.IsNullOrEmpty(path) ? "nome non disponibile" : path, message);
        Assert.Contains("nessuna eliminazione dedotta", message);
        Assert.Contains("anche nel Cestino; ripristinala solo se necessario", message);
        Assert.Contains("senza ricrearla se eliminata", message);
        Assert.Contains("Lascia aperta la cassetta", message);
        Assert.DoesNotContain("Riaprila", message);
        Assert.Equal(message, folder.DescribeError(message));
        var persisted = $"Cartella «{path}» non disponibile; nessuna eliminazione dedotta. " +
            "Riaprila in Outlook e attendi il prossimo controllo completo.";
        var projected = folder.DescribeError(persisted);
        Assert.Contains("senza ricrearla se eliminata", projected);
        Assert.DoesNotContain("Riaprila", projected);
        Assert.Equal(projected, folder.DescribeError(projected));
    }


}
