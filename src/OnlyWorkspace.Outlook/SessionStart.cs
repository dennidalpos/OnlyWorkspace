using System;

namespace OnlyWorkspace.Outlook;

/// <summary>
/// Runs the session start (archive indexer, session profile reset) once per connection: from OnStartupComplete when
/// Outlook loads the add-in at startup, or from OnConnection when it is connected to an Outlook already running
/// (COM add-ins dialog, re-enabled after being disabled), since Outlook then never calls OnStartupComplete.
/// </summary>
internal sealed class SessionStart
{
    private readonly Action _start;
    private bool _done;

    public SessionStart(Action start) => _start = start;

    public void Connected(bool afterStartup)
    {
        if (afterStartup) Run();
    }

    public void StartupComplete() => Run();

    private void Run()
    {
        if (_done) return;
        _done = true;
        _start();
    }
}
