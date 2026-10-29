using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public sealed class OpenAIClient : ILlmClient, IReasoningObserver, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LlmProfileConfig _profile;
    private readonly bool _disposeClient;

    public LlmTokenUsage? LastTokenUsage { get; private set; }
    public LlmCompletion? LastCompletion { get; private set; }
    public bool ReasoningObserved => System.Threading.Volatile.Read(ref _reasoningObserved);
    private bool _reasoningObserved;

    public OpenAIClient(LlmProfileConfig profile, string? secretToken = null, HttpClient? httpClient = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _disposeClient = httpClient == null;

        _httpClient = httpClient ?? LlmHttp.CreateClient(_profile, "OpenAI");

        if (!string.IsNullOrWhiteSpace(secretToken))
        {
            // Azure OpenAI takes a key in "api-key"; every other OpenAI-compatible service a bearer token.
            if (string.Equals(profile.AuthType, "apiKey", StringComparison.OrdinalIgnoreCase) && Caps.ApiKeyHeader.Length > 0)
            {
                _httpClient.DefaultRequestHeaders.Remove(Caps.ApiKeyHeader);
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(Caps.ApiKeyHeader, secretToken);
            }
            else
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretToken);
            }
        }
    }

    private ProviderCapabilities Caps => ProviderCatalog.Get(_profile.Type);

    public async Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await ListModelsAsync(cancellationToken).ConfigureAwait(false);
            bool hasModel = false;
            foreach (var m in models)
            {
                if (m.Equals(_profile.Model, StringComparison.OrdinalIgnoreCase))
                {
                    hasModel = true;
                    break;
                }
            }

            if (!hasModel && models.Count > 0 && !string.IsNullOrWhiteSpace(_profile.Model))
            {
                return LlmHealthResult.Failure($"OpenAI raggiungibile, ma il modello configurato '{_profile.Model}' non è tra i modelli disponibili.");
            }

            return LlmHealthResult.Success("openai", models);
        }
        catch (HttpRequestException ex)
        {
            return LlmHealthResult.Failure($"Errore di rete o HTTP verso OpenAI: {ex.Message}");
        }
        catch (Exception ex)
        {
            return LlmHealthResult.Failure($"Verifica non riuscita: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = GetEndpoint("models");
        using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataElem.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.GetString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        list.Add(id!);
                    }
                }
            }
        }
        return list;
    }

    /// <summary>
    /// OpenAI's own models endpoint declares no limits or capabilities; some compatible servers publish them
    /// on their own endpoints (see ProviderCatalog.DocsUrl of each connector).
    /// </summary>
    public async Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
    {
        var source = Caps.Capabilities;
        string? url = source switch
        {
            CapabilitySource.OpenRouter or CapabilitySource.Vllm => GetEndpoint("models"),
            CapabilitySource.Mistral => GetEndpoint("models/" + Uri.EscapeDataString(model)),
            CapabilitySource.LmStudio => "/api/v1/models",
            CapabilitySource.LlamaCpp => "/props",
            _ => null
        };
        if (url == null) return null;

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseModelInfo(source, doc.RootElement, model);
    }

    internal static ModelInfo? ParseModelInfo(CapabilitySource source, JsonElement root, string model)
    {
        var info = new ModelInfo();
        var c = info.Capabilities;
        switch (source)
        {
            case CapabilitySource.OpenRouter:
            {
                // https://openrouter.ai/docs/api/api-reference/models/get-models
                var item = FindModel(root, "data", "id", model);
                if (item == null) return null;
                var m = item.Value;
                info.ContextWindow = m.Int("context_length");
                if (m.TryGetProperty("top_provider", out var top)) info.MaxOutputTokens = top.Int("max_completion_tokens");
                var parameters = Strings(m, "supported_parameters");
                c.Source = "OpenRouter models";
                c.Tools = Has(parameters, "tools");
                c.Reasoning = Has(parameters, "reasoning", "include_reasoning", "reasoning_effort");
                c.StructuredOutput = Has(parameters, "structured_outputs", "response_format");
                if (m.TryGetProperty("reasoning", out var reasoning)) c.ReasoningLevels.AddRange(Strings(reasoning, "supported_efforts"));
                if (m.TryGetProperty("architecture", out var arch)) c.Vision = Has(Strings(arch, "input_modalities"), "image");
                return info;
            }
            case CapabilitySource.Mistral:
            {
                // https://docs.mistral.ai/api/endpoint/models
                info.ContextWindow = root.Int("max_context_length");
                if (!root.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object) return info;
                c.Source = "Mistral models";
                c.Tools = Flag(caps, "function_calling");
                c.Vision = Flag(caps, "vision");
                return info;
            }
            case CapabilitySource.LmStudio:
            {
                // https://lmstudio.ai/docs/developer/rest/list ("key" identifies the model)
                var item = FindModel(root, "models", "key", model);
                if (item == null) return null;
                var m = item.Value;
                info.ContextWindow = m.Int("max_context_length");
                if (!m.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object) return info;
                c.Source = "LM Studio api/v1/models";
                c.Tools = Flag(caps, "trained_for_tool_use");
                c.Vision = Flag(caps, "vision");
                if (caps.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
                {
                    c.Reasoning = CapabilityState.Supported;
                    c.ReasoningLevels.AddRange(Strings(reasoning, "allowed_options").Where(o => o != "off" && o != "on"));
                }
                else c.Reasoning = CapabilityState.Unsupported;
                return info;
            }
            case CapabilitySource.LlamaCpp:
            {
                // https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md (GET /props)
                if (root.TryGetProperty("default_generation_settings", out var settings)) info.ContextWindow = settings.Int("n_ctx");
                c.Source = "llama.cpp /props";
                if (root.TryGetProperty("chat_template_caps", out var caps)) c.Tools = Flag(caps, "supports_tools");
                if (root.TryGetProperty("modalities", out var modalities)) c.Vision = Flag(modalities, "vision");
                return info;
            }
            case CapabilitySource.Vllm:
            {
                var item = FindModel(root, "data", "id", model);
                if (item == null) return null;
                info.ContextWindow = item.Value.Int("max_model_len");
                return info;
            }
            default:
                return null;
        }
    }

    private static JsonElement? FindModel(JsonElement root, string array, string key, string model)
    {
        if (!root.TryGetProperty(array, out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in list.EnumerateArray())
            if (string.Equals(item.Str(key), model, StringComparison.OrdinalIgnoreCase)) return item;
        return null;
    }

    private static List<string> Strings(JsonElement parent, string name)
    {
        var list = new List<string>();
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        return list;
    }

    private static CapabilityState Has(List<string> values, params string[] any) =>
        any.Any(a => values.Contains(a, StringComparer.OrdinalIgnoreCase)) ? CapabilityState.Supported : CapabilityState.Unsupported;

    private static CapabilityState Flag(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? (value.GetBoolean() ? CapabilityState.Supported : CapabilityState.Unsupported)
            : CapabilityState.Unknown;

    public async IAsyncEnumerable<string> StreamChatAsync(
        IEnumerable<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastTokenUsage = null;
        LastCompletion = null;
        var messageList = new List<ChatMessage>(messages);
        using var reader = await OpenStreamAsync(messageList, null, cancellationToken).ConfigureAwait(false);
        await foreach (var chunk in ReadDeltasAsync(reader, null, null, cancellationToken).ConfigureAwait(false)) yield return chunk;
    }

    /// <summary>
    /// Text deltas of a streamed completion. <paramref name="plain"/> collects the body of a server that
    /// ignored "stream" and answered with one JSON document; <paramref name="refusal"/> the refusal text.
    /// </summary>
    private async IAsyncEnumerable<string> ReadDeltasAsync(
        StreamLineReader reader,
        StringBuilder? plain,
        StringBuilder? refusal,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? line;
        bool streamed = false;
        string? finishReason = null;
        int? observedPrompt = null, observedCompletion = null;
        System.Threading.Volatile.Write(ref _reasoningObserved, false);

        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var data = line.Substring(5).Trim();
                streamed = true;
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) break;
                if (data.Length == 0) continue;

                using var doc = JsonDocument.Parse(data);

                // Compatible servers report a failure after the 200 status as a chunk {"error":..}.
                if (doc.RootElement.TryGetProperty("error", out var streamError) && streamError.ValueKind != JsonValueKind.Null)
                {
                    var detail = streamError.ValueKind == JsonValueKind.String ? streamError.GetString() : streamError.Str("message");
                    throw new HttpRequestException("OpenAI ha interrotto la risposta: " + (string.IsNullOrEmpty(detail) ? "errore nel flusso" : detail));
                }

                // With include_usage every chunk carries "usage": null except the last one.
                if (doc.RootElement.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
                {
                    observedPrompt = usageElem.ObservedTokens("prompt_tokens") ?? observedPrompt;
                    observedCompletion = usageElem.ObservedTokens("completion_tokens") ?? observedCompletion;
                    SetUsage(observedPrompt, observedCompletion);
                    // Reasoning models that hide the text still count it (OpenAI o-series, GPT-5).
                    if (usageElem.Obj("completion_tokens_details", out var details) && details.Int("reasoning_tokens") > 0)
                        System.Threading.Volatile.Write(ref _reasoningObserved, true);
                }

                if (doc.RootElement.TryGetProperty("choices", out var choicesElem) &&
                    choicesElem.ValueKind == JsonValueKind.Array &&
                    choicesElem.GetArrayLength() > 0)
                {
                    var firstChoice = choicesElem[0];
                    if (firstChoice.TryGetProperty("finish_reason", out var finishElem) && finishElem.ValueKind == JsonValueKind.String)
                    {
                        finishReason = finishElem.GetString();
                        LastCompletion = new LlmCompletion
                        {
                            IsTruncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase),
                            Reason = finishReason
                        };
                    }

                    if (firstChoice.TryGetProperty("delta", out var deltaElem) && deltaElem.ValueKind == JsonValueKind.Object)
                    {
                        if (refusal != null && deltaElem.TryGetProperty("refusal", out var refusalElem) && refusalElem.ValueKind == JsonValueKind.String)
                        {
                            refusal.Append(refusalElem.GetString());
                        }
                        // Qwen and DeepSeek stream "reasoning_content", OpenRouter "reasoning": observed, never shown.
                        if (!string.IsNullOrEmpty(deltaElem.Str("reasoning_content")) || !string.IsNullOrEmpty(deltaElem.Str("reasoning")))
                            System.Threading.Volatile.Write(ref _reasoningObserved, true);
                        if (deltaElem.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.String)
                        {
                            var chunk = contentElem.GetString();
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                yield return chunk!;
                            }
                        }
                    }
                }
            }
            else if (plain != null && (plain.Length > 0 || line.TrimStart().StartsWith("{", StringComparison.Ordinal)))
            {
                plain.AppendLine(line);
            }
        }

        // A stream closed without finish_reason was cut: its text must not pass for a whole answer.
        // A refusal is reported by the caller with its own text.
        if (streamed && finishReason == null && (refusal == null || refusal.Length == 0))
            throw new HttpRequestException("OpenAI ha interrotto il flusso prima della conferma di completamento.");
        if (!streamed && plain == null) throw new HttpRequestException("OpenAI non ha restituito un flusso di risposta.");
    }

    public async Task<string> CompleteStructuredAsync(
        IEnumerable<ChatMessage> messages,
        string jsonSchema,
        CancellationToken cancellationToken = default)
    {
        LastTokenUsage = null;
        LastCompletion = null;
        var messageList = new List<ChatMessage>(messages);
        // Streamed like a chat: a structured answer over a long document can take minutes, and a request that
        // is not streamed must arrive whole within one wait. First-token and idle timeouts bound it instead.
        using var reader = await OpenStreamAsync(messageList, jsonSchema, cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder();
        var plain = new StringBuilder();
        var refusal = new StringBuilder();
        await foreach (var chunk in ReadDeltasAsync(reader, plain, refusal, cancellationToken).ConfigureAwait(false)) { text.Append(chunk); StructuredProgress.Report(chunk); }

        // Structured outputs: a declined request carries "refusal" and no content.
        if (refusal.Length > 0) throw new HttpRequestException("OpenAI ha rifiutato la richiesta: " + refusal);
        if (text.Length > 0) return LlmHttp.StripCodeFence(text.ToString());
        if (plain.Length > 0) return ReadCompletion(plain.ToString());
        throw new HttpRequestException(NoStructuredAnswer);
    }

    /// <summary>Answer of a completion that was not streamed (a compatible server that ignores "stream").</summary>
    private string ReadCompletion(string body)
    {
        using var doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
        {
            SetUsage(usageElem.ObservedTokens("prompt_tokens"), usageElem.ObservedTokens("completion_tokens"));
        }

        if (doc.RootElement.TryGetProperty("choices", out var choicesElem) &&
            choicesElem.ValueKind == JsonValueKind.Array &&
            choicesElem.GetArrayLength() > 0)
        {
            var firstChoice = choicesElem[0];
            if (firstChoice.TryGetProperty("finish_reason", out var finishElem) && finishElem.ValueKind == JsonValueKind.String)
            {
                var finishReason = finishElem.GetString();
                LastCompletion = new LlmCompletion
                {
                    IsTruncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase),
                    Reason = finishReason
                };
            }

            if (firstChoice.Obj("message", out var msgElem))
            {
                // Structured outputs: a declined request has "refusal" set and no content.
                var refusal = msgElem.Str("refusal");
                if (!string.IsNullOrEmpty(refusal)) throw new HttpRequestException("OpenAI ha rifiutato la richiesta: " + refusal);

                var text = msgElem.Str("content");
                // Compatible servers in json_object mode may still wrap the answer in a code fence.
                if (!string.IsNullOrEmpty(text)) return LlmHttp.StripCodeFence(text!);
            }
        }

        throw new HttpRequestException(NoStructuredAnswer);
    }

    private const string NoStructuredAnswer = "OpenAI non ha restituito una risposta strutturata.";

    /// <summary>Models that rejected temperature with an HTTP 400 (reasoning models: GPT-5, o-series).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoTemperature = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops the rejected parameter; true when the request is worth one more attempt.</summary>
    private bool TryAdapt(LlmRequestException ex) =>
        ex.Status == 400 && ex.Param == "temperature" && NoTemperature.TryAdd(_profile.Model, true);

    private async Task<StreamLineReader> OpenStreamAsync(List<ChatMessage> messages, string? jsonSchema, CancellationToken cancellationToken)
    {
        while (true)
        {
            messages = ModelRequestContext.Prepare(_profile, messages, jsonSchema);
            var json = JsonSerializer.Serialize(BuildChatPayload(messages, stream: true, jsonSchema: jsonSchema));
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, GetEndpoint(Caps.ChatPath)) { Content = content };
            try
            {
                return await StreamLineReader.OpenAsync(_httpClient, request, _profile, EnsureSuccessOrThrowAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (LlmRequestException ex) when (TryAdapt(ex))
            {
            }
        }
    }

    private Dictionary<string, object> BuildChatPayload(IEnumerable<ChatMessage> messages, bool stream, string? jsonSchema = null)
    {
        var msgList = new List<object>();
        foreach (var m in messages)
        {
            msgList.Add(new { role = m.Role, content = m.Content });
        }

        // No output limit: the provider applies the model's own.
        var payload = new Dictionary<string, object>
        {
            ["model"] = _profile.Model,
            ["stream"] = stream,
            ["messages"] = msgList
        };

        if (!NoTemperature.ContainsKey(_profile.Model))
        {
            payload["temperature"] = _profile.Temperature;
        }

        // Qwen 3.5-3.8 think by default and bill it as output (model-studio/deep-thinking): 13,000 tokens for one chat
        // answer on 20/10/2026. enable_thinking is a top-level field on the compatible endpoint.
        if (Caps.Type == "qwen")
        {
            payload["enable_thinking"] = !_profile.DisableThinking;
        }

        if (stream)
        {
            payload["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
        }

        if (jsonSchema != null)
        {
            if (!string.IsNullOrWhiteSpace(jsonSchema))
            {
                try
                {
                    var schema = System.Text.Json.Nodes.JsonNode.Parse(jsonSchema)!;
                    // Optional properties become nullable, so the model can still leave them out (as null).
                    LlmHttp.MakeSchemaStrict(schema, allRequired: true);
                    payload["response_format"] = new Dictionary<string, object>
                    {
                        ["type"] = "json_schema",
                        ["json_schema"] = new Dictionary<string, object>
                        {
                            ["name"] = "response_format",
                            ["strict"] = true,
                            ["schema"] = schema
                        }
                    };
                }
                catch
                {
                    payload["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };
                }
            }
            else
            {
                payload["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };
            }
        }

        return payload;
    }

    private string GetEndpoint(string relative)
    {
        var baseStr = _profile.BaseUrl.TrimEnd('/');
        if (baseStr.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return relative;
        }
        return "v1/" + relative;
    }

    private static Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken ct) => LlmHttp.EnsureSuccessAsync(response, "OpenAI", ct);

    private void SetUsage(int? prompt, int? completion) =>
        LastTokenUsage = prompt.HasValue && completion.HasValue
            ? new LlmTokenUsage { PromptTokens = prompt.Value, CompletionTokens = completion.Value } : null;

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
