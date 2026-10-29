using System.Text.Json;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Validation;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Llm;

namespace OnlyWorkspace.Tests;

public sealed class ContextBudgetCoherenceTests
{
    private static LlmProfileConfig Profile(int budget = 5325, int output = 2048, int context = 8192) =>
        new() { Id = "synthetic", Model = "synthetic", ContextBudgetTokens = budget, MaxOutputTokens = output, NumCtx = context };

    [Theory]
    [InlineData(30000, 2048, 8192)]
    [InlineData(6144, 2048, 8192)] // fits input + output, but leaves no safety margin
    [InlineData(0, 2048, 8192)]
    [InlineData(1, 8192, 8192)]
    [InlineData(1, 2048, 0)]
    [InlineData(1, int.MaxValue, 1)]
    public async Task InvalidBudget_IsRejectedByValidationSaveReloadAndDirectDispatch(int budget, int output, int context)
    {
        var profile = Profile(budget, output, context);
        var config = new WorkspaceConfig { DefaultProfile = profile.Id, Profiles = new() { profile } };
        Assert.False(ConfigurationValidator.Validate(config).IsValid);
        Assert.Throws<InvalidOperationException>(() => ProviderCatalog.PromptBudget(profile));
        var messages = new[] { ChatMessage.User("whole original") };
        Assert.Throws<InvalidOperationException>(() => ModelRequestContext.Prepare(profile, messages, "{\"tools\":[{}]}"));
        var directory = Path.Combine(Path.GetTempPath(), "ow-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new FileConfigurationService(directory, directory);
            Assert.Throws<InvalidOperationException>(() => service.SaveConfiguration(config));
            File.WriteAllText(Path.Combine(directory, "config.json"), JsonSerializer.Serialize(config));
            for (int i = 0; i < 2; i++) Assert.Throws<InvalidOperationException>(() => service.LoadConfiguration());
            var handler = new NeverSend();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            using var client = new OllamaClient(profile, httpClient: http);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteStructuredAsync(messages, "{}"));
            Assert.Equal(0, handler.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PairSaveAndImport_RejectInvalidBudgetWithoutReplacingTheDestination()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ow-budget-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new WorkspaceConfig { DefaultProfile = "synthetic", Profiles = new() { Profile() } };
            var vault = new OnlyWorkspace.Infrastructure.Security.DpapiSecretVault();
            string target = Path.Combine(directory, "target");
            ConfigurationBackupService.SaveConfiguration(target, config, vault);
            byte[] before = File.ReadAllBytes(Path.Combine(target, "config.json"));
            config.Profiles[0].ContextBudgetTokens = 30000;
            Assert.Throws<InvalidOperationException>(() => ConfigurationBackupService.SaveConfiguration(target, config, vault));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(target, "config.json")));
            string source = Path.Combine(directory, "source.json");
            File.WriteAllText(source, JsonSerializer.Serialize(config));
            var main = typeof(OnlyWorkspace.ConfigTool.Forms.MainConfigForm).Assembly.GetType("OnlyWorkspace.ConfigTool.Program")!.GetMethod("Main")!;
            Assert.Equal(1, (int)main.Invoke(null, new object[] { new[] { "import", source, target, "--skip-test" } })!);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(target, "config.json")));
            var service = new FileConfigurationService(target, directory);
            for (int i = 0; i < 2; i++) Assert.Equal(5325, ProviderCatalog.PromptBudget(service.LoadConfiguration().Profiles.Single()));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CoherentBudget_RespectsOutputMarginAndWholeSchema_WhileCloudRemainsProviderManaged()
    {
        var profile = Profile();
        Assert.Equal(5325, ProviderCatalog.PromptBudget(profile));
        Assert.Equal(0, ProviderCatalog.ComputeBudget(1, int.MaxValue));
        Assert.True(ProviderCatalog.ComputeBudget(int.MaxValue, 1) > 0);
        var messages = new[] { ChatMessage.User("END-OF-ORIGINAL") };
        Assert.Same(messages[0], ModelRequestContext.Prepare(profile, messages).Single());
        Assert.Throws<ContextTooLongException>(() => ModelRequestContext.Prepare(profile, messages, new string('s', 30000)));
        foreach (var provider in ProviderCatalog.All.Where(p => !p.SupportsNumCtx))
        {
            profile.Type = provider.Type;
            profile.ContextBudgetTokens = 0; profile.NumCtx = 0; profile.MaxOutputTokens = 0;
            Assert.Equal(0, ProviderCatalog.PromptBudget(profile));
            Assert.Same(messages[0], ModelRequestContext.Prepare(profile, messages, new string('s', 30000)).Single());
        }
    }

    private sealed class NeverSend : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Transport must not be reached.");
        }
    }
}
