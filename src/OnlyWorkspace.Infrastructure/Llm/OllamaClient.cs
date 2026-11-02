using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Infrastructure.Llm;

public sealed class OllamaClient : ILlmClient, IReasoningObserver, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LlmProfileConfig _profile;
    private readonly bool _disposeClient;

    public LlmTokenUsage? LastTokenUsage { get; private set; }
    public LlmCompletion? LastCompletion { get; private set; }
    public bool ReasoningObserved => System.Threading.Volatile.Read(ref _reasoningObserved);
    private bool _reasoningObserved;

    public OllamaClient(LlmProfileConfig profile, string? secretToken = null, HttpClient? httpClient = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _disposeClient = httpClient == null;

        _httpClient = httpClient ?? LlmHttp.CreateClient(_profile, "Ollama");

        if (!string.IsNullOrWhiteSpace(secretToken) && profile.AuthType.Equals("bearer", StringComparison.OrdinalIgnoreCase))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretToken);
        }
    }

    public async Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await ListModelsAsync(cancellationToken).ConfigureAwait(false);
            bool hasModel = false;
            foreach (var m in models)
            {
                if (m.Equals(_profile.Model, StringComparison.OrdinalIgnoreCase) || m.StartsWith(_profile.Model + ":", StringComparison.OrdinalIgnoreCase))
                {
                    hasModel = true;
                    break;
                }
            }

            if (!hasModel && models.Count > 0)
            {
                return LlmHealthResult.ModelMissing("ollama", models, $"Server raggiungibile, ma il modello configurato '{_profile.Model}' non è installato.");
            }

            return LlmHealthResult.Success("ollama", models);
        }
        catch (Exception ex)
        {
            Logging.OnlyWorkspaceLog.Logger.Warning(ex, "Ollama connection check failed");
            return LlmHealthResult.Failure(LlmErrorText.Describe(ex, _profile));
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("api/tags", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("models", out var modelsElem) && modelsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modelsElem.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var nameProp))
                {
                    var name = nameProp.GetString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        list.Add(name!);
                    }
                }
            }
        }
        return list;
    }

    /// <summary>Maximum context the model supports (model_info "*.context_length"), not the num_ctx in use.</summary>
    public async Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { model }), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("api/show", content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseModelInfo(doc.RootElement);
    }

    /// <summary>
    /// api/show "capabilities" values: https://github.com/ollama/ollama/blob/main/types/model/capability.go.
    /// Web search also needs the ollama.com key, so tool calling alone leaves it unknown.
    /// </summary>
    internal static ModelInfo ParseModelInfo(JsonElement root)
    {
        var info = new ModelInfo();
        if (root.TryGetProperty("model_info", out var modelInfo) && modelInfo.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in modelInfo.EnumerateObject())
            {
                if (property.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase) && property.Value.TryGetInt32(out var window))
                {
                    info.ContextWindow = window;
                    break;
                }
            }
        }
        if (root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in caps.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) set.Add(item.GetString()!);
            var c = info.Capabilities;
            c.Source = "Ollama api/show";
            c.Tools = set.Contains("tools") ? CapabilityState.Supported : CapabilityState.Unsupported;
            c.Reasoning = set.Contains("thinking") ? CapabilityState.Supported : CapabilityState.Unsupported;
            c.Vision = set.Contains("vision") ? CapabilityState.Supported : CapabilityState.Unsupported;
            if (c.Tools == CapabilityState.Unsupported) c.WebSearch = CapabilityState.Unsupported;
        }
        return info;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        IEnumerable<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastTokenUsage = null;
        LastCompletion = null;
        await foreach (var chunk in ReadChatAsync(messages, null, cancellationToken).ConfigureAwait(false)) yield return chunk;
    }

    private async IAsyncEnumerable<string> ReadChatAsync(
        IEnumerable<ChatMessage> messages,
        object? format,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        messages = ModelRequestContext.Prepare(_profile, OnlyWorkspace.Core.Privacy.CloudRequestGuard.Validate(_profile, messages, format == null ? null : JsonSerializer.Serialize(format)), format == null ? null : JsonSerializer.Serialize(format));
        var requestPayload = BuildChatPayload(messages, stream: true, format: format);
        var json = JsonSerializer.Serialize(requestPayload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = content };
        using var reader = await StreamLineReader.OpenAsync(_httpClient, request, _profile, EnsureSuccessOrThrowAsync, cancellationToken).ConfigureAwait(false);

        string? line;
        int? observedPrompt = null, observedCompletion = null;
        System.Threading.Volatile.Write(ref _reasoningObserved, false);
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            observedPrompt = doc.RootElement.ObservedTokens("prompt_eval_count") ?? observedPrompt;
            observedCompletion = doc.RootElement.ObservedTokens("eval_count") ?? observedCompletion;

            // A failure after the stream started arrives as a last line {"error":".."} (the status is already 200).
            var streamError = doc.RootElement.Str("error");
            if (streamError != null)
            {
                throw new HttpRequestException($"Ollama ha interrotto la risposta: {streamError}");
            }

            // Thinking models stream "thinking" before the answer when the profile allows it: observed, never shown.
            if (doc.RootElement.Obj("message", out var thinkingElem) && !string.IsNullOrEmpty(thinkingElem.Str("thinking")))
                System.Threading.Volatile.Write(ref _reasoningObserved, true);
            if (doc.RootElement.TryGetProperty("message", out var msgElem) &&
                msgElem.TryGetProperty("content", out var contentElem))
            {
                var chunk = contentElem.GetString();
                if (!string.IsNullOrEmpty(chunk))
                {
                    yield return chunk!;
                }
            }

            if (doc.RootElement.TryGetProperty("done", out var doneElem) && doneElem.GetBoolean())
            {
                string? doneReason = null;
                if (doc.RootElement.TryGetProperty("done_reason", out var drProp))
                {
                    doneReason = drProp.GetString();
                }
                bool isTruncated = string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase) ||
                                   (ProviderCatalog.Get(_profile.Type).SupportsNumCtx && _profile.NumCtx > 0 &&
                                    observedPrompt.HasValue && observedCompletion.HasValue && (long)observedPrompt.Value + observedCompletion.Value >= _profile.NumCtx);

                LastTokenUsage = observedPrompt.HasValue && observedCompletion.HasValue
                    ? new LlmTokenUsage { PromptTokens = observedPrompt.Value, CompletionTokens = observedCompletion.Value } : null;
                LastCompletion = new LlmCompletion { IsTruncated = isTruncated, Reason = doneReason };
                yield break;
            }
        }
        throw new HttpRequestException("Ollama ha interrotto il flusso prima della conferma di completamento.");
    }

    public async Task<string> CompleteStructuredAsync(
        IEnumerable<ChatMessage> messages,
        string jsonSchema,
        CancellationToken cancellationToken = default)
    {
        LastTokenUsage = null;
        LastCompletion = null;
        var text = new StringBuilder();
        await foreach (var chunk in ReadChatAsync(messages, SchemaOrJson(jsonSchema), cancellationToken).ConfigureAwait(false)) { text.Append(chunk); StructuredProgress.Report(chunk); }
        if (text.Length == 0) throw new HttpRequestException("Ollama non ha restituito una risposta strutturata.");
        return LlmHttp.StripCodeFence(text.ToString());
    }

    /// <summary>
    /// Ollama structured outputs: a JSON schema in "format" constrains keys and enums. With plain "json"
    /// the model may invent keys that do not match the structured response parser.
    /// </summary>
    private static object SchemaOrJson(string jsonSchema)
    {
        if (!string.IsNullOrWhiteSpace(jsonSchema))
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonSchema);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not a schema: plain JSON mode.
            }
        }
        return "json";
    }

    private Dictionary<string, object> BuildChatPayload(IEnumerable<ChatMessage> messages, bool stream, object? format = null)
    {
        var msgList = new List<object>();
        foreach (var m in messages)
        {
            msgList.Add(new { role = m.Role, content = m.Content });
        }

        // Ollama Cloud runs models at full context: only a local server takes num_ctx, num_predict and keep_alive.
        var caps = ProviderCatalog.Get(_profile.Type);
        var options = new Dictionary<string, object> { ["temperature"] = _profile.Temperature };
        if (caps.SupportsNumCtx)
        {
            options["num_ctx"] = _profile.NumCtx;
            options["num_predict"] = _profile.MaxOutputTokens;
        }
        var payload = new Dictionary<string, object>
        {
            ["model"] = _profile.Model,
            ["stream"] = stream,
            ["options"] = options,
            ["messages"] = msgList
        };
        if (caps.SupportsKeepAlive) payload["keep_alive"] = _profile.KeepAlive;

        // Thinking tokens count against num_predict and are not shown: off unless the profile allows them.
        if (_profile.DisableThinking)
        {
            payload["think"] = false;
        }

        if (format != null)
        {
            payload["format"] = format;
        }

        return payload;
    }

    private static Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken ct) => LlmHttp.EnsureSuccessAsync(response, "Ollama", ct);

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
