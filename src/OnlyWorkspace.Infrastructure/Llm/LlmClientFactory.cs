using System;
using System.Net.Http;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Infrastructure.Llm;

public static class LlmClientFactory
{
    public static ILlmClient CreateClient(LlmProfileConfig profile, string? secret = null, HttpClient? httpClient = null)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        ProviderCompatibility.Normalize(profile);
        if (!ProviderCatalog.IsKnown(profile.Type))
            throw new NotSupportedException($"tipo di profilo non supportato in questa versione ({profile.Type})");

        return ProviderCatalog.Get(profile.Type).Family switch
        {
            ApiFamily.Ollama => new OllamaClient(profile, secret, httpClient),
            ApiFamily.Anthropic => new AnthropicClient(profile, secret, httpClient),
            ApiFamily.Gemini => new GeminiClient(profile, secret, httpClient),
            _ => new OpenAIClient(profile, secret, httpClient)
        };
    }
}
