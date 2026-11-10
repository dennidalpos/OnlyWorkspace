namespace OnlyWorkspace.Tests;

// Retain final fixture states during managed verification. Deletes that simulate failures stay in the tests.
internal static class TestArtifacts
{
    internal static void DeleteDirectory(string path, bool recursive)
    {
        if (Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") == null)
            Directory.Delete(path, recursive);
    }

    internal static void DeleteFile(string path)
    {
        if (Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") == null)
            File.Delete(path);
    }
}
