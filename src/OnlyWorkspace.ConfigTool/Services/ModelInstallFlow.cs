using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Infrastructure.Privacy;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>
/// Button «Scarica e installa il modello» (#319): after the user's confirmation, downloads the model as the user from the source fixed in
/// the product into a staging folder, then asks Windows (UAC) to run <c>install-model</c> to put the verified folder next to the recognizer.
/// Only the model files travel: no mail text, no profile, no key.
/// </summary>
internal static class ModelInstallFlow
{
    public static bool Available => PiiModelSource.Default != null && PiiRecognizerClient.InstalledModelDirectory() != null;

    public static string UnavailableReason =>
        PiiModelSource.Default == null ? "Il download del modello non è ancora disponibile: la licenza dei dati del modello deve essere decisa."
        : "Il riconoscitore locale non è installato su questo PC: reinstalla il setup.";

    /// <summary>Checks the installed model (hashes ~1.2 GB, so off the UI thread). Null when the recognizer is not installed.</summary>
    public static Task<PiiModelStatus?> CheckAsync() => Task.Run(() =>
    {
        string? target = PiiRecognizerClient.InstalledModelDirectory();
        return target == null ? null : PiiModelStatus.Check(target, PiiModelSource.Default);
    });

    public static string Describe(PiiModelStatus s) => s.State switch
    {
        PiiModelState.Ready => $"Modello installato e verificato ({s.Detail}): nessun download necessario.",
        PiiModelState.Missing => "Modello non installato.",
        PiiModelState.Corrupt => $"Modello danneggiato ({s.Detail}): reinstallalo.",
        _ => $"Modello diverso da quello del prodotto ({s.Detail}): puoi sostituirlo."
    };

    public static string ButtonText(PiiModelStatus? s) => s?.State switch
    {
        PiiModelState.Ready => "Modello installato",
        PiiModelState.Corrupt => "Reinstalla il modello…",
        PiiModelState.Different => "Sostituisci il modello…",
        _ => "Scarica e installa il modello…"
    };

    public static async Task RunAsync(IWin32Window owner, Action<string> status, CancellationToken ct = default)
    {
        var source = PiiModelSource.Default;
        string? target = PiiRecognizerClient.InstalledModelDirectory();
        if (source == null || target == null) { status(UnavailableReason); return; }
        source.Validate();
        status("Controllo del modello installato…");
        var current = await CheckAsync();
        if (current is { State: PiiModelState.Ready }) { status(Describe(current)); return; }
        if (current != null) status(Describe(current));
        string size = (source.TotalSize / (1024.0 * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
        if (CopyableMessageBox.Show(owner, $"Scaricare il modello «{source.DisplayName}» da {source.Host}/{source.Repository} (circa {size} GB)?\n\n" +
                "Il download contiene solo i file del modello: nessuna email, nessun profilo e nessuna chiave lascia il PC. " +
                "I file sono controllati con le impronte fissate nel prodotto prima di installarli; l'installazione chiede l'autorizzazione di amministratore.\n\n" +
                source.LicenseNote, "Scarica il modello", MessageBoxButtons.YesNo, MessageBoxIcon.Question, "Scarica e installa", "Annulla") != DialogResult.Yes) return;

        string staging = ElevatedConfigWriter.CreateStagingDirectory();
        try
        {
            var progress = new Progress<PiiModelInstaller.Progress>(p => status($"Download del modello: {p.File}, {100 * p.Done / Math.Max(1, p.Total)}%…"));
            status("Download del modello in corso…");
            await PiiModelInstaller.DownloadAsync(source, staging, null, progress, ct);
            status("Installazione del modello: attendo l'autorizzazione di amministratore…");
            var result = await ElevatedConfigWriter.RunElevatedAsync("install-model " + ElevatedConfigWriter.Quote(staging), staging);
            status(result.Success ? "Modello installato e verificato. Il riconoscimento locale lo usa al prossimo avvio del riquadro."
                : result.Cancelled ? result.Message : "Installazione del modello non riuscita: " + result.Message);
        }
        catch (OperationCanceledException) { status("Download annullato."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            status("Download del modello non riuscito: " + ErrorText.For(ex));
        }
        finally { ElevatedConfigWriter.DeleteStagingDirectory(staging); }
    }
}
