using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Infrastructure.Llm;

public sealed class GeminiClient : ILlmClient, IDisposable
{
    private const string ApiKeyHeaderName = "x-goog-api-key";
    private readonly HttpClient _httpClient;
    private readonly LlmProfileConfig _profile;
    private readonly bool _disposeClient;

    public LlmTokenUsage? LastTokenUsage { get; private set; }
    public LlmCompletion? LastCompletion { get; private set; }

    public GeminiClient(LlmProfileConfig profile, string? secretToken = null, HttpClient? httpClient = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _disposeClient = httpClient == null;

        _httpClient = httpClient ?? LlmHttp.CreateClient(_profile, "Gemini");

        if (!string.IsNullOrWhiteSpace(secretToken))
        {
            _httpClient.DefaultRequestHeaders.Remove(ApiKeyHeaderName);
            _httpClient.DefaultRequestHeaders.Add(ApiKeyHeaderName, secretToken);
        }
    }

    public async Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await ListModelsAsync(cancellationToken).ConfigureAwait(false);
            bool hasModel = false;
            var cleanModel = StripModelsPrefix(_profile.Model);

            foreach (var m in models)
            {
                if (m.Equals(cleanModel, StringComparison.OrdinalIgnoreCase))
                {
                    hasModel = true;
                    break;
                }
            }

            if (!hasModel && models.Count > 0 && !string.IsNullOrWhiteSpace(_profile.Model))
            {
                return LlmHealthResult.Failure($"Gemini raggiungibile, ma il modello configurato '{_profile.Model}' non è tra i modelli disponibili.");
            }

            return LlmHealthResult.Success("gemini", models);
        }
        catch (HttpRequestException ex)
        {
            return LlmHealthResult.Failure($"Errore di rete o HTTP verso Gemini: {ex.Message}");
        }
        catch (Exception ex)
        {
            return LlmHealthResult.Failure($"Verifica non riuscita: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        // Default page is 50 models; 1000 is the maximum.
        using var response = await _httpClient.GetAsync(GetListModelsEndpoint() + "?pageSize=1000", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var list = new List<string>();
        if (doc.RootElement.Arr("models", out var modelsElem))
        {
            foreach (var item in modelsElem.EnumerateArray())
            {
                var name = item.Str("name");
                if (string.IsNullOrEmpty(name) || !SupportsGenerateContent(item)) continue;
                list.Add(StripModelsPrefix(name!));
            }
        }
        return list;
    }

    /// <summary>The list also carries embedding, image and video models: keep the ones that answer generateContent.</summary>
    private static bool SupportsGenerateContent(JsonElement model)
    {
        if (!model.Arr("supportedGenerationMethods", out var methods)) return true;
        foreach (var method in methods.EnumerateArray())
        {
            if (method.ValueKind == JsonValueKind.String && method.GetString() == "generateContent") return true;
        }
        return false;
    }

    private static string StripModelsPrefix(string model) =>
        model.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? model.Substring(7) : model;

    public async Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(GetListModelsEndpoint() + "/" + Uri.EscapeDataString(StripModelsPrefix(model)), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseModelInfo(doc.RootElement);
    }

    /// <summary>Model resource: https://ai.google.dev/api/models declares "thinking" but nothing for tools or search.</summary>
    internal static ModelInfo ParseModelInfo(JsonElement root)
    {
        var info = new ModelInfo { ContextWindow = root.Int("inputTokenLimit"), MaxOutputTokens = root.Int("outputTokenLimit") };
        if (root.TryGetProperty("thinking", out var thinking) && thinking.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            info.Capabilities.Reasoning = thinking.GetBoolean() ? CapabilityState.Supported : CapabilityState.Unsupported;
            info.Capabilities.Source = "Gemini models.get";
        }
        return info;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        IEnumerable<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastTokenUsage = null;
        LastCompletion = null;
        await foreach (var chunk in ReadContentAsync(messages, null, null, cancellationToken).ConfigureAwait(false)) yield return chunk;
    }

    private async IAsyncEnumerable<string> ReadContentAsync(
        IEnumerable<ChatMessage> messages,
        string? jsonSchema,
        StringBuilder? plain,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        messages = ModelRequestContext.Prepare(_profile, messages, jsonSchema);
        var requestPayload = BuildGeminiPayload(messages, jsonSchema);
        var json = JsonSerializer.Serialize(requestPayload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var endpoint = GetModelEndpoint("streamGenerateContent", "alt=sse");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        using var reader = await StreamLineReader.OpenAsync(_httpClient, request, _profile, EnsureSuccessOrThrowAsync, cancellationToken).ConfigureAwait(false);

        string? line;
        string? stopCause = null;
        var tokenCounts = new TokenCounts();
        bool anyText = false;
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

            if (root.Obj("error", out var errElem))
            {
                throw new HttpRequestException($"Gemini stream error: {errElem.Str("message") ?? "Unknown stream error"}");
            }

            stopCause = ReadUsage(root, stopCause, tokenCounts);

            var text = ExtractCandidateText(root);
            if (!string.IsNullOrEmpty(text))
            {
                anyText = true;
                yield return text!;
            }
        }

        if (!anyText || plain != null) ThrowIfBlocked(stopCause);
        // Without finishReason the stream was cut: its text must not pass for a whole answer.
        if (streamed && stopCause == null) throw new HttpRequestException("Gemini ha interrotto il flusso prima della conferma di completamento.");
        if (!streamed && plain == null) throw new HttpRequestException("Gemini non ha restituito un flusso di risposta.");
    }

    public async Task<string> CompleteStructuredAsync(
        IEnumerable<ChatMessage> messages,
        string jsonSchema,
        CancellationToken cancellationToken = default)
    {
        LastTokenUsage = null;
        LastCompletion = null;
        var plain = new StringBuilder();
        var text = new StringBuilder();
        await foreach (var chunk in ReadContentAsync(messages, jsonSchema, plain, cancellationToken).ConfigureAwait(false)) { text.Append(chunk); StructuredProgress.Report(chunk); }
        if (plain.Length == 0)
        {
            if (text.Length == 0) throw new HttpRequestException("Gemini non ha restituito una risposta strutturata.");
            return LlmHttp.StripCodeFence(text.ToString());
        }

        // Compatible gateways may return a complete JSON response despite the streaming endpoint.
        using var doc = JsonDocument.Parse(plain.ToString());

        var root = doc.RootElement;
        var stopCause = ReadUsage(root, null, new TokenCounts());
        ThrowIfBlocked(stopCause);
        var rawText = ExtractCandidateText(root);
        if (string.IsNullOrEmpty(rawText))
        {
            throw new HttpRequestException("Gemini non ha restituito una risposta strutturata.");
        }
        return LlmHttp.StripCodeFence(rawText!);
    }

    private sealed class TokenCounts
    {
        public int? Prompt;
        public int? Candidates;
        public int Thoughts;
    }

    /// <summary>Accumulates observed usage independently of the block or finish reason seen so far.</summary>
    private string? ReadUsage(JsonElement root, string? stopCause, TokenCounts counts)
    {
        if (root.Obj("promptFeedback", out var feedback)) stopCause = feedback.Str("blockReason") ?? stopCause;
        if (root.Arr("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            stopCause = candidates[0].Str("finishReason") ?? stopCause;
        }

        if (root.Obj("usageMetadata", out var usage))
        {
            counts.Prompt = usage.ObservedTokens("promptTokenCount") ?? counts.Prompt;
            counts.Candidates = usage.ObservedTokens("candidatesTokenCount") ?? counts.Candidates;
            // Thinking is optional, separate from candidate tokens, and cumulative rather than a delta.
            counts.Thoughts = usage.ObservedTokens("thoughtsTokenCount") ?? counts.Thoughts;
            LastTokenUsage = counts.Prompt.HasValue && counts.Candidates.HasValue && (long)counts.Candidates.Value + counts.Thoughts <= int.MaxValue
                ? new LlmTokenUsage { PromptTokens = counts.Prompt.Value, CompletionTokens = counts.Candidates.Value + counts.Thoughts } : null;
        }
        if (stopCause != null)
        {
            LastCompletion = new LlmCompletion { IsTruncated = stopCause == "MAX_TOKENS", Reason = stopCause };
        }

        return stopCause;
    }

    /// <summary>No text and a reason other than a normal stop: blocked prompt (promptFeedback) or filtered answer.</summary>
    private static void ThrowIfBlocked(string? stopCause)
    {
        if (stopCause == null || stopCause == "STOP" || stopCause == "MAX_TOKENS") return;
        throw new HttpRequestException($"Gemini non ha restituito testo: richiesta o risposta bloccata dal provider ({stopCause}).");
    }

    private Dictionary<string, object> BuildGeminiPayload(IEnumerable<ChatMessage> messages, string? jsonSchema)
    {
        var contentsList = new List<object>();
        var systemTexts = new List<string>();

        foreach (var m in messages)
        {
            if (m.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                systemTexts.Add(m.Content);
            }
            else
            {
                var role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
                contentsList.Add(new
                {
                    role = role,
                    parts = new[] { new { text = m.Content } }
                });
            }
        }

        if (contentsList.Count == 0)
        {
            contentsList.Add(new
            {
                role = "user",
                parts = new[] { new { text = "Process request." } }
            });
        }

        var genConfig = new Dictionary<string, object>();

        // Gemini 3 and later: Google recommends leaving the default (1.0); lower values can loop or degrade reasoning.
        var model = StripModelsPrefix(_profile.Model);
        if (model.StartsWith("gemini-1", StringComparison.OrdinalIgnoreCase) || model.StartsWith("gemini-2", StringComparison.OrdinalIgnoreCase))
        {
            genConfig["temperature"] = _profile.Temperature;
        }

        // No maxOutputTokens: the provider applies the model's own limit.

        if (jsonSchema != null)
        {
            // responseFormat.text replaces the deprecated responseSchema (v1beta reference, 2026-10).
            var textFormat = new Dictionary<string, object> { ["mimeType"] = "APPLICATION_JSON" };
            if (!string.IsNullOrWhiteSpace(jsonSchema))
            {
                try
                {
                    var schema = System.Text.Json.Nodes.JsonNode.Parse(jsonSchema)!;
                    // With optional properties gemini-3.8-flash closes the answer after the first array item:
                    // every property required, the optional ones nullable.
                    LlmHttp.MakeSchemaStrict(schema, allRequired: true);
                    textFormat["schema"] = schema;
                }
                catch (JsonException)
                {
                    // Not a schema: JSON output without one.
                }
            }
            genConfig["responseFormat"] = new Dictionary<string, object> { ["text"] = textFormat };
        }

        var payload = new Dictionary<string, object>
        {
            ["contents"] = contentsList,
            ["generationConfig"] = genConfig
        };

        if (systemTexts.Count > 0)
        {
            var combinedSystem = string.Join("\n\n", systemTexts);
            payload["systemInstruction"] = new
            {
                parts = new[] { new { text = combinedSystem } }
            };
        }

        return payload;
    }

    /// <summary>Text of the first candidate: all its text parts, thought summaries excluded.</summary>
    private static string? ExtractCandidateText(JsonElement root)
    {
        if (!root.Arr("candidates", out var candidates) || candidates.GetArrayLength() == 0 ||
            !candidates[0].Obj("content", out var content) || !content.Arr("parts", out var parts))
        {
            return null;
        }

        StringBuilder? text = null;
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
            var value = part.Str("text");
            if (!string.IsNullOrEmpty(value)) (text ??= new StringBuilder()).Append(value);
        }
        return text?.ToString();
    }

    private string GetModelEndpoint(string action, string? query = null)
    {
        var path = $"{GetListModelsEndpoint()}/{StripModelsPrefix(_profile.Model)}:{action}";
        if (!string.IsNullOrEmpty(query))
        {
            path += "?" + query;
        }
        return path;
    }

    private string GetListModelsEndpoint()
    {
        var baseStr = _profile.BaseUrl.TrimEnd('/');
        var prefix = baseStr.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) ? "" : "v1beta/";
        return $"{prefix}models";
    }

    private static Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken ct) => LlmHttp.EnsureSuccessAsync(response, "Gemini", ct);

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
