using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Validation;

namespace OnlyWorkspace.Infrastructure.Llm;

internal static class LlmHttp
{
#if NETFRAMEWORK
    /// <summary>
    /// Outlook.exe is a native host without a TargetFramework: .NET Framework then defaults to SSL3/TLS 1.0
    /// and cloud endpoints refuse the handshake. Let the OS pick the protocol (TLS 1.2/1.3), no hardcoded version.
    /// </summary>
    static LlmHttp()
    {
        try
        {
            AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
            AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);

            // Switches are read once: if another add-in already initialised ServicePointManager, fix the value itself.
            var current = System.Net.ServicePointManager.SecurityProtocol;
            if (current != System.Net.SecurityProtocolType.SystemDefault && (current & System.Net.SecurityProtocolType.Tls12) == 0)
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.SystemDefault;
            }
        }
        catch (Exception)
        {
            // Best effort: the request will report the TLS failure.
        }
    }
#endif

    /// <summary>Owned HttpClient for a profile: HTTP only on loopback, optional system proxy.</summary>
    public static HttpClient CreateClient(LlmProfileConfig profile, string providerName, bool retryTransient = true, HttpClientHandler? transport = null)
    {
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid BaseUrl: '{profile.BaseUrl}'", nameof(profile));
        }

        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !ConfigurationValidator.IsLoopback(uri))
        {
            throw new InvalidOperationException($"{providerName} over HTTP is only permitted on loopback. Remote endpoints require HTTPS.");
        }

        transport ??= new HttpClientHandler();
        transport.UseProxy = profile.UseSystemProxy;
        transport.AllowAutoRedirect = false;
        HttpMessageHandler handler = transport;
        // Cloud only: an Ollama error is rarely temporary and asking again can reload the model.
        if (retryTransient && profile.IsExternal) handler = new TransientRetryHandler(handler);
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(profile.BaseUrl.TrimEnd('/') + "/"),
            // Default 100 s would cut a longer FirstTokenTimeout (cold model load); streams are bounded by StreamLineReader.
            Timeout = TimeSpan.FromSeconds(Math.Max(100, profile.FirstTokenTimeoutSeconds))
        };
    }

    /// <summary>
    /// Cloud APIs share the error body {"error":{"message":..,"param":..}}, Ollama sends {"error":".."};
    /// throws with status and param.
    /// </summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string providerName, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            throw new LlmRequestException("Il server ha richiesto un reindirizzamento HTTP, bloccato per proteggere dati e credenziali. Configura l'URL finale dell'endpoint nel ConfigTool.", (int)response.StatusCode);

        string? details = null, param = null;
        try
        {
            // Stream reads plus response disposal work on net48 too; do not buffer an unbounded pending body.
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.Obj("error", out var error))
                {
                    details = error.Str("message");
                    param = error.Str("param");
                }
                else
                {
                    details = doc.RootElement.Str("error");
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fallback al codice HTTP
        }

        var message = string.IsNullOrEmpty(details)
            ? $"{providerName} API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
            : $"{providerName} API returned HTTP {(int)response.StatusCode}: {details}";

        throw new LlmRequestException(message, (int)response.StatusCode, param);
    }

    /// <summary>Removes a ```json fence around a JSON answer.</summary>
    public static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(7);
        else if (trimmed.StartsWith("```", StringComparison.Ordinal)) trimmed = trimmed.Substring(3);
        if (trimmed.EndsWith("```", StringComparison.Ordinal)) trimmed = trimmed.Substring(0, trimmed.Length - 3);
        return trimmed.Trim();
    }

    /// <summary>
    /// Strict structured output: additionalProperties:false on every object. With allRequired (OpenAI) every
    /// property is also listed in "required" and the optional ones become nullable.
    /// </summary>
    public static void MakeSchemaStrict(JsonNode? node, bool allRequired)
    {
        if (node is not JsonObject obj) return;

        if (obj["properties"] is JsonObject properties)
        {
            var required = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            if (obj["required"] is JsonArray declared)
            {
                foreach (var r in declared) if (r != null) required.Add(r.GetValue<string>());
            }

            var all = new JsonArray();
            foreach (var property in properties)
            {
                all.Add(property.Key);
                MakeSchemaStrict(property.Value, allRequired);
                if (!allRequired || required.Contains(property.Key) || property.Value is not JsonObject child) continue;

                if (child["type"] is JsonValue type) child["type"] = new JsonArray(type.GetValue<string>(), "null");
                if (child["enum"] is JsonArray values) values.Add(null);
            }
            if (allRequired) obj["required"] = all;
            obj["additionalProperties"] = false;
        }

        MakeSchemaStrict(obj["items"], allRequired);
    }
}

/// <summary>net48 HttpRequestException has no StatusCode.</summary>
internal sealed class LlmRequestException : HttpRequestException
{
    public int Status { get; }
    public string? Param { get; }

    public LlmRequestException(string message, int status, string? param = null) : base(message)
    {
        Status = status;
        Param = param;
    }
}

/// <summary>JsonElement reads that tolerate missing, null or wrongly typed fields.</summary>
internal static class JsonRead
{
    public static bool Obj(this JsonElement element, string name, out JsonElement child)
    {
        child = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Object;
    }

    public static bool Arr(this JsonElement element, string name, out JsonElement child)
    {
        child = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Array;
    }

    public static string? Str(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int Int(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>A missing or invalid token count is unknown, distinct from an explicitly observed zero.</summary>
    public static int? ObservedTokens(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= 0 ? i : null;
}

/// <summary>
/// Streaming response read line by line with first-token and idle timeouts.
/// net48 ReadLineAsync ignores tokens: on timeout/cancel the response is disposed to unblock the read.
/// </summary>
internal sealed class StreamLineReader : IDisposable
{
    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);

    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;
    private readonly CancellationTokenSource _watchdog;
    private readonly CancellationTokenRegistration _registration;
    private readonly CancellationToken _userToken;
    private readonly TimeSpan _idle;
    private TimeSpan _nextWait;
    private bool _firstLine = true;

    private StreamLineReader(HttpResponseMessage response, Stream stream, TimeSpan firstWait, TimeSpan idle, CancellationToken userToken)
    {
        _response = response;
        _reader = new StreamReader(stream, Encoding.UTF8);
        _userToken = userToken;
        _idle = idle;
        _nextWait = firstWait;
        _watchdog = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        _registration = _watchdog.Token.Register(() => _response.Dispose());
    }

    public static async Task<StreamLineReader> OpenAsync(
        HttpClient http,
        HttpRequestMessage request,
        LlmProfileConfig profile,
        Func<HttpResponseMessage, CancellationToken, Task> ensureSuccess,
        CancellationToken cancellationToken)
    {
        var firstToken = TimeSpan.FromSeconds(Math.Max(5, profile.FirstTokenTimeoutSeconds));
        var started = Stopwatch.StartNew();

        HttpResponseMessage? response = null;
        using (var firstCts = new CancellationTokenSource(firstToken))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, firstCts.Token))
        {
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                using var registration = linked.Token.Register(() => response.Dispose());
                await ensureSuccess(response, linked.Token).ConfigureAwait(false);
                var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                var remaining = firstToken - started.Elapsed;
                var idle = profile.IdleTimeoutSeconds > 0 ? TimeSpan.FromSeconds(profile.IdleTimeoutSeconds) : Timeout.InfiniteTimeSpan;
                return new StreamLineReader(response, stream, remaining < MinWait ? MinWait : remaining, idle, cancellationToken);
            }
            catch (Exception) when (linked.IsCancellationRequested)
            {
                response?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"Il modello non ha risposto entro {(int)firstToken.TotalSeconds} s.");
            }
            catch
            {
                response?.Dispose();
                throw;
            }
        }
    }

    public async Task<string?> ReadLineAsync()
    {
        _watchdog.CancelAfter(_nextWait);
        try
        {
            var line = await _reader.ReadLineAsync().ConfigureAwait(false);
            if (_firstLine && line != null)
            {
                _firstLine = false;
                _nextWait = _idle;
            }
            return line;
        }
        catch (Exception) when (_watchdog.IsCancellationRequested)
        {
            _userToken.ThrowIfCancellationRequested();
            throw new TimeoutException(_firstLine
                ? "Il modello non ha iniziato a rispondere entro il timeout del primo token."
                : $"Flusso interrotto: nessun dato dal modello per {(int)_idle.TotalSeconds} s.");
        }
    }

    public void Dispose()
    {
        _registration.Dispose();
        _watchdog.Dispose();
        _reader.Dispose();
        _response.Dispose();
    }
}
