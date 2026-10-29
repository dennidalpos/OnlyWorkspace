using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;
using Xunit;

namespace OnlyWorkspace.Tests;

public class FileConfigurationServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FileConfigurationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OnlyWorkspaceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void LoadConfiguration_AutomaticallyMarksCloudProfilesAsExternal()
    {
        var configJson = @"
        {
          ""schemaVersion"": 1,
          ""defaultProfile"": ""cloud-ai"",
          ""profiles"": [
            {
              ""id"": ""local-ollama"",
              ""displayName"": ""Ollama LAN"",
              ""type"": ""ollama"",
              ""baseUrl"": ""http://127.0.0.1:11434"",
              ""model"": ""llama3:8b"",
              ""external"": false
            },
            {
              ""id"": ""cloud-ai"",
              ""displayName"": ""OpenAI"",
              ""type"": ""openai"",
              ""baseUrl"": ""https://api.openai.com/v1"",
              ""model"": ""gpt-4o"",
              ""external"": false
            },
            {
              ""id"": ""cloud-claude"",
              ""displayName"": ""Claude"",
              ""type"": ""anthropic"",
              ""baseUrl"": ""https://api.anthropic.com"",
              ""model"": ""claude-3-5-sonnet"",
              ""external"": false
            },
            {
              ""id"": ""cloud-gemini"",
              ""displayName"": ""Gemini"",
              ""type"": ""gemini"",
              ""baseUrl"": ""https://generativelanguage.googleapis.com"",
              ""model"": ""gemini-1.5-flash"",
              ""external"": false
            }
          ]
        }";

        var configFile = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configFile, configJson);

        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);
        var config = service.LoadConfiguration();

        var ollama = config.Profiles.Find(p => p.Id == "local-ollama");
        var openai = config.Profiles.Find(p => p.Id == "cloud-ai");
        var anthropic = config.Profiles.Find(p => p.Id == "cloud-claude");
        var gemini = config.Profiles.Find(p => p.Id == "cloud-gemini");

        Assert.NotNull(ollama);
        Assert.False(ollama!.External);

        Assert.NotNull(openai);
        Assert.True(openai!.External);

        Assert.NotNull(anthropic);
        Assert.True(anthropic!.External);

        Assert.NotNull(gemini);
        Assert.True(gemini!.External);
    }

    [Fact]
    public void AttachmentOcr_IsOnWhenMissing_AndAnOffSettingSurvivesSave()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.json"),
            @"{ ""schemaVersion"": 1, ""defaultProfile"": ""local"", ""profiles"": [ { ""id"": ""local"", ""type"": ""ollama"", ""baseUrl"": ""http://127.0.0.1:11434"", ""model"": ""m"" } ] }");
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);
        var config = service.LoadConfiguration();
        Assert.True(config.AttachmentOcr);

        config.AttachmentOcr = false;
        service.SaveConfiguration(config);
        Assert.Contains("\"attachmentOcr\": false", File.ReadAllText(Path.Combine(_tempDir, "config.json")));
        Assert.False(new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir).LoadConfiguration().AttachmentOcr);
    }

    [Fact]
    public void RemovedProfileSwitchField_StillLoads_AndIsDroppedOnSave()
    {
        // Configurations written before the pane lost its profile choice (26/10/2026).
        string path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, TwoProfiles.Replace(@"""defaultProfile"": ""local-ollama"",", @"""defaultProfile"": ""local-ollama"", ""userCanSwitchProfile"": false,"));
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);
        var config = service.LoadConfiguration();
        Assert.Equal("local-ollama", config.DefaultProfile);

        service.SaveConfiguration(config);
        Assert.DoesNotContain("userCanSwitchProfile", File.ReadAllText(path));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private const string TwoProfiles = @"
        {
          ""schemaVersion"": 1,
          ""defaultProfile"": ""local-ollama"",
          ""profiles"": [
            { ""id"": ""local-ollama"", ""type"": ""ollama"", ""baseUrl"": ""http://127.0.0.1:11434"", ""model"": ""llama3:8b"" },
            { ""id"": ""cloud-ai"", ""type"": ""openai"", ""baseUrl"": ""https://api.openai.com/v1"", ""model"": ""gpt-4o"" }
          ]
        }";

    [Fact]
    public void UserFlags_KeepEachOtherAndUnknownKeysInPreferences()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), TwoProfiles);
        // A key left by an older version (the pane's profile choice) is kept as it is and has no effect.
        File.WriteAllText(Path.Combine(_tempDir, "preferences.json"), "{\"activeProfileId\":\"cloud-ai\"}");
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);
        service.LoadConfiguration();
        Assert.False(service.GetUserFlag("archiveWelcomeDeclined"));

        service.SetUserFlag("archiveWelcomeDeclined", true);
        service.SetUserFlag("guidedSetupDone", true);

        Assert.True(service.GetUserFlag("archiveWelcomeDeclined"));
        Assert.True(service.GetUserFlag("guidedSetupDone"));
        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "preferences.json"))))
            Assert.Equal("cloud-ai", document.RootElement.GetProperty("activeProfileId").GetString());

        File.WriteAllText(Path.Combine(_tempDir, "preferences.json"), "not json");
        Assert.ThrowsAny<JsonException>(() => service.GetUserFlag("guidedSetupDone"));
    }

    [Fact]
    public void Template_AllowsTheArchiveAndKeepsCardsOff()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "OnlyWorkspace.sln"))) root = Path.GetDirectoryName(root)!;
        File.Copy(Path.Combine(root, "deployment", "config.template.json"), Path.Combine(_tempDir, "config.json"));
        var config = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir).LoadConfiguration();

        Assert.True(config.Archive.Enabled);
        Assert.True(config.Archive.IndexAttachmentText);
    }

    [Fact]
    public void LoadConfiguration_MissingFile_FailsWithoutCreatingDefaults()
    {
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);

        Assert.Throws<FileNotFoundException>(() => service.LoadConfiguration());
        Assert.False(File.Exists(Path.Combine(_tempDir, "config.json")));
    }

    [Theory]
    [InlineData("{broken", typeof(JsonException))]
    [InlineData("null", typeof(InvalidOperationException))]
    [InlineData("{\"profiles\":[]}", typeof(InvalidOperationException))]
    public void LoadConfiguration_UnreadableOrInvalidFile_FailsClosed(string json, Type errorType)
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), json);
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);

        Assert.IsType(errorType, Record.Exception(() => service.LoadConfiguration()));
        Assert.Equal(json, File.ReadAllText(Path.Combine(_tempDir, "config.json")));
    }

    [Fact]
    public void LoadConfiguration_UnreadableVault_DoesNotSilentlyUseTheConfig()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), TwoProfiles);
        File.WriteAllBytes(Path.Combine(_tempDir, "secrets.bin"), new byte[] { 1, 2, 3, 4 });
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);

        Assert.Throws<CryptographicException>(() => service.LoadConfiguration());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadConfiguration_UnreadableVault_KeepsTheSecretsAlreadyLoaded(bool empty)
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), TwoProfiles);
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);
        service.StoreSecret("profile_cloud-ai", "chiave");
        service.LoadConfiguration();

        byte[] backup = File.ReadAllBytes(Path.Combine(_tempDir, "secrets.bin"));
        File.WriteAllBytes(Path.Combine(_tempDir, "secrets.bin"), empty ? Array.Empty<byte>() : new byte[] { 1, 2, 3, 4 });
        for (int i = 0; i < 2; i++)
        {
            Assert.Throws<CryptographicException>(() => service.LoadConfiguration());
            Assert.Equal("chiave", service.ResolveSecret("profile_cloud-ai"));
        }
        File.WriteAllBytes(Path.Combine(_tempDir, "secrets.bin"), backup);
        service.LoadConfiguration();
        Assert.Equal("chiave", service.ResolveSecret("profile_cloud-ai"));
    }

    [Fact]
    public void MissingVault_RevokesLoadedKeys_AcrossTwoReloads_AndCanBeRestored()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), TwoProfiles);
        var service = new FileConfigurationService(_tempDir, _tempDir);
        service.StoreSecret("profile_cloud-ai", "synthetic-old");
        service.LoadConfiguration();
        byte[] backup = File.ReadAllBytes(Path.Combine(_tempDir, "secrets.bin"));
        File.Delete(Path.Combine(_tempDir, "secrets.bin"));
        Assert.True(service.HasConfigurationChanged());
        for (int i = 0; i < 2; i++)
        {
            service.LoadConfiguration();
            Assert.Null(service.ResolveSecret("profile_cloud-ai"));
            Assert.False(service.HasConfigurationChanged());
        }
        File.WriteAllBytes(Path.Combine(_tempDir, "secrets.bin"), backup);
        Assert.True(service.HasConfigurationChanged());
        service.LoadConfiguration();
        Assert.Equal("synthetic-old", service.ResolveSecret("profile_cloud-ai"));
    }

    [Fact]
    public void LoadConfiguration_FileLockedBySave_IsRetriedAtTheNextCheck()
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(configPath, TwoProfiles);
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);

        using (new FileStream(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => service.LoadConfiguration());
        Assert.True(service.HasConfigurationChanged());

        service.LoadConfiguration();
        Assert.False(service.HasConfigurationChanged());
    }

    [Fact]
    public void UserFlag_PreferencesLockedByAnotherWriter_AreNotReplaced()
    {
        string preferences = Path.Combine(_tempDir, "preferences.json");
        File.WriteAllText(preferences, "{\"archiveOff\":true,\"guidedSetupDone\":true}");
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);

        // A writer that blocks reading but still lets the file be replaced: the update must not run on an empty read.
        using (new FileStream(preferences, FileMode.Open, FileAccess.Write, FileShare.Delete))
        {
            try { service.SetUserFlag("archiveWelcomeDeclined", true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        Assert.True(service.GetUserFlag("archiveOff"));
        Assert.True(service.GetUserFlag("guidedSetupDone"));
    }

    [Fact]
    public void SaveConfiguration_InvalidConfig_DoesNotReplaceExistingFile()
    {
        string path = Path.Combine(_tempDir, "config.json");
        string current = TwoProfiles;
        File.WriteAllText(path, current);
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);

        Assert.Throws<InvalidOperationException>(() => service.SaveConfiguration(new WorkspaceConfig { Profiles = new() }));
        Assert.Equal(current, File.ReadAllText(path));
    }

    [Fact]
    public void UserPreferences_InvalidShapeOrJson_PreserveBytesAndReportErrors()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), TwoProfiles);
        string preferences = Path.Combine(_tempDir, "preferences.json");
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);

        foreach (string invalid in new[] { "not json", "[]" })
        {
            File.WriteAllText(preferences, invalid);
            Assert.NotNull(Record.Exception(() => service.GetUserFlag("guidedSetupDone")));
            Assert.NotNull(Record.Exception(() => service.SetUserFlag("guidedSetupDone", true)));
            Assert.Equal(invalid, File.ReadAllText(preferences));
        }

        File.WriteAllText(preferences, "{\"guidedSetupDone\":\"true\"}");
        Assert.Throws<InvalidDataException>(() => service.GetUserFlag("guidedSetupDone"));
        File.WriteAllText(preferences, "{}");
        service.SetUserFlag("guidedSetupDone", true);
        Assert.True(service.GetUserFlag("guidedSetupDone"));
    }

    [Fact]
    public void ArchiveOff_LockedReadThrows_AndRecoversWithoutLosingTheChoice()
    {
        var service = new FileConfigurationService(_tempDir, _tempDir);
        Assert.False(service.GetUserFlag("archiveOff"));
        service.SetUserFlag("archiveOff", true);
        using (new FileStream(Path.Combine(_tempDir, "preferences.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => service.GetUserFlag("archiveOff"));
        Assert.True(service.GetUserFlag("archiveOff"));
    }

    [Fact]
    public void ConcurrentProcesses_PreserveEveryFlag()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), TwoProfiles);
        var service = new FileConfigurationService(_tempDir, _tempDir);
        service.SetUserFlag("archiveOff", true);
        // Public writers in independent processes, synchronized at the start. No private-method seam.
        string script = Path.Combine(_tempDir, "writer.ps1");
        File.WriteAllText(script, @"
param($assembly, $directory, $worker, $start)
$ErrorActionPreference = 'Stop'
Add-Type -Path $assembly
$service = [OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService]::new($directory, $directory, $null)
$gate = [Threading.EventWaitHandle]::OpenExisting($start)
if (-not $gate.WaitOne(20000)) { throw 'start timeout' }
for ($i = 0; $i -lt 30; $i++) {
    $service.SetUserFlag(('worker' + $worker + '_' + $i), $true)
    $service.SetUserFlag('shared', ($i % 2 -eq 0))
}
$gate.Dispose()
");
        string eventName = "Local\\ow-preferences-test-" + Guid.NewGuid().ToString("N");
        using var start = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, eventName);
#if NET48
        string shell = "powershell.exe";
#else
        string shell = "pwsh.exe";
#endif
        var workers = new List<System.Diagnostics.Process>();
        try
        {
            for (int i = 0; i < 2; i++)
                workers.Add(System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shell,
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" \"" + typeof(FileConfigurationService).Assembly.Location +
                    "\" \"" + _tempDir + "\" " + i + " \"" + eventName + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!);
            start.Set();
            foreach (var worker in workers)
            {
                Assert.True(worker.WaitForExit(30000), "Preference writer timeout");
                Assert.True(worker.ExitCode == 0, worker.StandardError.ReadToEnd());
            }
            Assert.True(service.GetUserFlag("archiveOff"));
            for (int w = 0; w < 2; w++)
                for (int i = 0; i < 30; i++) Assert.True(service.GetUserFlag("worker" + w + "_" + i));
            Assert.False(service.GetUserFlag("shared"));
        }
        finally
        {
            foreach (var worker in workers)
            {
                if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(); }
                worker.Dispose();
            }
        }
    }

    [Fact]
    public void HasConfigurationChanged_TracksConfigAndVaultAfterLoad()
    {
        string configPath = Path.Combine(_tempDir, "config.json");
        string secretsPath = Path.Combine(_tempDir, "secrets.bin");
        File.WriteAllText(configPath, TwoProfiles);
        var service = new FileConfigurationService(configDir: _tempDir, preferencesDir: _tempDir);
        Assert.True(service.HasConfigurationChanged());

        service.LoadConfiguration();
        Assert.False(service.HasConfigurationChanged());
        File.SetLastWriteTimeUtc(configPath, File.GetLastWriteTimeUtc(configPath).AddMinutes(2));
        Assert.True(service.HasConfigurationChanged());

        service.LoadConfiguration();
        Assert.False(service.HasConfigurationChanged());
        File.WriteAllBytes(secretsPath, Array.Empty<byte>());
        Assert.True(service.HasConfigurationChanged());
    }
}
