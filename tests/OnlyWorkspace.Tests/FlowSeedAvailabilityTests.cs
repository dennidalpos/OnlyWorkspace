using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>Ricostruisci pratica refuses a missing or moved starting email without the pane (#137).</summary>
public sealed class FlowSeedAvailabilityTests
{
    private static ArchiveFlowSeed Seed() => new() { EntryId = "e1", StoreId = "s1" };

    [Fact]
    public void NoSeed_AsksForASelection()
    {
        Assert.Equal(FlowSeedAvailability.NoSelection, FlowSeedAvailability.BeforeRead(null));
        Assert.Null(FlowSeedAvailability.BeforeRead(Seed()));
    }

    [Fact]
    public void SeedNotInTimeline_IsGone()
    {
        var flow = new ArchiveFlow();
        flow.Timeline.Add(new ArchiveFlowMail { EntryId = "e2", StoreId = "s1" });
        flow.Timeline.Add(new ArchiveFlowMail { EntryId = "e1", StoreId = "s2" });
        Assert.Equal(FlowSeedAvailability.SeedGone, FlowSeedAvailability.AfterRead(Seed(), false, flow));
        Assert.Equal(FlowSeedAvailability.SeedGone, FlowSeedAvailability.AfterRead(Seed(), false, new ArchiveFlow()));
    }

    [Fact]
    public void SeedInTimelineOrCopies_IsAvailable()
    {
        var direct = new ArchiveFlow();
        direct.Timeline.Add(new ArchiveFlowMail { EntryId = "e1", StoreId = "s1" });
        Assert.Null(FlowSeedAvailability.AfterRead(Seed(), false, direct));

        var copy = new ArchiveFlow();
        var mail = new ArchiveFlowMail { EntryId = "other", StoreId = "s9" };
        mail.Copies.Add(new ArchiveEmailRef { EntryId = "e1", StoreId = "s1" });
        copy.Timeline.Add(mail);
        Assert.Null(FlowSeedAvailability.AfterRead(Seed(), false, copy));
    }

    [Fact]
    public void SeedOpenInOutlook_IsAvailableEvenIfNotArchived() =>
        Assert.Null(FlowSeedAvailability.AfterRead(Seed(), true, new ArchiveFlow()));
}
