using OnlyWorkspace.Outlook;

namespace OnlyWorkspace.Tests;

public class SessionStartTests
{
    [Fact]
    public void ConnectedAtStartup_StartsOnStartupComplete()
    {
        int starts = 0;
        var session = new SessionStart(() => starts++);

        session.Connected(afterStartup: false);
        Assert.Equal(0, starts);
        session.StartupComplete();
        Assert.Equal(1, starts);
    }

    [Fact]
    public void ConnectedToRunningOutlook_StartsAtOnce()
    {
        // COM add-ins dialog or re-enabled after Outlook disabled it: OnStartupComplete never arrives.
        int starts = 0;
        var session = new SessionStart(() => starts++);

        session.Connected(afterStartup: true);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void StartRunsOnce()
    {
        int starts = 0;
        var session = new SessionStart(() => starts++);

        session.Connected(afterStartup: true);
        session.StartupComplete();
        session.StartupComplete();
        Assert.Equal(1, starts);
    }
}
