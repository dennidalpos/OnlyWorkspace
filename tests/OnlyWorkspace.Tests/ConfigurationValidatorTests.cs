using System.Collections.Generic;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Validation;
using Xunit;

namespace OnlyWorkspace.Tests;

public class ConfigurationValidatorTests
{
    [Fact]
    public void Validate_Succeeds_ForValidLocalOllamaConfig()
    {
        var config = new WorkspaceConfig
        {
            SchemaVersion = 1,
            DefaultProfile = "ollama-local",
            Profiles = new List<LlmProfileConfig>
            {
                new()
                {
                    Id = "ollama-local",
                    DisplayName = "Local Ollama",
                    Type = "ollama",
                    BaseUrl = "http://localhost:11434",
                    Model = "llama3.1:8b",
                    ContextBudgetTokens = ProviderCatalog.ComputeBudget(8192, 2048),
                    MaxOutputTokens = 2048,
                    Temperature = 0.3,
                    NumCtx = 8192
                }
            }
        };

        var result = ConfigurationValidator.Validate(config);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_Fails_WhenProfileTypeIsUnsupported()
    {
        var config = new WorkspaceConfig
        {
            DefaultProfile = "corp-gw",
            Profiles = new List<LlmProfileConfig>
            {
                new()
                {
                    Id = "corp-gw",
                    Type = "gateway",
                    BaseUrl = "https://gateway.corp.local"
                }
            }
        };

        var result = ConfigurationValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("«Servizio IA» non supportato (gateway)"));
    }

    [Fact]
    public void Validate_Fails_WhenRemoteEndpointUsesHttp()
    {
        var config = new WorkspaceConfig
        {
            DefaultProfile = "ollama-remote",
            Profiles = new List<LlmProfileConfig>
            {
                new()
                {
                    Id = "ollama-remote",
                    Type = "ollama",
                    BaseUrl = "http://ollama.company.lan:11434",
                    Model = "llama3.1:8b"
                }
            }
        };

        var result = ConfigurationValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("http:// è ammesso solo su questo PC"));
    }

    [Fact]
    public void Validate_Fails_WhenTemperatureIsOutOfRange()
    {
        var config = new WorkspaceConfig
        {
            DefaultProfile = "p1",
            Profiles = new List<LlmProfileConfig>
            {
                new()
                {
                    Id = "p1",
                    Type = "ollama",
                    BaseUrl = "http://127.0.0.1:11434",
                    Temperature = 2.5
                }
            }
        };

        var result = ConfigurationValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("«Temperatura» deve essere fra 0,0 e 2,0"));
    }

    [Fact]
    public void Validate_Succeeds_ForValidCloudProfiles()
    {
        var config = new WorkspaceConfig
        {
            SchemaVersion = 1,
            DefaultProfile = "openai-main",
            Profiles = new List<LlmProfileConfig>
            {
                new()
                {
                    Id = "openai-main",
                    Type = "openai",
                    BaseUrl = "https://api.openai.com/v1",
                    Model = "gpt-4o",
                    ContextBudgetTokens = 128000,
                    MaxOutputTokens = 4096,
                    Temperature = 0.3
                },
                new()
                {
                    Id = "anthropic-main",
                    Type = "anthropic",
                    BaseUrl = "https://api.anthropic.com",
                    Model = "claude-3-5-sonnet-20241022",
                    ContextBudgetTokens = 100000,
                    MaxOutputTokens = 4096,
                    Temperature = 0.3
                },
                new()
                {
                    Id = "gemini-main",
                    Type = "gemini",
                    BaseUrl = "https://generativelanguage.googleapis.com",
                    Model = "gemini-1.5-flash",
                    // Context and output are the provider's: no value needed.
                    ContextBudgetTokens = 0,
                    MaxOutputTokens = 0,
                    Temperature = 0.3
                }
            }
        };

        var result = ConfigurationValidator.Validate(config);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_ReportsError_WhenProfileTypeIsNull()
    {
        var config = new WorkspaceConfig
        {
            DefaultProfile = "p1",
            Profiles = new List<LlmProfileConfig> { new() { Id = "p1", Type = null! } }
        };

        var result = ConfigurationValidator.Validate(config);

        Assert.Contains(result.Errors, e => e.Contains("«Servizio IA» non supportato"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434", true, true)]
    [InlineData("http://127.0.0.1:11434", false, false)]
    [InlineData("https://ollama.example.com", true, false)]
    public void Validate_WarnsOnlyWhenLoopbackOllamaIsMarkedExternal(string baseUrl, bool external, bool expectWarning)
    {
        var config = new WorkspaceConfig
        {
            DefaultProfile = "p1",
            Profiles = new List<LlmProfileConfig>
            {
                new() { Id = "p1", Type = "ollama", BaseUrl = baseUrl, Model = "m", External = external }
            }
        };

        var result = ConfigurationValidator.Validate(config);

        Assert.Equal(expectWarning, result.Warnings.Exists(w => w.Contains("servizio esterno")));
    }

    // #241: errors name the profile as the list shows it and the field by its on-screen label, and say where it is.
    [Fact]
    public void Errors_UseDisplayedNameAndScreenLabels_AndNameTheField()
    {
        var config = new WorkspaceConfig
        {
            SchemaVersion = 0,
            DefaultProfile = "gone",
            Profiles = new List<LlmProfileConfig>
            {
                new() { Id = "p-local", DisplayName = "Ollama ufficio", Type = "ollama", BaseUrl = "ftp://x", NumCtx = 0, FirstTokenTimeoutSeconds = 0, IdleTimeoutSeconds = -1, Temperature = 3 }
            }
        };

        var result = ConfigurationValidator.Validate(config);

        Assert.Equal(result.Errors, result.Issues.ConvertAll(i => i.Message));
        foreach (var error in result.Errors)
        {
            foreach (var internalName in new[] { "URL endpoint", "SchemaVersion", "p-local", "NumCtx deve", "AuthType", "budget token" })
                Assert.DoesNotContain(internalName, error);
        }
        var profileIssues = result.Issues.FindAll(i => i.ProfileId == "p-local");
        Assert.All(profileIssues, i => Assert.StartsWith("Profilo «Ollama ufficio»: «", i.Message));
        Assert.Contains(profileIssues, i => i.Field == ValidationField.Endpoint && i.Message.Contains("«Indirizzo del servizio»"));
        Assert.Contains(profileIssues, i => i.Field == ValidationField.NumCtx && i.Message.Contains("«NumCtx (Ollama)»"));
        Assert.Contains(profileIssues, i => i.Field == ValidationField.Temperature);
        Assert.Contains(profileIssues, i => i.Field == ValidationField.FirstTokenTimeout && i.Message.Contains("«Timeout primo token (s)»"));
        Assert.Contains(profileIssues, i => i.Field == ValidationField.IdleTimeout);
        Assert.Contains(result.Issues, i => i.ProfileId == null && i.Field == ValidationField.DefaultProfile && i.Message.StartsWith("Generale: «Profilo predefinito»"));
        Assert.Contains(result.Issues, i => i.ProfileId == null && i.Field == "" && i.Message.Contains("Manutenzione e backup"));
    }

    [Fact]
    public void Errors_FallBackToTheId_WhenTheProfileHasNoDisplayedName()
    {
        var config = new WorkspaceConfig
        {
            DefaultProfile = "p1",
            Profiles = new List<LlmProfileConfig> { new() { Id = "p1", Type = "ollama", BaseUrl = "" } }
        };

        var issue = Assert.Single(ConfigurationValidator.Validate(config).Issues);

        Assert.Equal("p1", issue.ProfileId);
        Assert.Equal(ValidationField.Endpoint, issue.Field);
        Assert.StartsWith("Profilo «p1»: «Indirizzo del servizio»", issue.Message);
    }

}
