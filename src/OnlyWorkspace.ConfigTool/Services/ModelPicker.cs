using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.ConfigTool.Services;

/// <summary>
/// «Scegli modello…» of the profile page and of the setup wizard: asks the service for its models with the key typed so far
/// and lets the user pick one from a menu under the button. Sends no Outlook data.
/// </summary>
internal static class ModelPicker
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The service's models, sorted; <see cref="Error"/> says why the request failed (an empty list without error: the service has none).</summary>
    public sealed class Result
    {
        public IReadOnlyList<string> Models { get; }
        public string? Error { get; }

        public Result(IReadOnlyList<string> models, string? error)
        {
            Models = models;
            Error = error;
        }
    }

    public const string NoModels = "Nessun modello restituito dall'endpoint.";

    public static async Task<Result> ListAsync(LlmProfileConfig profile, string? secret,
        Func<LlmProfileConfig, string?, ILlmClient>? factory = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(profile.BaseUrl)) return new(Array.Empty<string>(), "Inserire un «Indirizzo del servizio» valido.");
        try
        {
            var client = (factory ?? ((p, s) => LlmClientFactory.CreateClient(p, s)))(profile, secret);
            try
            {
                using var cts = new CancellationTokenSource(timeout ?? Timeout);
                IReadOnlyList<string> models;
                try { models = await client.ListModelsAsync(cts.Token); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return new(Array.Empty<string>(), $"Nessuna risposta da '{profile.BaseUrl}' entro {(int)(timeout ?? Timeout).TotalSeconds} s.");
                }
                var sorted = models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
                return new(sorted, null);
            }
            finally { (client as IDisposable)?.Dispose(); }
        }
        catch (Exception ex)
        {
            OnlyWorkspace.Infrastructure.Logging.OnlyWorkspaceLog.Logger.Warning(ex, "Model discovery failed");
            return new(Array.Empty<string>(), $"Rilevamento dei modelli non riuscito: {LlmErrorText.Describe(ex, profile)}");
        }
    }

    /// <summary>The themed menu of the models; a click hands the chosen name to <paramref name="pick"/>.</summary>
    public static ContextMenuStrip Menu(IEnumerable<string> models, Action<string> pick)
    {
        var menu = new ContextMenuStrip();
        Theme.Style(menu);
        foreach (var m in models) menu.Items.Add(m, null, (_, _) => pick(m));
        // Built for one showing: freed once closed (the click has already run).
        menu.Closed += (_, _) => { if (menu.IsHandleCreated) menu.BeginInvoke(new Action(menu.Dispose)); };
        return menu;
    }

    public static void Show(Control anchor, IEnumerable<string> models, Action<string> pick) =>
        Menu(models, pick).Show(anchor, new Point(0, anchor.Height));

    /// <summary>Lists the models and opens the menu under <paramref name="anchor"/>; an empty list or an error is shown in a message.</summary>
    /// <returns>Whether the menu was opened.</returns>
    public static async Task<bool> PickAsync(Control anchor, LlmProfileConfig profile, string? secret, Action<string> pick,
        Func<LlmProfileConfig, string?, ILlmClient>? factory = null)
    {
        var result = await ListAsync(profile, secret, factory);
        if (anchor.IsDisposed) return false;
        if (result.Models.Count == 0)
        {
            CopyableMessageBox.Show(anchor.FindForm(), result.Error ?? NoModels, result.Error == null ? "Modelli" : "Rilevamento dei modelli", MessageBoxButtons.OK,
                result.Error == null ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            return false;
        }
        Show(anchor, result.Models, pick);
        return true;
    }
}
