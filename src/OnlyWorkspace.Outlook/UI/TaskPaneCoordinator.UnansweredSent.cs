using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.Outlook.UI;

public partial class TaskPaneCoordinator
{
    private int FollowUpMinDays => Math.Max(1, Math.Min(30, _config?.Limits?.FollowUpDays ?? 5));

    private Task ExecuteFollowUpScanAsync(AnalysisRequest request, Dictionary<string, string?> op) =>
        new UnansweredSentPresenter(this).ExecuteAsync(request, op);

    private sealed class UnansweredSentPresenter
    {
        private const int FollowUpMaxLookbackDays = 365;
        private readonly TaskPaneCoordinator _owner;

        public UnansweredSentPresenter(TaskPaneCoordinator owner) => _owner = owner;

        /// <summary>
        /// Local scan of Sent Items (no LLM) of the accounts chosen in the form, with its recipient, subject and
        /// period filters: each unanswered email gets a "Bozza sollecito" button.
        /// </summary>
        public async Task ExecuteAsync(AnalysisRequest request, Dictionary<string, string?> op)
        {
            var cts = _owner.BeginOperation();
            var ct = cts.Token;
            int turn = _owner._turn;
            var sw = Stopwatch.StartNew();
            _owner.PostStream(turn, new { type = "stream_start", kind = "action", action = "Ricerca · " + request.Goal.Label, prompt = (string?)null });

            try
            {
                int days = _owner.FollowUpMinDays;
                int lookback = Math.Max(days + 1, request.Days > 0 ? Math.Min(request.Days, FollowUpMaxLookbackDays) : FollowUpMaxLookbackDays);
                var filter = new EmailSearchQuery
                {
                    Recipient = request.Person.Length > 0 ? request.Person : null,
                    Subject = request.Subject.Length > 0 ? request.Subject : null
                };
                // Sent Items only, whatever the form says about folders.
                string scope = new AnalysisRequest { Stores = request.Stores }.AccountsLabel("Posta inviata")
                    + new AnalysisRequest { Person = request.Person, PersonRole = "recipient", Subject = request.Subject }.FiltersLabel() + $", ultimi {lookback} giorni";
                _owner.PostPhase(turn, "Analisi della posta inviata…");
                await Task.Yield(); // let the pane show the phase before the synchronous OOM scan

                var candidates = new List<FollowUpCandidate>();
                using (var scan = _owner._dataService.BeginUnansweredSentScan(days, lookback, request.Stores.Select(s => s.Key).ToList(), filter))
                {
                    while (!scan.Complete)
                    {
                        ct.ThrowIfCancellationRequested();
                        candidates.AddRange(scan.ReadNext(20));
                        _owner.PostPhase(turn, $"Posta inviata: {scan.Scanned} messaggi esaminati, {candidates.Count} senza risposta…");
                        await Task.Delay(1, ct);
                    }
                    if (scan.FoldersFailed > 0)
                        _owner.PostStream(turn, new { type = "stream_chunk", chunk = $"{scan.FoldersFailed} cartelle Posta inviata non accessibili: la copertura è parziale.\n\n" });
                }
                candidates = candidates.OrderByDescending(c => c.SentOn).ToList();
                var now = DateTime.Now;
                var text = candidates.Count == 0
                    ? $"Nessuna email inviata è senza risposta da almeno {days} giorni ({scope})."
                    : $"{(candidates.Count == 1 ? "1 email inviata" : candidates.Count + " email inviate")} senza risposta da almeno {days} giorni ({scope}). Scegli per quali preparare un sollecito:";
                _owner.PostStream(turn, new { type = "stream_chunk", chunk = text });

                var items = candidates.Select(c => new
                {
                    kind = "followup",
                    title = string.IsNullOrWhiteSpace(c.Subject) ? "(senza oggetto)" : c.Subject,
                    when = c.WhenLabel(now),
                    buttonLabel = "Bozza sollecito",
                    buttonTip = "Genera un sollecito indirizzato ai destinatari originali",
                    message = new { type = "followup_draft", entryId = c.EntryId, storeId = c.StoreId, to = c.Recipients }
                }).ToList();
                var export = ListExport(new[] { "Oggetto", "Destinatari", "Inviata", "Giorni senza risposta" },
                    candidates.Select(c => new[] { c.Subject, c.Recipients, c.SentOn.ToString("dd/MM/yyyy"), c.DaysWaiting(now).ToString() }));

                sw.Stop();
                _owner.PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op, items, export });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _owner.PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
            }
            catch (Exception ex)
            {
                OnlyWorkspaceLog.Logger.Error(ex, "Follow-up scan failed");
                _owner.PostStream(turn, new { type = "stream_error", message = "Analisi della posta inviata non riuscita. Riprova; se il problema continua, consulta il log.", retryable = true, op });
            }
            finally
            {
                _owner.EndOperation(cts);
            }
        }
    }
}
