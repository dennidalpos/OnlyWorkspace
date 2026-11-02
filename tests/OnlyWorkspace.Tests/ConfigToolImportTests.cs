using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

public sealed class ConfigToolImportTests : IDisposable
{
    private const string OldSecret = "synthetic-old-token";
    private const string NewSecret = "synthetic-new-token";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ow-import-" + Guid.NewGuid().ToString("N"));
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private string Target => Path.Combine(_directory, "target");
    private string ConfigPath => Path.Combine(Target, "config.json");
    private string VaultPath => Path.Combine(Target, "secrets.bin");
    private string Endpoint => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConfigToolImportTests()
    {
        Directory.CreateDirectory(_directory);
        _listener.Start();
    }

    [Fact]
    public void ImportPreservesPseudonymizationOptionsAndOlderProfilesDefaultOff()
    {
        Seed();
        var result = Run("{\"profiles\":[" + Profile("first", ",\"pseudonymizeAmounts\":true,\"pseudonymizeDates\":true") + "]}");
        Assert.Equal(0, result.Exit);
        var profile = Assert.Single(LoadConfig().Profiles);
        Assert.True(profile.PseudonymizeAmounts); Assert.True(profile.PseudonymizeDates);
        result = Run("{\"profiles\":[" + Profile("first") + "]}");
        Assert.Equal(0, result.Exit); profile = Assert.Single(LoadConfig().Profiles);
        Assert.False(profile.PseudonymizeAmounts); Assert.False(profile.PseudonymizeDates);
    }

    public static IEnumerable<object[]> Casing()
    {
        foreach (string profiles in new[] { "profiles", "Profiles", "PROFILES" })
        foreach (string auth in new[] { "auth", "Auth", "AUTH" })
        foreach (string secret in new[] { "secret", "Secret", "SECRET" })
            yield return new object[] { profiles, auth, secret };
    }

    [Theory]
    [MemberData(nameof(Casing))]
    public void CasingImportsTheSameSecretsAndKeepsTheirProfileAssociation(string profiles, string auth, string secret)
    {
        Seed();
        string first = Profile("first", ",\"secretRef\":\"existing\",\"" + auth + "\":{\"" + secret + "\":\"" + NewSecret + "\"}");
        string second = Profile("second", ",\"" + auth + "\":{\"" + secret + "\":\"synthetic-second-token\"}");
        var result = Run("{\"" + profiles + "\":[" + first + "," + second + "]}");
        Assert.Equal(0, result.Exit);
        var imported = LoadConfig();
        Assert.Equal(new[] { "first", "second" }, imported.Profiles.Select(p => p.Id));
        Assert.Equal(new[] { "profile_first_token", "profile_second_token" }, imported.Profiles.Select(p => p.SecretRef));
        var vault = LoadVault();
        Assert.Equal(NewSecret, vault.GetSecret("profile_first_token"));
        Assert.Equal("synthetic-second-token", vault.GetSecret("profile_second_token"));
        Assert.Equal(OldSecret, vault.GetSecret("existing"));
        Assert.DoesNotContain(NewSecret, File.ReadAllText(ConfigPath));
        AssertNoSecrets(result.Output);
        Assert.False(_listener.Pending());
        Assert.Empty(Directory.GetFiles(Target, "*.restore-*"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"auth\":null")]
    [InlineData(",\"auth\":{}")]
    [InlineData(",\"auth\":{\"secret\":null}")]
    [InlineData(",\"auth\":{\"secret\":\"\"}")]
    public void AbsentOrEmptyInlineSecretKeepsTheReferencedCredential(string auth)
    {
        Seed();
        var result = Run("{\"profiles\":[" + Profile("first", ",\"secretRef\":\"existing\"" + auth) + "]}");
        Assert.Equal(0, result.Exit);
        Assert.Equal("existing", LoadConfig().Profiles.Single().SecretRef);
        Assert.Equal(OldSecret, LoadVault().GetSecret("existing"));
        Assert.False(_listener.Pending());
        AssertNoSecrets(result.Output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedConfigReplacementRestoresTheOriginalPair(bool hadVault)
    {
        Seed();
        if (!hadVault) File.Delete(VaultPath);
        byte[] beforeConfig = File.ReadAllBytes(ConfigPath);
        byte[]? beforeVault = hadVault ? File.ReadAllBytes(VaultPath) : null;
        (int Exit, string Output) result;
        using (var locked = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = Run("{\"profiles\":[" + Profile("first", ",\"auth\":{\"secret\":\"" + NewSecret + "\"}") + "]}");
        Assert.Equal(2, result.Exit);
        Assert.Equal(beforeConfig, File.ReadAllBytes(ConfigPath));
        if (hadVault)
        {
            Assert.Equal(beforeVault, File.ReadAllBytes(VaultPath));
            Assert.Equal(OldSecret, LoadVault().GetSecret("existing"));
        }
        else Assert.False(File.Exists(VaultPath));
        Assert.DoesNotContain("[IMPORT SUCCESS]", result.Output);
        AssertNoSecrets(result.Output);
        Assert.False(_listener.Pending());
        Assert.Empty(Directory.GetFiles(Target, "*.restore-*"));
    }

    public static IEnumerable<object[]> InvalidInputs()
    {
        foreach (string auth in new[] {
            ",\"auth\":42", ",\"auth\":[]", ",\"auth\":\"synthetic-new-token\"",
            ",\"auth\":{\"secret\":42}", ",\"auth\":{\"secret\":[]}", ",\"auth\":{\"secret\":{}}",
            ",\"auth\":{},\"AUTH\":{}", ",\"auth\":{\"secret\":\"synthetic-new-token\",\"SECRET\":null}" })
            yield return new object[] { auth };
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void InvalidOrDuplicateAuthDoesNotWriteOrContactTheProvider(string auth)
    {
        AssertRejectedWithoutWrites("{\"profiles\":[" + Profile("first", auth) + "]}");
    }

    [Theory]
    [InlineData("{\"profiles\":[],\"Profiles\":[]}")]
    [InlineData("{\"profiles\":null}")]
    [InlineData("{\"profiles\":{}}")]
    [InlineData("{\"profiles\":[null]}")]
    [InlineData("{\"profiles\":[42]}")]
    [InlineData("{\"profiles\":[]}")]
    public void InvalidOrDuplicateProfilesDoesNotWrite(string source) => AssertRejectedWithoutWrites(source);

    private void AssertRejectedWithoutWrites(string source)
    {
        Seed();
        byte[] beforeConfig = File.ReadAllBytes(ConfigPath), beforeVault = File.ReadAllBytes(VaultPath);
        var result = Run(source);
        Assert.NotEqual(0, result.Exit);
        Assert.Equal(beforeConfig, File.ReadAllBytes(ConfigPath));
        Assert.Equal(beforeVault, File.ReadAllBytes(VaultPath));
        Assert.DoesNotContain("[IMPORT SUCCESS]", result.Output);
        AssertNoSecrets(result.Output);
        Assert.False(_listener.Pending());
        Assert.Empty(Directory.GetFiles(Target, "*.restore-*"));
    }

    private string Profile(string id, string extra = "") =>
        "{\"id\":\"" + id + "\",\"type\":\"ollama\",\"model\":\"fixture\",\"baseUrl\":\"" + Endpoint + "\"" + extra + "}";

    private void Seed()
    {
        var vault = new DpapiSecretVault();
        vault.SetSecret("existing", OldSecret);
        ConfigurationBackupService.SaveConfiguration(Target, new WorkspaceConfig { DefaultProfile = "previous", Profiles = new() {
            new() { Id = "previous", Type = "ollama", Model = "fixture", BaseUrl = Endpoint, SecretRef = "existing" } } }, vault);
    }

    private WorkspaceConfig LoadConfig() => JsonSerializer.Deserialize<WorkspaceConfig>(File.ReadAllText(ConfigPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private DpapiSecretVault LoadVault()
    {
        var vault = new DpapiSecretVault();
        vault.Load(VaultPath);
        return vault;
    }

    private static void AssertNoSecrets(string output)
    {
        Assert.DoesNotContain(OldSecret, output);
        Assert.DoesNotContain(NewSecret, output);
        Assert.DoesNotContain("synthetic-second-token", output);
    }

    private (int Exit, string Output) Run(string json)
    {
        string source = Path.Combine(_directory, "source.json");
        File.WriteAllText(source, json.Insert(1, "\"defaultProfile\":\"first\","));
        string tool = typeof(OnlyWorkspace.ConfigTool.Services.AdminPasswordService).Assembly.Location;
#if NET
        var start = new ProcessStartInfo("dotnet");
        foreach (string arg in new[] { tool, "import", source, Target, "--skip-test" }) start.ArgumentList.Add(arg);
#else
        var start = new ProcessStartInfo(tool, "import \"" + source + "\" \"" + Target + "\" --skip-test");
#endif
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new TimeoutException("Isolated CLI import timed out."); }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        _listener.Stop();
        TestArtifacts.DeleteDirectory(_directory, true);
    }
}
