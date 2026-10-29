using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Infrastructure.Llm;

/// <summary>
/// «Prova funzioni» of the ConfigTool: one short request per function with synthetic data only, one after the
/// other, no retry, at most <see cref="Timeout"/> each and <see cref="MaxOutputChars"/> of answer read. A timeout,
/// a refused key or a network error is Inconclusive: it says nothing about the model. Nothing of Outlook is sent.
/// </summary>
public static class ModelCapabilityProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public const int MaxOutputChars = 2000;

    internal const string JsonSchema =
        "{\"type\":\"object\",\"properties\":{\"colore\":{\"type\":\"string\",\"enum\":[\"rosso\",\"giallo\",\"verde\"]}," +
        "\"numero\":{\"type\":\"integer\"}},\"required\":[\"colore\",\"numero\"],\"additionalProperties\":false}";

    private static readonly ChatMessage Rules = ChatMessage.System(
        "Questa è una prova automatica di OnlyWorkspace con dati inventati. Rispondi in modo brevissimo, senza spiegazioni.");

    /// <summary>
    /// Runs the probes in order and reports each as it ends. Cancelling stops at once: the results already
    /// reported stay valid (partial run), the running one is dropped.
    /// </summary>
    /// <param name="reasoningClient">Client of the same profile with reasoning allowed; the others use <paramref name="client"/>.</param>
    /// <param name="web">The existing Ricerca web check of the profile; null when the connector has none.</param>
    public static async Task<IReadOnlyList<CapabilityProbeResult>> RunAsync(ILlmClient client, ILlmClient reasoningClient,
        IEnumerable<ProbeKind> kinds, Func<CancellationToken, Task<(bool? Supported, string Detail)>>? web,
        IProgress<CapabilityProbeResult>? progress, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var results = new List<CapabilityProbeResult>();
        foreach (var kind in kinds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var one = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            one.CancelAfter(timeout ?? Timeout);
            CapabilityProbeResult result;
            try
            {
                result = kind switch
                {
                    ProbeKind.Text => await TextAsync(client, one.Token).ConfigureAwait(false),
                    ProbeKind.Json => await JsonAsync(client, one.Token).ConfigureAwait(false),
                    ProbeKind.Reasoning => await ReasoningAsync(reasoningClient, one.Token).ConfigureAwait(false),
                    _ => await WebAsync(web, one.Token).ConfigureAwait(false)
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result = Result(kind, ProbeOutcome.Inconclusive, $"Nessuna risposta entro {(timeout ?? Timeout).TotalSeconds:0} secondi.");
            }
            catch (Exception ex)
            {
                result = Result(kind, ProbeOutcome.Inconclusive, "Prova non riuscita: " + LlmErrorText.Describe(ex, null));
            }
            results.Add(result);
            progress?.Report(result);
        }
        return results;
    }

    private static CapabilityProbeResult Result(ProbeKind kind, ProbeOutcome outcome, string detail) =>
        new() { Kind = kind, Outcome = outcome, Detail = detail, CheckedUtc = DateTime.UtcNow };

    /// <summary>The answer, read up to the limit: past it the stream is left, which cancels the request.</summary>
    private static async Task<string> ReadAsync(ILlmClient client, string question, CancellationToken cancellationToken, Func<bool>? stopEarly = null)
    {
        var text = new StringBuilder();
        await foreach (var chunk in client.StreamChatAsync(new[] { Rules, ChatMessage.User(question) }, cancellationToken).ConfigureAwait(false))
        {
            text.Append(chunk);
            if (text.Length >= MaxOutputChars || stopEarly?.Invoke() == true) break;
        }
        return text.Length > MaxOutputChars ? text.ToString(0, MaxOutputChars) : text.ToString();
    }

    private static async Task<CapabilityProbeResult> TextAsync(ILlmClient client, CancellationToken cancellationToken)
    {
        var text = (await ReadAsync(client, "Scrivi esattamente la parola PRONTO.", cancellationToken).ConfigureAwait(false)).Trim();
        return text.Length > 0
            ? Result(ProbeKind.Text, ProbeOutcome.Supported, "Risposta ricevuta: «" + (text.Length > 40 ? text.Substring(0, 40) + "…" : text) + "».")
            : Result(ProbeKind.Text, ProbeOutcome.Unsupported, "Il servizio ha concluso la risposta senza testo.");
    }

    private static async Task<CapabilityProbeResult> JsonAsync(ILlmClient client, CancellationToken cancellationToken)
    {
        var answer = await client.CompleteStructuredAsync(new[]
        {
            Rules, ChatMessage.User("Il semaforo della prova è verde e il numero della prova è 7. Riporta colore e numero.")
        }, JsonSchema, cancellationToken).ConfigureAwait(false);
        string? problem = SchemaProblem(answer);
        return problem == null
            ? Result(ProbeKind.Json, ProbeOutcome.Supported, "Risposta conforme allo schema richiesto.")
            : Result(ProbeKind.Json, ProbeOutcome.Unsupported, "Risposta non conforme allo schema: " + problem);
    }

    /// <summary>Checks the answer against <see cref="JsonSchema"/>; null when it conforms.</summary>
    public static string? SchemaProblem(string answer)
    {
        try
        {
            using var doc = JsonDocument.Parse(answer);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "non è un oggetto JSON.";
            if (root.EnumerateObject().Any(p => p.Name is not ("colore" or "numero"))) return "contiene campi non previsti.";
            if (!root.TryGetProperty("colore", out var colore) || colore.ValueKind != JsonValueKind.String ||
                colore.GetString() is not ("rosso" or "giallo" or "verde")) return "«colore» mancante o fuori dall'elenco.";
            if (!root.TryGetProperty("numero", out var numero) || numero.ValueKind != JsonValueKind.Number || !numero.TryGetInt64(out _))
                return "«numero» mancante o non intero.";
            return null;
        }
        catch (JsonException)
        {
            return "il testo non è JSON valido.";
        }
    }

    /// <summary>
    /// Supported only when reasoning is observed (thinking text or reasoning tokens); its absence is Inconclusive,
    /// since many services reason without showing it. Stops as soon as reasoning appears, to pay as little as possible.
    /// </summary>
    private static async Task<CapabilityProbeResult> ReasoningAsync(ILlmClient client, CancellationToken cancellationToken)
    {
        if (client is not IReasoningObserver observer)
            return Result(ProbeKind.Reasoning, ProbeOutcome.Inconclusive, "Questo connettore non rende osservabile il ragionamento all'app.");

        using var seen = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Reasoning streams before any answer text, without chunks the reader sees: watched apart.
        var watch = Task.Run(async () =>
        {
            while (!seen.IsCancellationRequested && !observer.ReasoningObserved) await Task.Delay(100).ConfigureAwait(false);
            if (observer.ReasoningObserved) seen.Cancel();
        });
        try
        {
            await ReadAsync(client, "Un treno parte alle 9:40 e arriva alle 11:15. Quanti minuti dura il viaggio? Rispondi solo con il numero.",
                seen.Token, () => observer.ReasoningObserved).ConfigureAwait(false);
        }
        catch (Exception) when (observer.ReasoningObserved)
        {
            // Stopped on purpose once reasoning appeared (or the limit hit after it): the observation stands.
        }
        finally
        {
            if (!seen.IsCancellationRequested) seen.Cancel();
            await watch.ConfigureAwait(false);
        }
        return observer.ReasoningObserved
            ? Result(ProbeKind.Reasoning, ProbeOutcome.Supported, "Ragionamento osservato nella risposta (non mostrato né salvato).")
            : Result(ProbeKind.Reasoning, ProbeOutcome.Inconclusive, "Nessun ragionamento osservabile: il modello può non ragionare o non mostrarlo.");
    }

    private static async Task<CapabilityProbeResult> WebAsync(Func<CancellationToken, Task<(bool? Supported, string Detail)>>? web, CancellationToken cancellationToken)
    {
        if (web == null) return Result(ProbeKind.Web, ProbeOutcome.Unsupported, "Il connettore non offre Ricerca web.");
        var (supported, detail) = await web(cancellationToken).ConfigureAwait(false);
        return Result(ProbeKind.Web, supported switch { true => ProbeOutcome.Supported, false => ProbeOutcome.Unsupported, _ => ProbeOutcome.Inconclusive }, detail);
    }
}
