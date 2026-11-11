using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OnlyWorkspace.Core.Models;

/// <summary>Wire protocol of a connector; one client implementation per family.</summary>
public enum ApiFamily { Ollama, OpenAI, Anthropic, Gemini }

/// <summary>Where model capabilities can be read without generating text.</summary>
public enum CapabilitySource { None, Anthropic, Gemini, Ollama }

/// <summary>How a connector runs a web search, see WebSearchService.</summary>
public enum WebSearchMode { None, OpenAIResponses, BasicResponses, AnthropicTool, GeminiGrounding, OllamaCom }

/// <summary>What a connector supports: drives ConfigTool fields, defaults, transport and validation.</summary>
public sealed class ProviderCapabilities
{
    public string Type { get; }
    public string DisplayName { get; }
    public ApiFamily Family { get; }
    public string DefaultBaseUrl { get; }
    /// <summary>Empty for cloud: model ids change often, pick one via "Rileva Modelli".</summary>
    public string DefaultModel { get; }
    /// <summary>Cloud provider: External is forced on.</summary>
    public bool IsExternal { get; }
    /// <summary>Fixed client-side context window (Ollama num_ctx).</summary>
    public bool SupportsNumCtx { get; }
    public bool SupportsKeepAlive { get; }
    public bool SupportsThinkToggle { get; }
    /// <summary>Accepted AuthType values, first is the default.</summary>
    public IReadOnlyList<string> AuthTypes { get; }
    /// <summary>Header carrying the key when AuthType is "apiKey".</summary>
    public string ApiKeyHeader { get; }
    /// <summary>Chat path relative to the versioned base URL (OpenAI family).</summary>
    public string ChatPath { get; }
    public CapabilitySource Capabilities { get; }
    public WebSearchMode WebSearch { get; }
    /// <summary>Basic Responses provider that accepts tool_choice {"type":"web_search"} (QwenCloud, proved 11/11/2026).</summary>
    public bool ForcesWebTool { get; }
    /// <summary>
    /// A real call with an account of the service has been made (docs/tecnico/verifica.md). False = «non provato»: only
    /// protocol fixtures back the connector (#206). The date is empty when the proof is declared but its day is not on record.
    /// </summary>
    public bool LiveProven { get; }
    public string LiveProvenOn { get; }

    /// <summary>Words shown beside a connector that has no real-service proof.</summary>
    public const string UntestedLabel = "non provato";
    public const string UntestedNote = "Non provato con un servizio reale: il collegamento è verificato solo con risposte simulate. Usa «Verifica connessione» con il tuo account prima di affidargli dati.";
    /// <summary>Official API reference, shown as tooltip.</summary>
    public string DocsUrl { get; }

    internal ProviderCapabilities(string type, string name, ApiFamily family, string baseUrl, bool external, string[] authTypes,
        string docsUrl, string model = "", string apiKeyHeader = "", string chatPath = "chat/completions",
        CapabilitySource capabilities = CapabilitySource.None, WebSearchMode webSearch = WebSearchMode.None,
        bool numCtx = false, bool keepAlive = false, bool thinkToggle = false, bool forcesWebTool = false,
        bool liveProven = false, string liveProvenOn = "")
    {
        ForcesWebTool = forcesWebTool;
        LiveProven = liveProven;
        LiveProvenOn = liveProvenOn;
        Type = type;
        DisplayName = name;
        Family = family;
        DefaultBaseUrl = baseUrl;
        DefaultModel = model;
        IsExternal = external;
        AuthTypes = authTypes;
        DocsUrl = docsUrl;
        ApiKeyHeader = apiKeyHeader;
        ChatPath = chatPath;
        Capabilities = capabilities;
        WebSearch = webSearch;
        SupportsNumCtx = numCtx;
        SupportsKeepAlive = keepAlive;
        SupportsThinkToggle = thinkToggle;
    }

    public bool RequiresSecret => !AuthTypes.Contains("none");
    /// <summary>Text of the service choice: «non provato» is part of it where no real-service proof exists.</summary>
    public override string ToString() => LiveProven ? DisplayName : DisplayName + " · " + UntestedLabel;
}

public sealed class ContextPreset
{
    public string Id { get; }
    public string Label { get; }
    /// <summary>num_ctx; 0 = custom.</summary>
    public int ContextWindow { get; }
    public int MaxOutputTokens { get; }

    internal ContextPreset(string id, string label, int window, int output)
    {
        Id = id;
        Label = label;
        ContextWindow = window;
        MaxOutputTokens = output;
    }

    public bool IsCustom => ContextWindow <= 0;
    public int Budget => ProviderCatalog.ComputeBudget(ContextWindow, MaxOutputTokens);
    public override string ToString() => Label;
}

public static class ProviderCatalog
{
    public const string Ollama = "ollama";
    public const string OpenAI = "openai";
    public const string OpenAICompatible = "openai-compatible";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";

    private static readonly string[] None = { "none", "bearer" };
    private static readonly string[] Bearer = { "bearer" };
    private static readonly string[] Key = { "apiKey" };

    // Selectable presets. Legacy protocols below are available only to existing profiles.
    public static IReadOnlyList<ProviderCapabilities> All { get; } = new[]
    {
        new ProviderCapabilities(Ollama, "Ollama", ApiFamily.Ollama, "http://127.0.0.1:11434", false, None,
            "https://docs.ollama.com/api", model: "qwen3.5:9b", capabilities: CapabilitySource.Ollama, webSearch: WebSearchMode.OllamaCom,
            numCtx: true, keepAlive: true, thinkToggle: true, liveProven: true),
        new ProviderCapabilities(Gemini, "Google Gemini", ApiFamily.Gemini, "https://generativelanguage.googleapis.com", true, Key,
            "https://ai.google.dev/api/models", apiKeyHeader: "x-goog-api-key",
            capabilities: CapabilitySource.Gemini, webSearch: WebSearchMode.GeminiGrounding, liveProven: true),
        new ProviderCapabilities(Anthropic, "Anthropic Claude", ApiFamily.Anthropic, "https://api.anthropic.com", true, Key,
            "https://platform.claude.com/docs/en/api/models", apiKeyHeader: "x-api-key",
            capabilities: CapabilitySource.Anthropic, webSearch: WebSearchMode.AnthropicTool, liveProven: true),
        new ProviderCapabilities(OpenAI, "OpenAI", ApiFamily.OpenAI, "https://api.openai.com", true, Bearer,
            "https://developers.openai.com/api/reference", webSearch: WebSearchMode.OpenAIResponses, liveProven: true),
        new ProviderCapabilities("qwen", "QwenCloud", ApiFamily.OpenAI, "https://token-plan.maas.qwencloudapi.com/compatible-mode/v1", true, Bearer,
            "https://www.alibabacloud.com/help/en/model-studio/compatibility-of-openai-with-dashscope", webSearch: WebSearchMode.BasicResponses,
            thinkToggle: true, forcesWebTool: true, liveProven: true, liveProvenOn: "2026-11-11"),
        new ProviderCapabilities(OpenAICompatible, "Compatibile OpenAI", ApiFamily.OpenAI, "", false, None,
            "https://developers.openai.com/api/reference")
    };

    private static readonly ProviderCapabilities[] Legacy =
    {
        new ProviderCapabilities("azure-openai", "Azure OpenAI (profilo esistente)", ApiFamily.OpenAI, "https://risorsa.openai.azure.com/openai/v1", true,
            new[] { "apiKey", "bearer" }, "https://learn.microsoft.com/en-us/azure/foundry/openai/api-version-lifecycle", apiKeyHeader: "api-key"),
        new ProviderCapabilities("perplexity", "Perplexity Sonar (profilo esistente)", ApiFamily.OpenAI, "https://api.perplexity.ai", true, Bearer,
            "https://docs.perplexity.ai/guides/chat-completions-guide", model: "sonar", chatPath: "sonar"),
        new ProviderCapabilities("ollama-cloud", "Ollama Cloud (profilo esistente)", ApiFamily.Ollama, "https://ollama.com", true, Bearer,
            "https://docs.ollama.com/api", capabilities: CapabilitySource.Ollama, webSearch: WebSearchMode.OllamaCom, thinkToggle: true)
    };

    public static bool IsLegacy(string? type) => Legacy.Any(p => string.Equals(p.Type, type?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Context presets for connectors with a fixed client-side window (Ollama num_ctx).</summary>
    public static IReadOnlyList<ContextPreset> Presets { get; } = BuildPresets();

    private static IReadOnlyList<ContextPreset> BuildPresets()
    {
        var sizes = new (int Window, int Output)[] { (8192, 1024), (16384, 2048), (32768, 4096), (65536, 4096), (131072, 4096), (262144, 4096) };
        var list = sizes.Select(s => new ContextPreset("ctx-" + s.Window / 1024 + "k",
            $"{Tokens(s.Window)} · risposta {Tokens(s.Output)} · prompt {Tokens(ComputeBudget(s.Window, s.Output))}", s.Window, s.Output)).ToList();
        list.Add(new ContextPreset("custom", "Personalizzato", 0, 0));
        return list;
    }

    /// <summary>Compact token count: 16K, 12,4K.</summary>
    public static string Tokens(int tokens)
    {
        double k = tokens / 1024.0;
        return (Math.Abs(k - Math.Round(k)) < 0.05 ? Math.Round(k).ToString("0", CultureInfo.InvariantCulture) : k.ToString("0.0", CultureInfo.GetCultureInfo("it-IT"))) + "K";
    }

    /// <summary>Presets that fit a model's declared window (all when unknown).</summary>
    public static IEnumerable<ContextPreset> PresetsFor(int modelContextWindow) =>
        Presets.Where(p => p.IsCustom || modelContextWindow <= 0 || p.ContextWindow <= modelContextWindow);

    /// <summary>
    /// Tokens of data a prompt may carry. 0 = no client-side limit: a cloud provider decides what fits and
    /// refuses what does not; only a fixed num_ctx has to be respected here (Ollama cuts the prompt silently).
    /// </summary>
    public static int PromptBudget(LlmProfileConfig profile)
    {
        if (!Get(profile.Type).SupportsNumCtx) return 0;
        int maximum = ComputeBudget(profile.NumCtx, profile.MaxOutputTokens);
        if (profile.NumCtx <= 0 || profile.MaxOutputTokens <= 0 || profile.ContextBudgetTokens <= 0 ||
            maximum <= 0 || profile.ContextBudgetTokens > maximum)
            throw new InvalidOperationException($"Profilo '{profile.Id}': budget Ollama non coerente con NumCtx e output (massimo {maximum}).");
        return profile.ContextBudgetTokens;
    }

    public static bool IsKnown(string? type) => All.Any(p => string.Equals(p.Type, type?.Trim(), StringComparison.OrdinalIgnoreCase)) || IsLegacy(type);

    /// <summary>Unknown types fall back to Ollama (validator rejects them anyway).</summary>
    public static ProviderCapabilities Get(string? type)
    {
        foreach (var p in All.Concat(Legacy))
        {
            if (string.Equals(p.Type, type?.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
        }
        if (ProviderCompatibility.IsRetired(type)) return All.First(p => p.Type == OpenAICompatible);
        return All[0];
    }

    /// <summary>
    /// Prompt budget = window − output − 10% margin (system prompt, chat overhead, ~3.5 chars/token estimate error).
    /// </summary>
    public static int ComputeBudget(int contextWindow, int maxOutputTokens)
    {
        if (contextWindow <= 0) return 0;
        int margin = Math.Max(512, contextWindow / 10);
        return (int)Math.Max(0L, Math.Min(int.MaxValue, (long)contextWindow - maxOutputTokens - margin));
    }

    /// <summary>Preset matching the profile values, or the custom entry.</summary>
    public static ContextPreset MatchPreset(LlmProfileConfig profile)
    {
        foreach (var preset in Presets)
        {
            if (preset.IsCustom) continue;
            if (profile.NumCtx == preset.ContextWindow && profile.MaxOutputTokens == preset.MaxOutputTokens && profile.ContextBudgetTokens == preset.Budget)
            {
                return preset;
            }
        }
        return Presets[Presets.Count - 1];
    }
}
