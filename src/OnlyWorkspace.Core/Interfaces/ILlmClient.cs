using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Interfaces;

public interface ILlmClient
{
    Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
    /// <summary>Limits and capabilities of a model; null when the provider exposes nothing for it.</summary>
    Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);
    Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default);
    LlmTokenUsage? LastTokenUsage { get; }
    LlmCompletion? LastCompletion { get; }
}
