using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Outlook.Interop;
using OnlyWorkspace.Outlook.Ribbon;
using OnlyWorkspace.Outlook.Services;
using OnlyWorkspace.Outlook.UI;
using MSOutlook = Microsoft.Office.Interop.Outlook;

namespace OnlyWorkspace.Outlook;

[ComVisible(true)]
[Guid("7F1B4E82-959D-4965-B6AA-243CE04B46E1")]
[ProgId("OnlyWorkspace.Addin")]
[ClassInterface(ClassInterfaceType.AutoDispatch)]
public class OnlyWorkspaceAddin : IDTExtensibility2, IRibbonExtensibility, ICustomTaskPaneConsumer
{
    static OnlyWorkspaceAddin()
    {
        AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            OnlyWorkspaceLog.Logger.Fatal(ex, "Unhandled AppDomain exception occurred. IsTerminating: {IsTerminating}", args.IsTerminating);
            OnlyWorkspaceLog.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            OnlyWorkspaceLog.Logger.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private static Assembly? CurrentDomain_AssemblyResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requestedName = new AssemblyName(args.Name);
            // COM add-ins share the default AppDomain: only reuse an already-loaded copy
            // (e.g. from another add-in) when it is at least the requested version.
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => string.Equals(a.GetName().Name, requestedName.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.GetName().Version)
                .FirstOrDefault();
            if (loaded != null && (requestedName.Version == null || loaded.GetName().Version >= requestedName.Version))
            {
                return loaded;
            }

            string addinDir = Path.GetDirectoryName(typeof(OnlyWorkspaceAddin).Assembly.Location) ?? string.Empty;
            if (!string.IsNullOrEmpty(addinDir))
            {
                string targetPath = Path.Combine(addinDir, requestedName.Name + ".dll");
                if (File.Exists(targetPath))
                {
                    return Assembly.LoadFrom(targetPath);
                }
            }
        }
        catch
        {
            // Ignore secondary resolution errors
        }
        return null;
    }

    private readonly SessionStart _sessionStart;
    private MSOutlook.Application? _outlookApp;
    private OutlookDataService? _dataService;
    private TaskPaneManager? _paneManager;
    private readonly List<object> _ribbonUis = new();
    private System.Windows.Forms.Timer? _ribbonStateTimer;

    public OnlyWorkspaceAddin() => _sessionStart = new SessionStart(StartSession);

    public void OnConnection(object application, ext_ConnectMode connectMode, object addInInst, ref Array custom)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        OnlyWorkspaceLog.Initialize();
        try
        {
            UiSync.Ensure();
            _outlookApp = application as MSOutlook.Application;
            if (_outlookApp != null)
            {
                _dataService = new OutlookDataService(_outlookApp);
                _paneManager = new TaskPaneManager(_outlookApp, _dataService);
                _onQuit = OnOutlookQuit;
                ((MSOutlook.ApplicationEvents_11_Event)_outlookApp).Quit += _onQuit;
                OnlyWorkspaceLog.Logger.Information("OnlyWorkspace Add-in connected to Outlook ({Mode}) in {Ms} ms", connectMode, sw.ElapsedMilliseconds);
            }
            _sessionStart.Connected(connectMode == ext_ConnectMode.ext_cm_AfterStartup);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "OnConnection failed");
        }
    }

    private MSOutlook.ApplicationEvents_11_QuitEventHandler? _onQuit;

    /// <summary>
    /// Fast shutdown (Outlook 2010+) skips OnBeginShutdown and OnDisconnection: Quit may be the only signal.
    /// Cancel managed work here without COM calls or I/O.
    /// </summary>
    private void OnOutlookQuit()
    {
        _ribbonStateTimer?.Stop();
        UiThreadWatch.Stop();
        _archiveIndexer?.Stop();
        _paneManager?.Stop();
        OnlyWorkspaceLog.Logger.Information("Outlook Quit event: shutdown started");
    }

    private ArchiveIndexer? _archiveIndexer;

    public void OnDisconnection(ext_DisconnectMode removeMode, ref Array custom)
    {
        try
        {
            _ribbonStateTimer?.Dispose();
            _ribbonStateTimer = null;
            foreach (var ribbon in _ribbonUis)
                if (Marshal.IsComObject(ribbon)) Marshal.ReleaseComObject(ribbon);
            _ribbonUis.Clear();
            _archiveIndexer?.Dispose();
            _archiveIndexer = null;
            _paneManager?.Dispose();
            _paneManager = null;
            _dataService = null;

            if (_outlookApp != null)
            {
                if (_onQuit != null) ((MSOutlook.ApplicationEvents_11_Event)_outlookApp).Quit -= _onQuit;
                _onQuit = null;
                Marshal.ReleaseComObject(_outlookApp);
                _outlookApp = null;
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Error while disconnecting add-in");
        }

        OnlyWorkspaceLog.Logger.Information("OnlyWorkspace Add-in disconnected from Outlook ({Mode})", removeMode);
        OnlyWorkspaceLog.CloseAndFlush();

        // No WaitForPendingFinalizers: blocking the STA during host shutdown can deadlock Outlook.
        GC.Collect();
    }

    public void OnAddInsUpdate(ref Array custom) { }

    public void OnStartupComplete(ref Array custom) => _sessionStart.StartupComplete();

    private void StartSession()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        UiThreadWatch.Start();
        // File-system only (no OOM): safe off the UI thread.
        System.Threading.Tasks.Task.Run(OutlookDataService.CleanupStaleScanDirectories);
        // The first configuration load in the process is slow: paid here on the pool, not by the first indexing tick.
        System.Threading.Tasks.Task.Run(OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService.WarmUp);

        // Mail archive: does nothing until the administrator enables it and the user activates it.
        try
        {
            if (_dataService != null)
            {
                UiSync.Ensure();
                _archiveIndexer = new ArchiveIndexer(_dataService);
                _archiveIndexer.Start();
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to start the archive indexer");
        }
        // Outlook disables an add-in whose startup is slow: the time spent here counts.
        OnlyWorkspaceLog.Logger.Information("Session started in {Ms} ms", sw.ElapsedMilliseconds);
    }

    public void OnBeginShutdown(ref Array custom) { }

    public string GetCustomUI(string ribbonId) => RibbonXml.Build(ribbonId);

    public void OnRibbonLoad(object ribbonUi)
    {
        if (!_ribbonUis.Contains(ribbonUi)) _ribbonUis.Add(ribbonUi);
        if (_ribbonStateTimer != null) return;
        // Invalidate only dynamic controls. Visibility callbacks read the window's lightweight selection on the STA.
        _ribbonStateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _ribbonStateTimer.Tick += (_, _) => { InvalidateClearChat(); InvalidateEmailActions(); };
        _ribbonStateTimer.Start();
    }

    private void InvalidateClearChat()
    {
        foreach (var ribbon in _ribbonUis)
        {
            try { ((dynamic)ribbon).InvalidateControl("btnNewChat"); }
            catch (Exception ex) { OnlyWorkspaceLog.Logger.Debug("Ribbon state update unavailable: {Kind}", ex.GetType().Name); }
        }
    }

    private static readonly string[] EmailActionRibbonIds =
    {
        "quickSummary", "quickSplitReply", "quickSecurity", "quickEmailHint",
        "btnSummary", "btnSplitReply", "btnSecurity", "emailMore", "emailSelectionHint"
    };

    private void InvalidateEmailActions()
    {
        foreach (var ribbon in _ribbonUis)
            foreach (var id in EmailActionRibbonIds)
                try { ((dynamic)ribbon).InvalidateControl(id); }
                catch (Exception ex) { OnlyWorkspaceLog.Logger.Debug("Ribbon email state unavailable: {Kind}", ex.GetType().Name); }
    }

    public bool GetCanActOnEmail(IRibbonControl? control)
    {
        object? window = null;
        try
        {
            UiSync.Ensure();
            window = WindowOf(control);
            return window != null && _dataService?.CanActOnSelectedEmail(window) == true;
        }
        catch { return false; }
        finally { if (window != null && Marshal.IsComObject(window)) Marshal.ReleaseComObject(window); }
    }

    public bool GetNeedsEmailSelection(IRibbonControl? control) => !GetCanActOnEmail(control);

    public bool GetCanClearChat(IRibbonControl control)
    {
        object? window = null;
        try
        {
            window = WindowOf(control);
            return _paneManager?.TryGetExistingTaskPane(window)?.Coordinator.CanClearChat == true;
        }
        catch { return false; }
        finally { if (window != null && Marshal.IsComObject(window)) Marshal.ReleaseComObject(window); }
    }

    /// <summary>customUI loadImage: the app icon for the controls that name it (image="ow16" / "ow32").</summary>
    public object? LoadImage(string imageId)
    {
        try
        {
            return RibbonImages.Load(imageId);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Ribbon image {ImageId} not loaded", imageId);
            return null;
        }
    }

    public void CTPFactoryAvailable(ICTPFactory ctpFactoryInst)
    {
        _paneManager?.SetCTPFactory(ctpFactoryInst);
    }

    /// <summary>Common path of the ribbon callbacks: finds (or creates) the window's pane and runs the action on it.</summary>
    private void Run(IRibbonControl? control, Action<TaskPaneEntry> action, bool show = true, [CallerMemberName] string callback = "")
        => Run(control, (entry, _) => action(entry), show, callback);

    /// <summary>As Run; the action learns whether the pane was already on screen (a second click then closes its screen).</summary>
    private void Run(IRibbonControl? control, Action<TaskPaneEntry, bool> action, bool show = true, [CallerMemberName] string callback = "")
    {
        var callbackWatch = System.Diagnostics.Stopwatch.StartNew();
        long initialBeatAge = UiThreadWatch.BeatAgeMs;
        using var activity = UiThreadWatch.Activity("ribbon " + callback);
        try
        {
            OnlyWorkspaceLog.Logger.Information("Ribbon callback: {Callback}", callback);
            UiSync.Ensure();

            var phase = System.Diagnostics.Stopwatch.StartNew();
            var window = WindowOf(control);
            OnlyWorkspaceLog.Logger.Information("Ribbon phase {Callback}: window context {ElapsedMs} ms", callback, phase.ElapsedMilliseconds);
            phase.Restart();
            var entry = _paneManager?.GetOrCreateTaskPane(window);
            OnlyWorkspaceLog.Logger.Information("Ribbon phase {Callback}: get pane {ElapsedMs} ms", callback, phase.ElapsedMilliseconds);
            if (entry == null)
            {
                OnlyWorkspaceLog.Logger.Warning("Ribbon callback {Callback}: no task pane for the window", callback);
                return;
            }

            // A hidden pane skips the selection updates: refresh it when it comes back.
            bool wasVisible = entry.TaskPane.Visible;
            if (show && !wasVisible)
            {
                var visibilityWatch = System.Diagnostics.Stopwatch.StartNew();
                entry.TaskPane.Visible = true;
                OnlyWorkspaceLog.Logger.Information("Pane creation: show {ElapsedMs} ms", visibilityWatch.ElapsedMilliseconds);
                entry.ScheduleContextUpdate();
            }
            if (show) entry.Control.RequestContentFocus();
            phase.Restart();
            action(entry, wasVisible);
            OnlyWorkspaceLog.Logger.Information("Ribbon phase {Callback}: action {ElapsedMs} ms", callback, phase.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Ribbon callback {Callback} failed", callback);
        }
        finally
        {
            OnlyWorkspaceLog.Logger.Information("Ribbon callback {Callback}: total {ElapsedMs} ms; heartbeat age on entry {InitialBeatAgeMs} ms, on exit {FinalBeatAgeMs} ms",
                callback, callbackWatch.ElapsedMilliseconds, initialBeatAge, UiThreadWatch.BeatAgeMs);
        }
    }

    /// <summary>Explorer or Inspector of the control; null (= active window) for a context menu, whose Context is a Selection.</summary>
    private static object? WindowOf(IRibbonControl? control)
    {
        object? context = null;
        try
        {
            context = control?.Context;
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to retrieve context from IRibbonControl, defaulting to active window");
        }

        if (context == null || context is MSOutlook.Explorer || context is MSOutlook.Inspector) return context;
        Marshal.ReleaseComObject(context);
        return null;
    }

    // Ribbon callbacks: Office calls them by the name written in RibbonXml.

    private void RunEmailAction(IRibbonControl control, Action<TaskPaneEntry> action, [CallerMemberName] string callback = "")
    {
        // Recheck at click time too: Office may still have the previous getVisible value cached.
        if (GetCanActOnEmail(control)) Run(control, action, callback: callback);
    }

    public void OnSummaryClicked(IRibbonControl control) => RunEmailAction(control, e => _ = e.Coordinator.ExecuteSummaryAsync());

    public void OnReplyAcceptClicked(IRibbonControl control) => RunEmailAction(control, e => _ = e.Coordinator.ExecuteReplyAsync("accept"));

    public void OnReplyDeclineClicked(IRibbonControl control) => RunEmailAction(control, e => _ = e.Coordinator.ExecuteReplyAsync("decline"));

    public void OnReplyCustomClicked(IRibbonControl control) => RunEmailAction(control, e => e.Coordinator.ShowCustomInstructionInput("reply"));

    public void OnSecurityClicked(IRibbonControl control) => RunEmailAction(control, e => _ = e.Coordinator.ExecuteSecurityCheckAsync());

    public void OnActionItemsClicked(IRibbonControl control) => RunEmailAction(control, e => _ = e.Coordinator.ExecuteActionItemsAsync());

    public void OnExtractDataClicked(IRibbonControl control) => RunEmailAction(control, e => _ = e.Coordinator.ExecuteDataExtractionAsync());

    public void OnBriefingClicked(IRibbonControl control) => Run(control, e => _ = e.Coordinator.ExecuteBriefingAsync());

    public void OnAnalysisPresetClicked(IRibbonControl control) => Run(control, (e, toggle) => e.Coordinator.ShowAnalysisDrawer(control.Tag ?? "summary", toggle));

    public void OnAnalysisClicked(IRibbonControl control) => Run(control, (e, toggle) => e.Coordinator.ShowAnalysisDrawer("summary", toggle));

    public void OnWriteClicked(IRibbonControl control) => Run(control, e => e.Coordinator.ShowWriteInput());

    public void OnImproveClicked(IRibbonControl control) => Run(control, e => _ = e.Coordinator.ExecuteImproveAsync(control.Tag ?? "correct"));

    public void OnTranslateClicked(IRibbonControl control) => Run(control, e => _ = e.Coordinator.ExecuteTranslateAsync(control.Tag ?? "en"));

    public void OnSubjectClicked(IRibbonControl control) => Run(control, e => _ = e.Coordinator.ExecuteSubjectAsync());

    public void OnChatClicked(IRibbonControl control) => Run(control, e => e.Coordinator.ShowCustomInstructionInput());

    public void OnNewChatClicked(IRibbonControl control) => Run(control, e => e.Coordinator.ClearChat());

    public void OnHistoryClicked(IRibbonControl control) => Run(control, (e, toggle) => e.Coordinator.ShowHistoryDrawer(toggle));

    public void OnCancelClicked(IRibbonControl control)
    {
        // No pane in this window = nothing running here: do not create one.
        OnlyWorkspaceLog.Logger.Information("Ribbon callback: {Callback}", nameof(OnCancelClicked));
        try
        {
            UiSync.Ensure();
            // The work of this window, or else the work running in the panes of the other windows.
            var pane = _paneManager?.TryGetExistingTaskPane(WindowOf(control));
            if (pane != null) pane.Coordinator.StopFromRibbon();
            else PaneJobs.StopOthers(0);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Ribbon callback {Callback} failed", nameof(OnCancelClicked));
        }
    }

    public void OnArchiveInfoClicked(IRibbonControl control) => Run(control, e => e.Coordinator.ShowInfoDrawer(section: "archive"));

    public void OnInfoClicked(IRibbonControl control) => Run(control, (e, toggle) => e.Coordinator.ShowInfoDrawer(toggle));

    public void OnTogglePaneClicked(IRibbonControl control) => Run(control, e =>
    {
        e.Coordinator.HideEmailContext();
        e.TaskPane.Visible = !e.TaskPane.Visible;
    }, show: false);

    public void OnConfigClicked(IRibbonControl control)
    {
        OnlyWorkspaceLog.Logger.Information("Ribbon callback: {Callback}", nameof(OnConfigClicked));
        try
        {
            UiSync.Ensure();
            _paneManager?.TryGetExistingTaskPane(WindowOf(control))?.Coordinator.HideEmailContext();
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Could not hide email context before opening configuration");
        }
        if (ConfigToolLauncher.TryLaunch()) return; // never throws, logs its own failures
        var pane = _paneManager?.TryGetExistingTaskPane(WindowOf(control));
        if (pane != null && pane.TaskPane.Visible) pane.Coordinator.ShowConfigUnavailable();
        else System.Windows.Forms.MessageBox.Show(TaskPaneCoordinator.ConfigUnavailableText, "OnlyWorkspace",
            System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Context menu of an email: control.Tag is the action, control.Context the Selection under the pointer,
    /// which is not always the Explorer's selection.
    /// </summary>
    public void OnContextActionClicked(IRibbonControl control) => Run(control, entry =>
    {
        string? entryId = null, storeId = null;
        if (control.Context is MSOutlook.Selection selection)
        {
            try
            {
                _dataService?.TryGetFirstMailId(selection, out entryId, out storeId);
            }
            finally
            {
                Marshal.ReleaseComObject(selection);
            }
        }
        entry.Coordinator.ExecuteOnEmail(control.Tag, entryId, storeId);
    });
}
