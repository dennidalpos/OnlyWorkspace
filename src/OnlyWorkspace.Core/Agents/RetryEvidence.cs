using System;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Core.Agents;

/// <summary>
/// When Riprova, after a model failure (timeout, cut or invalid answer), may answer again from the emails and web
/// results the last request already read instead of searching and reading again: same kind of request, same text,
/// same data policy of the profile and same conversation, read less than <see cref="KeptFor"/> ago.
/// </summary>
public static class RetryEvidence
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(15);

    public static string Key(string kind, string request, LlmProfileConfig profile, string conversationId) =>
        kind + "\n" + request + "\n" + ConversationPolicy.Key(profile) + "\n" + conversationId;

    public static bool Reusable(bool retry, string? keptKey, DateTime keptReadUtc, string key, DateTime nowUtc) =>
        retry && keptKey == key && nowUtc - keptReadUtc < KeptFor && nowUtc >= keptReadUtc;
}
