using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Security;

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
    private readonly Func<Pseudonymizer, IEnumerable<ChatMessage>, IReadOnlyList<ChatMessage>>? _maskMessages;
    private readonly Func<CancellationToken, Task<IReadOnlyList<PrivacyEntity>>>? _entities;
    private readonly IPseudonymizationAuditStore _audit;

    /// <param name="status">Receives the start and outcome of each request to the model (the pane's IA indicator).</param>
    public LlmRequestDispatcher(ILlmClient client, LlmProfileConfig profile, Action? validatePolicy = null, ModelServiceStatus? status = null,
        Action? requestStarting = null, Func<CancellationToken, Task<IReadOnlyList<PrivacyEntity>>>? entities = null,
        IPseudonymizationAuditStore? audit = null)
        : this(client, profile, validatePolicy, status, requestStarting, null, entities, audit)
    {
    }

    // A deterministic masking refusal can be exercised without relying on runtime-specific regex optimizations.
    internal LlmRequestDispatcher(ILlmClient client, LlmProfileConfig profile, Action? validatePolicy, ModelServiceStatus? status,
        Action? requestStarting, Func<Pseudonymizer, IEnumerable<ChatMessage>, IReadOnlyList<ChatMessage>>? maskMessages,
        Func<CancellationToken, Task<IReadOnlyList<PrivacyEntity>>>? entities = null, IPseudonymizationAuditStore? audit = null)
    {
        _client = client;
        _profile = profile;
        _validatePolicy = validatePolicy;
        _status = status;
        _requestStarting = requestStarting;
        _maskMessages = maskMessages; _entities = entities; _audit = audit ?? new PseudonymizationAuditStore();
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
        Pseudonymizer? mask = null;
        Pseudonymizer.StreamRestorer? restorer = null;
        var request = _status?.Begin();
        // Enumerated by hand: a yield cannot sit in a try with a catch, and the outcome needs the exception.
        IAsyncEnumerator<string>? chunks = null;
        try
        {
            try
            {
                mask = await CreateMaskAsync(cancellationToken);
                var outgoing = await PrepareAsync(mask, prepared, cancellationToken);
                restorer = mask == null ? null : new Pseudonymizer.StreamRestorer(mask);
                chunks = _client.StreamChatAsync(outgoing, cancellationToken).GetAsyncEnumerator(cancellationToken);
                _requestStarting?.Invoke();
            }
            catch (Exception ex)
            {
                Record(mask, ex is OperationCanceledException ? "cancelled" : "failed");
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
                    Record(mask, ex is OperationCanceledException ? "cancelled" : "failed");
                    request?.Failed(ex, cancellationToken);
                    throw;
                }
                cancellationToken.ThrowIfCancellationRequested();
                _validatePolicy?.Invoke();
                string shown = restorer?.Push(chunks.Current) ?? chunks.Current;
                if (shown.Length > 0) yield return shown;
            }
            Record(mask, "completed");
            request?.Succeeded();
        }
        finally
        {
            try { if (mask?.Audit.Outcome == "dispatching") Record(mask, "cancelled"); }
            finally
            {
                request?.Abandoned();
                if (chunks != null) await chunks.DisposeAsync().ConfigureAwait(false);
            }
        }
        if (restorer != null && restorer.Flush() is { Length: > 0 } rest) yield return rest;
    }

    public async Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _validatePolicy?.Invoke();
        var prepared = ModelRequestContext.Prepare(_profile, messages, jsonSchema);
        Pseudonymizer? mask = null;
        var request = _status?.Begin();
        string result;
        try
        {
            mask = await CreateMaskAsync(cancellationToken);
            var outgoing = await PrepareAsync(mask, prepared, cancellationToken, jsonSchema);
            _requestStarting?.Invoke();
            result = await _client.CompleteStructuredAsync(outgoing, jsonSchema, cancellationToken).ConfigureAwait(false);
            Record(mask, "completed");
            request?.Succeeded();
        }
        catch (Exception ex)
        {
            Record(mask, ex is OperationCanceledException ? "cancelled" : "failed");
            request?.Failed(ex, cancellationToken);
            throw;
        }
        finally { request?.Abandoned(); }
        cancellationToken.ThrowIfCancellationRequested();
        _validatePolicy?.Invoke();
        return mask?.RestoreJson(result) ?? result;
    }

    private async Task<Pseudonymizer?> CreateMaskAsync(CancellationToken ct)
    {
        if (!Pseudonymize) return null;
        try
        {
            var entities = _entities == null ? Array.Empty<PrivacyEntity>() : await _entities(ct);
            ct.ThrowIfCancellationRequested(); _validatePolicy?.Invoke();
            return new Pseudonymizer(entities, _profile.PseudonymizeAmounts, _profile.PseudonymizeDates);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            WriteRecord(new PseudonymizationAudit { Outcome = "blocked" });
            throw new DataEgressBlockedException("Dizionario o strategie di pseudonimizzazione non disponibili: nessun dato inviato.");
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> PrepareAsync(Pseudonymizer? mask, IReadOnlyList<ChatMessage> prepared, CancellationToken ct, string? schema = null)
    {
        if (mask == null) return prepared;
        var input = new List<ChatMessage>(prepared);
        if (schema != null) input.Add(ChatMessage.System(schema));
        var masked = await Task.Run(() => _maskMessages == null ? mask.Mask(input, ct) : _maskMessages(mask, input), ct);
        // Schema keywords, property names and required/enum contracts must stay exact. Refuse a schema
        // containing detectable personal data rather than sending it outside the masking boundary.
        if (schema != null && masked[masked.Count - 1].Content != schema)
            throw new DataEgressBlockedException("Lo schema contiene dati personali riconosciuti: la richiesta non è stata inviata.");
        var outgoing = schema == null ? masked : new List<ChatMessage>(System.Linq.Enumerable.Take(masked, prepared.Count));
        ModelRequestContext.Prepare(_profile, outgoing, schema);
        ct.ThrowIfCancellationRequested(); _validatePolicy?.Invoke();
        // A failed write prevents transport. "dispatching" is an attempt, not proof of server receipt.
        await Task.Run(() => Record(mask, "dispatching"), ct);
        ct.ThrowIfCancellationRequested(); _validatePolicy?.Invoke();
        return outgoing;
    }

    private void Record(Pseudonymizer? mask, string outcome)
    {
        if (mask == null) return;
        mask.Audit.Outcome = outcome;
        WriteRecord(mask.Audit);
    }

    private void WriteRecord(PseudonymizationAudit record)
    {
        try { _audit.Write(record); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw new DataEgressBlockedException("Registro della pseudonimizzazione non disponibile. Nessun nuovo invio consentito; un invio già avviato potrebbe essere stato ricevuto."); }
    }

    public void Dispose() => (_client as IDisposable)?.Dispose();
}
