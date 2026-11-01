using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Infrastructure.Archive;
using OnlyWorkspace.Core.Archive;
using System.Linq;

namespace OnlyWorkspace.ConfigTool.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly string _configDir;
    private readonly string? _preferencesDir;
    private readonly string? _archiveDir;
    private readonly FileConfigurationService _configService;
    private readonly SingleInstanceManager? _singleInstance;
    private readonly SynchronizationContext _syncContext;
    private readonly object _showGate = new();
    private bool _startupComplete;
    private bool _showPending;

    private readonly System.Windows.Forms.Timer _statusAnimation = new() { Interval = 100 };
    private readonly Dictionary<(StatusLight, int), Bitmap> _statusImages = new();
    private readonly Dictionary<(StatusLight, int), Icon> _archiveIcons = new();
    private StatusLight _archiveLight, _aiLight;
    private int _statusFrame, _profileVersion;
    private bool _statusImagesDisposed;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _itemStatus;
    private readonly ToolStripMenuItem _itemAutostart;

    private MainConfigForm? _mainForm;
    private bool _firstMinimizeNotificationShown;
    private bool _isExiting;
    private ArchiveEngine? _archiveEngine;
    private ArchiveSearchForm? _archiveForm;
    private readonly System.Windows.Forms.Timer _archiveTimer = new() { Interval = 15000 };
    private readonly ToolStripMenuItem _itemArchiveStatus = new("Archivio: inizializzazione…") { Enabled = false };
    private readonly CancellationTokenSource _archiveLifetime = new();
    private bool _archiveRefreshRunning;
    private bool _archiveClosed;
    private ArchiveCapacityState? _lastDiskState;

    public TrayApplicationContext(string? configDir = null, bool startMinimized = false, SingleInstanceManager? singleInstance = null,
        string? preferencesDir = null, string? archiveDir = null, string? startPage = null)
    {
        // Not SynchronizationContext.Current: before any control exists it can be the base context,
        // whose Post runs on the thread pool and would build the form on a thread without a message loop.
        _syncContext = new WindowsFormsSynchronizationContext();
        _configDir = configDir ?? OnlyWorkspace.Core.AppPaths.ConfigDirectory;
        _preferencesDir = preferencesDir;
        _archiveDir = archiveDir;
        _configService = new FileConfigurationService(_configDir, _preferencesDir);
        _singleInstance = singleInstance;

        if (_singleInstance != null)
        {
            _singleInstance.ShowWindowRequested += OnSingleInstanceShowRequested;
        }

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var itemConfig = new ToolStripMenuItem("Configurazione OnlyWorkspace…", null, (s, e) => ShowConfigForm())
        {
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        var archiveMenu = new ToolStripMenuItem("Archivio");
        archiveMenu.DropDownItems.Add(_itemArchiveStatus);
        archiveMenu.DropDownItems.Add(new ToolStripMenuItem("Ricerca e stato archivio…", null, (_, _) => ShowArchiveForm()));
        _contextMenu.Items.Add(archiveMenu);

        var aiMenu = new ToolStripMenuItem("IA");
        _itemStatus = new ToolStripMenuItem("IA: non verificata") { Enabled = false };
        aiMenu.DropDownItems.Add(_itemStatus);
        aiMenu.DropDownItems.Add(new ToolStripMenuItem("Verifica / annulla connessione del profilo predefinito", null, async (_, _) => await CheckConnectivityAsync()));
        _contextMenu.Items.Add(aiMenu);

        var configurationMenu = new ToolStripMenuItem("Configurazione");
        configurationMenu.DropDownItems.Add(itemConfig);
        configurationMenu.DropDownItems.Add(new ToolStripMenuItem("Configurazione guidata…", null, (_, _) => { ShowConfigForm(); _mainForm?.RunSetupWizard(); }));
        configurationMenu.DropDownItems.Add(new ToolStripMenuItem("Apri cartella configurazione", null, (_, _) => OpenDataDirectory()));
        configurationMenu.DropDownItems.Add(new ToolStripMenuItem("Apri cartella log", null, (_, _) => OpenLogDirectory()));
        _itemAutostart = new ToolStripMenuItem("Avvia con Windows", null, (_, _) => ToggleAutostart())
        {
            CheckOnClick = true,
            Checked = StartupManager.IsRunAtStartupEnabled()
        };
        configurationMenu.DropDownItems.Add(_itemAutostart);
        _contextMenu.Items.Add(configurationMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var itemExit = new ToolStripMenuItem("Esci da OnlyWorkspace", null, (s, e) => ExitApplication());
        _contextMenu.Items.Add(itemExit);
        _contextMenu.Opening += (_, _) => _itemAutostart.Checked = StartupManager.IsRunAtStartupEnabled();

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIconHelper.GetTrayIcon(),
            ContextMenuStrip = _contextMenu,
            Text = "OnlyWorkspace · clic: archivio, tasto destro: menu",
            Visible = true
        };

        _notifyIcon.DoubleClick += (s, e) => ShowArchiveForm();
        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowArchiveForm();
            }
        };

        _statusAnimation.Tick += (_, _) => { _statusFrame = (_statusFrame + 1) % 12; UpdateStatusImages(); };
        UpdateProfileStatus();
        _archiveTimer.Tick += async (_, _) => await RefreshArchiveStatusAsync();

        if (!startMinimized)
        {
            ShowConfigForm(startPage);
        }
        else
        {
            _notifyIcon.ShowBalloonTip(
                2500,
                "OnlyWorkspace",
                "Attivo nell'area di notifica: l'archivio si aggiorna con Outlook aperto. Clic sull'icona per cercare nell'archivio, tasto destro per la configurazione.",
                ToolTipIcon.Info);
        }
        bool showPending;
        lock (_showGate)
        {
            _startupComplete = true;
            showPending = _showPending;
            _showPending = false;
        }
        if (showPending) ShowConfigForm(_singleInstance?.TakeRequestedPage());
        StartArchiveEngine();
        _archiveTimer.Start();
    }

    private void StartArchiveEngine()
    {
        try
        {
            var settings = new CachedConfiguration(new FileConfigurationService(_configDir));
            WorkspaceConfig Config() => settings.Current;
            // Asked once per tray start (A123): a Windows OCR language installed since the last start rereads the scans left unread.
            var ocrLanguage = Task.Run(() => OnlyWorkspace.Infrastructure.Security.AttachmentTextExtractor.OcrLanguageAsync(CancellationToken.None));
            _archiveEngine = new ArchiveEngine(directory: _archiveDir, enabled: () => Config().Archive?.Enabled == true,
                indexAttachmentText: () => Config().Archive?.IndexAttachmentText != false,
                attachmentOcr: () => Config().AttachmentOcr,
                ocrLanguage: () => ocrLanguage.Status == TaskStatus.RanToCompletion ? ocrLanguage.Result : null);
            _ = ObserveArchiveStartupAsync();
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            // Another Windows session owns the engine; this tray connects to it.
            _ = RefreshArchiveStatusAsync();
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Archive engine startup failed");
            SetArchiveStatus(StatusLight.Error, "Archivio: " + ErrorText.For(ex));
        }
    }

    private async Task ObserveArchiveStartupAsync()
    {
        try
        {
            if (_archiveEngine != null) await _archiveEngine.Ready;
            await RefreshArchiveStatusAsync();
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Archive engine listener could not start");
            if (!_isExiting) SetArchiveStatus(StatusLight.Error, "Archivio non disponibile: " + ErrorText.For(ex));
        }
    }

    public void ShowArchiveForm()
    {
        if (_isExiting) return;
        if (_archiveForm == null || _archiveForm.IsDisposed) _archiveForm = new ArchiveSearchForm();
        if (_archiveForm.WindowState == FormWindowState.Minimized) _archiveForm.WindowState = FormWindowState.Normal;
        _archiveForm.Show();
        _archiveForm.Activate();
        _archiveForm.BringToFront();
    }

    private async Task RefreshArchiveStatusAsync()
    {
        if (_isExiting || _archiveRefreshRunning) return;
        _archiveRefreshRunning = true;
        try
        {
            if (_configService.LoadConfiguration().Archive?.Enabled != true)
            {
                SetArchiveStatus(StatusLight.Off, "Archivio non abilitato");
                _lastDiskState = null;
                return;
            }
            var states = await new ArchiveClient(string.Empty, lifetime: _archiveLifetime.Token).ProfilesAsync();
            if (_isExiting) return;
            var diskState = states.Count == 0 ? ArchiveCapacityState.Available : states.Max(s => s.Stats.DiskState);
            if (_lastDiskState != diskState && diskState != ArchiveCapacityState.Available)
                _notifyIcon.ShowBalloonTip(5000, "Spazio archivio OnlyWorkspace", ArchiveCapacity.Message(diskState), diskState == ArchiveCapacityState.Full ? ToolTipIcon.Warning : ToolTipIcon.Info);
            _lastDiskState = diskState;
            int failed = states.Count(s => s.Error != null || s.Stats.FoldersFailed > 0);
            var light = StatusLights.Worst(states.Select(s => StatusLights.Archive(true, true, s.Paused, s.Phase, s.Error,
                s.Stats.FoldersFailed, s.Stats.BodiesPending, System.Math.Max(0, s.Stats.Folders - s.Stats.FoldersDone))));
            string status = states.Count == 0 ? "nessun archivio" : StatusLights.Label(light);
            // Red always says why: the first archive with a reason (indexer error, space limit, unreadable folders).
            string? reason = light == StatusLight.Error
                ? states.Select(s => StatusLights.ArchiveReason(s.Phase, s.Error, s.Stats.FoldersFailed)).FirstOrDefault(r => r != null)
                : null;
            SetArchiveStatus(light, $"Archivio: {status} · {states.Sum(s => s.Stats.Emails):N0} email" + (reason != null ? $" · {reason}" : string.Empty));
            string tooltip = $"OnlyWorkspace · archivio {status}" + (failed > 0 ? $" · {failed} con errori" : string.Empty);
            _notifyIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 60) + "…" : tooltip;
        }
        catch (OperationCanceledException) when (_isExiting) { }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning("Archive tray status failed: {Kind}", ex.GetType().Name);
            if (!_isExiting) SetArchiveStatus(StatusLight.Error, "Archivio non disponibile: " + ErrorText.For(ex));
            if (!_isExiting && _archiveEngine == null && (ex is IOException || ex is TimeoutException)) StartArchiveEngine();
        }
        finally { _archiveRefreshRunning = false; }
    }

    /// <summary>Text and light change together: a failure never leaves the image of the previous state.</summary>
    private void SetArchiveStatus(StatusLight light, string text)
    {
        _archiveLight = light;
        _itemArchiveStatus.Text = text;
        UpdateStatusImages();
    }

    private void OnSingleInstanceShowRequested()
    {
        lock (_showGate)
        {
            if (!_startupComplete)
            {
                _showPending = true;
                return;
            }
        }
        string? page = _singleInstance?.TakeRequestedPage();
        _syncContext.Post(_ => ShowConfigForm(page), null);
    }

    public void ShowConfigForm(string? page = null)
    {
        if (_isExiting) return;

        if (_mainForm == null || _mainForm.IsDisposed)
        {
            _mainForm = new MainConfigForm(_configDir, this, _preferencesDir);
            _mainForm.Icon = AppIconHelper.GetAppIcon();
        }

        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }

        _mainForm.Show();
        _mainForm.Activate();
        _mainForm.BringToFront();
        if (page == SingleInstanceManager.MailboxesPage) _mainForm.ShowMailboxes();
    }

    public void NotifyMinimizedToTray()
    {
        if (_isExiting) return;

        if (!_firstMinimizeNotificationShown)
        {
            _firstMinimizeNotificationShown = true;
            _notifyIcon.ShowBalloonTip(
                3000,
                "OnlyWorkspace",
                "La finestra è nascosta nell'area di notifica. Tasto destro sull'icona → Configurazione per riaprirla; clic per cercare nell'archivio.",
                ToolTipIcon.Info);
        }
    }

    public void UpdateProfileStatus()
    {
        _profileVersion++;
        _connectionCheck?.Cancel();
        try
        {
            var config = _configService.LoadConfiguration();
            var profile = config.Profiles.Find(p => string.Equals(p.Id, config.DefaultProfile, StringComparison.OrdinalIgnoreCase));
            SetAiStatus(StatusLight.Off, profile == null ? "IA: nessun profilo predefinito" : $"IA: non verificata · {profile.DisplayName} ({profile.Model})");
        }
        catch { SetAiStatus(StatusLight.Error, "IA: configurazione non leggibile"); }
    }

    private void SetAiStatus(StatusLight light, string text)
    {
        _aiLight = light;
        _itemStatus.Text = text;
        UpdateStatusImages();
    }

    private void UpdateStatusImages()
    {
        if (_isExiting) return;
        bool animate = StatusDot.AnimationsEnabled;
        Bitmap ImageFor(StatusLight light)
        {
            var key = (light, light == StatusLight.Working && animate ? _statusFrame : 0);
            if (!_statusImages.TryGetValue(key, out var image)) _statusImages[key] = image = StatusDot.Image(light, 16, key.Item2);
            return image;
        }
        _itemArchiveStatus.Image = ImageFor(_archiveLight);
        _itemStatus.Image = ImageFor(_aiLight);
        var iconKey = (_archiveLight, _archiveLight == StatusLight.Working && animate ? _statusFrame : 0);
        if (!_archiveIcons.TryGetValue(iconKey, out var icon))
        {
            using var bitmap = AppIconHelper.GetTrayIcon().ToBitmap();
            using (var g = Graphics.FromImage(bitmap))
            {
                int size = bitmap.Width;
                g.FillEllipse(SystemBrushes.Window, size / 2, size / 2, size / 2, size / 2);
                g.DrawImage(ImageFor(_archiveLight), new Rectangle(size / 2 - 2, size / 2 - 2, size / 2 + 4, size / 2 + 4));
            }
            var handle = bitmap.GetHicon();
            try { using var borrowed = Icon.FromHandle(handle); icon = (Icon)borrowed.Clone(); }
            finally { DestroyIcon(handle); }
            _archiveIcons[iconKey] = icon;
        }
        _notifyIcon.Icon = icon;
        string text = "OnlyWorkspace · " + _itemArchiveStatus.Text;
        _notifyIcon.Text = text.Length > 63 ? text.Substring(0, 60) + "…" : text;
        _statusAnimation.Enabled = animate && (_archiveLight == StatusLight.Working || _aiLight == StatusLight.Working);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private InteractiveRequest? _connectionCheck;

    private async Task CheckConnectivityAsync()
    {
        if (_isExiting) return;
        if (_connectionCheck != null) { _connectionCheck.Cancel(); return; }
        using var run = new InteractiveRequest();
        _connectionCheck = run;
        int version = _profileVersion;
        SetAiStatus(StatusLight.Working, "IA: verifica della connessione in corso…");
        try
        {
            var config = _configService.LoadConfiguration();
            var profile = config.Profiles.Find(p => string.Equals(p.Id, config.DefaultProfile, StringComparison.OrdinalIgnoreCase));
            if (profile == null) { SetAiStatus(StatusLight.Off, "IA: nessun profilo predefinito"); return; }
            string? secret = _configService.ResolveSecret(profile.SecretRef);
            var client = LlmClientFactory.CreateClient(profile, secret: secret);
            OnlyWorkspace.Core.Models.LlmHealthResult health;
            try { health = await client.CheckHealthAsync(run.Token).ConfigureAwait(true); run.Token.ThrowIfCancellationRequested(); }
            finally { (client as IDisposable)?.Dispose(); }
            if (_isExiting || version != _profileVersion) return;
            SetAiStatus(health.IsHealthy ? StatusLight.Ok : StatusLight.Error, health.IsHealthy
                ? $"IA: connessione verificata · {profile.DisplayName} ({profile.Model})"
                : "IA: connessione non riuscita · " + health.ErrorMessage);
            _notifyIcon.ShowBalloonTip(3500, "OnlyWorkspace - Connessione IA", _itemStatus.Text ?? "IA", health.IsHealthy ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            if (!_isExiting && version == _profileVersion) SetAiStatus(StatusLight.Waiting, run.TimedOut ? "IA: tempo massimo di 10 secondi superato." : "IA: verifica annullata.");
        }
        catch (Exception ex)
        {
            if (!_isExiting && version == _profileVersion) SetAiStatus(StatusLight.Error, "IA: " + ErrorText.For(ex));
        }
        finally { _connectionCheck = null; }
    }

    private void OpenDataDirectory()
    {
        try
        {
            if (!Directory.Exists(_configDir))
            {
                Directory.CreateDirectory(_configDir);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = _configDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show("Impossibile aprire la cartella dati: " + ErrorText.For(ex), "Cartella dati", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenLogDirectory()
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OnlyWorkspace",
                "logs");

            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = logDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            CopyableMessageBox.Show("Impossibile aprire la cartella dei log: " + ErrorText.For(ex), "Cartella dei log", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleAutostart()
    {
        bool targetState = _itemAutostart.Checked;
        bool success = StartupManager.SetRunAtStartup(targetState);
        if (!success)
        {
            _itemAutostart.Checked = !targetState;
            CopyableMessageBox.Show("Impossibile modificare l'avvio automatico di Windows.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void ExitApplication()
    {
        if (_isExiting) return;
        if (_mainForm != null && !_mainForm.IsDisposed && !_mainForm.TryCloseForExit()) return;

        _isExiting = true;
        _connectionCheck?.Cancel();
        CloseArchiveEngine();
        _mainForm?.Dispose();
        _mainForm = null;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        DisposeStatusImages();

        _singleInstance?.Dispose();

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isExiting = true;
            _connectionCheck?.Cancel();
            CloseArchiveEngine();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            DisposeStatusImages();
            _singleInstance?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void DisposeStatusImages()
    {
        if (_statusImagesDisposed) return;
        _statusImagesDisposed = true;
        _statusAnimation.Stop(); _statusAnimation.Dispose();
        foreach (var image in _statusImages.Values) image.Dispose();
        foreach (var icon in _archiveIcons.Values) icon.Dispose();
        _statusImages.Clear(); _archiveIcons.Clear();
    }

    private void CloseArchiveEngine()
    {
        if (_archiveClosed) return;
        _archiveClosed = true;
        _archiveTimer.Stop();
        _archiveTimer.Dispose();
        if (!_archiveLifetime.IsCancellationRequested) _archiveLifetime.Cancel();
        _archiveLifetime.Dispose();
        _archiveForm?.Dispose();
        _archiveForm = null;
        var engine = _archiveEngine;
        _archiveEngine = null;
        if (engine != null)
        {
            try { engine.Dispose(); }
            catch (Exception ex) { OnlyWorkspaceLog.Logger.Error(ex, "Archive engine shutdown failed"); }
        }
    }
}
