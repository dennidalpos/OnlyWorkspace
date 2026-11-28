using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Prompts;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Logging;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// Writing actions on the email being written (Danny, 20/10/2026): Scrivi, Migliora, Traduci, Oggetto. One model call
/// on the draft alone: no mail search, no web search. The text goes back into Outlook only on the user's click.
/// </summary>
public partial class TaskPaneCoordinator
{
    private void StartComposeAction(string type, JsonElement message)
    {
        switch (type)
        {
            case "compose_improve": _ = ExecuteImproveAsync(GetString(message, "style") ?? "correct"); break;
            case "compose_translate": _ = ExecuteTranslateAsync(GetString(message, "language") ?? "en"); break;
            case "compose_subject": _ = ExecuteSubjectAsync(); break;
            case "compose_apply": ApplyWrittenText(message); break;
        }
    }

    /// <summary>Ribbon «Scrivi con IA»: the pane's composer asks what to write.</summary>
    public void ShowWriteInput() => ShowCustomInstructionInput("chat");

    public Task ExecuteImproveAsync(string style)
    {
        var label = style switch
        {
            "formal" => "Più formale",
            "friendly" => "Più cordiale",
            "shorter" => "Più breve",
            "clearer" => "Più chiaro",
            _ => "Correggi"
        };
        var op = new Dictionary<string, string?> { ["type"] = "compose_improve", ["style"] = style };
        return ExecuteComposeAsync(op, "Migliora · " + label, text => PromptCatalog.CreateImprovePrompt(text, style));
    }

    public Task ExecuteTranslateAsync(string language)
    {
        var name = PromptCatalog.TranslateLanguages.TryGetValue(language, out var known) ? known : language;
        var op = new Dictionary<string, string?> { ["type"] = "compose_translate", ["language"] = language };
        return ExecuteComposeAsync(op, "Traduci · " + name, text => PromptCatalog.CreateTranslatePrompt(text, language));
    }

    public Task ExecuteSubjectAsync()
    {
        var op = new Dictionary<string, string?> { ["type"] = "compose_subject" };
        return ExecuteComposeAsync(op, "Oggetto", PromptCatalog.CreateSubjectPrompt, subject: true);
    }

    /// <summary>
    /// The selected text, else what the user wrote above the quoted thread, goes to the model once; the result
    /// offers Sostituisci selezione, Inserisci or Usa come oggetto.
    /// </summary>
    private async Task ExecuteComposeAsync(Dictionary<string, string?> op, string opName, Func<string, IEnumerable<ChatMessage>> promptFactory, bool subject = false)
    {
        var cts = BeginOperation();
        var ct = cts.Token;
        int turn = _turn;
        ILlmClient? client = null;
        LlmProfileConfig? activeProfile = null;
        var sw = Stopwatch.StartNew();
        PostStream(turn, new { type = "stream_start", kind = "action", action = opName, prompt = (string?)null });

        try
        {
            var draft = _dataService.GetComposeDraft(_contextWindow);
            if (draft == null)
            {
                PostStream(turn, new { type = "stream_error", message = "Apri o inizia un'email da scrivere, poi riprova.", retryable = true, op });
                return;
            }
            PostDraftContext(turn, draft);
            bool fromSelection = !subject && draft.Selection.Length > 0;
            var text = fromSelection ? draft.Selection : draft.OwnText;
            if (string.IsNullOrWhiteSpace(text))
            {
                PostStream(turn, new { type = "stream_error", message = "Scrivi o seleziona del testo nell'email, poi riprova.", retryable = true, op });
                return;
            }
            if (fromSelection) op["selection"] = draft.Selection;

            activeProfile = GetActiveProfile();
            if (activeProfile == null)
            {
                PostNotConfigured();
                return;
            }
            var messages = promptFactory(_contextBuilder.EnsureFits(text, ProviderCatalog.PromptBudget(activeProfile)));
            client = CreateLlmClient(activeProfile, wholeEmails: false);
            OnlyWorkspaceLog.Logger.Information("Starting {Op} with model {Model} on {BaseUrl}", op["type"], activeProfile.Model, activeProfile.BaseUrl);
            PostPhase(turn, "Scrittura del testo…");

            var response = new StringBuilder();
            var timings = new PhaseTimings();
            var generation = Stopwatch.StartNew();
            await foreach (var chunk in client.StreamChatAsync(messages, ct))
            {
                response.Append(chunk);
                PostStream(turn, new { type = "stream_chunk", chunk });
            }
            timings.AddGeneration(generation.ElapsedMilliseconds);

            sw.Stop();
            await RecordActionInHistoryAsync(activeProfile, opName + ": " + DraftLabel(draft), response.ToString(), ct, DraftRefOf(draft), opName);
            PostStream(turn, new
            {
                type = "stream_end",
                latencyMs = sw.ElapsedMilliseconds,
                truncated = client.LastCompletion?.IsTruncated == true,
                op,
                writing = subject ? new { insert = false, replace = false, newMail = false, subject = true } : WritingTargets(true, draft)
            });
            timings.LogCompleted(OnlyWorkspaceLog.Logger, op["type"], sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
        }
        catch (Exception ex)
        {
            LogFailure(ex, op["type"]);
            PostStream(turn, new { type = "stream_error", message = LlmErrorText.Describe(ex, activeProfile), retryable = Retryable(ex), op });
        }
        finally
        {
            DisposeClient(client);
            EndOperation(cts);
        }
    }

    /// <summary>Where the written text can go: the pane shows one button for each.</summary>
    private static object? WritingTargets(bool writing, ComposeDraft? draft) => !writing ? null : new
    {
        insert = draft != null,
        replace = draft != null && draft.Selection.Length > 0,
        newMail = draft == null,
        subject = false
    };

    /// <summary>Inserisci, Sostituisci selezione, Usa come oggetto, Nuova email: only on the user's click.</summary>
    private void ApplyWrittenText(JsonElement message)
    {
        var text = GetString(message, "text");
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            switch (GetString(message, "mode"))
            {
                case "replace":
                    if (_dataService.ReplaceDraftSelection(GetString(message, "selection") ?? string.Empty, text!, _contextWindow))
                        Toast("Testo sostituito nell'email. Rivedilo prima di inviare.", "success");
                    else
                        Toast("Il testo selezionato è cambiato: seleziona di nuovo il testo o usa Inserisci al cursore.", "error");
                    break;
                case "subject":
                    var subject = text!.Trim().Split('\n')[0].Trim().Trim('"', '«', '»');
                    bool set = _dataService.SetDraftSubject(subject, _contextWindow);
                    Toast(set ? "Oggetto impostato." : "Nessuna email in scrittura in questa finestra.", set ? "success" : "error");
                    break;
                case "new_mail":
                    var (newSubject, body) = ComposeDraft.SplitSubject(text!);
                    _dataService.OpenNewMail(newSubject, body);
                    Toast("Nuova email aperta in Outlook. Aggiungi i destinatari e rivedila prima di inviare.", "success");
                    break;
                default:
                    var (insertSubject, insertBody) = ComposeDraft.SplitSubject(text!);
                    var target = _dataService.GetComposeDraft(_contextWindow);
                    bool inserted = _dataService.InsertIntoDraft(target?.WithoutDraftSignature(insertBody) ?? insertBody, _contextWindow);
                    // The model proposes a subject only for an email without one: it goes where it belongs.
                    if (inserted && insertSubject.Length > 0 && string.IsNullOrWhiteSpace(target?.Subject))
                        _dataService.SetDraftSubject(insertSubject, _contextWindow);
                    Toast(inserted ? "Testo inserito al cursore. Rivedilo prima di inviare." : "Nessuna email in scrittura in questa finestra.", inserted ? "success" : "error");
                    break;
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to apply written text");
            Toast("Inserimento non riuscito. Verifica che l'email sia ancora aperta in scrittura e riprova.", "error");
        }
    }

    private static string DraftLabel(ComposeDraft draft) =>
        string.IsNullOrWhiteSpace(draft.Subject) ? "nuova email" : draft.Subject;

    /// <summary>The draft as a history reference: no EntryId until Outlook saves it, so it cannot be reopened.</summary>
    private static HistoryContextReference DraftRefOf(ComposeDraft draft) => new()
    {
        Subject = DraftLabel(draft),
        ItemType = "draft",
        Date = DateTime.Now
    };

    private void PostDraftContext(int turn, ComposeDraft draft)
    {
        PostStream(turn, new
        {
            type = "stream_context",
            entryId = "draft:" + draft.Subject,
            subject = "In scrittura: " + DraftLabel(draft),
            meta = draft.Kind switch { "reply" => "Risposta", "forward" => "Inoltro", _ => "Nuova email" } +
                   (string.IsNullOrWhiteSpace(draft.To) ? string.Empty : " • A: " + draft.To),
            threadPosition = 1
        });
    }
}
