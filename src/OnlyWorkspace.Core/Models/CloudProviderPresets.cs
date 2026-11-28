using System;
using System.Collections.Generic;
using System.Linq;

namespace OnlyWorkspace.Core.Models;

/// <summary>
/// Address presets for the «Compatibile OpenAI» connector: they fill the endpoint only, they add no provider type and
/// restore none of the retired ones. The region is what the provider declares (docs/tecnico/provider-ia-ue.md, checked
/// 4 November 2026), not a compliance attestation; the base URLs come from each provider's own documentation.
/// </summary>
public sealed class CloudProviderPreset
{
    public string Id { get; }
    public string Name { get; }
    public string Country { get; }
    /// <summary>True when the provider declares processing in the EU.</summary>
    public bool EuRegion { get; }
    public string BaseUrl { get; }
    public string DocsUrl { get; }

    internal CloudProviderPreset(string id, string name, string country, bool euRegion, string baseUrl, string docsUrl)
    {
        Id = id;
        Name = name;
        Country = country;
        EuRegion = euRegion;
        BaseUrl = baseUrl;
        DocsUrl = docsUrl;
    }

    public string RegionLabel => EuRegion ? "dati UE" : "dati fuori UE";
    public override string ToString() => $"{Name} · {Country} · {RegionLabel}";
}

public static class CloudProviderPresets
{
    /// <summary>EU first, then non-EU; alphabetical inside each group.</summary>
    public static IReadOnlyList<CloudProviderPreset> All { get; } = new[]
    {
        new CloudProviderPreset("ionos", "IONOS AI Model Hub", "Germania", true, "https://openai.inference.de-txl.ionos.com/v1",
            "https://docs.ionos.com/cloud/ai/ai-model-hub"),
        new CloudProviderPreset("mistral", "Mistral AI", "Francia", true, "https://api.mistral.ai/v1",
            "https://docs.mistral.ai/api/"),
        new CloudProviderPreset("ovhcloud", "OVHcloud AI Endpoints", "Francia", true, "https://oai.endpoints.kepler.ai.cloud.ovh.net/v1",
            "https://www.ovhcloud.com/en/public-cloud/ai-endpoints/"),
        new CloudProviderPreset("scaleway", "Scaleway Generative APIs", "Francia", true, "https://api.scaleway.ai/v1",
            "https://www.scaleway.com/en/developers/api/generative-apis"),
        new CloudProviderPreset("stackit", "STACKIT AI Model Serving", "Germania", true, "https://api.openai-compat.model-serving.eu01.onstackit.cloud/v1",
            "https://docs.stackit.cloud/products/data-and-ai/ai-model-serving/")
    }.OrderByDescending(p => p.EuRegion).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Preset whose address equals <paramref name="baseUrl"/> (trailing slash ignored), or null.</summary>
    public static CloudProviderPreset? Match(string? baseUrl)
    {
        string wanted = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return wanted.Length == 0 ? null : All.FirstOrDefault(p => string.Equals(p.BaseUrl, wanted, StringComparison.OrdinalIgnoreCase));
    }
}
