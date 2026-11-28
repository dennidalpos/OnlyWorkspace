using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Tests;

/// <summary>A138: only a failure of the extended columns alone is remembered for the store.</summary>
public sealed class ArchiveColumnProbeTests
{
    [Fact]
    public void ExtendedColumnsRead_AreKept()
    {
        Assert.True(ArchiveColumnProbe.Probe(() => { }, () => throw new InvalidOperationException("not reached"), out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void ExtendedColumnsRejected_WhileTheBasicOnesRead_AreDropped()
    {
        var rejected = new InvalidOperationException("synthetic IMAP column failure");
        Assert.False(ArchiveColumnProbe.Probe(() => throw rejected, () => { }, out var failure));
        Assert.Same(rejected, failure);
    }

    [Fact]
    public void StoreClosing_GivesNoVerdict()
    {
        var closed = new ArchiveStoreClosedException();
        Assert.Null(ArchiveColumnProbe.Probe(() => throw closed, () => throw new ArchiveStoreClosedException(), out var failure));
        Assert.Same(closed, failure);
    }
}
