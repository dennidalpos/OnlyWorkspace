using System;
using System.Text.RegularExpressions;

namespace OnlyWorkspace.Core.Models;

/// <summary>
/// What is known up front about a model's request parameters, so the first request after a start is not the one the
/// provider rejects with HTTP 400. Unknown models fall back to the clients' learn-from-400 path.
/// </summary>
public readonly struct ModelTraits
{
    /// <summary>The model refuses a temperature (reasoning models, current Claude models).</summary>
    public bool NoTemperature { get; }
    /// <summary>The model does not know Anthropic's output_config.format.</summary>
    public bool NoOutputFormat { get; }
    /// <summary>Output token cap lower than the clients' default; 0 = none known.</summary>
    public int MaxOutputTokens { get; }

    private ModelTraits(bool noTemperature = false, bool noOutputFormat = false, int maxOutputTokens = 0)
    {
        NoTemperature = noTemperature;
        NoOutputFormat = noOutputFormat;
        MaxOutputTokens = maxOutputTokens;
    }

    public static readonly ModelTraits None = new();

    // Anthropic: Opus 4.7+, every Sonnet/Haiku/Opus 5 and Fable reject temperature (non-default values at least).
    private static readonly Regex AnthropicNoTemperature = new(@"^claude-(fable|(opus|sonnet|haiku)-5|opus-4-([7-9]|\d{2}))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AnthropicLegacy = new(@"^claude-3", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // OpenAI reasoning models: GPT-5 and the o-series.
    private static readonly Regex OpenAiNoTemperature = new(@"^(gpt-5|o\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="type">Provider type as in <see cref="ProviderCatalog"/>.</param>
    public static ModelTraits For(string? type, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return None;
        string id = model!.Trim();
        switch (ProviderCatalog.Get(type).Family)
        {
            case ApiFamily.Anthropic:
                if (AnthropicNoTemperature.IsMatch(id)) return new ModelTraits(noTemperature: true);
                if (AnthropicLegacy.IsMatch(id))
                    return new ModelTraits(noOutputFormat: true, maxOutputTokens: id.IndexOf("3-5", StringComparison.OrdinalIgnoreCase) >= 0 ? 8192 : 4096);
                return None;
            case ApiFamily.OpenAI when string.Equals(ProviderCatalog.Get(type).Type, ProviderCatalog.OpenAI, StringComparison.OrdinalIgnoreCase):
                return OpenAiNoTemperature.IsMatch(id) ? new ModelTraits(noTemperature: true) : None;
            default:
                return None;
        }
    }
}
