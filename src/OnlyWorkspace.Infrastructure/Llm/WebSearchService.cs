using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Prompts;

namespace OnlyWorkspace.Infrastructure.Llm;

public enum WebSearchSupport { Unverified, Supported, Unsupported }

/// <summary>
/// The model answered without trying a search, which a provider that cannot force the tool (QwenCloud, xAI, Anthropic
/// here) may do when it judges the web useless. Actions go on without web evidence; capability probes stay unverified.
/// </summary>
public sealed class WebSearchSkippedException : HttpRequestException
{
    public WebSearchSkippedException(string message) : base(message) { }
}

public sealed class WebSearchResult
{
    public string Text { get; set; } = string.Empty;
    public List<WebSource> Sources { get; } = new();
    /// <summary>The request was longer than the web input limit: only its end reached the search.</summary>
    public bool InputCut { get; set; }

    public void AddEvidenceTo(ICollection<ChatMessage> messages)
    {
        var cited = string.Join("\n", Sources.Select((source, index) => $"[W{index + 1}] {source.Title}: {source.Url}"));
        messages.Add(PromptCatalog.CreateWebEvidenceMessage(Text, cited));
    }

    // [label](https://…), label possibly bracketed itself as in QwenCloud's "[[21]](https://…)".
    private static readonly Regex Link = new(@" ?\[((?:[^\[\]\n]|\[[^\[\]\n]*\])+)\]\((https?://[^\s)]+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex NumberLabel = new(@"^\[?\d+\]?$", RegexOptions.CultureInvariant);
    private static readonly Regex PlainUrl = new(@"https://[^\s<>""`\[\]]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Text shown as it is: the pane renders no links, so a provider citation becomes [Wn] when its address is one
    /// of <see cref="Sources"/>, its label when that says something, and nothing when it is only the provider's number.
    /// </summary>
    public string DisplayText() => PlainUrl.Replace(Link.Replace(Text, match =>
    {
        string space = match.Value.StartsWith(" ", StringComparison.Ordinal) ? " " : string.Empty;
        int index = Uri.TryCreate(match.Groups[2].Value, UriKind.Absolute, out var url)
            ? Sources.FindIndex(source => source.Url == url.AbsoluteUri) : -1;
        if (index >= 0) return $"{space}[W{index + 1}]";
        string label = match.Groups[1].Value;
        return NumberLabel.IsMatch(label) ? string.Empty : space + label;
    }), match =>
    {
        // Some providers cite a source as plain text instead of a Markdown link.
        // Recognize only an address already returned as a source, never invent a reference.
        int length = match.Length;
        while (length > 0)
        {
            string address = match.Value.Substring(0, length);
            int index = Uri.TryCreate(address, UriKind.Absolute, out var url)
                ? Sources.FindIndex(source => source.Url == url.AbsoluteUri) : -1;
            if (index >= 0) return $"[W{index + 1}]" + match.Value.Substring(length);
            if (".,;:!?)".IndexOf(match.Value[length - 1]) < 0) break;
            length--;
        }
        return match.Value;
    });
}

/// <summary>One bounded, explicitly requested web operation. No mail data is sent during capability checks.</summary>
public sealed class WebSearchService : IDisposable
{
    public string? LastCheckError { get; private set; }
    private readonly LlmProfileConfig _profile;
    private readonly string? _modelSecret;
    private readonly string? _webSecret;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Action? _externalRequestStarting;
    private ProviderCapabilities Caps => ProviderCatalog.Get(_profile.Type);
    /// <summary>Responses API without OpenAI-only controls (tool_choice, max_tool_calls, include): QwenCloud, xAI.</summary>
    private bool Basic => Caps.WebSearch == WebSearchMode.BasicResponses;

    public WebSearchService(LlmProfileConfig profile, string? modelSecret, string? webSecret = null, HttpClient? httpClient = null,
        Action? externalRequestStarting = null)
    {
        _profile = profile;
        _modelSecret = modelSecret;
        _webSecret = webSecret;
        _externalRequestStarting = externalRequestStarting;
        _ownsHttp = httpClient == null;
        _http = httpClient ?? LlmHttp.CreateClient(profile, "Web Search");
        if (httpClient == null) _http.Timeout = RequestTimeout(profile);
    }

    /// <summary>
    /// The search response is not streamed and arrives whole after the searches: QwenCloud took 131 s on 17/10/2026,
    /// so 90 s cut every real search. A longer first-token timeout on the profile raises it.
    /// </summary>
    internal static TimeSpan RequestTimeout(LlmProfileConfig profile) =>
        TimeSpan.FromSeconds(Math.Max(180, profile.FirstTokenTimeoutSeconds));

    /// <summary>Limit of one <see cref="ForCheck"/> attempt; callers wait a little longer before giving up.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Capability probe from the ConfigTool: one attempt (no transient retry) and a short timeout, so an
    /// unsupported or slow model is reported in about a minute instead of three. QwenCloud answered the probe in
    /// 24.6 s on 17/10/2026 and the previous 30 s cut the attempt before.
    /// </summary>
    public static WebSearchService ForCheck(LlmProfileConfig profile, string? modelSecret, string? webSecret)
    {
        var direct = new LlmProfileConfig { Type = profile.Type, BaseUrl = profile.BaseUrl, UseSystemProxy = profile.UseSystemProxy, External = false };
        var http = LlmHttp.CreateClient(direct, "Web Search", retryTransient: false);
        http.Timeout = CheckTimeout;
        return new WebSearchService(profile, modelSecret, webSecret, http) { _ownsProbeHttp = true };
    }

    private bool _ownsProbeHttp;

    /// <summary>Hosts that receive data during a web search with this profile.</summary>
    public static IReadOnlyList<string> Destinations(LlmProfileConfig profile)
    {
        var hosts = new List<string>();
        if (Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var url)) hosts.Add(url.Host);
        if (ProviderCatalog.Get(profile.Type).WebSearch == WebSearchMode.OllamaCom && !hosts.Contains("ollama.com")) hosts.Add("ollama.com");
        return hosts;
    }

    /// <summary>What leaves the PC during one web search.</summary>
    public static string Disclosure(LlmProfileConfig profile) => ProviderCatalog.Get(profile.Type).WebSearch == WebSearchMode.OllamaCom
        ? "Il modello locale ricava una query (al massimo 240 caratteri) dalla richiesta, testo email incluso, e la invia a ollama.com."
        // Italian text: the thousands separator must not follow the Windows display language.
        : "Fino a " + MaxSearchInput.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("it-IT")) +
          $" caratteri della richiesta, testo email incluso, vanno a {string.Join(", ", Destinations(profile))} per la ricerca web.";

    private const int MaxSearchInput = 6000;

    /// <summary>
    /// Shown under the answer when <see cref="WebSearchResult.InputCut"/>: the accepted notice covers this much data,
    /// so the limit stays and the user learns that the start of a longer request was not searched (A44).
    /// </summary>
    public static readonly string InputCutNotice = "La ricerca web ha usato solo gli ultimi " +
        MaxSearchInput.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("it-IT")) +
        " caratteri della richiesta: quanto scritto prima non è stato cercato sul web.";

    public async Task<WebSearchSupport> CheckSupportAsync(CancellationToken ct = default)
    {
        LastCheckError = null;
        if (string.IsNullOrWhiteSpace(_profile.Model)) { LastCheckError = "Seleziona un modello."; return WebSearchSupport.Unverified; }
        var mode = Caps.WebSearch;
        if (mode == WebSearchMode.None) { LastCheckError = "Il connettore non offre Web Search."; return WebSearchSupport.Unsupported; }
        try
        {
            if (mode == WebSearchMode.AnthropicTool)
            {
                using var doc = await SendAsync(HttpMethod.Get, ProviderUrl("models/" + Uri.EscapeDataString(_profile.Model)), null, false, ct).ConfigureAwait(false);
                var capability = Path(doc.RootElement, "capabilities", "server_tools", "web_search", "supported");
                if (capability.ValueKind == JsonValueKind.False) return WebSearchSupport.Unsupported;
                if (capability.ValueKind == JsonValueKind.True)
                {
                    using var probe = await SendAsync(HttpMethod.Post, ProviderUrl("messages"), new
                    {
                        model = _profile.Model, max_tokens = 256,
                        messages = new[] { new { role = "user", content = "Cerca sul web una fonte pubblica per la data odierna UTC." } },
                        tools = new[] { new { type = "web_search_20250305", name = "web_search", max_uses = 1 } }
                    }, false, ct).ConfigureAwait(false);
                    var blocks = Path(probe.RootElement, "content");
                    if (blocks.ValueKind == JsonValueKind.Array)
                        foreach (var block in blocks.EnumerateArray())
                            if (Str(block, "type") == "web_search_tool_result")
                            {
                                var result = Path(block, "content");
                                if (result.ValueKind == JsonValueKind.Object)
                                {
                                    LastCheckError = "Il server ha rifiutato la ricerca: " + Str(result, "error_code");
                                    return WebSearchSupport.Unverified;
                                }
                                return WebSearchSupport.Supported;
                            }
                    LastCheckError = "Il modello accetta Web Search ma la sonda non ha eseguito la ricerca.";
                    return WebSearchSupport.Unverified;
                }
            }
            else if (mode == WebSearchMode.OllamaCom)
            {
                using var show = await SendAsync(HttpMethod.Post, ProviderUrl("api/show"), new { model = _profile.Model }, false, ct).ConfigureAwait(false);
                var caps = Path(show.RootElement, "capabilities");
                if (caps.ValueKind != JsonValueKind.Array) { LastCheckError = "Capacità del modello Ollama non disponibili."; return WebSearchSupport.Unverified; }
                if (!caps.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "tools")) return WebSearchSupport.Unsupported;
                if (string.IsNullOrWhiteSpace(_webSecret)) { LastCheckError = "Chiave Ollama Web Search mancante."; return WebSearchSupport.Unverified; }
                using var keyProbe = await SendAsync(HttpMethod.Post, "https://ollama.com/api/web_search", new { query = "OnlyWorkspace", max_results = 1 }, true, ct).ConfigureAwait(false);
                if (Path(keyProbe.RootElement, "results").ValueKind == JsonValueKind.Array) return WebSearchSupport.Supported;
                LastCheckError = "Ollama Web Search non ha restituito risultati validi.";
                return WebSearchSupport.Unverified;
            }
            else
            {
                bool responses = mode != WebSearchMode.GeminiGrounding;
                object payload = responses
                    ? Basic
                        ? new { model = _profile.Model, input = "Usa web_search per trovare una fonte pubblica sulla data odierna UTC e cita la fonte.", tools = new[] { new { type = "web_search" } }, store = false }
                        : new { model = _profile.Model, input = "Cerca sul web una fonte pubblica per la data odierna UTC.", max_output_tokens = 256, tools = new[] { new { type = "web_search", search_context_size = "low" } }, tool_choice = "required", max_tool_calls = 1 }
                    : new { contents = new[] { new { parts = new[] { new { text = "Cerca con Google una fonte pubblica per la data odierna UTC." } } } }, tools = new[] { new { google_search = new { } } }, generationConfig = new { maxOutputTokens = 256 } };
                using var probe = await SendAsync(HttpMethod.Post,
                    responses ? ProviderUrl("responses") : ProviderUrl("models/" + Uri.EscapeDataString(_profile.Model) + ":generateContent"),
                    payload,
                    false, ct).ConfigureAwait(false);
                var root = probe.RootElement;
                if (responses)
                {
                    if (Basic)
                    {
                        ParseResult(mode, root);
                        return WebSearchSupport.Supported;
                    }
                    var output = Path(root, "output");
                    if (output.ValueKind == JsonValueKind.Array && output.EnumerateArray().Any(item => SearchCallCompleted(item, basic: false)))
                        return WebSearchSupport.Supported;
                    LastCheckError = "Il provider non ha completato la ricerca sintetica.";
                    return WebSearchSupport.Unverified;
                }
                var grounding = Path(root, "candidates");
                if (grounding.ValueKind != JsonValueKind.Array || grounding.GetArrayLength() == 0) { LastCheckError = "Gemini non ha restituito candidati."; return WebSearchSupport.Unverified; }
                if (HasGeminiGrounding(Path(grounding[0], "groundingMetadata"))) return WebSearchSupport.Supported;
                LastCheckError = "Gemini ha accettato lo strumento ma non ha eseguito la ricerca sintetica.";
                return WebSearchSupport.Unverified;
            }
        }
        catch (HttpRequestException ex) when ((ex.Message.Contains("400") || ex.Message.Contains("422")) &&
            (ex.Message.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) >= 0 || ex.Message.IndexOf("unsupported", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            LastCheckError = "Il modello non accetta lo strumento Web Search.";
            return WebSearchSupport.Unsupported;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { LastCheckError = ex.Message; return WebSearchSupport.Unverified; }
        LastCheckError = "La sonda non ha restituito una ricerca web eseguita.";
        return WebSearchSupport.Unverified;
    }

    public Task<WebSearchResult> SearchAsync(IEnumerable<ChatMessage> messages, CancellationToken ct = default, string? action = null)
    {
        ct.ThrowIfCancellationRequested();
        // Arbitrary questions, sender domains and locally generated queries are not verified structured inputs.
        return Task.FromException<WebSearchResult>(new OnlyWorkspace.Core.Privacy.DataEgressBlockedException(
            OnlyWorkspace.Core.Privacy.CloudFactsRequest.BlockedMessage));
    }

    internal static WebSearchResult ParseResult(WebSearchMode mode, JsonElement root)
    {
        var result = new WebSearchResult();
        bool basic = mode == WebSearchMode.BasicResponses;
        if (mode is WebSearchMode.OpenAIResponses or WebSearchMode.BasicResponses)
        {
            var output = Path(root, "output");
            bool searched = false, attempted = false;
            if (output.ValueKind == JsonValueKind.Array) foreach (var item in output.EnumerateArray())
            {
                if (Str(item, "type") == "web_search_call")
                {
                    attempted = true;
                    if (SearchCallCompleted(item, basic))
                    {
                        searched = true;
                        var sources = Path(item, "action", "sources");
                        if (sources.ValueKind == JsonValueKind.Array) foreach (var source in sources.EnumerateArray()) AddSource(result, Str(source, "url"), Str(source, "title"));
                    }
                }
                if (Str(item, "type") != "message") continue;
                var content = Path(item, "content");
                if (content.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (Str(part, "type") != "output_text") continue;
                    result.Text += Str(part, "text");
                    var annotations = Path(part, "annotations");
                    if (annotations.ValueKind == JsonValueKind.Array) foreach (var citation in annotations.EnumerateArray()) AddSource(result, Str(citation, "url"), Str(citation, "title"));
                }
            }
            // A failed or unverifiable search stays an error; no search at all is only possible where the tool is not forced.
            if (!searched) throw basic && !attempted
                ? new WebSearchSkippedException("Il modello ha risposto senza eseguire la ricerca web.")
                : new HttpRequestException("Il provider non ha eseguito la ricerca web richiesta.");
        }
        else if (mode == WebSearchMode.AnthropicTool)
        {
            var content = Path(root, "content");
            bool searched = false, attempted = false;
            if (content.ValueKind == JsonValueKind.Array) foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") == "server_tool_use") attempted = true;
                if (Str(block, "type") == "web_search_tool_result")
                {
                    var hits = Path(block, "content");
                    if (hits.ValueKind == JsonValueKind.Object) throw new HttpRequestException("Anthropic Web Search: " + Str(hits, "error_code"));
                    searched = true;
                    if (hits.ValueKind == JsonValueKind.Array) foreach (var hit in hits.EnumerateArray()) AddSource(result, Str(hit, "url"), Str(hit, "title"));
                }
                if (Str(block, "type") != "text") continue;
                result.Text += Str(block, "text");
                var citations = Path(block, "citations");
                if (citations.ValueKind == JsonValueKind.Array) foreach (var citation in citations.EnumerateArray()) AddSource(result, Str(citation, "url"), Str(citation, "title"));
            }
            if (!searched) throw attempted
                ? new HttpRequestException("Anthropic non ha eseguito la ricerca web richiesta.")
                : new WebSearchSkippedException("Il modello ha risposto senza eseguire la ricerca web.");
        }
        else if (mode == WebSearchMode.GeminiGrounding)
        {
            var candidates = Path(root, "candidates");
            if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0) throw new HttpRequestException("Gemini non ha restituito una risposta web.");
            var candidate = candidates[0];
            var parts = Path(candidate, "content", "parts");
            if (parts.ValueKind == JsonValueKind.Array) foreach (var part in parts.EnumerateArray()) result.Text += Str(part, "text");
            var metadata = Path(candidate, "groundingMetadata");
            var chunks = Path(metadata, "groundingChunks");
            if (chunks.ValueKind == JsonValueKind.Array) foreach (var chunk in chunks.EnumerateArray()) AddSource(result, Str(Path(chunk, "web"), "uri"), Str(Path(chunk, "web"), "title"));
            if (!HasGeminiGrounding(metadata)) throw new HttpRequestException("Gemini non ha eseguito Google Search per questa risposta.");
        }
        if (string.IsNullOrWhiteSpace(result.Text)) throw new HttpRequestException("La ricerca web non ha restituito una risposta.");
        if (result.Sources.Count == 0) throw new HttpRequestException("La ricerca web non ha restituito fonti HTTPS verificabili.");
        return result;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, object? payload, bool webKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (payload != null) request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var key = webKey ? _webSecret : _modelSecret;
        var caps = Caps;
        if (!string.IsNullOrWhiteSpace(key))
        {
            // Like the model clients: Anthropic and Gemini always take their key header, whatever AuthType says.
            bool keyHeader = caps.ApiKeyHeader.Length > 0 &&
                (caps.Family is ApiFamily.Anthropic or ApiFamily.Gemini || string.Equals(_profile.AuthType, "apiKey", StringComparison.OrdinalIgnoreCase));
            if (!webKey && keyHeader) request.Headers.TryAddWithoutValidation(caps.ApiKeyHeader, key);
            else if (webKey || caps.Family != ApiFamily.Ollama || string.Equals(_profile.AuthType, "bearer", StringComparison.OrdinalIgnoreCase)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        if (!webKey && caps.Family == ApiFamily.Anthropic) request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        ct.ThrowIfCancellationRequested();
        if (webKey || _profile.IsExternal) _externalRequestStarting?.Invoke();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await LlmHttp.EnsureSuccessAsync(response, Caps.DisplayName + " Web Search", ct).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    private string ProviderUrl(string relative)
    {
        var baseUrl = _profile.BaseUrl.TrimEnd('/') + "/";
        if (Caps.Family == ApiFamily.Ollama) return baseUrl + relative;
        if (Caps.Family == ApiFamily.Gemini) return baseUrl + (baseUrl.TrimEnd('/').EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) ? "" : "v1beta/") + relative;
        return baseUrl + (baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "" : "v1/") + relative;
    }

    private static JsonElement Path(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out root)) return default;
        }
        return root;
    }
    private static bool HasGeminiGrounding(JsonElement metadata)
    {
        var queries = Path(metadata, "webSearchQueries");
        var chunks = Path(metadata, "groundingChunks");
        return queries.ValueKind == JsonValueKind.Array && queries.GetArrayLength() > 0 ||
               chunks.ValueKind == JsonValueKind.Array && chunks.GetArrayLength() > 0;
    }
    private static bool SearchCallCompleted(JsonElement item, bool basic)
    {
        if (Str(item, "type") != "web_search_call") return false;
        var status = Str(item, "status");
        if (!basic) return status == "completed";
        if (Path(item, "status").ValueKind != JsonValueKind.Undefined && status != "completed") return false;
        var sources = Path(item, "action", "sources");
        var result = new WebSearchResult();
        if (sources.ValueKind == JsonValueKind.Array)
            foreach (var source in sources.EnumerateArray()) AddSource(result, Str(source, "url"), Str(source, "title"));
        // QwenCloud can put URLs in final message annotations instead of action.sources.
        // ParseResult still requires a final answer and HTTPS sources before accepting this evidence.
        return result.Sources.Count > 0 ||
            (status == "completed" || (Str(Path(item, "action"), "type") == "search" && !string.IsNullOrWhiteSpace(Str(Path(item, "action"), "query"))));
    }
    private static string Str(JsonElement root, string name)
    {
        var value = Path(root, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }
    private static void AddSource(WebSearchResult result, string url, string title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return;
        if (result.Sources.Any(s => s.Url == uri.AbsoluteUri) || result.Sources.Count >= 8) return;
        result.Sources.Add(new WebSource { Url = uri.AbsoluteUri, Title = string.IsNullOrWhiteSpace(title) ? uri.Host : title.Substring(0, Math.Min(title.Length, 160)) });
    }
    public void Dispose() { if (_ownsHttp || _ownsProbeHttp) _http.Dispose(); }
}
