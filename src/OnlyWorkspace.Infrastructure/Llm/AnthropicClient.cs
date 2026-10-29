using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Infrastructure.Llm;

public sealed class AnthropicClient : ILlmClient, IDisposable
{
    private const string DefaultAnthropicVersion = "2023-06-01";
    private const string RefusalMessage = "Anthropic ha rifiutato la richiesta (filtro di sicurezza del provider).";
    private readonly HttpClient _httpClient;
    private readonly LlmProfileConfig _profile;
    private readonly bool _disposeClient;

    public LlmTokenUsage? LastTokenUsage { get; private set; }
    public LlmCompletion? LastCompletion { get; private set; }

    public AnthropicClient(LlmProfileConfig profile, string? secretToken = null, HttpClient? httpClient = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _disposeClient = httpClient == null;

        _httpClient = httpClient ?? LlmHttp.CreateClient(_profile, "Anthropic");

        if (!string.IsNullOrWhiteSpace(secretToken))
        {
            _httpClient.DefaultRequestHeaders.Remove("x-api-key");
            _httpClient.DefaultRequestHeaders.Add("x-api-key", secretToken);
        }

        if (!_httpClient.DefaultRequestHeaders.Contains("anthropic-version"))
        {
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", DefaultAnthropicVersion);
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
                if (m.Equals(_profile.Model, StringComparison.OrdinalIgnoreCase))
                {
                    hasModel = true;
                    break;
                }
            }

            if (!hasModel && models.Count > 0 && !string.IsNullOrWhiteSpace(_profile.Model))
            {
                return LlmHealthResult.Failure($"Anthropic raggiungibile, ma il modello configurato '{_profile.Model}' non è tra i modelli disponibili.");
            }

            return LlmHealthResult.Success("anthropic", models);
        }
        catch (HttpRequestException ex)
        {
            return LlmHealthResult.Failure($"Errore di rete o HTTP verso Anthropic: {ex.Message}");
        }
        catch (Exception ex)
        {
            return LlmHealthResult.Failure($"Verifica non riuscita: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        // Default page is 20 models; 1000 is the maximum.
        using var response = await _httpClient.GetAsync(GetEndpoint("models?limit=1000"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var list = new List<string>();
        if (doc.RootElement.Arr("data", out var dataElem))
        {
            foreach (var item in dataElem.EnumerateArray())
            {
                var id = item.Str("id");
                if (!string.IsNullOrEmpty(id)) list.Add(id!);
            }
        }
        return list;
    }

    public async Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(GetEndpoint("models/" + Uri.EscapeDataString(model)), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseModelInfo(doc.RootElement);
    }

    /// <summary>Models API "capabilities": https://platform.claude.com/docs/en/api/models (limits may be null).</summary>
    internal static ModelInfo ParseModelInfo(JsonElement root)
    {
        var info = new ModelInfo { ContextWindow = root.Int("max_input_tokens"), MaxOutputTokens = root.Int("max_tokens") };
        if (!root.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object) return info;

        var c = info.Capabilities;
        c.Source = "Anthropic Models API";
        // Client tools belong to the Messages API itself; the capabilities object lists server tools only.
        c.Tools = CapabilityState.Supported;
        c.Reasoning = Supported(caps, "thinking");
        if (caps.TryGetProperty("effort", out var effort))
        {
            foreach (var level in new[] { "low", "medium", "high", "xhigh", "max" })
                if (Supported(effort, level) == CapabilityState.Supported) c.ReasoningLevels.Add(level);
        }
        c.WebSearch = caps.TryGetProperty("server_tools", out var server) ? Supported(server, "web_search") : CapabilityState.Unknown;
        c.Vision = Supported(caps, "image_input");
        c.StructuredOutput = Supported(caps, "structured_outputs");
        return info;
    }

    private static CapabilityState Supported(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty("supported", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? (flag.GetBoolean() ? CapabilityState.Supported : CapabilityState.Unsupported)
            : CapabilityState.Unknown;

    public async IAsyncEnumerable<string> StreamChatAsync(
        IEnumerable<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastTokenUsage = null;
        LastCompletion = null;
        var messageList = new List<ChatMessage>(messages);
        using var reader = await OpenStreamAsync(messageList, null, cancellationToken).ConfigureAwait(false);
        await foreach (var chunk in ReadDeltasAsync(reader, null, cancellationToken).ConfigureAwait(false)) yield return chunk;
    }

    private async IAsyncEnumerable<string> ReadDeltasAsync(
        StreamLineReader reader,
        StringBuilder? plain,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? line;
        int? inputTokens = null;
        int? outputTokens = null;
        string? stopReason = null;
        bool anyText = false;
        bool completed = false;
        bool streamed = false;

        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (plain != null && !string.IsNullOrWhiteSpace(line) &&
                    !line.StartsWith("event:", StringComparison.OrdinalIgnoreCase) && !line.StartsWith(":")) plain.AppendLine(line);
                continue;
            }
            streamed = true;

            var data = line.Substring(5).Trim();
            if (data.Length == 0) continue;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = root.Str("type");

            if (type == "message_start")
            {
                if (root.Obj("message", out var msgElem) && msgElem.Obj("usage", out var usageElem))
                {
                    inputTokens = usageElem.ObservedTokens("input_tokens") ?? inputTokens;
                    // message_start output is only the initial count; wait for a message_delta observation.
                }
            }
            else if (type == "message_delta")
            {
                // output_tokens is cumulative and includes thinking tokens.
                if (root.Obj("usage", out var usageElem))
                {
                    inputTokens = usageElem.ObservedTokens("input_tokens") ?? inputTokens;
                    outputTokens = usageElem.ObservedTokens("output_tokens") ?? outputTokens;
                }
                if (root.Obj("delta", out var deltaElem)) stopReason = deltaElem.Str("stop_reason") ?? stopReason;
            }
            else if (type == "content_block_delta")
            {
                // thinking_delta / signature_delta carry no "text".
                var chunk = root.Obj("delta", out var deltaElem) ? deltaElem.Str("text") : null;
                if (!string.IsNullOrEmpty(chunk))
                {
                    anyText = true;
                    yield return chunk!;
                }
            }
            else if (type == "error")
            {
                var errorMsg = root.Obj("error", out var errElem) ? errElem.Str("message") : null;
                throw new HttpRequestException($"Anthropic stream error: {errorMsg ?? "Unknown stream error"}");
            }
            else if (type == "message_stop")
            {
                completed = true;
                break;
            }
        }

        if (streamed)
        {
            SetUsage(inputTokens, outputTokens);
            SetCompletion(stopReason);
        }
        if (stopReason == "refusal" && (!anyText || plain != null)) throw new HttpRequestException(RefusalMessage);
        // Without message_stop the stream was cut: its text must not pass for a whole answer.
        if (streamed && !completed) throw new HttpRequestException("Anthropic ha interrotto il flusso prima della conferma di completamento.");
        if (!streamed && plain == null) throw new HttpRequestException("Anthropic non ha restituito un flusso di risposta.");
    }

    public async Task<string> CompleteStructuredAsync(
        IEnumerable<ChatMessage> messages,
        string jsonSchema,
        CancellationToken cancellationToken = default)
    {
        LastTokenUsage = null;
        LastCompletion = null;
        var messageList = new List<ChatMessage>(messages);
        using var reader = await OpenStreamAsync(messageList, jsonSchema, cancellationToken).ConfigureAwait(false);
        var plain = new StringBuilder();
        var text = new StringBuilder();
        await foreach (var chunk in ReadDeltasAsync(reader, plain, cancellationToken).ConfigureAwait(false)) { text.Append(chunk); StructuredProgress.Report(chunk); }
        if (plain.Length == 0)
        {
            if (text.Length == 0) throw new HttpRequestException("Anthropic non ha restituito una risposta strutturata.");
            return LlmHttp.StripCodeFence(text.ToString());
        }

        // Compatible gateways may return a complete JSON response despite stream=true.
        using var doc = JsonDocument.Parse(plain.ToString());
        var root = doc.RootElement;

        var stopReason = root.Str("stop_reason");
        if (root.Obj("usage", out var usageElem))
        {
            SetUsage(usageElem.ObservedTokens("input_tokens"), usageElem.ObservedTokens("output_tokens"));
        }
        SetCompletion(stopReason);
        if (stopReason == "refusal") throw new HttpRequestException(RefusalMessage);

        // Models that think put a "thinking" block before the text one.
        if (root.Arr("content", out var contentElem))
        {
            foreach (var block in contentElem.EnumerateArray())
            {
                if (block.Str("type") == "text") text.Append(block.Str("text"));
            }
        }

        if (text.Length == 0) throw new HttpRequestException("Anthropic non ha restituito una risposta strutturata.");
        return LlmHttp.StripCodeFence(text.ToString());
    }

    private void SetUsage(int? inputTokens, int? outputTokens)
    {
        // A partial observation must not clear a cloud card's unresolved attempt.
        LastTokenUsage = inputTokens.HasValue && outputTokens.HasValue ? new LlmTokenUsage
        {
            PromptTokens = inputTokens.Value,
            CompletionTokens = outputTokens.Value
        } : null;
    }

    private void SetCompletion(string? stopReason)
    {
        if (stopReason == null) return;
        LastCompletion = new LlmCompletion
        {
            IsTruncated = stopReason == "max_tokens" || stopReason == "model_context_window_exceeded",
            Reason = stopReason
        };
    }

    [Flags]
    private enum ModelQuirks { None = 0, NoTemperature = 1, NoOutputFormat = 2 }

    /// <summary>
    /// Per model, learned from HTTP 400: current models reject temperature (Opus 4.7+, Sonnet 5, Fable; non-default
    /// values on Sonnet 5.5 and Haiku 5.5), older ones do not know output_config.format.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ModelQuirks> Quirks = new(StringComparer.OrdinalIgnoreCase);

    private ModelQuirks KnownQuirks => Quirks.TryGetValue(_profile.Model, out var q) ? q : ModelQuirks.None;

    // max_tokens is mandatory; thinking tokens count against the streaming allowance.
    private const int StreamMaxTokens = 64000;
    private static readonly Regex OutputCapRegex = new(@"max_tokens\D+\d+\s*>\s*(\d+)", RegexOptions.Compiled);

    /// <summary>Output cap of older models, learned from the HTTP 400 that states it ("max_tokens: 64000 > 8192").</summary>
    private static readonly ConcurrentDictionary<string, int> OutputCaps = new(StringComparer.OrdinalIgnoreCase);

    private int MaxTokens()
    {
        return OutputCaps.TryGetValue(_profile.Model, out var cap) ? Math.Min(cap, StreamMaxTokens) : StreamMaxTokens;
    }

    /// <summary>Drops the rejected parameter; true when the request is worth one more attempt.</summary>
    private bool TryAdapt(LlmRequestException ex)
    {
        if (ex.Status != 400) return false;
        var capMatch = OutputCapRegex.Match(ex.Message);
        if (capMatch.Success && int.TryParse(capMatch.Groups[1].Value, out var cap) && cap > 0)
        {
            // Worth another attempt only if the cap is news.
            bool known = OutputCaps.TryGetValue(_profile.Model, out var old) && old <= cap;
            if (!known) OutputCaps[_profile.Model] = cap;
            return !known;
        }

        var quirk = ModelQuirks.None;
        if (ex.Message.IndexOf("temperature", StringComparison.OrdinalIgnoreCase) >= 0) quirk = ModelQuirks.NoTemperature;
        else if (ex.Message.IndexOf("output_config", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 ex.Message.IndexOf("schema", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 ex.Message.IndexOf("structured", StringComparison.OrdinalIgnoreCase) >= 0) quirk = ModelQuirks.NoOutputFormat;

        var quirks = KnownQuirks;
        if (quirk == ModelQuirks.None || (quirks & quirk) != 0) return false;
        Quirks[_profile.Model] = quirks | quirk;
        return true;
    }

    private async Task<StreamLineReader> OpenStreamAsync(List<ChatMessage> messages, string? jsonSchema, CancellationToken cancellationToken)
    {
        while (true)
        {
            messages = ModelRequestContext.Prepare(_profile, messages, jsonSchema);
            var json = JsonSerializer.Serialize(BuildMessagesPayload(messages, jsonSchema));
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, GetEndpoint("messages")) { Content = content };
            try
            {
                return await StreamLineReader.OpenAsync(_httpClient, request, _profile, EnsureSuccessOrThrowAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (LlmRequestException ex) when (TryAdapt(ex))
            {
            }
        }
    }

    private Dictionary<string, object> BuildMessagesPayload(IEnumerable<ChatMessage> messages, string? jsonSchema)
    {
        var systemBuilder = new StringBuilder();
        var msgList = new List<object>();

        foreach (var m in messages)
        {
            if (m.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                if (systemBuilder.Length > 0) systemBuilder.Append("\n\n");
                systemBuilder.Append(m.Content);
            }
            else
            {
                var role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                msgList.Add(new { role = role, content = m.Content });
            }
        }

        if (jsonSchema != null)
        {
            // Kept with output_config.format too: it is the only constraint on models without structured outputs.
            if (systemBuilder.Length > 0) systemBuilder.Append("\n\n");
            systemBuilder.Append("IMPORTANT: Respond ONLY with a valid JSON object matching this schema or structure:\n");
            systemBuilder.Append(jsonSchema);
            systemBuilder.Append("\nDo not include any explanation or markdown formatting outside the JSON.");
        }

        if (msgList.Count == 0)
        {
            msgList.Add(new { role = "user", content = "Process request." });
        }

        var quirks = KnownQuirks;
        var payload = new Dictionary<string, object>
        {
            ["model"] = _profile.Model,
            ["max_tokens"] = MaxTokens(),
            ["stream"] = true,
            ["messages"] = msgList
        };

        if ((quirks & ModelQuirks.NoTemperature) == 0)
        {
            payload["temperature"] = Math.Min(1.0, Math.Max(0.0, _profile.Temperature));
        }

        if (systemBuilder.Length > 0)
        {
            payload["system"] = systemBuilder.ToString();
        }

        if (jsonSchema != null && (quirks & ModelQuirks.NoOutputFormat) == 0 && TryParseObjectSchema(jsonSchema) is { } schema)
        {
            payload["output_config"] = new Dictionary<string, object>
            {
                ["format"] = new Dictionary<string, object> { ["type"] = "json_schema", ["schema"] = schema }
            };
        }

        return payload;
    }

    /// <summary>Schema for output_config.format (needs additionalProperties:false); null when it is not an object schema.</summary>
    private static JsonNode? TryParseObjectSchema(string jsonSchema)
    {
        try
        {
            var schema = JsonNode.Parse(jsonSchema);
            if (schema is not JsonObject obj || obj["properties"] is not JsonObject) return null;
            LlmHttp.MakeSchemaStrict(schema, allRequired: false);
            return schema;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken ct) => LlmHttp.EnsureSuccessAsync(response, "Anthropic", ct);

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
