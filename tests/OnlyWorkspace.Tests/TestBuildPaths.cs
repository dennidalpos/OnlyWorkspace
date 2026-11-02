using System.Reflection;

namespace OnlyWorkspace.Tests;

internal static class TestBuildPaths
{
    internal static string Get(string name) => typeof(TestBuildPaths).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == name).Value
        ?? throw new InvalidOperationException("Missing build path: " + name);

    internal static string RepositoryRoot => Get("RepositoryRoot");
}
