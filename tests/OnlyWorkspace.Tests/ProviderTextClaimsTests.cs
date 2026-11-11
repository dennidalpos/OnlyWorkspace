using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Tests;

/// <summary>
/// A179: the ConfigTool tooltips, wizard notes and admin guides say things about each provider (how many presets, which have web
/// search, thinking toggle, num_ctx, a default address). They stay true only while the catalog says the same: a change here must
/// bring the texts named in each case along.
/// </summary>
public sealed class ProviderTextClaimsTests
{
    [Fact]
    public void ProfilePageTooltip_SixPresets_OpenAICompatibleWithoutWebSearchOrCapabilityReading()
    {
        // MainConfigForm.ProfilePage «Sei servizi preimpostati…», docs/admin-guide.md «Six new-profile presets».
        Assert.Equal(6, ProviderCatalog.All.Count);
        var generic = ProviderCatalog.Get(ProviderCatalog.OpenAICompatible);
        Assert.Equal(WebSearchMode.None, generic.WebSearch);
        Assert.Equal(CapabilitySource.None, generic.Capabilities);
        Assert.Equal(string.Empty, generic.DefaultBaseUrl);
        Assert.Equal(string.Empty, generic.DefaultModel);
    }

    [Fact]
    public void LegacyProviders_AreOutsideThePresets_AndKeepTheirOwnProtocol()
    {
        // «I precedenti restano nei loro profili», docs/admin/provider-ia.md.
        foreach (var type in new[] { "azure-openai", "perplexity", "ollama-cloud" })
        {
            Assert.True(ProviderCatalog.IsLegacy(type));
            Assert.DoesNotContain(ProviderCatalog.All, p => p.Type == type);
            Assert.True(ProviderCatalog.Get(type).IsExternal);
        }
    }

    [Fact]
    public void ThinkingToggle_IsOllamaAndQwen_NumCtxAndKeepAliveOnlyLocalOllama()
    {
        // «Disattiva il ragionamento nei modelli supportati (Ollama, Qwen)», «Solo Ollama» on NumCtx, keep alive and budget.
        var toggled = ProviderCatalog.All.Where(p => p.SupportsThinkToggle).Select(p => p.Type).OrderBy(t => t).ToArray();
        Assert.Equal(new[] { ProviderCatalog.Ollama, "qwen" }, toggled);
        Assert.Equal(new[] { ProviderCatalog.Ollama }, ProviderCatalog.All.Where(p => p.SupportsNumCtx).Select(p => p.Type).ToArray());
        Assert.Equal(new[] { ProviderCatalog.Ollama }, ProviderCatalog.All.Where(p => p.SupportsKeepAlive).Select(p => p.Type).ToArray());
    }

    [Fact]
    public void WebSearchKeyField_BelongsToOllamaOnly_AndEveryOtherSearchIsTheProvidersOwn()
    {
        // «Chiave Ollama Web», docs/admin/configtool.md (ollama.com key only for Ollama).
        Assert.Equal(new[] { ProviderCatalog.Ollama }, ProviderCatalog.All.Where(p => p.WebSearch == WebSearchMode.OllamaCom).Select(p => p.Type).ToArray());
        Assert.Equal(WebSearchMode.AnthropicTool, ProviderCatalog.Get(ProviderCatalog.Anthropic).WebSearch);
        Assert.Equal(WebSearchMode.GeminiGrounding, ProviderCatalog.Get(ProviderCatalog.Gemini).WebSearch);
        Assert.Equal(WebSearchMode.OpenAIResponses, ProviderCatalog.Get(ProviderCatalog.OpenAI).WebSearch);
        Assert.Equal(WebSearchMode.BasicResponses, ProviderCatalog.Get("qwen").WebSearch);
    }

    [Fact]
    public void LocalOllama_IsLoopbackAndNotExternal_EveryOtherNamedPresetIsCloud()
    {
        // Wizard note «Per Ollama su questo PC è http://127.0.0.1:11434», tooltip «(cloud)» / «(locale)».
        var ollama = ProviderCatalog.Get(ProviderCatalog.Ollama);
        Assert.Equal("http://127.0.0.1:11434", ollama.DefaultBaseUrl);
        Assert.False(ollama.IsExternal);
        Assert.All(ProviderCatalog.All.Where(p => p.Type != ProviderCatalog.Ollama && p.Type != ProviderCatalog.OpenAICompatible), p => Assert.True(p.IsExternal, p.Type));
        Assert.False(ProviderCatalog.Get(ProviderCatalog.OpenAICompatible).IsExternal);
    }

    [Fact]
    public void CloudProviders_HaveNoDefaultModel_SoTheEmptyBoxHintApplies()
    {
        // ModelHint «Scegli il modello con «Scegli modello…»» is shown only where DefaultModel is empty.
        Assert.All(ProviderCatalog.All.Where(p => p.IsExternal), p => Assert.Equal(string.Empty, p.DefaultModel));
        Assert.NotEqual(string.Empty, ProviderCatalog.Get(ProviderCatalog.Ollama).DefaultModel);
    }
}
