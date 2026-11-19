using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.ConfigTool.Forms;

/// <summary>Exports the notice and authorization report from the saved configuration.</summary>
internal static class DataEgressPdfExportFlow
{
    public static async Task RunAsync(Form owner, Button exportButton, string configDirectory, string windowsUser,
        Action<string> logActivity, Action<string, Tone> setStatus)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Esporta l'informativa sull'uscita dei dati",
            Filter = "Documento PDF (*.pdf)|*.pdf",
            DefaultExt = "pdf",
            AddExtension = true,
            FileName = $"OnlyWorkspace-informativa-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.pdf"
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        exportButton.Enabled = false;
        try
        {
            var savedService = new FileConfigurationService(configDirectory);
            var saved = await Task.Run(() => savedService.LoadConfiguration());
            var workstation = new DataEgressWorkstation
            {
                Machine = Environment.MachineName,
                ExportedBy = windowsUser,
                ProductVersion = ProductInfo.Version,
                Windows = $"{WorkstationInfo.WindowsProductName()} {WorkstationInfo.WindowsDisplayVersion()} ({Environment.OSVersion.Version})".Trim(),
                Outlook = $"{WorkstationInfo.OutlookVersion()} {WorkstationInfo.OutlookBitness()}",
                WebView2 = WorkstationInfo.WebView2Version(),
                ConfigPath = Path.Combine(configDirectory, "config.json"),
                GeneratedUtc = DateTime.UtcNow
            };
            var privacyAudit = await Task.Run(() => new PseudonymizationAuditStore().Read());
            string html = DataEgressReport.RenderHtml(saved, workstation, privacyAudit, savedService.ResolveSecret);
            var consent = saved.DataEgress?.Consent;
            string footer = consent == null ? "Informativa non accettata" : $"Informativa v{consent.NoticeVersion} · SHA-256 {consent.NoticeHash}";
            await DataEgressPdfExporter.ExportAsync(owner, html, dialog.FileName, footer);
            logActivity("Informativa esportata in PDF: " + dialog.FileName);
            setStatus("Informativa esportata: " + dialog.FileName, Tone.Success);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or COMException)
        {
            logActivity("Esportazione informativa non riuscita: " + ex);
            setStatus("Esportazione non riuscita: " + ErrorText.For(ex), Tone.Danger);
            CopyableMessageBox.Show(owner, "Esportazione non riuscita: " + ErrorText.For(ex) +
                (ex is WebView2RuntimeNotFoundException ? "\n\nServe il runtime Microsoft Edge WebView2, lo stesso richiesto dal riquadro di Outlook." : ""),
                "Uscita dei dati", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            exportButton.Enabled = true;
        }
    }
}
