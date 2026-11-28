using System.Reflection;

namespace OnlyWorkspace.Core;

public static class ProductInfo
{
    /// <summary>Product version x.y.z (Directory.Build.props), without the source revision suffix.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(ProductInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        int plus = informational!.IndexOf('+');
        return plus < 0 ? informational : informational.Substring(0, plus);
    }
}
