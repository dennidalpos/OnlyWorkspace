using System;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;
using Xunit;

namespace OnlyWorkspace.Tests;

public class LlmClientFactoryTests
{
    [Fact]
    public void CreateClient_WithOllamaProfile_ReturnsOllamaClient()
    {
        var profile = new LlmProfileConfig
        {
            Id = "local-ollama",
            Type = "ollama",
            BaseUrl = "http://127.0.0.1:11434",
            Model = "llama3:8b"
        };

        var client = LlmClientFactory.CreateClient(profile);
        Assert.NotNull(client);
        Assert.IsType<OllamaClient>(client);
    }

    [Fact]
    public void CreateClient_WithUnsupportedProvider_ThrowsNotSupportedException()
    {
        var profile = new LlmProfileConfig
        {
            Id = "future-gateway",
            Type = "gateway",
            BaseUrl = "https://gateway.internal.net",
            Model = "default"
        };

        Assert.Throws<NotSupportedException>(() => LlmClientFactory.CreateClient(profile));
    }
}
