using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Infrastructure.Llm;

/// <summary>
/// Production dispatch boundary, also usable with a recording client without Outlook or a network. At an exposure
/// level that asks for it, personal data is replaced with placeholders before transport and restored in the answer.
/// </summary>
public sealed class LlmRequestDispatcher : ILlmClient, IDisposable
{
    private readonly ILlmClient _client;
    private readonly LlmProfileConfig _profile;
    private readonly Action? _validatePolicy;
    private readonly ModelServiceStatus? _status;
    private readonly Action? _requestStarting;
    private readonly Func<Pseudonymizer, IEnumerable<ChatMessage>, IReadOnlyList<ChatMessage>> _maskMessages;

    /// <param name="status">Receives the start and outcome of each request to the model (the pane's IA indicator).</param>
    public LlmRequestDispatcher(ILlmClient client, LlmProfileConfig profile, Action? validatePolicy = null, ModelServiceStatus? status = null,
        Action? requestStarting = null)
        : this(client, profile, validatePolicy, status, requestStarting, (mask, messages) => mask.Mask(messages))
    {
    }

    // A deterministic masking refusal can be exercised without relying on runtime-specific regex optimizations.
    internal LlmRequestDispatcher(ILlmClient client, LlmProfileConfig profile, Action? validatePolicy, ModelServiceStatus? status,
        Action? requestStarting, Func<Pseudonymizer, IEnumerable<ChatMessage>, IReadOnlyList<ChatMessage>> maskMessages)
    {
        _client = client;
        _profile = profile;
        _validatePolicy = validatePolicy;
        _status = status;
        _requestStarting = requestStarting;
        _maskMessages = maskMessages;
    }

    private bool Pseudonymize => ExposurePolicy.Of(_profile).Pseudonymize;

    public LlmTokenUsage? LastTokenUsage => _client.LastTokenUsage;
    public LlmCompletion? LastCompletion => _client.LastCompletion;
    public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => _client.CheckHealthAsync(cancellationToken);
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => _client.ListModelsAsync(cancellationToken);
    public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => _client.GetModelInfoAsync(model, cancellationToken);

    public async IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _validatePolicy?.Invoke();
        var prepared = ModelRequestContext.Prepare(_profile, messages);
        var mask = Pseudonymize ? new Pseudonymizer() : null;
        var restorer = mask == null ? null : new Pseudonymizer.StreamRestorer(mask);
        var request = _status?.Begin();
        // Enumerated by hand: a yield cannot sit in a try with a catch, and the outcome needs the exception.
        IAsyncEnumerator<string>? chunks = null;
        try
        {
            try
            {
                var outgoing = mask == null ? prepared : _maskMessages(mask, prepared);
                chunks = _client.StreamChatAsync(outgoing, cancellationToken).GetAsyncEnumerator(cancellationToken);
                _requestStarting?.Invoke();
            }
            catch (Exception ex)
            {
                request?.Failed(ex, cancellationToken);
                throw;
            }
            while (true)
            {
                try
                {
                    if (!await chunks.MoveNextAsync().ConfigureAwait(false)) break;
                }
                catch (Exception ex)
                {
                    request?.Failed(ex, cancellationToken);
                    throw;
                }
                cancellationToken.ThrowIfCancellationRequested();
                _validatePolicy?.Invoke();
                string shown = restorer?.Push(chunks.Current) ?? chunks.Current;
                if (shown.Length > 0) yield return shown;
            }
            request?.Succeeded();
        }
        finally
        {
            request?.Abandoned();
            if (chunks != null) await chunks.DisposeAsync().ConfigureAwait(false);
        }
        if (restorer != null && restorer.Flush() is { Length: > 0 } rest) yield return rest;
    }

    public async Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _validatePolicy?.Invoke();
        var prepared = ModelRequestContext.Prepare(_profile, messages, jsonSchema);
        var mask = Pseudonymize ? new Pseudonymizer() : null;
        var request = _status?.Begin();
        string result;
        try
        {
            var outgoing = mask == null ? prepared : _maskMessages(mask, prepared);
            _requestStarting?.Invoke();
            result = await _client.CompleteStructuredAsync(outgoing, jsonSchema, cancellationToken).ConfigureAwait(false);
            request?.Succeeded();
        }
        catch (Exception ex)
        {
            request?.Failed(ex, cancellationToken);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        _validatePolicy?.Invoke();
        return mask?.RestoreJson(result) ?? result;
    }

    public void Dispose() => (_client as IDisposable)?.Dispose();
}
