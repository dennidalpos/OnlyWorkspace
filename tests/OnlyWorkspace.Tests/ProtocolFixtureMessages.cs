using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Tests;

/// <summary>Protocol-only synthetic fixtures. Privacy tests must use the production request builder or unapproved messages.</summary>
internal static class ProtocolFixtureMessages
{
    private static IEnumerable<ChatMessage> Prepare(IEnumerable<ChatMessage> messages, string? schema)
    {
        var proof = new CloudRequestProof(schema);
        return messages.Select(m => new ChatMessage(m.Role, m.Content, proof)).ToArray();
    }
    internal static Task<string> CompleteProtocolAsync(this ILlmClient client, IEnumerable<ChatMessage> messages,
        string schema, CancellationToken cancellationToken = default) => client.CompleteStructuredAsync(Prepare(messages, schema), schema, cancellationToken);
    internal static IAsyncEnumerable<string> StreamProtocolAsync(this ILlmClient client, IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default) => client.StreamChatAsync(Prepare(messages, null), cancellationToken);
}
