using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.Outlook.UI;

[ComVisible(true)]
[Guid("C9EFA167-93C5-4309-8EE3-92576F0E0BF7")]
[ProgId("OnlyWorkspace.TaskPaneControl")]
public class TaskPaneControl : UserControl
{
    private static Task<CoreWebView2Environment>? _sharedEnvironmentTask;
    private static readonly object _envLock = new();
    private static bool _loaderPathSet;
    private readonly Func<Task<CoreWebView2Environment>> _getEnvironment;

    private WebView2? _webView;
    private CoreWebView2? _core;
    private CoreWebView2Environment? _environment;
    private Control? _errorPanel;
    private int _generation;
    private bool _browserRecoveryPending;
    private bool _initStarted;
    private bool _isInitialized;
    private bool _pageReady;
    private bool _contentFocusPending;
    private readonly EmbeddedPageBoundary _pageBoundary = new();
    private const int MaxPendingMessages = 2000;
    private readonly Queue<string> _pendingMessages = new();
    // Thread that created the control: before its handle exists, only that thread may queue.
    private readonly int _ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;

    public event Action<JsonElement>? MessageReceived;
    public event Action? ControlReady;

    /// <summary>An explicit ribbon action enters the native WebView before the page focuses its field or heading.</summary>
    internal void RequestContentFocus()
    {
        if (IsDisposed || Disposing || !IsHandleCreated || !Visible) return;
        if (_pageReady && _webView != null)
        {
            _contentFocusPending = false;
            _webView.Focus();
        }
        else
        {
            // Keep the request only while this pane still owns focus: initialization must not steal it back.
            _contentFocusPending = true;
            Focus();
        }
    }

    protected override void OnLeave(EventArgs e)
    {
        _contentFocusPending = false;
        base.OnLeave(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible) _contentFocusPending = false;
        base.OnVisibleChanged(e);
    }

    public TaskPaneControl() : this(GetOrCreateEnvironmentAsync) { }

    /// <summary>Isolated runtime tests supply their own environment without touching the user's WebView data.</summary>
    internal TaskPaneControl(Func<Task<CoreWebView2Environment>> getEnvironment)
    {
        var watch = Stopwatch.StartNew();
        _getEnvironment = getEnvironment;
        InitializeComponent();
        OnlyWorkspaceLog.Logger.Information("Pane creation: control constructor {ElapsedMs} ms", watch.ElapsedMilliseconds);
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
        AutoScaleMode = AutoScaleMode.Font;
        Name = "TaskPaneControl";
        ResumeLayout(false);
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        UiSync.Ensure();
        // Return to Office before creating WebView2. Every callback still runs on this control's STA.
        var queued = Stopwatch.StartNew();
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || Disposing) return;
            OnlyWorkspaceLog.Logger.Information("Pane creation: WebView2 queued for {ElapsedMs} ms; heartbeat age {BeatAgeMs} ms",
                queued.ElapsedMilliseconds, Services.UiThreadWatch.BeatAgeMs);
            _ = InitializeWebViewAsync();
        }));
    }

    public async Task InitializeWebViewAsync()
    {
        // Guard set before the first await: OnCreateControl can fire again on handle recreation.
        if (_initStarted || IsDisposed || Disposing) return;
        _initStarted = true;
        int generation = ++_generation;
        var watch = Stopwatch.StartNew();
        RemoveFatalMessage();

        try
        {
            OnlyWorkspaceLog.Logger.Information("Initializing WebView2 for TaskPaneControl");
            var view = new WebView2
            {
                Dock = DockStyle.Fill
            };
            _webView = view;
            Controls.Add(view);
            OnlyWorkspaceLog.Logger.Information("Pane creation: WebView2 control {ElapsedMs} ms", watch.ElapsedMilliseconds);

            var env = await _getEnvironment();
            OnlyWorkspaceLog.Logger.Information("Pane creation: WebView2 environment ready {ElapsedMs} ms", watch.ElapsedMilliseconds);
            if (IsDisposed || generation != _generation) return;
            _environment = env;
            env.BrowserProcessExited += Environment_BrowserProcessExited;

            var ensureWatch = Stopwatch.StartNew();
            var initialization = view.EnsureCoreWebView2Async(env);
            OnlyWorkspaceLog.Logger.Information("Pane creation: EnsureCoreWebView2Async synchronous call {ElapsedMs} ms", ensureWatch.ElapsedMilliseconds);
            await initialization;
            if (IsDisposed || generation != _generation || view.CoreWebView2 == null) return;
            _core = view.CoreWebView2;

            var settings = _webView.CoreWebView2.Settings;
            settings.IsStatusBarEnabled = false;
            settings.AreDefaultContextMenusEnabled = true;
            settings.IsWebMessageEnabled = true;
            // WCAG 1.4.4: Ctrl+rotella resta attivo; Ctrl+/- sono già spenti da AreBrowserAcceleratorKeysEnabled.
            settings.IsZoomControlEnabled = true;
            settings.AreBrowserAcceleratorKeysEnabled = false;
#if !DEBUG
            settings.AreDevToolsEnabled = false;
#endif

            _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            _webView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
            _webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            _webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            NavigateToEmbeddedPage();

            _isInitialized = true;
            OnlyWorkspaceLog.Logger.Information("WebView2 initialized successfully");
            OnlyWorkspaceLog.Logger.Information("Pane creation: WebView2 initialization {ElapsedMs} ms", watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            if (IsDisposed || generation != _generation) return;
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to initialize WebView2 in TaskPaneControl");
            ReleaseWebView();
            ShowFatalMessage("Impossibile caricare Microsoft Edge WebView2.\nVerificare che il runtime sia installato.\n\n" + ex.Message);
        }
    }

    private void ShowFatalMessage(string text)
    {
        try
        {
            if (IsDisposed) return;
            RemoveFatalMessage();
            var panel = new Panel { Dock = DockStyle.Fill };
            var lblError = new TextBox
            {
                Text = text,
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.Vertical
            };
            var retry = new Button { Text = "Riprova a caricare il pannello", Dock = DockStyle.Bottom, AutoSize = true };
            retry.Click += async (_, _) =>
            {
                if (_initStarted || _browserRecoveryPending) return;
                ReleaseWebView();
                await InitializeWebViewAsync();
            };
            panel.Controls.Add(lblError);
            panel.Controls.Add(retry);
            _errorPanel = panel;
            Controls.Add(panel);
            panel.BringToFront();
        }
        catch
        {
            // Control is being torn down
        }
    }

    private void NavigateToEmbeddedPage()
    {
        var html = HtmlAssets.GetMainHtml();
        _pageReady = false;
        _pageBoundary.BeginNavigation(html);
        _webView?.NavigateToString(html);
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!ReferenceEquals(sender, _core) || !_pageBoundary.AllowNavigation(e.Uri))
        {
            e.Cancel = true;
        }
    }

    private static void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
    }

    private void CoreWebView2_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        try
        {
            if (!ReferenceEquals(sender, _core)) return;
            OnlyWorkspaceLog.Logger.Error("WebView2 process failed: {Kind} ({Reason})", e.ProcessFailedKind, e.Reason);

            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                _pageReady = false;
                _pageBoundary.Reset();
                // Recreation waits for the environment's exit event, whose order is not guaranteed.
                return;
            }

            if (!IsDisposed && (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                                e.ProcessFailedKind == CoreWebView2ProcessFailedKind.FrameRenderProcessExited))
            {
                NavigateToEmbeddedPage();
            }
            // GPU/utility failures recover in the runtime: do not reset a healthy document or replay it.
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to recover from WebView2 process failure");
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement root;
        try
        {
            if (!ReferenceEquals(sender, _core)) return;
            if (!_pageBoundary.IsTrustedMessageSource(e.Source, _webView?.CoreWebView2?.Source))
            {
                OnlyWorkspaceLog.Logger.Debug("Ignoring web message from unexpected source: {Source}", e.Source);
                return;
            }

            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            root = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "Ignoring invalid web message");
            return;
        }

        try
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("type", out var typeProp) &&
                typeProp.ValueKind == JsonValueKind.String &&
                typeProp.GetString() == "ready")
            {
                // Queued first (a ribbon action that opened the pane), then the fresh state of ControlReady.
                _pageReady = true;
                if (_contentFocusPending)
                {
                    _contentFocusPending = false;
                    if (ContainsFocus) _webView?.Focus();
                }
                FlushPendingMessages();
                ControlReady?.Invoke();
                return;
            }

            MessageReceived?.Invoke(root);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Web message handler failed");
        }
    }

    private void Environment_BrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        if (e.BrowserProcessExitKind != CoreWebView2BrowserProcessExitKind.Failed ||
            !ReferenceEquals(sender, _environment) || IsDisposed || Disposing || _browserRecoveryPending) return;
        lock (_envLock)
        {
            if (_sharedEnvironmentTask?.Status == TaskStatus.RanToCompletion && ReferenceEquals(_sharedEnvironmentTask.Result, sender))
                _sharedEnvironmentTask = null;
        }
        _browserRecoveryPending = true;
        _pageReady = false;
        _pageBoundary.Reset();
        // Leave the event callback before disposing native controls. Each affected pane gets one attempt.
        try
        {
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (IsDisposed || Disposing) return;
                    ReleaseWebView();
                    await InitializeWebViewAsync();
                }
                finally { _browserRecoveryPending = false; }
            }));
        }
        catch (InvalidOperationException) { _browserRecoveryPending = false; }
    }

    private void RemoveFatalMessage()
    {
        if (_errorPanel == null) return;
        Controls.Remove(_errorPanel);
        _errorPanel.Dispose();
        _errorPanel = null;
    }

    private void ReleaseWebView()
    {
        ++_generation;
        var webView = _webView;
        var core = _core;
        _webView = null;
        _core = null;
        _initStarted = false;
        _isInitialized = false;
        _pageReady = false;
        _pageBoundary.Reset();
        if (_environment != null) _environment.BrowserProcessExited -= Environment_BrowserProcessExited;
        _environment = null;
        if (core != null)
        {
            try
            {
                core.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                core.ProcessFailed -= CoreWebView2_ProcessFailed;
                core.NavigationStarting -= CoreWebView2_NavigationStarting;
                core.NewWindowRequested -= CoreWebView2_NewWindowRequested;
            }
            catch (Exception ex) { OnlyWorkspaceLog.Logger.Debug(ex, "Failed to detach obsolete WebView2 events"); }
        }
        if (webView != null)
        {
            Controls.Remove(webView);
            try { webView.Dispose(); }
            catch (Exception ex) { OnlyWorkspaceLog.Logger.Debug(ex, "WebView2 dispose failed"); }
        }
    }

    public void PostMessage(object message)
    {
        if (IsDisposed || Disposing) return;
        // No handle yet (first ribbon click on a new window): queue on the owner thread, never drop.
        if (!IsHandleCreated && System.Threading.Thread.CurrentThread.ManagedThreadId != _ownerThread) return;

        if (IsHandleCreated && InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => PostMessage(message)));
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Debug(ex, "PostMessage marshal to UI thread failed");
            }
            return;
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(message);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to serialize pane message");
            return;
        }

        // Messages sent before the page signalled 'ready' (e.g. a ribbon action that opened the pane)
        // are queued instead of silently dropped.
        if (!_isInitialized || !_pageReady || _webView?.CoreWebView2 == null)
        {
            QueueJson(json);
            return;
        }

        if (!SendJson(json))
        {
            _pageReady = false;
            QueueJson(json);
        }
    }

    private void QueueJson(string json)
    {
        if (_pendingMessages.Count >= MaxPendingMessages) _pendingMessages.Dequeue();
        _pendingMessages.Enqueue(json);
    }

    private void FlushPendingMessages()
    {
        while (_pendingMessages.Count > 0 && _pageReady && _webView?.CoreWebView2 != null)
        {
            if (!SendJson(_pendingMessages.Peek())) { _pageReady = false; break; }
            _pendingMessages.Dequeue();
        }
    }

    private bool SendJson(string json)
    {
        try
        {
            if (_core == null || !_pageBoundary.IsTrustedDocument(_core.Source)) return false;
            _core.PostWebMessageAsJson(json);
            return true;
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Debug(ex, "PostWebMessageAsJson failed");
            return false;
        }
    }

    /// <summary>Under _envLock: set once per process. WebView2 refuses a second call once an environment exists.</summary>
    private static void EnsureLoaderPath()
    {
        if (_loaderPathSet) return;
        try
        {
            string addinDir = Path.GetDirectoryName(typeof(TaskPaneControl).Assembly.Location) ?? string.Empty;
            string arch = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            string loaderPath = Path.Combine(addinDir, "runtimes", arch, "native");
            if (!Directory.Exists(loaderPath))
            {
                loaderPath = addinDir;
            }
            if (Directory.Exists(loaderPath))
            {
                CoreWebView2Environment.SetLoaderDllFolderPath(loaderPath);
                _loaderPathSet = true;
                OnlyWorkspaceLog.Logger.Information("Configured WebView2 loader DLL folder: {Path}", loaderPath);
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to SetLoaderDllFolderPath for WebView2");
        }
    }

    private static Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync()
    {
        lock (_envLock)
        {
            if (_sharedEnvironmentTask == null || _sharedEnvironmentTask.IsFaulted || _sharedEnvironmentTask.IsCanceled)
            {
                EnsureLoaderPath();

                var userDataFolder = OnlyWorkspace.Core.AppPaths.WebViewDirectory;
                Directory.CreateDirectory(userDataFolder);

                _sharedEnvironmentTask = CoreWebView2Environment.CreateAsync(null, userDataFolder);
            }
            return _sharedEnvironmentTask;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseWebView();
            RemoveFatalMessage();
            _pendingMessages.Clear();
        }
        base.Dispose(disposing);
    }
}
