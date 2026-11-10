using System;
using System.Threading;
using OnlyWorkspace.AttachmentExtractor;
using Xunit;

namespace OnlyWorkspace.Tests;

/// <summary>The extractor's RTF thread (A135): its failure reaches the caller instead of ending the process.</summary>
public sealed class StaThreadTests
{
    [Fact]
    public void Run_ReturnsTheResult_OnAnStaThread()
    {
        int caller = Environment.CurrentManagedThreadId;
        var (apartment, thread) = StaThread.Run(() => (Thread.CurrentThread.GetApartmentState(), Environment.CurrentManagedThreadId));

        Assert.Equal(ApartmentState.STA, apartment);
        Assert.NotEqual(caller, thread);
    }

    [Fact]
    public void Run_RethrowsTheWorkFailure_ToTheCaller()
    {
        var error = Assert.Throws<ArgumentException>(() => StaThread.Run<string>(() => throw new ArgumentException("synthetic RTF failure")));

        Assert.Equal("synthetic RTF failure", error.Message);
        Assert.Contains(nameof(Run_RethrowsTheWorkFailure_ToTheCaller), error.StackTrace);
    }
}
