using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace OnlyWorkspace.ConfigTool.Services;

public sealed class SingleInstanceManager : IDisposable
{
    // scope isolates instances (default: current user; tests pass a unique value).
    private static string MutexName(string? scope) => $"Local\\OnlyWorkspace_ConfigTool_Mutex_{scope ?? Environment.UserName}";
    private static string EventName(string? scope) => $"Local\\OnlyWorkspace_ConfigTool_ShowEvent_{scope ?? Environment.UserName}";
    private static string MailboxesEventName(string? scope) => $"Local\\OnlyWorkspace_ConfigTool_ShowMailboxesEvent_{scope ?? Environment.UserName}";

    /// <summary>Page argument of the window request: Archivio email → Le mie cassette.</summary>
    public const string MailboxesPage = "mailboxes";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _mailboxesEvent;
    private Thread? _listenerThread;
    private volatile bool _disposed;
    private readonly object _signalGate = new();
    private Action? _showWindowRequested;
    private bool _pendingShow;
    private string? _requestedPage;

    /// <summary>Page asked with the last window request, once; null for the default page.</summary>
    public string? TakeRequestedPage()
    {
        lock (_signalGate)
        {
            string? page = _requestedPage;
            _requestedPage = null;
            return page;
        }
    }

    public event Action? ShowWindowRequested
    {
        add
        {
            bool pending;
            lock (_signalGate)
            {
                _showWindowRequested += value;
                pending = _pendingShow;
                _pendingShow = false;
            }
            if (pending) value?.Invoke();
        }
        remove { lock (_signalGate) _showWindowRequested -= value; }
    }

    private SingleInstanceManager(Mutex mutex, EventWaitHandle showEvent, EventWaitHandle mailboxesEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _mailboxesEvent = mailboxesEvent;

        _listenerThread = new Thread(ListenForSignals)
        {
            IsBackground = true,
            Name = "OnlyWorkspace_SingleInstanceListener"
        };
        _listenerThread.Start();
    }

    private void ListenForSignals()
    {
        while (!_disposed && _showEvent != null && _mailboxesEvent != null)
        {
            try
            {
                int signaled = WaitHandle.WaitAny(new WaitHandle[] { _showEvent, _mailboxesEvent });
                if (_disposed) break;
                Action? handler;
                lock (_signalGate)
                {
                    if (signaled == 1) _requestedPage = MailboxesPage;
                    handler = _showWindowRequested;
                    if (handler == null) _pendingShow = true;
                }
                handler?.Invoke();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // Continue listening
            }
        }
    }

    public static bool TryStartPrimary(out SingleInstanceManager? manager, string? scope = null)
    {
        manager = null;
        try
        {
            var mutex = new Mutex(true, MutexName(scope), out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(scope));
            var mailboxesEvent = new EventWaitHandle(false, EventResetMode.AutoReset, MailboxesEventName(scope));
            manager = new SingleInstanceManager(mutex, showEvent, mailboxesEvent);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Asks the running instance to show its window, on <paramref name="page"/> when given.</summary>
    public static bool SignalPrimaryInstance(string? scope = null, string? page = null)
    {
        try
        {
            string name = page == MailboxesPage ? MailboxesEventName(scope) : EventName(scope);
            if (EventWaitHandle.TryOpenExisting(name, out var showEvent))
            {
                using (showEvent)
                {
                    AllowPrimaryToForeground();
                    showEvent.Set();
                    return true;
                }
            }
        }
        catch
        {
            // Ignore error if signal fails
        }
        return false;
    }

    /// <summary>
    /// The instance started from the ribbon may take the foreground, the tray process may not: without handing the
    /// right over, the tray's Activate only flashes the taskbar button and the window stays behind Outlook.
    /// </summary>
    private static void AllowPrimaryToForeground()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(self.ProcessName))
            {
                using (other)
                {
                    if (other.Id != self.Id && other.SessionId == self.SessionId) AllowSetForegroundWindow(other.Id);
                }
            }
        }
        catch
        {
            // Best effort: the window still opens, behind the active one.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _showEvent?.Set();
            _showEvent?.Dispose();
            _showEvent = null;
            _mailboxesEvent?.Dispose();
            _mailboxesEvent = null;
        }
        catch { }

        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;
        }
        catch { }
    }
}
