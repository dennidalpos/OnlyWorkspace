using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Infrastructure.Llm;

public static class LlmErrorText
{
    private static readonly Regex HttpStatusRegex = new(@"(?:HTTP\s*|success:\s*)([45]\d\d)\b", RegexOptions.IgnoreCase);

    /// <summary>User-facing Italian message with the likely cause and what to check.</summary>
    public static string Describe(Exception ex, LlmProfileConfig? profile)
    {
        var target = profile == null ? "il modello" : string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName;
        var root = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;

        if (root is HttpRequestException http)
        {
            if (http.InnerException is WebException { Status: WebExceptionStatus.SecureChannelFailure or WebExceptionStatus.TrustFailure })
            {
                return $"Connessione sicura (TLS) con {target} non riuscita. Verifica data e ora del PC, il proxy e l'eventuale ispezione HTTPS dell'antivirus.";
            }

            // net48 HttpRequestException has no StatusCode: clients write "HTTP 404" / EnsureSuccessStatusCode "success: 404".
            var status = HttpStatusRegex.Match(http.Message);
            if (http is LlmRequestException { Status: >= 300 and < 400 })
                return "Il server ha richiesto un reindirizzamento HTTP, bloccato per proteggere dati e credenziali. Imposta l'indirizzo finale del servizio nella Configurazione.";
            // Provider/stream text may be in any language and contain request data. Keep it in the log.
            if (!status.Success && http.InnerException == null)
                return $"{target} ha interrotto o rifiutato la risposta. Riprova; se il problema continua, consulta il log.";
            if (status.Success && IsContextOverflow(status.Groups[1].Value, http.Message))
            {
                return $"{target}: il testo è troppo lungo per il contesto del modello. Non è stato elaborato a pezzi, per non rispondere su una parte sola: " +
                       "scegli un modello con un contesto più ampio o riduci l'ambito.";
            }
            return !status.Success
                ? $"Impossibile contattare {target}. Verifica la rete, l'URL dell'endpoint o che il server sia attivo."
                : status.Groups[1].Value switch
                {
                    "401" or "403" => $"{target} ha rifiutato le credenziali. Verifica la chiave di accesso nella Configurazione.",
                    "404" => $"{target}: indirizzo o modello non trovato. Verifica «Indirizzo del servizio» e «Modello» nella Configurazione.",
                    "408" => $"{target} non ha risposto in tempo. Riprova tra qualche istante.",
                    "429" => $"{target} sta limitando le richieste (troppe richieste o quota esaurita). Riprova tra poco.",
                    var code when code.StartsWith("5") => $"{target} ha un problema temporaneo (errore {code}). Riprova più tardi.",
                    var code => $"{target} ha rifiutato la richiesta (errore {code}). Verifica le impostazioni del profilo nella Configurazione."
                };
        }

        return root switch
        {
            // These domain exceptions contain application-authored Italian instructions.
            ContextTooLongException or DataEgressBlockedException or InvalidModelOutputException => root.Message,
            TimeoutException => $"{target} non ha risposto in tempo. Riprova o aumenta i timeout del profilo.",
            TaskCanceledException => $"{target} non ha risposto in tempo. Riprova tra qualche istante.",
            JsonException => $"{target} ha restituito una risposta non valida. Riprova.",
            InvalidOperationException ioe when ioe.Message.Contains("Object Model") => "Outlook era occupato. Riprova.",
            _ => "Operazione non riuscita. Riprova; se il problema continua, consulta il log."
        };
    }

    // OpenAI "context_length_exceeded" / "maximum context length", Anthropic "prompt is too long", Gemini "input token count … exceeds".
    private static readonly Regex ContextOverflowRegex = new(
        @"context[ _]length|context window|prompt is too long|too many tokens|token count.{0,40}exceeds|tokens? exceeds?",
        RegexOptions.IgnoreCase);

    /// <summary>The provider refused the request because the prompt does not fit the model (the limit is its own, not ours).</summary>
    private static bool IsContextOverflow(string status, string message) =>
        status == "413" || (status == "400" && ContextOverflowRegex.IsMatch(message));

}
