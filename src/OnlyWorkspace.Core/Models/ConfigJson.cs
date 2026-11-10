using System.Text.Json;

namespace OnlyWorkspace.Core.Models;

public static class ConfigJson
{
    /// <summary>config.json is written in camelCase, like config.template.json and the documentation.</summary>
    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
