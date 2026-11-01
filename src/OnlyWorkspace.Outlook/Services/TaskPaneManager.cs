using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Outlook.Interop;
using OnlyWorkspace.Outlook.UI;
using MSOutlook = Microsoft.Office.Interop.Outlook;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace OnlyWorkspace.Outlook.Services;

public sealed class TaskPaneEntry : IDisposable
{
    private const int SelectionDebounceMs = 250;
    private const int ConfigCheckMs = 2000;

    private readonly WinFormsTimer _selectionDebounce;
    private readonly WinFormsTimer _configCheck;
    private const int WidthCheckMs = 300;
    private const int MaxWidthAttempts = 4;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;

    private readonly int _minimumWidth;
    private readonly WinFormsTimer _widthCheck;
    private bool _applyingWidth;
    private int _widthAttempts;
    private bool _isDisposed;
    private bool _stopping;
    private bool _measuringWidth;

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    private async void RecordWidthMetrics()
    {
        if (_measuringWidth || _isDisposed || _stopping || !Control.IsHandleCreated) return;
        _measuringWidth = true;
        try
        {
            int width = TaskPane.Width;
            uint dpi = GetDpiForWindow(Control.Handle);
            int awareness = GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(Control.Handle));
            int nativeWidth = Control.ClientSize.Width, nativeHeight = Control.ClientSize.Height;
            string display = System.Windows.Forms.Screen.FromControl(Control).DeviceName;
            string? metrics = await Control.ReadLayoutMetricsAsync();
            if (!_isDisposed && !_stopping)
                OnlyWorkspaceLog.Logger.Information("Pane layout: Office width {OfficeWidth}; minimum {Minimum}; native client {NativeWidth}x{NativeHeight}; window DPI {Dpi}; awareness {Awareness}; display {Display}; CSS {Metrics}",
                    width, _minimumWidth, nativeWidth, nativeHeight, dpi, awareness, display, metrics ?? "not-ready");
        }
        catch (Exception ex) { OnlyWorkspaceLog.Logger.Debug("Pane layout measurement unavailable: {Kind}", ex.GetType().Name); }
        finally { _measuringWidth = false; }
    }

    public object Window { get; }
    public _CustomTaskPane TaskPane { get; }
    public TaskPaneControl Control { get; }
    public TaskPaneCoordinator Coordinator { get; }

    public TaskPaneEntry(object window, _CustomTaskPane taskPane, TaskPaneControl control, TaskPaneCoordinator coordinator, int minimumWidth = 0)
    {
        Window = window;
        TaskPane = taskPane;
        Control = control;
        Coordinator = coordinator;
        _minimumWidth = minimumWidth;
        _widthCheck = new WinFormsTimer { Interval = WidthCheckMs };
        _widthCheck.Tick += OnWidthCheckTick;
        Control.SizeChanged += KeepMinimumWidth;

        _selectionDebounce = new WinFormsTimer { Interval = SelectionDebounceMs };
        _selectionDebounce.Tick += OnSelectionDebounceTick;

        // A ConfigTool save reaches an open pane without waiting for the user to click something.
        _configCheck = new WinFormsTimer { Interval = ConfigCheckMs };
        _configCheck.Tick += OnConfigCheckTick;
        _configCheck.Start();
    }

    private void OnConfigCheckTick(object? sender, EventArgs e)
    {
        if (_isDisposed || _stopping) return;
        try
        {
            _ = Coordinator.CheckDeletedHistoryAsync();
            if (TaskPane.Visible) Coordinator.ReloadIfConfigChanged();
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Configuration check failed");
        }
    }

    /// <summary>
    /// Office owns the splitter drag: writing the width from inside it makes Office resize again at once (a loop of
    /// 540 ↔ 392 units several times a second, a flickering and frozen Outlook). The width is restored only after the
    /// pane has been still for a moment and no mouse button is down, with a few attempts per resize.
    /// </summary>
    private void KeepMinimumWidth(object? sender, EventArgs e)
    {
        if (_isDisposed || _stopping || _applyingWidth || _minimumWidth <= 0) return;
        _widthAttempts = 0;
        _widthCheck.Stop();
        _widthCheck.Start();
    }

    private static bool MouseButtonDown() => (GetAsyncKeyState(VkLButton) & 0x8000) != 0 || (GetAsyncKeyState(VkRButton) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private void OnWidthCheckTick(object? sender, EventArgs e)
    {
        if (_isDisposed || _stopping) { _widthCheck.Stop(); return; }
        if (_applyingWidth || MouseButtonDown()) return;
        try
        {
            _applyingWidth = true;
            if (!TaskPane.Visible) { _widthCheck.Stop(); return; }
            int width = TaskPane.Width;
            if (width >= _minimumWidth) { _widthCheck.Stop(); RecordWidthMetrics(); return; }
            if (++_widthAttempts > MaxWidthAttempts) { _widthCheck.Stop(); RecordWidthMetrics(); return; }
            TaskPane.Width = _minimumWidth;
            bool applied = TaskPane.Width >= _minimumWidth;
            OnlyWorkspaceLog.Logger.Information("TaskPane minimum width attempt {Attempt}: from {Width} to {Minimum} Office units; accepted {Accepted}; viewport {Viewport}px; display {Display}",
                _widthAttempts, width, _minimumWidth, applied, Control.ClientSize.Width, System.Windows.Forms.Screen.FromControl(Control).DeviceName);
            if (applied) { _widthCheck.Stop(); RecordWidthMetrics(); }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Minimum pane width not applied");
            _widthCheck.Stop();
        }
        finally { _applyingWidth = false; }
    }

    /// <summary>
    /// Coalesces bursts of SelectionChange events (e.g. arrow-key scrolling) into a single context refresh.
    /// </summary>
    public void ScheduleContextUpdate()
    {
        if (_isDisposed || _stopping) return;
        _selectionDebounce.Stop();
        _selectionDebounce.Start();
    }

    private void OnSelectionDebounceTick(object? sender, EventArgs e)
    {
        _selectionDebounce.Stop();
        if (_isDisposed || _stopping) return;

        try
        {
            if (TaskPane.Visible)
            {
                Coordinator.UpdateContext();
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Debounced context update failed");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            Control.SizeChanged -= KeepMinimumWidth;
            _widthCheck.Stop();
            _widthCheck.Tick -= OnWidthCheckTick;
            _widthCheck.Dispose();
            _selectionDebounce.Stop();
            _selectionDebounce.Tick -= OnSelectionDebounceTick;
            _selectionDebounce.Dispose();
            _configCheck.Stop();
            _configCheck.Tick -= OnConfigCheckTick;
            _configCheck.Dispose();
        }
        catch { }

        try
        {
            Coordinator.Dispose();
        }
        catch { }

        try
        {
            TaskPane.Visible = false;
            TaskPane.Delete();
        }
        catch { }

        try
        {
            Control.Dispose();
        }
        catch { }

        try
        {
            Marshal.ReleaseComObject(TaskPane);
        }
        catch { }
    }

    public void Stop()
    {
        _stopping = true;
        _widthCheck.Stop();
        _selectionDebounce.Stop();
        _configCheck.Stop();
        Coordinator.Dispose();
    }
}

public sealed class TaskPaneManager : IDisposable
{
    private readonly MSOutlook.Application _application;
    private readonly OutlookDataService _dataService;
    private ICTPFactory? _ctpFactory;
    private readonly Dictionary<object, TaskPaneEntry> _entries = new();
    private readonly Dictionary<object, Action> _windowUnsubscribers = new();
    private bool _stopping;

    /// <summary>Quit can be the only shutdown event; cancel managed work without touching Outlook COM.</summary>
    public void Stop()
    {
        _stopping = true;
        foreach (var entry in _entries.Values) entry.Stop();
    }

    public TaskPaneManager(MSOutlook.Application application, OutlookDataService dataService)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
    }

    public void SetCTPFactory(ICTPFactory ctpFactory)
    {
        _ctpFactory = ctpFactory;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    /// <summary>
    /// System DPI (primary monitor at sign-in). Office takes the pane width in system-DPI pixels and rescales
    /// it for the monitor the window is on: with the window's own DPI the pane came out 250 px wide on a
    /// 100% screen next to a 150% primary. Read in a system-aware context: the hosted control is
    /// DPI-virtualized and would report 96.
    /// </summary>
    private static int SystemDpi()
    {
        try
        {
            var previous = SetThreadDpiAwarenessContext(new IntPtr(-2)); // DPI_AWARENESS_CONTEXT_SYSTEM_AWARE
            try
            {
                return (int)Math.Max(96, GetDpiForSystem());
            }
            finally
            {
                if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
            }
        }
        catch (Exception)
        {
            return 96; // needs Windows 10 1607+
        }
    }

    public TaskPaneEntry? TryGetExistingTaskPane(object? window = null)
    {
        if (_stopping) return null;
        var targetWindow = window ?? _application.ActiveExplorer() ?? (object?)_application.ActiveInspector();
        return targetWindow != null && _entries.TryGetValue(targetWindow, out var entry) ? entry : null;
    }

    public TaskPaneEntry? GetOrCreateTaskPane(object? window = null)
    {
        if (_stopping) return null;
        if (_ctpFactory == null)
        {
            OnlyWorkspaceLog.Logger.Warning("Cannot create TaskPane: _ctpFactory is null");
            return null;
        }

        var targetWindow = window ?? _application.ActiveExplorer() ?? (object?)_application.ActiveInspector();
        if (targetWindow == null)
        {
            OnlyWorkspaceLog.Logger.Warning("Cannot create TaskPane: targetWindow is null");
            return null;
        }

        if (_entries.TryGetValue(targetWindow, out var existing))
        {
            if (!existing.TaskPane.Visible)
            {
                existing.Coordinator.ReloadConfiguration();
            }
            else
            {
                existing.Coordinator.ReloadIfConfigChanged();
            }
            return existing;
        }

        _CustomTaskPane? taskPane = null;
        try
        {
            OnlyWorkspaceLog.Logger.Information("Creating CustomTaskPane via ICTPFactory for window: {Window}", targetWindow);
            var phase = Stopwatch.StartNew();
            taskPane = _ctpFactory.CreateCTP("OnlyWorkspace.TaskPaneControl", "OnlyWorkspace", targetWindow);
            OnlyWorkspaceLog.Logger.Information("Pane creation: CreateCTP {ElapsedMs} ms (includes control construction)", phase.ElapsedMilliseconds);
            if (taskPane == null)
            {
                OnlyWorkspaceLog.Logger.Error("ICTPFactory.CreateCTP returned null");
                return null;
            }

            taskPane.DockPosition = MsoCTPDockPosition.msoCTPDockPositionRight;

            var content = taskPane.ContentControl;
            if (content is not TaskPaneControl control)
            {
                OnlyWorkspaceLog.Logger.Error("taskPane.ContentControl is not TaskPaneControl. Actual type: {Type}", content?.GetType().FullName ?? "null");
                return null;
            }

            // 380 px at 100%, in system-DPI pixels (see SystemDpi). Never narrower than 360 px (TaskPaneEntry.KeepMinimumWidth):
            // with the pane's frame and scroll bar the page still gets the 340 px of its one-row layout.
            int dpi = SystemDpi();
            taskPane.Width = 380 * dpi / 96;
            int minimumWidth = 360 * dpi / 96;
            OnlyWorkspaceLog.Logger.Information("TaskPane width {Width} Office units at system DPI {Dpi}", taskPane.Width, dpi);

            phase.Restart();
            var coordinator = new TaskPaneCoordinator(control, _dataService, targetWindow);
            OnlyWorkspaceLog.Logger.Information("Pane creation: coordinator {ElapsedMs} ms", phase.ElapsedMilliseconds);
            var entry = new TaskPaneEntry(targetWindow, taskPane, control, coordinator, minimumWidth);
            _entries[targetWindow] = entry;
            taskPane = null; // ownership transferred to entry

            TrackWindow(targetWindow);

            OnlyWorkspaceLog.Logger.Information("CustomTaskPane created and tracked successfully");
            return entry;
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to create CustomTaskPane for window");
            return null;
        }
        finally
        {
            // A pane that failed half-way must not stay orphaned inside Outlook with a live WebView2.
            if (taskPane != null)
            {
                DeleteOrphanPane(taskPane);
            }
        }
    }

    private static void DeleteOrphanPane(_CustomTaskPane taskPane)
    {
        try
        {
            taskPane.Visible = false;
            taskPane.Delete();
        }
        catch { }

        try
        {
            Marshal.ReleaseComObject(taskPane);
        }
        catch { }
    }

    private void TrackWindow(object window)
    {
        if (_windowUnsubscribers.ContainsKey(window)) return;

        try
        {
            if (window is MSOutlook.Explorer explorer)
            {
                var events = (MSOutlook.ExplorerEvents_10_Event)explorer;
                MSOutlook.ExplorerEvents_10_SelectionChangeEventHandler onSelection = () => OnSelectionChange(explorer);
                MSOutlook.ExplorerEvents_10_CloseEventHandler onClose = () => CloseWindowEntry(explorer);
                // A reply written in the reading pane changes no selection: the pane must switch to the writing actions anyway.
                MSOutlook.ExplorerEvents_10_InlineResponseEventHandler onInline = item =>
                {
                    if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                    OnSelectionChange(explorer);
                };
                MSOutlook.ExplorerEvents_10_InlineResponseCloseEventHandler onInlineClose = () => OnSelectionChange(explorer);
                events.SelectionChange += onSelection;
                events.Close += onClose;
                events.InlineResponse += onInline;
                events.InlineResponseClose += onInlineClose;
                _windowUnsubscribers[window] = () =>
                {
                    events.SelectionChange -= onSelection;
                    events.Close -= onClose;
                    events.InlineResponse -= onInline;
                    events.InlineResponseClose -= onInlineClose;
                };
            }
            else if (window is MSOutlook.Inspector inspector)
            {
                var events = (MSOutlook.InspectorEvents_10_Event)inspector;
                MSOutlook.InspectorEvents_10_CloseEventHandler onClose = () => CloseWindowEntry(inspector);
                events.Close += onClose;
                _windowUnsubscribers[window] = () => events.Close -= onClose;
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to attach events to Outlook window");
        }
    }

    private void UntrackWindow(object window)
    {
        if (_windowUnsubscribers.TryGetValue(window, out var unsubscribe))
        {
            _windowUnsubscribers.Remove(window);
            try
            {
                unsubscribe();
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Debug(ex, "Failed to detach events from Outlook window");
            }
        }
    }

    private void OnSelectionChange(object explorer)
    {
        if (_stopping) return;
        try
        {
            if (_entries.TryGetValue(explorer, out var entry))
            {
                entry.ScheduleContextUpdate();
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "SelectionChange handler failed");
        }
    }

    private void CloseWindowEntry(object window)
    {
        // The window RCW is shared (dictionary key, coordinator context, ActiveExplorer());
        // it must NOT be released here, otherwise later callers hit a detached RCW.
        try
        {
            UntrackWindow(window);

            if (_entries.TryGetValue(window, out var entry))
            {
                _entries.Remove(window);
                entry.Dispose();
                OnlyWorkspaceLog.Logger.Information("TaskPane disposed on window close");
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to clean up TaskPane on window close");
        }
    }

    public void Dispose()
    {
        foreach (var window in new List<object>(_windowUnsubscribers.Keys))
        {
            UntrackWindow(window);
        }

        foreach (var kvp in _entries)
        {
            kvp.Value.Dispose();
        }
        _entries.Clear();

        if (_ctpFactory != null)
        {
            try
            {
                Marshal.ReleaseComObject(_ctpFactory);
            }
            catch { }
            _ctpFactory = null;
        }
    }
}
