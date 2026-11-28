using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using OnlyWorkspace.Core;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>
/// Prints the data egress report to an A4 PDF with a hidden WebView2 (the runtime the add-in already requires):
/// the page is the host-generated HTML only, scripts off, every other navigation refused.
/// https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.printtopdfasync
/// </summary>
internal static class DataEgressPdfExporter
{
    private static bool _loaderPathSet;

    /// <summary>The loader of the process architecture, as the add-in does (TaskPaneControl.EnsureLoaderPath): a root one may be the other.</summary>
    private static void EnsureLoaderPath()
    {
        if (_loaderPathSet) return;
        string dir = Path.GetDirectoryName(typeof(DataEgressPdfExporter).Assembly.Location) ?? string.Empty;
        string native = Path.Combine(dir, "runtimes", Environment.Is64BitProcess ? "win-x64" : "win-x86", "native");
        CoreWebView2Environment.SetLoaderDllFolderPath(Directory.Exists(native) ? native : dir);
        _loaderPathSet = true;
    }

    public static async Task ExportAsync(Control owner, string html, string path, string footer)
    {
        EnsureLoaderPath();
        string userData = Path.Combine(AppPaths.TempDirectory, "report-" + Guid.NewGuid().ToString("N"));
        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            controller = await environment.CreateCoreWebView2ControllerAsync(owner.Handle);
            controller.IsVisible = false;
            var view = controller.CoreWebView2;
            view.Settings.IsScriptEnabled = false;
            view.Settings.AreDefaultContextMenusEnabled = false;
            view.Settings.AreDevToolsEnabled = false;
            view.Settings.IsWebMessageEnabled = false;
            view.Settings.IsStatusBarEnabled = false;

            bool started = false;
            var loaded = new TaskCompletionSource<bool>();
            view.NavigationStarting += (_, e) =>
            {
                // NavigateToString's own data: URI, once; links in the report (none expected) never open.
                if (started || !e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
                started = true;
            };
            view.NewWindowRequested += (_, e) => e.Handled = true;
            view.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            view.NavigateToString(html);
            if (!await loaded.Task) throw new InvalidOperationException("La pagina dell'informativa non si è caricata.");

            var settings = environment.CreatePrintSettings();
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.PageWidth = 8.27;
            settings.PageHeight = 11.69;
            settings.MarginTop = settings.MarginBottom = 0.6;
            settings.MarginLeft = settings.MarginRight = 0.55;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = true;
            settings.HeaderTitle = Core.Privacy.DataEgressReport.Title;
            settings.FooterUri = footer;
            if (!await view.PrintToPdfAsync(path, settings)) throw new InvalidOperationException("WebView2 non ha scritto il PDF.");
        }
        finally
        {
            controller?.Close();
            try { if (Directory.Exists(userData)) Directory.Delete(userData, recursive: true); }
            catch (IOException) { } // The browser process may still hold its profile for a moment: %TEMP% is cleaned by Windows.
            catch (UnauthorizedAccessException) { }
        }
    }
}
