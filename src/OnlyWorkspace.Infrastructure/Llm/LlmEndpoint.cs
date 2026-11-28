using System;

namespace OnlyWorkspace.Infrastructure.Llm;

/// <summary>Relative API paths only: resolving a path must not initialize the HTTP/TLS transport.</summary>
internal static class LlmEndpoint
{
    public static string WithV1Prefix(string baseUrl, string relative)
    {
        // Profiles may already include /v1, including behind a gateway; do not append it twice.
        var basePath = baseUrl.TrimEnd('/');
        return basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? relative : "v1/" + relative;
    }
}
