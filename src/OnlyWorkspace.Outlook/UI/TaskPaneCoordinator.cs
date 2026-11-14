using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core;
using OnlyWorkspace.Core.Context;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Agents;
using OnlyWorkspace.Core.Archive;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Prompts;
using OnlyWorkspace.Core.Security;
using OnlyWorkspace.Infrastructure.Configuration;
using OnlyWorkspace.Infrastructure.Llm;
using OnlyWorkspace.Infrastructure.Logging;
using OnlyWorkspace.Infrastructure.Security;
using OnlyWorkspace.Outlook.Services;

namespace OnlyWorkspace.Outlook.UI;

/// <summary>
/// Bridges the WebView2 pane and Outlook. All members run on the Outlook UI thread: awaits deliberately
/// keep the WinForms SynchronizationContext (no ConfigureAwait(false)) because continuations touch the
/// Outlook Object Model, which is STA-bound.
/// </summary>
public partial class TaskPaneCoordinator : IDisposable
{
    private readonly TaskPaneControl _control;
    private readonly IOutlookDataService _dataService;
    private readonly Func<IArchiveIndexer?> _archiveSource;
    private IArchiveIndexer? CurrentArchive => _archiveSource();
    private readonly object? _contextWindow;
    private readonly IConfigurationService _configService;
    private readonly IContextBuilder _contextBuilder;
    private readonly IHistoryStorageService _historyStorage;
    private ConversationHistory _currentConversation = new();
    private readonly PaneOperations _operations = new();
    // What the page shows of the running work: replayed to a page loaded again, listed by the other windows' panes.
    private readonly PaneJobReplay _replay = new();
    private readonly ActionItemSaves _actionItemSaves = new();
    private readonly int _jobId;
    private DateTime _jobsNotifiedUtc;
    // Bumped by every new operation: stream_* messages of a superseded one are dropped.
    private int _turn;
    // Web Search action of the message that starts the next turn, and of the running turn (WebSearchAction ids).
    private string? _pendingWebAction;
    private string? _webActionForTurn;
    private readonly TurnWebSources _webForTurn = new();
    private readonly StringBuilder _turnText = new();
    private List<HistoryContextReference> _mailSourcesForTurn = new();

    private string? _lastDiagnosticsPath;
    private WorkspaceConfig? _config;
    private bool _isDisposed;
    private int _policyEpoch;
    // IA indicator of the top bar: the active profile's service, from real requests only.
    private readonly ModelServiceStatus _modelStatus = new();

    public TaskPaneCoordinator(
        TaskPaneControl control,
        IOutlookDataService dataService,
        object? contextWindow = null,
        IConfigurationService? configService = null,
        IContextBuilder? contextBuilder = null,
        IHistoryStorageService? historyStorage = null,
        Func<IArchiveIndexer?>? archiveSource = null)
    {
        _archiveSource = archiveSource ?? ArchiveIndexerAccess.Current;
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _contextWindow = contextWindow;
        _configService = configService ?? new FileConfigurationService();
        _contextBuilder = contextBuilder ?? new ContextBuilder();
        _historyStorage = historyStorage ?? new DpapiHistoryStorageService();


        _control.ControlReady += OnControlReady;
        _control.MessageReceived += OnMessageReceived;
        _jobId = PaneJobs.Register(contextWindow is Microsoft.Office.Interop.Outlook.Inspector ? "finestra di un'email" : "finestra principale",
            () => _replay, StopActiveTask);
        PaneJobs.Changed += PostOtherJobs;
        _modelStatus.Changed += PostModelStatus;

        LoadInitialConfiguration();
    }

    /// <summary>
    /// Inside the ribbon callback that creates the pane: only what its action needs (configuration and IA indicator).
    /// The page does not exist yet; OnControlReady posts profiles and context and starts the history cleanup, so the
    /// Object Model reads, the key comparison and the messages stay out of Outlook's UI-thread callback (A157).
    /// </summary>
    private void LoadInitialConfiguration()
    {
        try
        {
            _config = _configService.LoadConfiguration();
            ConfigureModelStatus();
        }
        catch (Exception ex)
        {
            // OnControlReady loads again and shows the error in the page.
            _config = null;
            OnlyWorkspaceLog.Logger.Warning(ex, "Configuration not loaded at pane creation");
            _modelStatus.Configure(null, null, "Configurazione non valida o assente.");
        }
    }

    private void OnControlReady()
    {
        UiSync.Ensure();
        ReloadConfiguration();
        // A page loaded again (WebView2 restarted) shows the work still running instead of an empty pane.
        if (_replay.Restore() is { } restore) _control.PostMessage(restore);
        PostOtherJobs();
    }

    private void NotifyJobs(object message)
    {
        var type = message is IDictionary<string, object?> map && map.TryGetValue("type", out var mapped)
            ? mapped as string : message.GetType().GetProperty("type")?.GetValue(message) as string;
        bool boundary = type is "stream_start" or "stream_end" or "stream_error" or "stream_cancelled";
        // Progress reaches the other panes at most once a second.
        if (!boundary && DateTime.UtcNow - _jobsNotifiedUtc < TimeSpan.FromSeconds(1)) return;
        _jobsNotifiedUtc = DateTime.UtcNow;
        PaneJobs.Notify();
    }

    /// <summary>Work running in the panes of the other Outlook windows, shown in this pane's job bar.</summary>
    private void PostOtherJobs()
    {
        if (_isDisposed) return;
        _control.PostMessage(new
        {
            type = "other_jobs",
            jobs = PaneJobs.Others(_jobId).Select(j => new { id = j.Id, window = j.Window, action = j.Action, done = j.Done, total = j.Total }).ToList()
        });
    }

    /// <summary>Interrompi on the ribbon: the work of this window, or else the work running in the other windows.</summary>
    public void StopFromRibbon()
    {
        if (_replay.Running) StopActiveTask();
        else if (PaneJobs.StopOthers(_jobId) > 0) OnlyWorkspaceLog.Logger.Information("Ribbon Interrompi stopped the work of another Outlook window");
    }

    /// <summary>Picks up a ConfigTool save made while the pane stays open.</summary>
    public void ReloadIfConfigChanged()
    {
        if (_configService.HasConfigurationChanged()) ReloadConfiguration();
    }

    public void ReloadConfiguration()
    {
        if (_isDisposed) return;

        var oldProfile = GetActiveProfile();
        string? oldPolicy = oldProfile == null ? null : ConversationPolicy.Key(oldProfile);
        string? oldSecret = oldProfile == null ? null : _configService.ResolveSecret(oldProfile.SecretRef);
        string? oldWebSecret = oldProfile == null ? null : _configService.ResolveSecret(oldProfile.WebSearchSecretRef);
        try
        {
            try
            {
                _config = _configService.LoadConfiguration();
            }
            catch (Exception ex)
            {
                _config = null;
                InvalidatePolicyOperations();
                OnlyWorkspaceLog.Logger.Error(ex, "Failed to load configuration");
                _modelStatus.Configure(null, null, "Configurazione non valida o assente.");
                _control.PostMessage(new
                {
                    type = "config_error",
                    message = "Configurazione non valida o assente: apri la Configurazione per verificarla."
                });
                return;
            }

            var newProfile = GetActiveProfile();
            bool policyChanged = oldPolicy != (newProfile == null ? null : ConversationPolicy.Key(newProfile));
            bool credentialsChanged = oldSecret != (newProfile == null ? null : _configService.ResolveSecret(newProfile.SecretRef)) ||
                oldWebSecret != (newProfile == null ? null : _configService.ResolveSecret(newProfile.WebSearchSecretRef));
            if (policyChanged || credentialsChanged)
            {
                InvalidatePolicyOperations();
                if (policyChanged && _currentConversation.Messages.Count > 0) Toast(ConversationPolicy.ChangedMessage);
            }
            OnlyWorkspaceLog.Initialize(_config?.Logging?.Level);
            InitializeProfilesInUi();
            UpdateContext();

            var history = _config?.History;
            if (history?.Enabled == true)
            {
                int retentionDays = history.RetentionDays;
                int maxConversations = history.MaxConversations;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _historyStorage.CleanupExpiredConversationsAsync(retentionDays, maxConversations).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        OnlyWorkspaceLog.Logger.Warning(ex, "Background history retention cleanup encountered an issue");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to initialize TaskPane coordinator");
        }
    }

    public void UpdateContext()
    {
        if (_isDisposed) return;
        _ = CheckDeletedHistoryAsync();

        try
        {
            UiSync.Ensure();
            // An inline reply keeps its original selected: the draft still decides which actions the pane shows.
            var draftSubject = _dataService.GetComposeSubject(_contextWindow);
            var email = draftSubject == null ? _dataService.GetSelectedEmailHeader(_contextWindow) : null;
            _control.PostMessage(new
            {
                type = "context_update",
                hasEmail = email != null,
                entryId = email?.EntryId,
                storeId = email?.StoreId,
                subject = email?.Subject,
                // The email being written: the pane offers the writing actions instead of the email ones.
                compose = draftSubject == null ? null : new { subject = draftSubject, kind = ComposeDraft.KindOf(draftSubject) }
            });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to update email context");
            _control.PostMessage(new { type = "context_update", hasEmail = false });
        }
    }

    // Exchange senders expose an X.500 DN ("/o=…"): show the display name only.
    private static string SenderOf(SelectedEmailInfo email) =>
        string.IsNullOrWhiteSpace(email.SenderEmail) || email.SenderEmail.StartsWith("/", StringComparison.Ordinal)
            ? email.SenderName
            : $"{email.SenderName} <{email.SenderEmail}>";

    /// <summary>The email a history entry is about: the pane shows it under the prompt and reopens it.</summary>
    private HistoryContextReference HistoryRefOf(SelectedEmailInfo email) => new()
    {
        OutlookProfile = _dataService.GetProfileName(),
        MessageId = email.MessageId,
        CapturedUtc = _sourceReadUtc,
        EntryId = email.EntryId,
        StoreId = email.StoreId,
        Subject = email.Subject,
        Sender = email.SenderName,
        Date = email.ReceivedTime
    };

    /// <summary>Header lines prepended to the email text so the model knows subject, sender and date.</summary>
    private static string EmailHeaderOf(SelectedEmailInfo email, bool withSender)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(email.Subject)) sb.AppendLine("Oggetto: " + email.Subject);
        if (withSender)
        {
            sb.AppendLine("Da: " + SenderOf(email));
            sb.AppendLine($"Data: {email.ReceivedTime:dd/MM/yyyy HH:mm}");
        }
        return sb.Length == 0 ? string.Empty : sb.AppendLine().ToString();
    }

    /// <param name="mode">"chat" for free questions, "reply" for a reply draft guided by an instruction.</param>
    public void ShowCustomInstructionInput(string mode = "chat", string? initialPrompt = null)
    {
        // A just-opened pane may not have received its deferred selection update yet.
        if (mode == "reply") UpdateContext();
        _control.PostMessage(new
        {
            type = "show_input",
            mode,
            prompt = initialPrompt ?? string.Empty
        });
    }

    /// <summary>The IA indicator for the loaded configuration; the active profile, or null without one.</summary>
    private LlmProfileConfig? ConfigureModelStatus()
    {
        if (_config == null) return null;
        if (_config.Profiles == null || _config.Profiles.Count == 0)
        {
            _modelStatus.Configure(null, null, "Nessun profilo IA configurato.");
            return null;
        }

        var activeProfile = GetActiveProfile() ?? _config.Profiles[0];
        bool blocked = !DataEgress.AllowsModel(_config, activeProfile, _configService.ResolveSecret);
        _modelStatus.Configure(activeProfile, ModelStatusKey(activeProfile), blocked ? DataEgress.BlockedMessageFor(activeProfile) : null);
        return activeProfile;
    }

    private void InitializeProfilesInUi()
    {
        if (_config == null) return;

        var activeProfile = ConfigureModelStatus();
        if (activeProfile == null)
        {
            _control.PostMessage(new
            {
                type = "config_error",
                message = "Nessun profilo IA configurato. Configura almeno un profilo per usare OnlyWorkspace."
            });
            return;
        }
        bool blocked = !DataEgress.AllowsModel(_config, activeProfile, _configService.ResolveSecret);

        _control.PostMessage(new
        {
            type = "profiles_init",
            activeProfileName = DisplayNameOf(activeProfile),
            activeProvider = activeProfile.Type,
            isCloudActive = activeProfile.IsExternal,
            activeBlocked = blocked,
            blockedMessage = DataEgress.BlockedMessageFor(activeProfile),
            webSearchEnabled = activeProfile.WebSearchEnabled,
            badge = BadgeOf(activeProfile)
        });
    }

    /// <summary>Policy and credentials epoch: a changed profile or key starts the indicator again from «non verificato».</summary>
    private string ModelStatusKey(LlmProfileConfig profile) =>
        ConversationPolicy.Key(profile) + "|" + System.Threading.Volatile.Read(ref _policyEpoch);

    /// <summary>Also raised from a request's thread: everything comes from the snapshot, PostMessage marshals.</summary>
    private void PostModelStatus()
    {
        if (_isDisposed) return;
        var status = _modelStatus.Current;
        _control.PostMessage(new
        {
            type = "ai_status",
            light = status.Light,
            label = status.Label,
            reason = status.Reason,
            at = status.AtUtc?.ToLocalTime().ToString("HH:mm"),
            external = status.External
        });
    }

    /// <summary>Model and server shown under the pane's top bar: who receives the data of the next action.</summary>
    private static object BadgeOf(LlmProfileConfig profile) => new
    {
        model = profile.Model,
        host = HostOf(profile.BaseUrl),
        region = profile.IsExternal ? ServiceRegions.Label(profile.ServiceRegion) : string.Empty,
        webHosts = WebActionLabels(profile).Count > 0 ? string.Join(", ", WebSearchService.Destinations(profile)) : string.Empty,
        webActions = WebActionLabels(profile)
    };

    /// <summary>Names of the actions that search the web with this profile (empty when Web Search is off).</summary>
    private static List<string> WebActionLabels(LlmProfileConfig profile) =>
        WebSearchAction.All.Where(a => WebSearchAction.Uses(profile, a.Key)).Select(a => a.Value).ToList();

    private void OnMessageReceived(JsonElement message)
    {
        if (_isDisposed) return;
        UiSync.Ensure();
        ReloadIfConfigChanged();

        var type = GetString(message, "type");
        if (PaneMessageRules.NeedsEmail(type))
        {
            // Explicit history/retry targets keep their original source, independently of the current selection.
            if (EmailRefOf(message) == null && _dataService?.CanActOnSelectedEmail(_contextWindow) != true)
            {
                UpdateContext();
                Toast(PaneMessageRules.NoEmailToast, "info");
                return;
            }
        }
        // The synchronous part of each handler: what runs before its first await.
        using var activity = UiThreadWatch.Activity("pane " + type);
        _pendingWebAction = WebSearchAction.Of(type);
        // When the click reached the add-in, against «Starting …»: on 27/10/2026 a summary started 28 s after a
        // click and the log could not tell where the time went (A154). The type only, never the request.
        if (_pendingWebAction != null) OnlyWorkspaceLog.Logger.Information("Pane request: {Type}", type);
        switch (type)
        {
            // Actions on the selected email.
            case "summary": _ = ExecuteSummaryAsync(EmailRefOf(message)); break;
            case "email_followup": _ = ExecuteEmailFollowUpAsync(EmailRefOf(message)); break;
            case "reply": _ = ExecuteReplyAsync(GetString(message, "mode") ?? "accept", EmailRefOf(message)); break;
            case "security": _ = ExecuteSecurityCheckAsync(EmailRefOf(message)); break;
            case "action_items": _ = ExecuteActionItemsAsync(EmailRefOf(message)); break;
            case "open_action_item": OpenActionItemDraft(message); break;
            case "extract_data": _ = ExecuteDataExtractionAsync(EmailRefOf(message)); break;
            case "save_csv": SaveCsv(message); break;
            case "briefing": _ = ExecuteBriefingAsync(); break;
            case "apply_category": ApplyCategory(message); break;
            case "followup_draft": StartFollowUpDraft(message); break;
            case "custom_reply": StartCustomReply(message); break;
            case "refine_reply": StartRefineReply(message); break;
            case "insert_draft": InsertDraftFrom(message); break;

            // Writing on the email being written.
            case "compose_improve":
            case "compose_translate":
            case "compose_subject":
            case "compose_apply":
                StartComposeAction(type!, message);
                break;

            // Cerca e analizza, chat.
            case "analysis_open": OpenAnalysis(message); break;
            case "analysis_pick_folder": PickAnalysisFolder(); break;
            case "mail_analysis":
                var analysisRequest = AnalysisRequest.Parse(message, out var analysisRaw);
                if (analysisRequest != null) _ = ExecuteMailAnalysisAsync(analysisRequest, analysisRaw, IsRetry(message));
                else Toast("Richiesta non valida. Nei fatti usa riferimenti espliciti, importi con valuta e scadenze in formato AAAA-MM-GG.", "info");
                break;
            case "analysis_question":
                var analysisQuestion = GetString(message, "question");
                if (!string.IsNullOrWhiteSpace(analysisQuestion)) _ = ExecuteAnalysisQuestionAsync(analysisQuestion!, GetString(message, "analysisId"));
                break;
            case "save_action_items": SaveActionItems(message); break;
            case "get_action_item_states": SaveActionItems(message); break;
            case "chat": StartChat(message); break;
            case "cancel": StopActiveTask(); break;
            case "stop_other":
                if (message.TryGetProperty("id", out var otherJob) && otherJob.TryGetInt32(out var otherId)) PaneJobs.Stop(otherId);
                break;

            // Guide, diagnostics and configuration.
            case "get_connection_info": SendConnectionInfo(); break;
            case "check_connection": _ = CheckActiveConnectionAsync(); break;
            case "export_diagnostics": ExportDiagnostics(); break;
            case "open_diagnostics_folder": OpenDiagnosticsFolder(); break;
            case "open_config":
                bool mailboxes = GetString(message, "page") == ConfigToolLauncher.MailboxesPage;
                if (!ConfigToolLauncher.TryLaunch(mailboxes: mailboxes)) ShowConfigUnavailable();
                break;
            case "open_web_source": OpenWebSource(GetString(message, "url")); break;

            // History.
            case "list_history": _ = SendHistoryListAsync(GetString(message, "requestId")); break;
            case "load_history": _ = LoadHistoryAsync(GetString(message, "id")); break;
            case "delete_history": _ = DeleteHistoryAsync(GetString(message, "id"), GetString(message, "requestId")); break;
            case "delete_all_history": _ = DeleteAllHistoryAsync(GetString(message, "requestId")); break;
            case "new_chat": ClearChat(); break;

            // Archive and flow.
            case "archive_get": _ = SendArchiveStatusAsync(); break;
            case "archive_light_get": _ = SendArchiveLightAsync(); break;
            case "archive_open_email": _ = OpenArchivedEmailAsync(message.Clone()); break;
            case "archive_pause":
            case "archive_index_now":
            case "archive_delete":
                _ = HandleArchiveCommandAsync(type!, message.Clone());
                break;
        }
    }

    private void StartFollowUpDraft(JsonElement message)
    {
        var followUpRef = EmailRefOf(message);
        if (followUpRef == null) return;
        // "to" travels in the op so Rigenera keeps the recipients.
        var followUpTo = GetString(message, "to") ?? string.Empty;
        var followUpOp = new Dictionary<string, string?> { ["type"] = "followup_draft", ["to"] = followUpTo };
        _ = ExecutePromptStreamAsync(followUpOp, followUpRef, "Risposta · Sollecito", body => PromptCatalog.CreateFollowUpPrompt(body, followUpTo));
    }

    private void StartCustomReply(JsonElement message)
    {
        var instruction = GetString(message, "instruction");
        if (!string.IsNullOrWhiteSpace(instruction)) _ = ExecuteCustomReplyAsync(instruction!, EmailRefOf(message));
    }

    private void StartRefineReply(JsonElement message)
    {
        var refineInstruction = GetString(message, "instruction");
        var refineDraft = GetString(message, "draft");
        if (!string.IsNullOrWhiteSpace(refineInstruction) && !string.IsNullOrWhiteSpace(refineDraft))
            _ = ExecuteRefineReplyAsync(refineInstruction!, refineDraft!, EmailRefOf(message), GetString(message, "followUp") == "1");
    }

    private void InsertDraftFrom(JsonElement message)
    {
        var text = GetString(message, "text");
        bool replyAll = message.TryGetProperty("replyAll", out var raProp) && raProp.ValueKind == JsonValueKind.True;
        bool followUp = message.TryGetProperty("followUp", out var fuProp) && fuProp.ValueKind == JsonValueKind.True;
        if (!string.IsNullOrWhiteSpace(text)) InsertDraft(text!, replyAll, EmailRefOf(message), followUp);
    }

    private void OpenAnalysis(JsonElement message)
    {
        int requestId = message.TryGetProperty("requestId", out var contextRequestId) && contextRequestId.ValueKind == JsonValueKind.Number &&
            contextRequestId.TryGetInt32(out var parsedRequestId) ? parsedRequestId : 0;
        _ = GuardArchiveUiAsync(() => SendAnalysisContextAsync(GetString(message, "scope"), requestId));
    }

    private void StartChat(JsonElement message)
    {
        var prompt = GetString(message, "prompt");
        if (string.IsNullOrWhiteSpace(prompt)) return;
        if (message.TryGetProperty("regenerate", out var regen) && regen.ValueKind == JsonValueKind.True) DropLastChatTurn(prompt!);
        _ = ExecuteChatAsync(prompt!, EmailRefOf(message), IsRetry(message));
    }

    private static bool IsRetry(JsonElement message) => message.TryGetProperty("retry", out var retry) && retry.ValueKind == JsonValueKind.True;

    /// <summary>What the last request on the mail read before asking the model, for Riprova (see <see cref="RetryEvidence"/>).</summary>
    private sealed class ReadEvidence
    {
        public string Key { get; set; } = string.Empty;
        public DateTime ReadUtc { get; set; }
        public MailEvidencePack Pack { get; set; } = null!;
        public string? Notice { get; set; }
        public string Listing { get; set; } = string.Empty;
        public WebSearchResult? Web { get; set; }
        public List<HistoryContextReference> Inputs { get; set; } = new();
        public List<DocumentOutcome> Documents { get; set; } = new();
    }

    private ReadEvidence? _readEvidence;

    private string EvidenceKey(string kind, string request, LlmProfileConfig profile) =>
        RetryEvidence.Key(kind, request, profile, _currentConversation.Id);

    private ReadEvidence? ReusableEvidence(bool retry, string key) =>
        _readEvidence is { } kept && RetryEvidence.Reusable(retry, kept.Key, kept.ReadUtc, key, DateTime.UtcNow) ? kept : null;

    /// <summary>A kept web result answers again without a new search; its sources are the turn's sources.</summary>
    private Task<WebSearchResult?> ReuseWeb(WebSearchResult web)
    {
        _webForTurn.Take(_turn, web.Sources, web.InputCut);
        return Task.FromResult<WebSearchResult?>(web);
    }

    /// <summary>Web Search sources open in the browser: https only, without credentials in the address.</summary>
    private void OpenWebSource(string? sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri) || sourceUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(sourceUri.UserInfo)) return;
        try { Process.Start(new ProcessStartInfo(sourceUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to open source");
            Toast("Impossibile aprire la fonte. Verifica che sia ancora disponibile e riprova.", "error");
        }
    }

    private static string? GetString(JsonElement message, string property)
    {
        return message.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    /// <summary>Email an operation is bound to (sent back by Rigenera/Riprova); null = current selection.</summary>
    private sealed class EmailRef
    {
        public EmailRef(string entryId, string? storeId) { EntryId = entryId; StoreId = storeId; }
        public string EntryId { get; }
        public string? StoreId { get; }
    }

    private static EmailRef? EmailRefOf(JsonElement message)
    {
        var entryId = GetString(message, "entryId");
        return string.IsNullOrEmpty(entryId) ? null : new EmailRef(entryId!, GetString(message, "storeId"));
    }

    /// <summary>Ribbon context menu: runs the action on that email (ids empty = the selected one).</summary>
    public void ExecuteOnEmail(string action, string? entryId, string? storeId)
    {
        var target = string.IsNullOrEmpty(entryId) ? null : new EmailRef(entryId!, storeId);
        _pendingWebAction = WebSearchAction.Of(action is "accept" or "decline" ? "reply" : action);
        _ = action switch
        {
            "summary" => ExecuteSummaryAsync(target),
            "accept" or "decline" => ExecuteReplyAsync(action, target),
            "security" => ExecuteSecurityCheckAsync(target),
            "action_items" => ExecuteActionItemsAsync(target),
            _ => Task.CompletedTask
        };
    }

    // Ribbon entry points: the pane message handler sets the Web Search action, the ribbon bypassed it (17/10/2026).
    public Task ExecuteSummaryAsync() => WithWebAction("summary", () => ExecuteSummaryAsync(null));

    private Task ExecuteSummaryAsync(EmailRef? target)
    {
        var op = new Dictionary<string, string?> { ["type"] = "summary" };
        return ExecutePromptStreamAsync(op, target, "Sintesi", body => PromptCatalog.CreateSummaryPrompt(body));
    }

    public Task ExecuteReplyAsync(string? mode) => WithWebAction("reply", () => ExecuteReplyAsync(mode, null));

    private Task ExecuteReplyAsync(string? mode, EmailRef? target)
    {
        var op = new Dictionary<string, string?> { ["type"] = "reply", ["mode"] = mode };
        return string.Equals(mode, "decline", StringComparison.OrdinalIgnoreCase)
            ? ExecutePromptStreamAsync(op, target, "Risposta · Rifiuta con alternativa", body => PromptCatalog.CreateDeclineReplyPrompt(body))
            : ExecutePromptStreamAsync(op, target, "Risposta · Accetta", body => PromptCatalog.CreateAcceptReplyPrompt(body));
    }

    private Task ExecuteCustomReplyAsync(string instruction, EmailRef? target)
    {
        var op = new Dictionary<string, string?> { ["type"] = "custom_reply", ["instruction"] = instruction };
        return ExecutePromptStreamAsync(op, target, "Risposta · " + Truncate(instruction, 60), body => PromptCatalog.CreateCustomReplyPrompt(body, instruction), instruction);
    }

    /// <summary>New version of an existing draft; the op carries draft and instruction so Rigenera repeats the same step.</summary>
    private Task ExecuteRefineReplyAsync(string instruction, string draft, EmailRef? target, bool followUp)
    {
        var op = new Dictionary<string, string?>
        {
            ["type"] = "refine_reply",
            ["instruction"] = instruction,
            ["draft"] = draft,
            ["followUp"] = followUp ? "1" : null
        };
        return ExecutePromptStreamAsync(op, target, "Risposta · " + Truncate(instruction, 60), body => PromptCatalog.CreateRefineReplyPrompt(body, draft, instruction), instruction);
    }

    // op: the UI message that re-runs this turn (Rigenera/Riprova); gets the email ids once resolved.
    private async Task ExecutePromptStreamAsync(Dictionary<string, string?> op, EmailRef? target, string opName, Func<string, IEnumerable<ChatMessage>> promptFactory, string? userPrompt = null)
    {
        var cts = BeginOperation();
        var ct = cts.Token;
        int turn = _turn;
        ILlmClient? client = null;
        LlmProfileConfig? activeProfile = null;
        // opName may carry the user's instruction: the log gets the operation type only (no PII).
        string logOp = op["type"] ?? "action";

        var sw = Stopwatch.StartNew();
        PostStream(turn, new { type = "stream_start", kind = "action", action = opName, prompt = userPrompt });

        try
        {
            // The ribbon callback returns before the email is read: Outlook times the add-in's callbacks.
            await Task.Yield();
            var email = target != null
                ? await _dataService.GetEmailByEntryIdAsync(target.EntryId, target.StoreId, ct)
                : await _dataService.GetCurrentSelectedEmailAsync(_contextWindow, ct);
            if (email == null)
            {
                var error = target != null
                    ? "L'email originale non è più disponibile (spostata o eliminata)."
                    : "Seleziona o apri un'email, poi riprova.";
                PostStream(turn, new { type = "stream_error", message = error, retryable = target == null, op });
                return;
            }
            op["entryId"] = email.EntryId;
            op["storeId"] = email.StoreId;
            PostEmailContext(turn, email);

            activeProfile = GetActiveProfile();
            if (activeProfile == null)
            {
                PostNotConfigured();
                return;
            }

            // A reminder is about my own sent message only: with the whole thread the model mixes up who is writing.
            bool followUp = op["type"] == "followup_draft" || (op.TryGetValue("followUp", out var followUpFlag) && followUpFlag == "1");
            bool isThread = !followUp && email.ThreadMessages != null && email.ThreadMessages.Count > 1;

            // Only the summary reads the attachments.
            bool sendsAttachments = op["type"] == "summary" && AttachmentContentAllowed(activeProfile);

            string cleanedBody;
            // A conversation cut to its most recent messages: the model and the reader are told.
            string conversationNotice = isThread ? ConversationNotice(email) : string.Empty;
            if (isThread)
            {
                PostPhase(turn, $"Lettura del thread ({email.ThreadMessages!.Count} messaggi)…");
                var sbThread = new StringBuilder();
                if (conversationNotice.Length > 0) sbThread.AppendLine("[" + conversationNotice.Trim() + "]").AppendLine();
                for (int i = 0; i < email.ThreadMessages.Count; i++)
                {
                    var msg = email.ThreadMessages[i];
                    var cleanedPart = _contextBuilder.CleanEmailBody(msg.Body, msg.Subject);
                    if (!string.IsNullOrWhiteSpace(cleanedPart))
                    {
                        sbThread.AppendLine($"[Messaggio #{i + 1} - {msg.SenderName} ({msg.ReceivedTime:dd/MM/yyyy HH:mm})]:");
                        sbThread.AppendLine(cleanedPart);
                        sbThread.AppendLine();
                    }
                }
                cleanedBody = sbThread.ToString().Trim();
            }
            else
            {
                cleanedBody = _contextBuilder.CleanEmailBody(email.Body, email.Subject);
            }

            string? attachmentNotice = null;
            if (op["type"] == "summary")
            {
                var attachments = await BuildAttachmentContextAsync(email, activeProfile, turn, ct);
                cleanedBody += attachments.Text;
                attachmentNotice = AttachmentNoticeFor(activeProfile, attachments);
            }

            // A reminder can be written from subject and sender alone (sent email with attachments only).
            if (string.IsNullOrWhiteSpace(cleanedBody) && !followUp)
            {
                PostStream(turn, new { type = "stream_error", message = "Il corpo dell'email è vuoto o protetto: non c'è testo da elaborare.", retryable = false });
                return;
            }

            // Thread parts already carry sender/date.
            cleanedBody = EmailHeaderOf(email, withSender: !isThread) + cleanedBody;

            // The whole text in one request, or nothing: an answer built on blocks is not reliable.
            var messages = promptFactory(_contextBuilder.EnsureFits(cleanedBody, ProviderCatalog.PromptBudget(activeProfile))).ToList();
            EnsurePromptFits(messages, activeProfile);
            client = CreateLlmClient(activeProfile);

            OnlyWorkspaceLog.Logger.Information("Starting {Op} with model {Model} on {BaseUrl}", logOp, activeProfile.Model, activeProfile.BaseUrl);
            PostPhase(turn, "Generazione in corso…");

            var response = new StringBuilder();
            var timings = new PhaseTimings();
            var answerMessages = (await AddWebEvidenceAsync(activeProfile, messages, turn, ct, timings: timings)).ToList();
            EnsurePromptFits(answerMessages, activeProfile);
            var generation = Stopwatch.StartNew();
            await foreach (var chunk in client.StreamChatAsync(answerMessages, ct))
            {
                response.Append(chunk);
                PostStream(turn, new { type = "stream_chunk", chunk });
            }
            timings.AddGeneration(generation.ElapsedMilliseconds);
            // A reply is a draft to insert: the note goes beside it, never into its text.
            if (conversationNotice.Length > 0 && op["type"] == "summary")
            {
                var note = "\n\n---\n" + conversationNotice.Trim();
                response.Append(note);
                PostStream(turn, new { type = "stream_chunk", chunk = note });
            }
            else if (conversationNotice.Length > 0) Toast(conversationNotice.Trim(), "info");

            sw.Stop();
            await RecordActionInHistoryAsync(activeProfile, $"{opName}: {email.Subject}", response.ToString(), ct, HistoryRefOf(email), opName);
            if (attachmentNotice != null) PostStream(turn, new { type = "stream_chunk", chunk = attachmentNotice });

            var tokenUsage = client.LastTokenUsage;
            if (tokenUsage != null)
            {
                OnlyWorkspaceLog.Logger.Information("Token usage for {Op}: Prompt={PromptTokens}, Output={OutputTokens}, Truncated={Truncated}",
                    logOp, tokenUsage.PromptTokens, tokenUsage.CompletionTokens, client.LastCompletion?.IsTruncated == true);
            }

            PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = client.LastCompletion?.IsTruncated == true, op });
            timings.LogCompleted(OnlyWorkspaceLog.Logger, logOp, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
            OnlyWorkspaceLog.Logger.Information("{Op} cancelled by user", logOp);
        }
        catch (Exception ex)
        {
            LogFailure(ex, logOp);
            PostStream(turn, new { type = "stream_error", message = LlmErrorText.Describe(ex, activeProfile), retryable = Retryable(ex), op });
        }
        finally
        {
            DisposeClient(client);
            EndOperation(cts);
        }
    }

    /// <summary>
    /// Lines under an answer on one email: the attachments not read and why, whatever the model says; then, with a
    /// local profile that read some, a caution (a small model can pair an amount with the wrong label on a dense form).
    /// </summary>
    private static string? AttachmentNoticeFor(LlmProfileConfig profile, EmailAttachments attachments)
    {
        var notice = AnalysisReport.EmailDocuments(attachments.Documents);
        if (!profile.IsExternal && attachments.Documents.Any(d => d.WasRead)) notice += "\n\n*Importi e date letti dagli allegati: verificali sul documento.*";
        return notice.Length == 0 ? null : notice;
    }

    /// <summary>Line under an answer built on part of a list: a fixed context takes whole items while they fit.</summary>
    private static string? LeftOutNotice(int included, int total, string what) =>
        included >= total ? null : $"\n\n*Risposta basata su {included} {what} su {total}: gli altri non entrano nel contesto del profilo in uso.*";

    /// <summary>Asking again cannot help when the text does not fit the profile.</summary>
    private static bool Retryable(Exception ex) => ex is not ContextTooLongException and not DataEgressBlockedException;

    /// <summary>A refusal by the exposure level or the notice is the configured policy, not a fault: no error and no stack.</summary>
    private static void LogFailure(Exception ex, object? op)
    {
        if (ex is DataEgressBlockedException) OnlyWorkspaceLog.Logger.Information("{Op} refused by the data egress policy: {Reason}", op, ex.Message);
        else OnlyWorkspaceLog.Logger.Error(ex, "Failed to execute {Op}", op);
    }


    /// <summary>
    /// Attachment text for the summary (helper process). With an external profile just the names are listed,
    /// unless the profile explicitly allows attachment content to leave the network.
    /// </summary>
    private Task<EmailAttachments> BuildAttachmentContextAsync(SelectedEmailInfo email, LlmProfileConfig profile, int turn, CancellationToken ct) =>
        BuildAttachmentContextAsync((accept, token) => _dataService.CopyAttachmentsAsync(email.EntryId, email.StoreId, accept, token), profile, turn, ct);

    /// <summary>The same reading for the files attached to the email being written (no stored copy exists: they are read from the open draft).</summary>
    private Task<EmailAttachments> DraftAttachmentsAsync(LlmProfileConfig profile, int turn, CancellationToken ct) =>
        BuildAttachmentContextAsync((accept, token) => _dataService.CopyDraftAttachmentsAsync(_contextWindow, accept, token), profile, turn, ct);

    private async Task<EmailAttachments> BuildAttachmentContextAsync(Func<Func<AttachmentFacts, bool>, CancellationToken, Task<AttachmentCopies?>> copyAttachments, LlmProfileConfig profile, int turn, CancellationToken ct)
    {
        bool local = profile.SendsAttachmentText;
        var copies = await copyAttachments(a => local && DocumentOutcome.CanRead(a) && !a.IsInline && !a.IsPecReceiptData && AttachmentTextExtractor.IsSupported(a.Extension), ct);
        if (copies == null) return EmailAttachments.None;

        try
        {
            var sb = new StringBuilder();
            var documents = new List<DocumentOutcome>();
            ct.ThrowIfCancellationRequested();
            if (copies.Saved.Count > 0) PostPhase(turn, $"Lettura allegati ({copies.Saved.Count})…");
            foreach (var attachment in copies.All.Where(a => !a.IsInline && !a.IsPecReceiptData))
            {
                var outcome = new DocumentOutcome { Name = attachment.Name, SourceId = (copies.All.ToList().IndexOf(attachment) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                documents.Add(outcome);
                var path = copies.Saved.Where(s => s.Key == attachment).Select(s => s.Value).FirstOrDefault();
                if (!local || path == null || !DocumentOutcome.CanRead(attachment))
                {
                    outcome.Status = DocumentOutcome.NotCopied(attachment, AttachmentTextExtractor.IsSupported(attachment.Extension), local);
                    continue;
                }
                // Every document whole; past the reader's safety limit it counts as not read, never as read in part.
                var extraction = await AttachmentTextExtractor.ExtractAsync(path, AttachmentTextExtractor.WholeDocumentChars, AnalysisExtractorTimeout, ct, OcrEnabled);
                outcome.ApplyExtraction(extraction);
                if (outcome.WasRead) sb.Append("\n\n[Allegato: ").Append(attachment.Name).Append("]\n").Append(extraction.Text);
                else if (extraction.Error != null) OnlyWorkspaceLog.Logger.Warning("Attachment extraction failed: {Error}", extraction.Error);
            }
            sb.Append(AnalysisReport.NotReadForModel(documents));
            foreach (var d in documents) d.PreparedForRequest = d.WasRead;
            TrackDocuments(documents);
            return new EmailAttachments(sb.ToString(), documents);
        }
        finally
        {
            DeleteScanDirectory(copies.Directory);
        }
    }

    /// <summary>Attachment text for the model and what happened to each attachment, for the lines the pane shows.</summary>
    private sealed class EmailAttachments
    {
        public static readonly EmailAttachments None = new(string.Empty, Array.Empty<DocumentOutcome>());

        public EmailAttachments(string text, IReadOnlyList<DocumentOutcome> documents)
        {
            Text = text;
            Documents = documents;
        }

        public string Text { get; }
        public IReadOnlyList<DocumentOutcome> Documents { get; }
    }

    private (string Key, EmailAttachments Attachments) _chatAttachments;

    /// <summary>Attachments of the email the chat is about, read once per email and profile: questions follow one another.</summary>
    private async Task<EmailAttachments> ChatAttachmentsAsync(SelectedEmailInfo email, LlmProfileConfig profile, int turn, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(email.EntryId)) return EmailAttachments.None;
        var key = email.StoreId + "|" + email.EntryId + "|" + ConversationPolicy.Key(profile) + "|" + OcrEnabled;
        if (_chatAttachments.Key == key)
        {
            foreach (var d in _chatAttachments.Attachments.Documents) { d.IncludedInRequest = false; d.PreparedForRequest = d.WasRead; }
            TrackDocuments(_chatAttachments.Attachments.Documents);
            return _chatAttachments.Attachments;
        }
        var attachments = await BuildAttachmentContextAsync(email, profile, turn, ct);
        foreach (var d in attachments.Documents) { d.EntryId = email.EntryId; d.StoreId = email.StoreId; d.EmailSubject = email.Subject; d.EmailDate = email.ReceivedTime; }
        _chatAttachments = (key, attachments);
        return attachments;
    }

    public Task ExecuteActionItemsAsync() => WithWebAction("action_items", () => ExecuteActionItemsAsync(null));

    /// <summary>Structured extraction of tasks/appointments; the pane offers each as an unsaved Outlook draft.</summary>
    private Task ExecuteActionItemsAsync(EmailRef? target) =>
        ExecuteStructuredEmailAsync("action_items", target, "Attività e appuntamenti", "Ricerca di attività e appuntamenti…",
            (body, email) => PromptCatalog.CreateActionItemsPrompt(body, ReferenceDateOf(email.ReceivedTime)), PromptCatalog.ActionItemsJsonSchema, RenderActionItems);

    private static (string Text, object? Extra) RenderActionItems(string json, SelectedEmailInfo email)
    {
        var items = ActionItemParser.Parse(json);
        var source = $"Da email «{email.Subject}» di {SenderOf(email)} del {email.ReceivedTime:dd/MM/yyyy}.";
        var sb = new StringBuilder();
        if (items.Diagnostics.IsPartial) sb.AppendLine(items.Diagnostics.Describe());
        if (items.Count == 0)
        {
            sb.Append("Nessuna attività o appuntamento individuato nell'email.");
        }
        else
        {
            sb.Append(items.Count == 1 ? "Trovata 1 voce" : $"Trovate {items.Count} voci").AppendLine(": apri la bozza, verifica i dati e salvala in Outlook.");
            foreach (var item in items)
            {
                sb.Append("- **").Append(item.IsAppointment ? "Appuntamento" : "Attività").Append("** — ").Append(item.Title).Append(" (").Append(item.WhenLabel).AppendLine(")");
            }
        }

        var payload = items.Select(i => new
        {
            kind = i.Kind,
            title = i.Title,
            when = i.WhenLabel,
            due = i.Due?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            start = i.Start?.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture),
            end = i.End?.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture),
            allDay = i.AllDay,
            location = i.Location,
            notes = string.IsNullOrWhiteSpace(i.Notes) ? source : i.Notes + "\n\n" + source,
            source = new { entryId = email.EntryId, storeId = email.StoreId, label = email.Subject }
        }).ToList();
        var export = ListExport(new[] { "Tipo", "Titolo", "Quando", "Luogo", "Note" },
            items.Select(i => new[] { i.Kind, i.Title, i.WhenLabel, i.Location, i.Notes }), items.Diagnostics);
        return (sb.ToString(), new { items = payload, export });
    }

    public Task ExecuteDataExtractionAsync() => WithWebAction("extract_data", () => ExecuteDataExtractionAsync(null));

    /// <summary>Order/invoice data as a table the pane can copy (TSV) or save as CSV.</summary>
    private Task ExecuteDataExtractionAsync(EmailRef? target) =>
        ExecuteStructuredEmailAsync("extract_data", target, "Estrazione dati", "Estrazione di importi, riferimenti e scadenze…",
            (body, _) => PromptCatalog.CreateStructuredDataPrompt(body), PromptCatalog.StructuredDataJsonSchema, RenderStructuredData);

    private static (string Text, object? Extra) RenderStructuredData(string json, SelectedEmailInfo email)
    {
        var data = StructuredDataParser.Parse(json);
        if (data.IsEmpty) return ((data.Diagnostics.IsPartial ? data.Diagnostics.Describe() + "\n" : "") + "Nessun dato commerciale strutturato valido individuato nell'email.", null);

        var text = $"Dati estratti ({data.DocumentType}): {data.Fields.Count} campi" + (data.Lines.Count > 0 ? $", {data.Lines.Count} righe" : "") +
                   ". Verifica i valori con l'email originale prima di usarli.";
        if (data.Diagnostics.IsPartial) text = data.Diagnostics.Describe() + "\n" + text;
        var separator = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator == ";" ? ';' : ',';
        var table = new
        {
            documentType = data.DocumentType,
            fields = data.Fields.Select(f => new[] { f.Key, f.Value }).ToList(),
            lineHeaders = ExtractedData.LineHeaders,
            lines = data.Lines,
            tsv = data.ToTsv(),
            csv = data.ToCsv(separator)
        };
        return (text, new { table });
    }

    /// <summary>
    /// Shared flow for JSON operations on one email: resolve, confirm external send, CompleteStructuredAsync,
    /// then <paramref name="render"/> returns the bubble text and extra stream_end fields (items/table).
    /// </summary>
    private async Task ExecuteStructuredEmailAsync(string opType, EmailRef? target, string actionName, string phase,
        Func<string, SelectedEmailInfo, IEnumerable<ChatMessage>> promptFactory, string jsonSchema,
        Func<string, SelectedEmailInfo, (string Text, object? Extra)> render)
    {
        var cts = BeginOperation();
        var ct = cts.Token;
        int turn = _turn;
        ILlmClient? client = null;
        LlmProfileConfig? activeProfile = null;
        var op = new Dictionary<string, string?> { ["type"] = opType };

        var sw = Stopwatch.StartNew();
        PostStream(turn, new { type = "stream_start", kind = "action", action = actionName, prompt = (string?)null });

        try
        {
            // The ribbon callback returns before the email is read: Outlook times the add-in's callbacks.
            await Task.Yield();
            var email = target != null
                ? await _dataService.GetEmailByEntryIdAsync(target.EntryId, target.StoreId, ct)
                : await _dataService.GetCurrentSelectedEmailAsync(_contextWindow, ct);
            if (email == null)
            {
                var error = target != null
                    ? "L'email originale non è più disponibile (spostata o eliminata)."
                    : "Seleziona o apri un'email, poi riprova.";
                PostStream(turn, new { type = "stream_error", message = error, retryable = target == null, op });
                return;
            }
            op["entryId"] = email.EntryId;
            op["storeId"] = email.StoreId;
            PostEmailContext(turn, email);

            activeProfile = GetActiveProfile();
            if (activeProfile == null)
            {
                PostNotConfigured();
                return;
            }
            string json;
            string attachmentNotice = string.Empty;
            var timings = new PhaseTimings();
            if (ExposurePolicy.SendsFactsOnly(activeProfile))
            {
                if (opType != "action_items") throw new DataEgressBlockedException(FactsOnlyMessage);
                var mail = new ChatMailItem
                {
                    EntryId = email.EntryId,
                    StoreId = email.StoreId,
                    Subject = email.Subject,
                    Received = email.ReceivedTime,
                    Facts = ArchiveFactExtractor.Extract(email.Body ?? "")
                };
                var pack = MailEvidencePack.Chronological(new[] { new MailEvidence { Mail = mail } });
                var safe = new CloudFactsRequest(pack);
                client = CreateLlmClient(activeProfile, wholeEmails: false);
                var factsGeneration = Stopwatch.StartNew();
                var protectedJson = await client.CompleteStructuredAsync(safe.Messages, CloudFactsRequest.Schema, ct);
                timings.AddGeneration(factsGeneration.ElapsedMilliseconds);
                ModelOutput.EnsureComplete(client.LastCompletion);
                var localItems = safe.ReadActionItems(protectedJson);
                json = JsonSerializer.Serialize(new
                {
                    items = localItems.Select(i => new
                    {
                        type = i.Kind,
                        title = i.Title,
                        due = i.Due?.ToString("yyyy-MM-dd"),
                        start = i.Start?.ToString(i.AllDay ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm"),
                        end = i.End?.ToString(i.AllDay ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm"),
                        date_text = i.DateText,
                        source = "M1"
                    })
                });
            }
            else
            {
                bool attachmentsAllowed = AttachmentContentAllowed(activeProfile);

                var content = _contextBuilder.CleanEmailBody(email.Body, email.Subject);
                string? pecOriginal = null;
                // PEC: the message to work on is the attached original (with its documents), not the envelope.
                if (attachmentsAllowed && PecMessage.IsEnvelope(email.Subject))
                {
                    PostPhase(turn, "Lettura del messaggio originale della PEC…");
                    pecOriginal = await ReadPecMessageAsync(email.EntryId, email.StoreId, ct);
                }
                // The invoice or the order is usually the attached document, not the email text.

                if (!string.IsNullOrEmpty(pecOriginal)) content = pecOriginal!;
                else
                {
                    var attachments = await BuildAttachmentContextAsync(email, activeProfile, turn, ct);
                    content += attachments.Text;
                    attachmentNotice = AnalysisReport.EmailDocuments(attachments.Documents);
                }
                var body = _contextBuilder.EnsureFits(EmailHeaderOf(email, withSender: true) + content, ProviderCatalog.PromptBudget(activeProfile));

                client = CreateLlmClient(activeProfile);
                PostPhase(turn, phase);
                var groundedPrompt = await AddWebEvidenceAsync(activeProfile, promptFactory(body, email), turn, ct, jsonSchema, timings);
                var generation = Stopwatch.StartNew();
                json = await client.CompleteStructuredAsync(groundedPrompt, jsonSchema, ct);
                timings.AddGeneration(generation.ElapsedMilliseconds);
                ModelOutput.EnsureComplete(client.LastCompletion);
            }
            var (text, extra) = render(json, email);
            PostStream(turn, new { type = "stream_chunk", chunk = text });
            await RecordActionInHistoryAsync(activeProfile, actionName + ": " + email.Subject, StructuredHistoryText(text, extra), ct, HistoryRefOf(email), actionName);
            if (attachmentNotice.Length > 0) PostStream(turn, new { type = "stream_chunk", chunk = attachmentNotice });

            sw.Stop();
            var end = new Dictionary<string, object?>
            {
                ["type"] = "stream_end",
                ["latencyMs"] = sw.ElapsedMilliseconds,
                ["truncated"] = false,
                ["op"] = op
            };
            if (extra != null)
            {
                foreach (var p in extra.GetType().GetProperties()) end[p.Name] = p.GetValue(extra);
            }
            PostStream(turn, end);
            timings.LogCompleted(OnlyWorkspaceLog.Logger, opType, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "{Op} failed", opType);
            PostStream(turn, new { type = "stream_error", message = LlmErrorText.Describe(ex, activeProfile), retryable = Retryable(ex), op });
        }
        finally
        {
            DisposeClient(client);
            EndOperation(cts);
        }
    }

    /// <summary>Keep every extracted row in history too, with a fence email data cannot terminate.</summary>
    private static string StructuredHistoryText(string text, object? extra)
    {
        if (extra == null) return text;
        var table = extra.GetType().GetProperty("table")?.GetValue(extra) ?? extra.GetType().GetProperty("export")?.GetValue(extra);
        var tsv = table?.GetType().GetProperty("tsv")?.GetValue(table) as string;
        if (string.IsNullOrEmpty(tsv)) return text;
        int longest = 0, run = 0;
        foreach (char c in tsv!)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string fence = new('`', Math.Max(3, longest + 1));
        return text + "\n\n" + fence + "text\n" + tsv + "\n" + fence;
    }

    /// <summary>CSV goes to the user's Downloads folder (no file dialog: owner-less dialogs can open behind Outlook).</summary>
    private void SaveCsv(JsonElement message)
    {
        var csv = GetString(message, "csv");
        if (string.IsNullOrEmpty(csv)) return;
        try
        {
            var folder = KnownFolders.Downloads();
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"OnlyWorkspace_dati_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); // BOM: Excel opens UTF-8 correctly
            Toast("CSV salvato in " + path, "success");
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to save CSV");
            Toast("Salvataggio CSV non riuscito. Verifica la cartella di destinazione e riprova.", "error");
        }
    }

    /// <summary>Item comes back from the pane: re-validated with the same parser rules before touching Outlook.</summary>
    private void OpenActionItemDraft(JsonElement message)
    {
        try
        {
            var item = _actionItemSaves.Resolve(message);
            _dataService.DisplayActionItemDraft(item);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to open action item draft");
            Toast("Bozza non aperta: fonte completa o risultato non disponibile. Riprova l'estrazione.", "error");
        }
    }

    /// <summary>The single request of a calendar scan: items checked against their email, which also gives their origin note.</summary>
    private async Task<ExtractedActionItems> ExtractCalendarItemsAsync(ILlmClient client, MailEvidencePack pack, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        bool includeDetails = pack.Whole > 0;
        var external = GetActiveProfile() is { } active && ExposurePolicy.SendsFactsOnly(active);
        var safe = external ? new CloudFactsRequest(pack, GetActiveProfile()!.SendsAttachmentText) : null;
        var json = await client.CompleteStructuredAsync(safe?.Messages ?? PromptCatalog.CreateCalendarScanPrompt(pack.Render(includeDetails)),
            external ? CloudFactsRequest.Schema : PromptCatalog.CalendarScanJsonSchema, ct);
        LogMailAnswer(client, pack, watch.ElapsedMilliseconds);
        ModelOutput.EnsureComplete(client.LastCompletion);
        var items = safe == null ? ActionItemParser.Parse(json) : safe.ReadActionItems(json);
        MailAnswerVerifier.Verify(items, pack, includeDetails);
        foreach (var item in items)
        {
            // The origin comes from the pack, never from the model's words.
            if (item.Source < 1 || item.Source > pack.Count) continue;
            var m = pack.Items[item.Source - 1].Mail;
            var source = $"Da email «{m.Subject}» di {m.SenderName} del {m.Received:dd/MM/yyyy}.";
            item.Notes = string.IsNullOrWhiteSpace(item.Notes) ? source : item.Notes + "\n\n" + source;
        }
        return items;
    }

    /// <summary>True for an item already in the calendar (up to a year ahead) or among the open tasks.</summary>
    private Func<ExtractedActionItem, bool> InOutlook(IReadOnlyList<ExtractedActionItem> items)
    {
        var lastStart = items.Where(i => i.IsAppointment && i.Start.HasValue).Select(i => i.Start!.Value).DefaultIfEmpty(DateTime.Today).Max();
        var horizon = lastStart > DateTime.Today.AddYears(1) ? DateTime.Today.AddYears(1) : lastStart;
        var calendar = items.Any(i => i.IsAppointment)
            ? _dataService.GetAppointments(new CalendarQuery { StartDate = DateTime.Today, EndDate = horizon.Date.AddDays(1), MaxResults = 200 })
            : new List<AppointmentInfo>();
        var openTasks = items.Any(i => !i.IsAppointment) ? _dataService.GetOpenTasks(500) : new List<ExtractedActionItem>();
        return i => ActionItemMerger.ExistsInCalendar(i, calendar) || ActionItemMerger.ExistsInTasks(i, openTasks);
    }

    /// <summary>Text of the message a PEC envelope carries as attached email (postacert.eml); empty when not readable.</summary>
    private async Task<string> ReadPecMessageAsync(string entryId, string? storeId, CancellationToken ct)
    {
        var copies = await _dataService.CopyAttachmentsAsync(entryId, storeId, a => a.Extension == "msg", ct);
        if (copies == null) return string.Empty;
        try
        {
            var path = copies.Saved.Select(s => s.Value).FirstOrDefault();
            if (path == null) return string.Empty;
            var message = await AttachmentTextExtractor.ExtractAsync(path, AttachmentTextExtractor.WholeDocumentChars, AnalysisExtractorTimeout, ct, OcrEnabled);
            return message.Truncated ? string.Empty : message.Text;
        }
        finally
        {
            DeleteScanDirectory(copies.Directory);
        }
    }

    private const int MaxBriefingEmails = 25;

    /// <summary>Today's agenda (deterministic) + LLM triage of unread emails; categories are applied only on click.</summary>
    public async Task ExecuteBriefingAsync()
    {
        _pendingWebAction = WebSearchAction.Briefing;
        var cts = BeginOperation();
        var ct = cts.Token;
        int turn = _turn;
        LlmProfileConfig? activeProfile = null;
        var op = new Dictionary<string, string?> { ["type"] = "briefing" };
        var sw = Stopwatch.StartNew();
        PostStream(turn, new { type = "stream_start", kind = "action", action = "Briefing di oggi", prompt = (string?)null });

        try
        {
            activeProfile = GetActiveProfile();
            if (activeProfile == null)
            {
                PostNotConfigured();
                return;
            }

            PostPhase(turn, "Lettura di agenda e posta non letta…");
            await Task.Yield();
            var appointments = _dataService.GetAppointments(new CalendarQuery { StartDate = DateTime.Today, EndDate = DateTime.Today.AddDays(1), MaxResults = 30 })
                .OrderBy(a => a.Start).ToList();
            var unread = _dataService.GetUnreadInbox(MaxBriefingEmails);
            var categories = _dataService.GetCategoryNames();

            var it = System.Globalization.CultureInfo.GetCultureInfo("it-IT");
            var agendaText = BriefingContext.FormatAgenda(appointments);

            var md = new StringBuilder();
            md.Append("**Agenda di ").Append(DateTime.Today.ToString("dddd d MMMM", it)).AppendLine("**").AppendLine().Append(agendaText);
            md.AppendLine().AppendLine("Copertura posta: fino alle ultime 25 email non lette della Posta in arrivo predefinita. Allegati letti solo se consentiti dal profilo.");

            if (unread.Count == 0)
            {
                md.AppendLine().AppendLine("Nessuna email non letta in Posta in arrivo.");
                PostStream(turn, new { type = "stream_chunk", chunk = md.ToString() });
                PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
                return;
            }


            var emails = new StringBuilder();
            var actionFacts = await ActionFactsAsync(unread.Select(e => new ArchiveEmailRef { EntryId = e.EntryId, StoreId = e.StoreId }).ToList(), AttachmentContentAllowed(activeProfile), ct);
            for (int i = 0; i < unread.Count; i++)
            {
                var e = unread[i];
                ct.ThrowIfCancellationRequested();
                if (!ExposurePolicy.Of(activeProfile).WholeEmails)
                {
                    TrackMailInput(e.EntryId, e.StoreId, e.InternetMessageId);
                    if (actionFacts.TryGetValue(e.StoreId + "\n" + e.EntryId, out var factsText))
                        emails.Append(BriefingContext.Email(i + 1, e, factsText, string.Empty, facts: true));
                    else md.AppendLine().Append("Email senza fatti locali utilizzabili: ").Append(SecurityContentAnalysis.MarkdownText(e.Subject));
                    continue;
                }
                var original = await _dataService.GetEmailByEntryIdAsync(e.EntryId, e.StoreId, ct)
                    ?? throw new InvalidOperationException("Un'email del Briefing non è più disponibile: riprova per aggiornare l'elenco.");
                // A source of the saved briefing: deleting its last copy removes the conversation.
                TrackMailInput(original.EntryId, string.IsNullOrEmpty(original.StoreId) ? e.StoreId : original.StoreId, original.MessageId);
                var attachments = await BuildAttachmentContextAsync(original, activeProfile, turn, ct);
                var notRead = AnalysisReport.NotReadList(attachments.Documents);
                if (notRead.Length > 0) md.AppendLine().Append("Allegati non letti (email ").Append(i + 1).Append("): ").AppendLine(SecurityContentAnalysis.MarkdownText(notRead));
                ct.ThrowIfCancellationRequested();
                emails.Append(BriefingContext.Email(i + 1, e, _contextBuilder.CleanEmailBody(original.Body, original.Subject), attachments.Text));
            }
            await CompleteBriefingAsync(activeProfile, agendaText, md, emails.ToString(), unread, categories, turn, sw, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Briefing failed");
            PostStream(turn, new { type = "stream_error", message = LlmErrorText.Describe(ex, activeProfile), retryable = Retryable(ex), op });
        }
        finally
        {
            EndOperation(cts);
        }
    }

    /// <summary>Completes the briefing from collected data; the agenda is displayed independently of model permissions.</summary>
    private async Task CompleteBriefingAsync(LlmProfileConfig activeProfile, string agendaText, StringBuilder md,
        string emailText, IReadOnlyList<EmailSearchResult> unread, IReadOnlyCollection<string> categories,
        int turn, Stopwatch sw, CancellationToken ct)
    {
        ILlmClient? client = null;
        var op = new Dictionary<string, string?> { ["type"] = "briefing" };
        try
        {
            int budget = ProviderCatalog.PromptBudget(activeProfile);
            int exposureBudget = ExposurePolicy.Of(activeProfile).PackTokens;
            if (exposureBudget > 0) _contextBuilder.EnsureFits(emailText, exposureBudget);
            var messages = PromptCatalog.CreateBriefingPrompt(BriefingContext.AgendaForModel(activeProfile, agendaText), _contextBuilder.EnsureFits(emailText, budget), categories, DateTime.Today).ToList();
            EnsurePromptFits(messages, activeProfile);

            // Collection already chose originals or local facts; the agenda above follows the same permission.
            client = CreateLlmClient(activeProfile, wholeEmails: false);
            PostPhase(turn, $"Analisi di {unread.Count} email non lette…");
            var timings = new PhaseTimings();
            var groundedPrompt = (await AddWebEvidenceAsync(activeProfile, messages, turn, ct, PromptCatalog.BriefingJsonSchema, timings)).ToList();
            EnsurePromptFits(groundedPrompt, activeProfile);
            var generation = Stopwatch.StartNew();
            var json = await client.CompleteStructuredAsync(groundedPrompt, PromptCatalog.BriefingJsonSchema, ct);
            timings.AddGeneration(generation.ElapsedMilliseconds);
            BriefingResult briefing;
            string? triageFailure = null;
            try
            {
                ModelOutput.EnsureComplete(client.LastCompletion);
                briefing = BriefingParser.Parse(json, unread.Count, categories);
            }
            catch (InvalidModelOutputException ex)
            {
                // The agenda stays valid: the unread emails are listed without priorities, saying why.
                OnlyWorkspaceLog.Logger.Warning(ex, "Briefing triage unusable");
                briefing = new BriefingResult();
                triageFailure = ex.Message + " Priorità e categorie non assegnate.";
            }

            if (briefing.Summary.Length > 0) md.AppendLine().AppendLine("**In sintesi**").AppendLine().AppendLine(briefing.Summary);
            md.AppendLine().Append("**Email non lette (").Append(unread.Count).AppendLine(")**").AppendLine();
            if (triageFailure != null) md.AppendLine(triageFailure).AppendLine();

            var triaged = briefing.Emails.OrderBy(t => t.PriorityRank).ThenBy(t => t.Index).ToList();
            var untriaged = Enumerable.Range(0, unread.Count).Where(i => triaged.All(t => t.Index != i));
            foreach (var t in triaged)
            {
                var e = unread[t.Index];
                md.Append("- **").Append(char.ToUpperInvariant(t.Priority[0]) + t.Priority.Substring(1)).Append("** — ").Append(e.Subject).Append(" (").Append(e.SenderName).Append(')');
                if (t.Reason.Length > 0) md.Append(": ").Append(t.Reason);
                md.AppendLine();
            }
            foreach (var i in untriaged)
            {
                md.Append("- ").Append(unread[i].Subject).Append(" (").Append(unread[i].SenderName).AppendLine(")");
            }
            PostStream(turn, new { type = "stream_chunk", chunk = md.ToString() });

            var items = triaged.Where(t => t.Category.Length > 0).Select(t => new
            {
                kind = "triage",
                title = unread[t.Index].Subject,
                when = $"Priorità {t.Priority} · {unread[t.Index].SenderName}",
                buttonLabel = $"Applica categoria «{t.Category}»",
                buttonTip = "Aggiunge la categoria all'email in Outlook",
                once = true,
                message = new { type = "apply_category", entryId = unread[t.Index].EntryId, storeId = unread[t.Index].StoreId, category = t.Category }
            }).ToList();

            sw.Stop();
            // Reuse only the validated model result at Minimal: the display also lists local-only mail metadata.
            string? modelContent = ExposurePolicy.Of(activeProfile).WholeEmails ? null : JsonSerializer.Serialize(new
            {
                summary = briefing.Summary,
                emails = triaged.Select(t => new { id = t.Index + 1, priority = t.Priority, category = t.Category, reason = t.Reason })
            });
            await RecordActionInHistoryAsync(activeProfile, "Briefing di oggi", md.ToString(), ct, modelContent: modelContent);
            PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = client.LastCompletion?.IsTruncated == true, op, items });
            timings.LogCompleted(OnlyWorkspaceLog.Logger, "briefing", sw.ElapsedMilliseconds);
        }
        finally
        {
            DisposeClient(client);
        }
    }

    private void ApplyCategory(JsonElement message)
    {
        var entryId = GetString(message, "entryId");
        var category = GetString(message, "category");
        if (string.IsNullOrEmpty(entryId) || string.IsNullOrWhiteSpace(category)) return;
        try
        {
            // Only categories that exist in the master list (the pane message is not trusted blindly).
            if (!_dataService.GetCategoryNames().Contains(category!, StringComparer.OrdinalIgnoreCase)) return;
            _dataService.ApplyCategory(entryId!, GetString(message, "storeId"), category!);
            Toast($"Categoria «{category}» applicata.", "success");
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to apply category");
            Toast("Impossibile applicare la categoria. Verifica che l'email sia disponibile e riprova.", "error");
        }
    }

    private const int FollowUpMaxLookbackDays = 365;

    private int FollowUpMinDays => Math.Max(1, Math.Min(30, _config?.Limits?.FollowUpDays ?? 5));

    /// <summary>
    /// Local scan of Sent Items (no LLM) of the accounts chosen in the form, with its recipient, subject and
    /// period filters: each unanswered email gets a "Bozza sollecito" button.
    /// </summary>
    private async Task ExecuteFollowUpScanAsync(AnalysisRequest request, Dictionary<string, string?> op)
    {
        var cts = BeginOperation();
        var ct = cts.Token;
        int turn = _turn;
        var sw = Stopwatch.StartNew();
        PostStream(turn, new { type = "stream_start", kind = "action", action = "Ricerca · " + request.Goal.Label, prompt = (string?)null });

        try
        {
            int days = FollowUpMinDays;
            int lookback = Math.Max(days + 1, request.Days > 0 ? Math.Min(request.Days, FollowUpMaxLookbackDays) : FollowUpMaxLookbackDays);
            var filter = new EmailSearchQuery { Recipient = request.Person.Length > 0 ? request.Person : null, Subject = request.Subject.Length > 0 ? request.Subject : null };
            // Sent Items only, whatever the form says about folders.
            string scope = new AnalysisRequest { Stores = request.Stores }.AccountsLabel("Posta inviata")
                + new AnalysisRequest { Person = request.Person, PersonRole = "recipient", Subject = request.Subject }.FiltersLabel() + $", ultimi {lookback} giorni";
            PostPhase(turn, "Analisi della posta inviata…");
            await Task.Yield(); // let the pane show the phase before the synchronous OOM scan

            var candidates = new List<FollowUpCandidate>();
            using (var scan = _dataService.BeginUnansweredSentScan(days, lookback, request.Stores.Select(s => s.Key).ToList(), filter))
            {
                while (!scan.Complete)
                {
                    ct.ThrowIfCancellationRequested();
                    candidates.AddRange(scan.ReadNext(20));
                    PostPhase(turn, $"Posta inviata: {scan.Scanned} messaggi esaminati, {candidates.Count} senza risposta…");
                    await Task.Delay(1, ct);
                }
                if (scan.FoldersFailed > 0)
                    PostStream(turn, new { type = "stream_chunk", chunk = $"{scan.FoldersFailed} cartelle Posta inviata non accessibili: la copertura è parziale.\n\n" });
            }
            candidates = candidates.OrderByDescending(c => c.SentOn).ToList();
            var now = DateTime.Now;
            var text = candidates.Count == 0
                ? $"Nessuna email inviata è senza risposta da almeno {days} giorni ({scope})."
                : $"{(candidates.Count == 1 ? "1 email inviata" : candidates.Count + " email inviate")} senza risposta da almeno {days} giorni ({scope}). Scegli per quali preparare un sollecito:";
            PostStream(turn, new { type = "stream_chunk", chunk = text });

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
            PostStream(turn, new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op, items, export });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostStream(turn, new { type = "stream_cancelled", message = "Operazione interrotta." });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Error(ex, "Follow-up scan failed");
            PostStream(turn, new { type = "stream_error", message = "Analisi della posta inviata non riuscita. Riprova; se il problema continua, consulta il log.", retryable = true, op });
        }
        finally
        {
            EndOperation(cts);
        }
    }

    /// <summary>A result list as clipboard text (TSV) and CSV, for the same Copia/Salva CSV buttons of the tables; null when empty.</summary>
    private static object? ListExport(string[] headers, IEnumerable<string[]> rows, ExtractionDiagnostics? diagnostics = null)
    {
        var list = rows.ToList();
        if (list.Count == 0 && diagnostics?.IsPartial != true) return null;
        var separator = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator == ";" ? ';' : ',';
        string warning = diagnostics?.IsPartial == true ? diagnostics.Describe() : string.Empty;
        return new
        {
            tsv = (warning.Length > 0 ? AnalysisTable.ToDelimited(new[] { "Copertura", warning }, Array.Empty<string[]>(), '\t', quote: false) : "") + AnalysisTable.ToDelimited(headers, list, '\t', quote: false),
            csv = (warning.Length > 0 ? AnalysisTable.ToDelimited(new[] { "Copertura", warning }, Array.Empty<string[]>(), separator, quote: true) : "") + AnalysisTable.ToDelimited(headers, list, separator, quote: true)
        };
    }

    public Task ExecuteSecurityCheckAsync() => WithWebAction("security", () => ExecuteSecurityCheckAsync(null));

    private Task WithWebAction(string messageType, Func<Task> run)
    {
        _pendingWebAction = WebSearchAction.Of(messageType);
        return run();
    }

    /// <summary>
    /// Local evidence first (Defender kept apart, static reading of the copies), then the model's quoted reading when
    /// the profile and its exposure level allow it. The pane receives the structured report: provisional while the
    /// check runs, final at the end, the same one saved in history.
    /// </summary>
    private async Task ExecuteSecurityCheckAsync(EmailRef? target)
    {
        var cts = BeginOperation();
        var ct = cts.Token;
        int turn = _turn;
        LlmProfileConfig? activeProfile = null;
        var op = new Dictionary<string, string?> { ["type"] = "security" };
        EmailSecurityData? data = null;
        EmailSecurityReport? report = null;
        string webText = string.Empty;
        var sw = Stopwatch.StartNew();
        PostStream(turn, new { type = "stream_start", kind = "action", action = "Verifica sicurezza", prompt = (string?)null });

        SecurityReportData PostReport(SecurityCheckState state, string model)
        {
            var view = SecurityReportView.From(report!, state, model);
            PostStream(turn, new { type = "security_report", report = view });
            return view;
        }

        // Interrompi, errors and refusals keep what was checked: the report says the check is partial.
        Task FinishAsync(SecurityCheckState state, string model, string? limit, object terminal) =>
            FinishSecurityCheckAsync(data, report, activeProfile, cts, turn, state, model, limit, webText, terminal);

        try
        {
            // The ribbon callback returns before the email is read: Outlook times the add-in's callbacks.
            await Task.Yield();
            data = _dataService.GetEmailSecurityData(_contextWindow, target?.EntryId, target?.StoreId);
            if (data == null)
            {
                var error = target != null ? "L'email originale non è più disponibile (spostata o eliminata)." : "Seleziona o apri un'email, poi riprova.";
                PostStream(turn, new { type = "stream_error", message = error, retryable = target == null, op });
                return;
            }
            op["entryId"] = data.Email.EntryId;
            op["storeId"] = data.Email.StoreId;
            PostEmailContext(turn, data.Email);
            activeProfile = GetActiveProfile();

            if (data.Input.Attachments.Count > 0 && await _dataService.CopyAttachmentsAsync(data.Email.EntryId, data.Email.StoreId, _ => true, ct) is { } files)
            {
                data.Input.Attachments = files.All;
                data = new EmailSecurityData(data.Email, data.Input, files.Directory, files.Saved);
            }

            if (data.SavedAttachments.Count > 0)
            {
                PostPhase(turn, "Scansione allegati con Microsoft Defender…");
                foreach (var saved in data.SavedAttachments)
                {
                    var scan = await DefenderScanner.ScanFileAsync(saved.Value, TimeSpan.FromSeconds(60), ct);
                    saved.Key.ScanStatus = scan.Status;
                    saved.Key.ScanDetail = scan.Detail;
                }
                // Static reading of the copies Defender did not block; images shown in the body are not documents.
                var readable = data.SavedAttachments.Where(s => s.Key.ScanStatus is AttachmentScanStatus.Clean or AttachmentScanStatus.Failed && !s.Key.IsInline).ToList();
                if (readable.Count > 0) PostPhase(turn, "Lettura statica di allegati, script e link…");
                foreach (var saved in readable)
                {
                    var inspection = await AttachmentTextExtractor.InspectAsync(saved.Value, AnalysisExtractorTimeout, ct);
                    saved.Key.Inspection = inspection;
                    saved.Key.HasMacros = inspection.HasMacros;
                    if (inspection.Error != null) saved.Key.InspectionDetail = "lettura statica non completata (" + inspection.Error + ").";
                }
            }

            report = EmailSecurityAnalyzer.Analyze(data.Input);
            OnlyWorkspaceLog.Logger.Information("Security check: observations {Observations}, limits {Limits}",
                string.Join(",", report.Observations.Select(o => o.Code).Distinct()), report.Limits.Count);
            if (activeProfile == null)
            {
                await FinishAsync(SecurityCheckState.Done, "Lettura IA del contenuto non eseguita: nessun profilo disponibile.", "Lettura IA del contenuto non eseguita.",
                    new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
            }
            else if (!ExposurePolicy.Of(activeProfile).WholeEmails)
            {
                // The local checks are the answer: the level sends no email text.
                await FinishAsync(SecurityCheckState.Done, "Lettura IA del contenuto non eseguita. " + WholeEmailsRefused(activeProfile),
                    "Lettura IA del contenuto non eseguita per il livello di esposizione del profilo.",
                    new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
            }
            else
            {
                PostReport(SecurityCheckState.Running, string.Empty);
                // Sender and link domains come from the mail and are not masked: no web query is built from them.
                if (WebForTurn(activeProfile))
                    OnlyWorkspaceLog.Logger.Information("Security web search skipped: queries built from mail domains are blocked");
                var review = await SecurityContentAnalysis.ReviewAsync(activeProfile,
                    token => BuildSecuritySourcesAsync(data, report, activeProfile, turn, token),
                    report.CloudContextText(),
                    () =>
                    {
                        PostPhase(turn, "Lettura del contenuto e riscontro delle citazioni…");
                        return CreateLlmClient(activeProfile);
                    }, ct);
                review.Assessment.AddTo(report);
                await FinishAsync(SecurityCheckState.Done, review.Status, review.Completed ? null : review.Status,
                    new { type = "stream_end", latencyMs = sw.ElapsedMilliseconds, truncated = false, op });
                OnlyWorkspaceLog.Logger.Information("Security content review: accepted {Accepted}, rejected {Rejected}",
                    review.Assessment.Findings.Count, review.Assessment.RejectedFindings);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishAsync(SecurityCheckState.Stopped, "Verifica interrotta: lettura IA non completata.", "Verifica interrotta prima della fine.",
                new { type = "stream_cancelled", message = "Verifica interrotta: conservati i controlli già eseguiti." });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning("Security check incomplete: {ErrorType}", ex.GetType().Name);
            await FinishAsync(SecurityCheckState.Done, "Lettura IA del contenuto non completata.", "Controlli locali o lettura IA non completati.",
                new { type = "stream_error", message = LlmErrorText.Describe(ex, activeProfile), retryable = Retryable(ex), op });
        }
        finally
        {
            EndOperation(cts);
            DeleteScanDirectory(data?.ScanDirectory);
        }
    }

    /// <summary>A failure while building or saving the partial report must never hide the turn's terminal message.</summary>
    private async Task FinishSecurityCheckAsync(EmailSecurityData? data, EmailSecurityReport? report, LlmProfileConfig? profile,
        CancellationTokenSource cts, int turn, SecurityCheckState state, string model, string? limit, string webText, object terminal)
    {
        try
        {
            if (data == null) return;
            report ??= EmailSecurityAnalyzer.Analyze(data.Input);
            if (limit != null) report.Limit(limit, partial: true);
            var view = SecurityReportView.From(report, state, model);
            PostStream(turn, new { type = "security_report", report = view });
            if (profile == null) return;
            await RecordActionInHistoryAsync(profile, "Verifica sicurezza: " + data.Email.Subject,
                SecurityReportView.ToMarkdown(view) + webText, _operations.ResultToken(cts), HistoryRefOf(data.Email), "Verifica sicurezza", view);
        }
        catch (OperationCanceledException)
        {
            // Superseded or a new chat: the result is not kept.
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to finalize security check");
            if (report != null)
            {
                report.Limit("Risultato non conservato nello Storico.", partial: false);
                PostStream(turn, new { type = "security_report", report = SecurityReportView.From(report, state, model) });
            }
            // Stop remains Stop; a successful check whose finalization failed must expose the failure.
            if (state != SecurityCheckState.Stopped)
                terminal = new
                {
                    type = "stream_error",
                    message = "Impossibile completare la verifica sicurezza. I controlli disponibili restano visibili.",
                    retryable = true,
                    op = new { type = "security", entryId = data?.Email.EntryId, storeId = data?.Email.StoreId }
                };
        }
        finally
        {
            PostStream(turn, terminal);
        }
    }

    private static string WholeEmailsRefused(LlmProfileConfig profile) =>
        ExposurePolicy.Of(profile).Level == ExposureLevel.LocalOnly
            ? "Il profilo cloud è al livello di esposizione «0 · Solo locale»: non invia nulla. Per questa azione usare un profilo locale."
            : FactsOnlyMessage;

    private async Task<IReadOnlyList<SecuritySource>> BuildSecuritySourcesAsync(EmailSecurityData data, EmailSecurityReport report,
        LlmProfileConfig profile, int turn, CancellationToken ct)
    {
        var sources = SecurityContentAnalysis.CreateSources(data.Email.Subject, data.Email.SenderName, data.Email.SenderEmail, data.Email.Body, report);
        for (int i = 0; i < data.Input.Attachments.Count; i++)
        {
            var facts = data.Input.Attachments[i];
            if (facts.IsInline || facts.IsPecReceiptData) continue;
            var saved = data.SavedAttachments.FirstOrDefault(s => ReferenceEquals(s.Key, facts));
            bool forbidden = !SecurityContentAnalysis.AttachmentContentAllowed(profile, facts) && facts.ScanStatus is not (AttachmentScanStatus.Threat or AttachmentScanStatus.Removed);
            string? excluded = !SecurityContentAnalysis.AttachmentContentAllowed(profile, facts)
                ? forbidden ? "invio del contenuto non consentito dal profilo" : "escluso dopo il controllo antivirus"
                : !AttachmentTextFormats.HasTextReader(facts.Extension) ? "formato senza estrazione del testo" : saved.Key == null ? "copia locale non disponibile" : null;
            if (excluded == null)
            {
                PostPhase(turn, "Lettura dell’allegato per la verifica sicurezza…");
                var extraction = await AttachmentTextExtractor.ExtractAsync(saved.Value, AttachmentTextExtractor.WholeDocumentChars, AnalysisExtractorTimeout, ct, OcrEnabled);
                if (extraction.Truncated || extraction.Error != null || extraction.Problem != null || string.IsNullOrWhiteSpace(extraction.Text))
                    excluded = extraction.Truncated ? "oltre il limite di lettura: nessun testo parziale usato" : "testo non leggibile integralmente";
                else sources.Add(SecuritySource.Attachment(i, facts.Name, extraction.Text));
            }
            // Not sending a text the profile forbids is policy, not a gap of the check.
            if (excluded != null) report.Limit("«" + facts.Name + "»: contenuto non incluso nella lettura IA (" + excluded + ").", partial: !forbidden);
        }
        return sources;
    }

    private static void DeleteScanDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return;
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to delete attachment scan folder");
        }
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>The administrator enabled Web Search for the action that started this turn (no question to the user).</summary>
    private bool WebForTurn(LlmProfileConfig profile) => WebSearchAction.Uses(profile, _webActionForTurn);

    private CancellationTokenSource BeginOperation()
    {
        _sourceReadUtc = DateTime.UtcNow;
        Inputs.Clear();
        _actionFactsForTurn.Clear();
        _documentsForTurn.Clear();
        _originals.Clear();
        _turn++;
        _webActionForTurn = _pendingWebAction;
        _pendingWebAction = null;
        _webForTurn.Begin(_turn);
        _turnText.Clear();
        _mailSourcesForTurn = new List<HistoryContextReference>();
        ArchiveIndexer.InteractiveBegin();
        return _operations.Begin();
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        ArchiveIndexer.InteractiveEnd();
        // A turn that ends without a closing message (superseded, Outlook closing) leaves nothing to replay.
        if (_operations.IsCurrent(cts) && _replay.Running)
        {
            _replay.ClearTurn();
            PaneJobs.Notify();
        }
        _operations.End(cts);
    }

    private void CancelActiveTask() => _operations.Cancel();

    /// <summary>Interrompi (ribbon, pane, Esc): the running turn keeps its partial result, Contatti stops too.</summary>
    public void StopActiveTask() => _operations.Stop();

    /// <summary>Which email the running turn is about: the pane labels its results with it.</summary>
    private void PostEmailContext(int turn, SelectedEmailInfo email)
    {
        TrackSelectedInputs(email);
        PostStream(turn, new
        {
            type = "stream_context",
            entryId = email.EntryId,
            storeId = email.StoreId,
            subject = string.IsNullOrWhiteSpace(email.Subject) ? "(Senza oggetto)" : email.Subject,
            meta = $"{SenderOf(email)} • {email.ReceivedTime:dd/MM/yyyy HH:mm}",
            threadPosition = email.ThreadPosition
        });
    }

    private void PostPhase(int turn, string message)
    {
        PostStream(turn, new { type = "stream_phase", message });
    }

    private void PostStream(int turn, object message)
    {
        if (_isDisposed || turn != _turn) return;
        Dictionary<string, object?> fields = message is IDictionary<string, object?> dictionary
            ? new Dictionary<string, object?>(dictionary)
            : message.GetType().GetProperties().ToDictionary(p => p.Name, p => (object?)p.GetValue(message));
        fields["turnId"] = $"{_jobId}:{turn}";
        if (fields.TryGetValue("items", out var actionItems) && actionItems != null)
            fields["items"] = _actionItemSaves.Register(actionItems);
        if (fields.TryGetValue("type", out var messageType) && messageType as string == "stream_chunk" && fields.TryGetValue("chunk", out var chunk))
            _turnText.Append(chunk as string);
        if (fields.TryGetValue("type", out var terminalType) && terminalType as string is "stream_end" or "stream_error" or "stream_cancelled")
            fields["documents"] = DocumentSnapshot();
        _replay.Record(fields);
        NotifyJobs(fields);
        var type = fields.TryGetValue("type", out var mappedType) ? mappedType as string : null;
        if (type == "stream_end" || type == "stream_error")
        {
            fields["webSources"] = WebCitations.Used(_turnText.ToString(), _webForTurn.Sources);
            if (type == "stream_end" && _webForTurn.Notice() is { } webNotice) fields["webNotice"] = webNotice;
            // A flow analysis brings its own [Mn] sources: the chat's empty list hid them (17/10/2026).
            if (!fields.TryGetValue("mailSources", out var own) || own == null) fields["mailSources"] = _mailSourcesForTurn;
            _control.PostMessage(fields);
            return;
        }
        _control.PostMessage(fields);
    }

    private static void EnsurePromptFits(IEnumerable<ChatMessage> messages, LlmProfileConfig profile) =>
        ModelRequestContext.Prepare(profile, messages);

    private async Task<WebSearchResult?> SearchForActionAsync(LlmProfileConfig profile, IEnumerable<ChatMessage> messages, int turn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureProfileCurrent(profile);
        EnsurePromptFits(messages, profile);
        if (!WebForTurn(profile) || turn != _turn) return null;
        // Only a masked search at Esteso exists: any other profile (local, Ollama) goes on from Outlook data
        // instead of aborting the action on a block it can never pass.
        if (!WebSearchService.AllowsMaskedSearch(profile))
        {
            OnlyWorkspaceLog.Logger.Information("Web search skipped: profile {Profile} has no masked search", profile.Id);
            return null;
        }
        // The action goes on from Outlook data, as after any failed search, and says why. SearchAsync blocks every
        // query except the masked ones of an Esteso cloud profile; the notice is checked first so the reason names the host.
        if (!DataEgress.AllowsWeb(_config, profile, _configService.ResolveSecret))
            throw new DataEgressBlockedException("ricerca web verso " + string.Join(", ", DataEgress.WebHosts(profile)) + " non autorizzata dall'informativa sull'uscita dei dati (Configurazione, Privacy e dati).");
        var secret = string.IsNullOrEmpty(profile.SecretRef) ? null : _configService.ResolveSecret(profile.SecretRef);
        var webSecret = string.IsNullOrEmpty(profile.WebSearchSecretRef) ? null : _configService.ResolveSecret(profile.WebSearchSecretRef);
        UiSync.Ensure();
        var ui = SynchronizationContext.Current!;
        using var service = new WebSearchService(profile, secret, webSecret,
            externalRequestStarting: () =>
            {
                if (_configService.HasConfigurationChanged())
                    throw new OperationCanceledException("La policy del profilo è cambiata.");
                EnsureProfileCurrent(profile);
                if (secret != _configService.ResolveSecret(profile.SecretRef) || webSecret != _configService.ResolveSecret(profile.WebSearchSecretRef) ||
                    !DataEgress.AllowsWeb(_config, profile, _configService.ResolveSecret))
                    throw new DataEgressBlockedException(DataEgress.BlockedMessageFor(profile));
                ui.Send(_ => PostStream(turn, new { type = "cloud_used" }), null);
            });
        PostPhase(turn, "Ricerca web in corso…");
        // Esteso: the request text leaves only through a pseudonymization map built for this search.
        OnlyWorkspace.Core.Privacy.Pseudonymizer? mask = WebSearchService.AllowsMaskedSearch(profile)
            ? new OnlyWorkspace.Core.Privacy.Pseudonymizer(await ReadPrivacyEntitiesOnUiAsync(ui, ct), true, true) : null;
        var result = await service.SearchAsync(messages, ct, _webActionForTurn, mask, _privacyAudit ?? new OnlyWorkspace.Infrastructure.Security.PseudonymizationAuditStore());
        // A search started in parallel can outlive its turn: the next turn keeps its own sources and notice.
        _webForTurn.Take(turn, result.Sources, result.InputCut);
        return result;
    }

    /// <summary>
    /// Web references shown apart from what was found in Outlook: a skipped or failed search leaves a note
    /// instead of stopping the action (on 18/10/2026 a 180 s timeout discarded a scan of 59 emails).
    /// </summary>
    private async Task<string> SeparateWebReferencesAsync(LlmProfileConfig profile, string query, string heading, int turn, CancellationToken ct)
    {
        try
        {
            var web = await SearchForActionAsync(profile, new[] { ChatMessage.User(query) }, turn, ct);
            return web == null ? string.Empty : $"\n\n**{heading}**\n" + web.DisplayText();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (WebSearchSkippedException)
        {
            OnlyWorkspaceLog.Logger.Information("Web search skipped by the model for profile {Profile}", profile.Id);
            _webForTurn.MarkSkipped(turn);
            return string.Empty;
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Web search failed for profile {Profile}: the action goes on without it", profile.Id);
            return $"\n\n**{heading}**\nRicerca web non riuscita: {LlmErrorText.Describe(ex, profile)}";
        }
    }

    /// <summary>
    /// A web search on the user's question alone, started as soon as the turn knows it needs one, while Outlook data is
    /// still being searched and read: the search used to start only after the reading (20-144 s, Danny 26/10/2026).
    /// Null when the action does not search the web. Failures surface where the result joins the request.
    /// </summary>
    private Task<WebSearchResult?>? StartWebSearch(LlmProfileConfig profile, string question, int turn, CancellationToken ct)
    {
        if (!WebForTurn(profile) || string.IsNullOrWhiteSpace(question)) return null;
        var search = SearchForActionAsync(profile, new[] { ChatMessage.User(question) }, turn, ct);
        // Observed even when the turn ends before it needs the result (no email found, stopped).
        _ = search.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return search;
    }

    private Task<IEnumerable<ChatMessage>> AddWebEvidenceAsync(LlmProfileConfig profile, IEnumerable<ChatMessage> messages, int turn, CancellationToken ct, string? jsonSchema = null, PhaseTimings? timings = null) =>
        MergeWebAsync(profile, messages, list => SearchForActionAsync(profile, list, turn, ct), ct, jsonSchema, timings);

    /// <summary>The result of <see cref="StartWebSearch"/> joins the request, with the same checks as a search made now.</summary>
    private Task<IEnumerable<ChatMessage>> AddWebEvidenceAsync(LlmProfileConfig profile, IEnumerable<ChatMessage> messages, Task<WebSearchResult?> started, CancellationToken ct, string? jsonSchema = null) =>
        MergeWebAsync(profile, messages, _ => started, ct, jsonSchema);

    private async Task<IEnumerable<ChatMessage>> MergeWebAsync(LlmProfileConfig profile, IEnumerable<ChatMessage> messages,
        Func<List<ChatMessage>, Task<WebSearchResult?>> search, CancellationToken ct, string? jsonSchema, PhaseTimings? timings = null)
    {
        var list = ModelRequestContext.Prepare(profile, messages, jsonSchema);
        WebSearchResult? web;
        int turn = _turn;
        var searchWatch = Stopwatch.StartNew();
        try
        {
            web = await search(list);
            if (web != null) timings?.AddWeb(searchWatch.ElapsedMilliseconds);
        }
        catch (WebSearchSkippedException)
        {
            timings?.AddWeb(searchWatch.ElapsedMilliseconds);
            // The model judged the web useless for this request (e.g. a contact lookup): answer from Outlook data only.
            OnlyWorkspaceLog.Logger.Information("Web search skipped by the model for profile {Profile}", profile.Id);
            _webForTurn.MarkSkipped(turn);
            return list;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not ContextTooLongException && ex is not DataEgressBlockedException && ex is not OperationCanceledException)
        {
            // The answer still rests on the Outlook data: a 180 s search timeout discarded 85 minutes of reading
            // 362 emails on 18/10/2026. The model is told, so the answer says the web is missing.
            timings?.AddWeb(searchWatch.ElapsedMilliseconds);
            OnlyWorkspaceLog.Logger.Warning(ex, "Web search failed for profile {Profile}: the answer goes on without it", profile.Id);
            list.Add(PromptCatalog.CreateWebFailedMessage(LlmErrorText.Describe(ex, profile)));
            return list;
        }
        if (web == null) return list;
        web.AddEvidenceTo(list);
        EnsureProfileCurrent(profile);
        ModelRequestContext.Prepare(profile, list, jsonSchema);
        return list;
    }

    private void PostNotConfigured()
    {
        _control.PostMessage(new
        {
            type = "config_error",
            message = "OnlyWorkspace non è ancora configurato: crea un profilo IA nella Configurazione."
        });
    }

    private void Toast(string message, string kind = "info", string? action = null)
    {
        _control.PostMessage(new { type = "toast", message, kind, action });
    }

    /// <summary>Rigenera on the latest chat bubble: drop that user/assistant pair so it is not sent twice as history.</summary>
    private void DropLastChatTurn(string prompt)
    {
        var messages = _currentConversation.Messages;
        int n = messages.Count;
        if (n >= 2 && messages[n - 1].Role == "assistant" && messages[n - 2].Role == "user" && messages[n - 2].Content == prompt)
        {
            messages.RemoveRange(n - 2, 2);
        }
    }

    /// <summary>Summaries and drafts join the conversation: saved in history and usable as context by the next chat turn.</summary>
    private async Task RecordActionInHistoryAsync(LlmProfileConfig profile, string label, string response, CancellationToken ct,
        HistoryContextReference? email = null, string? action = null, SecurityReportData? security = null, string? modelContent = null)
    {
        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;
        if (string.IsNullOrWhiteSpace(response)) return;

        EnsureProfileCurrent(profile);
        if (!ConversationPolicy.Matches(_currentConversation, profile))
        {
            if (_currentConversation.Messages.Count > 0) Toast(ConversationPolicy.ChangedMessage);
            _currentConversation = ConversationPolicy.Create(profile, Truncate(label, 40));
        }
        else if (_currentConversation.Messages.Count == 0)
        {
            _currentConversation.Title = Truncate(label, 40);
        }

        var now = DateTimeOffset.UtcNow;
        PreserveInputSources();
        _currentConversation.Messages.Add(new HistoryChatMessage { Role = "user", Content = label, Timestamp = now, Email = email, Action = action });
        _currentConversation.Messages.Add(new HistoryChatMessage { Role = "assistant", Content = response, ModelContent = modelContent, Timestamp = now, WebSources = WebCitations.Used(response, _webForTurn.Sources), MailSources = new List<HistoryContextReference>(_mailSourcesForTurn), Security = security, Documents = DocumentSnapshot() });

        if (_config?.History?.Enabled != true) return;
        try
        {
            await _historyStorage.SaveConversationAsync(_currentConversation);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to save action in history");
        }
    }

    private static string DisplayNameOf(LlmProfileConfig profile)
    {
        return string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName;
    }

    private static string Truncate(string text, int max)
    {
        return text.Length > max ? text.Substring(0, max - 1).TrimEnd() + "…" : text;
    }

    private static void DisposeClient(ILlmClient? client)
    {
        try
        {
            (client as IDisposable)?.Dispose();
        }
        catch { } // best-effort release of a client already replaced
    }

    private async Task SendHistoryListAsync(string? requestId = null, bool deleted = false)
    {
        try
        {
            var list = await _historyStorage.ListConversationsAsync();
            var items = list.Select(l => new
            {
                id = l.Id,
                title = l.Title,
                updatedAt = l.UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                messageCount = l.MessageCount,
                email = MailRefForPane(l.Email)
            }).ToList();

            _control.PostMessage(new { type = "history_list", items, enabled = _config?.History?.Enabled == true, requestId, deleted });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to list conversation history");
            _control.PostMessage(new { type = "history_list", items = Array.Empty<object>(), enabled = _config?.History?.Enabled == true, error = "Impossibile leggere lo storico.", requestId, deleted });
        }
    }

    /// <summary>The email of a history entry as the pane shows it: subject, then sender and date.</summary>
    private static object? MailRefForPane(HistoryContextReference? email) => email == null ? null : new
    {
        entryId = email.EntryId,
        storeId = email.StoreId,
        subject = email.Subject,
        itemType = email.ItemType,
        meta = string.Join(" · ", new[] { email.Sender, email.Date?.ToString("dd/MM/yyyy HH:mm") }.Where(v => !string.IsNullOrWhiteSpace(v)))
    };

    private async Task LoadHistoryAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            var conv = await _historyStorage.LoadConversationAsync(id!);
            if (conv != null)
            {
                CancelActiveTask();
                _currentConversation = conv;
                _analysisEvidence = null;
                var msgs = conv.Messages.Select(m => new
                {
                    role = m.Role,
                    content = m.Content,
                    webSources = m.WebSources,
                    mailSources = m.MailSources,
                    email = MailRefForPane(m.Email),
                    action = m.Action,
                    documents = m.Documents,
                    security = m.Security
                }).ToList();
                // Another profile continues in a new conversation (BeginConversationTurn): the pane says so.
                var active = GetActiveProfile();
                var made = _config?.Profiles.FirstOrDefault(p => p.Id == conv.ProfileId);

                _control.PostMessage(new
                {
                    type = "history_loaded",
                    title = conv.Title,
                    messages = msgs,
                    otherProfile = active != null && made != null && made.Id != active.Id ? DisplayNameOf(made) : null,
                    policyChanged = active != null && !ConversationPolicy.Matches(conv, active)
                });
            }
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to load conversation {Id}", id);
            Toast("Impossibile aprire la conversazione.", "error");
        }
    }

    private async Task DeleteHistoryAsync(string? id, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            if (!await _historyStorage.DeleteConversationAsync(id!))
                throw new InvalidOperationException("The requested conversation was not deleted.");
            if (_currentConversation.Id == id)
            {
                ResetConversation(preservePage: true);
            }
            await SendHistoryListAsync(requestId, deleted: true);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to delete conversation {Id}", id);
            _control.PostMessage(new { type = "history_delete_failed", requestId });
            Toast("Eliminazione non riuscita.", "error");
        }
    }

    private async Task DeleteAllHistoryAsync(string? requestId)
    {
        try
        {
            await _historyStorage.DeleteAllConversationsAsync();
            ResetConversation(preservePage: true);
            await SendHistoryListAsync(requestId, deleted: true);
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to delete all history");
            _control.PostMessage(new { type = "history_delete_failed", requestId });
            Toast("Eliminazione non riuscita.", "error");
        }
    }

    public bool CanClearChat => !_operations.HasWork && _currentConversation.Messages.Count > 0;

    public void ClearChat()
    {
        if (!_operations.HasWork) StartNewChat();
    }

    public void StartNewChat()
    {
        ResetConversation(preservePage: false);
    }

    private void ResetConversation(bool preservePage)
    {
        _turn++;
        CancelActiveTask();
        _replay.ClearTurn();
        _analysisEvidence = null;
        var profile = GetActiveProfile();
        _currentConversation = profile == null ? new ConversationHistory() : ConversationPolicy.Create(profile);
        _control.PostMessage(new { type = "new_chat_ready", preservePage });
    }

    private DateTime _sourceReadUtc = DateTime.UtcNow;
    private bool _checkingDeletedHistory;
    internal async Task CheckDeletedHistoryAsync()
    {
        if (_checkingDeletedHistory || _currentConversation.ContextReferences.Count == 0 && _currentConversation.Messages.Count == 0 || _historyStorage is not IHistorySourceInvalidation invalidation) return;
        _checkingDeletedHistory = true;
        var conversation = _currentConversation;
        try
        {
            if (!await invalidation.IsValidAsync(conversation) && ReferenceEquals(conversation, _currentConversation))
            {
                StartNewChat();
                _control.PostMessage(new { type = "history_changed" });
                Toast("Conversazione rimossa: usava un'email eliminata dall'archivio.", "info");
            }
        }
        catch (Exception ex) { OnlyWorkspaceLog.Logger.Warning("History invalidation check failed: {Kind}", ex.GetType().Name); }
        finally { _checkingDeletedHistory = false; }
    }

    private void InsertDraft(string text, bool replyAll, EmailRef? source, bool followUp = false)
    {
        // A reply or a reminder keeps Outlook's subject (RE: …): a subject line written by the model is dropped.
        text = ComposeDraft.SplitSubject(text).Body;
        try
        {
            if (followUp && source != null)
            {
                _dataService.OpenFollowUpDraft(source.EntryId, source.StoreId, text);
                Toast("Sollecito aperto in Outlook, indirizzato ai destinatari originali. Rivedilo prima di inviare.", "success");
                return;
            }
            var atCursor = _dataService.InsertReplyDraft(text, _contextWindow, replyAll, source?.EntryId, source?.StoreId);
            Toast(replyAll && !atCursor ? "Bozza «Rispondi a tutti» aperta in Outlook." : "Bozza inserita in Outlook. Rivedila prima di inviare.", "success");
            OnlyWorkspaceLog.Logger.Information("Draft inserted into Outlook (replyAll={ReplyAll}, atCursor={AtCursor})", replyAll, atCursor);
        }
        catch (Exception ex)
        {
            Toast("Inserimento bozza non riuscito. Verifica che Outlook sia disponibile e riprova.", "error");
            OnlyWorkspaceLog.Logger.Error(ex, "Failed to insert reply draft");
        }
    }

    private void SendConnectionInfo()
    {
        var activeProfile = GetActiveProfile();
        if (activeProfile == null)
        {
            _control.PostMessage(new
            {
                type = "connection_info",
                isValid = false,
                errorMessage = "Nessun profilo IA configurato o configurazione non valida.",
                outlookVersion = _dataService.GetOutlookVersion(),
                outlookBitness = _dataService.GetOutlookBitness(),
                productVersion = ProductInfo.Version
            });
            return;
        }

        _control.PostMessage(new
        {
            type = "connection_info",
            isValid = true,
            id = activeProfile.Id,
            displayName = DisplayNameOf(activeProfile),
            provider = activeProfile.Type,
            model = activeProfile.Model,
            endpoint = activeProfile.BaseUrl,
            isExternal = activeProfile.IsExternal,
            webSearchActions = WebActionLabels(activeProfile),
            outlookVersion = _dataService.GetOutlookVersion(),
            outlookBitness = _dataService.GetOutlookBitness(),
            productVersion = ProductInfo.Version
        });
    }

    private async Task CheckActiveConnectionAsync()
    {
        var activeProfile = GetActiveProfile();
        if (activeProfile == null)
        {
            _control.PostMessage(PaneMessageRules.ConnectionFailed(PaneMessageRules.NoProfileMessage));
            return;
        }

        var connectionCheck = _modelStatus.CaptureConnectionCheck();
        ILlmClient? client = null;
        try
        {
            client = CreateLlmClient(activeProfile, wholeEmails: false);
            var sw = Stopwatch.StartNew();
            var result = await client.CheckHealthAsync();
            sw.Stop();
            if (!connectionCheck.Report(result.IsHealthy, result.ErrorMessage)) return;

            _control.PostMessage(PaneMessageRules.ConnectionHealth(result.IsHealthy, result.Version, result.AvailableModels.Count, sw.ElapsedMilliseconds, result.ErrorMessage));
        }
        catch (Exception ex)
        {
            if (!connectionCheck.IsCurrent) return;
            if (ModelServiceStatus.IsServiceFailure(ex)) connectionCheck.Report(false, LlmErrorText.Describe(ex, activeProfile));
            _control.PostMessage(PaneMessageRules.ConnectionFailed(LlmErrorText.Describe(ex, activeProfile)));
        }
        finally
        {
            DisposeClient(client);
        }
    }

    public const string ConfigUnavailableText = "Strumento di configurazione non trovato. Contatta l'amministratore.";

    public void ShowConfigUnavailable() => Toast(ConfigUnavailableText, "error");

    /// <summary>toggle: a second click on a pane already on screen closes the screen instead of reopening it.</summary>
    public void ShowHistoryDrawer(bool toggle = false)
    {
        _control.PostMessage(new { type = "toggle_history", toggle });
    }

    public void HideEmailContext() => _control.PostMessage(new { type = "hide_email_context" });

    public void ShowInfoDrawer(bool toggle = false, string? section = null)
    {
        _control.PostMessage(new { type = "toggle_info", toggle, section });
    }

    /// <summary>Opens the "Cerca e analizza" form on one kind of search ("summary", "followups"...).</summary>
    public void ShowAnalysisDrawer(string preset = "summary", bool toggle = false)
    {
        _control.PostMessage(new { type = "toggle_analysis", preset, toggle });
    }

    private void ExportDiagnostics()
    {
        try
        {
            var zipPath = DiagnosticsExporter.CreateDiagnosticsZip(
                outlookVersion: _dataService.GetOutlookVersion(),
                outlookBitness: _dataService.GetOutlookBitness());
            _lastDiagnosticsPath = zipPath;
            Toast("Diagnostica salvata: " + Path.GetFileName(zipPath), "success", "open_diagnostics_folder");
            OnlyWorkspaceLog.Logger.Information("Diagnostics ZIP exported to {ZipPath}", zipPath);
        }
        catch (Exception ex)
        {
            Toast("Esportazione diagnostica non riuscita. Verifica la cartella di destinazione e riprova.", "error");
            OnlyWorkspaceLog.Logger.Error(ex, "Diagnostics export failed");
        }
    }

    private void OpenDiagnosticsFolder()
    {
        // Only the path produced by this coordinator is opened: the web page never supplies a path.
        var path = _lastDiagnosticsPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + path + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            OnlyWorkspaceLog.Logger.Warning(ex, "Failed to open diagnostics folder");
        }
    }

    private LlmProfileConfig? GetActiveProfile()
    {
        if (_config == null || _config.Profiles == null || _config.Profiles.Count == 0)
        {
            return null;
        }

        // The administrator's default profile (ConfigTool): the pane has no profile choice of its own.
        return _config.Profiles.FirstOrDefault(p => string.Equals(p.Id, _config.DefaultProfile, StringComparison.OrdinalIgnoreCase)) ?? _config.Profiles[0];
    }

    private void InvalidatePolicyOperations()
    {
        System.Threading.Interlocked.Increment(ref _policyEpoch);
        _turn++;
        CancelActiveTask();
        _replay.ClearTurn();
    }

    private void EnsureProfileCurrent(LlmProfileConfig profile)
    {
        var current = GetActiveProfile();
        if (current == null || ConversationPolicy.Key(current) != ConversationPolicy.Key(profile))
            throw new OperationCanceledException("La policy del profilo è cambiata.");
    }

    // Only Minimo sends typed facts alone; Standard and Esteso send masked text, so raising the level unlocks the action.
    public const string FactsOnlyMessage =
        "Questa azione richiede il testo dell'email, che il livello «1 · Minimo» non invia: escono solo fatti estratti in locale. " +
        "Alzare il livello a «2 · Standard» o usare un profilo locale.";

    /// <summary>The action sends whole emails, appointments or contacts: refused at a cards-only exposure level, before any request.</summary>
    private static void EnsureWholeEmails(LlmProfileConfig profile)
    {
        if (!ExposurePolicy.Of(profile).WholeEmails) throw new DataEgressBlockedException(WholeEmailsRefused(profile));
    }

    /// <param name="wholeEmails">The action sends whole Outlook items; false for archive evidence, the user's own text or a probe.</param>
    private ILlmClient CreateLlmClient(LlmProfileConfig profile, bool wholeEmails = true)
    {
        UiSync.Ensure();
        var dispatchUi = System.Threading.SynchronizationContext.Current!;
        if (profile.IsExternal && _configService.HasConfigurationChanged())
            throw new OperationCanceledException("La policy del profilo è cambiata.");
        // Nothing leaves the PC towards a server the administrator's notice does not cover. Checked first: with no notice
        // the level is not the reason, and naming it sent users to raise a level that would still be blocked.
        if (!DataEgress.AllowsModel(_config, profile, _configService.ResolveSecret)) throw new DataEgressBlockedException(DataEgress.BlockedMessageFor(profile));
        if (wholeEmails) EnsureWholeEmails(profile);
        string? secret = null;
        if (!string.IsNullOrEmpty(profile.SecretRef))
        {
            secret = _configService.ResolveSecret(profile.SecretRef);
        }
        // Merely creating a client sends nothing: local list/count/fact answers may never dispatch it.
        int turn = _turn;
        int epoch = System.Threading.Volatile.Read(ref _policyEpoch);
        string? webSecret = _configService.ResolveSecret(profile.WebSearchSecretRef);
        // Only the active profile paints the IA indicator.
        var active = GetActiveProfile();
        var status = active != null && ConversationPolicy.Key(active) == ConversationPolicy.Key(profile) ? _modelStatus : null;
        return new LlmRequestDispatcher(LlmClientFactory.CreateClient(profile, secret), profile, () =>
        {
            if (profile.IsExternal && _configService.HasConfigurationChanged())
                throw new OperationCanceledException("La policy del profilo è cambiata.");
            if (epoch != System.Threading.Volatile.Read(ref _policyEpoch) || secret != _configService.ResolveSecret(profile.SecretRef) ||
                webSecret != _configService.ResolveSecret(profile.WebSearchSecretRef))
                throw new OperationCanceledException("La policy del profilo è cambiata.");
            if (!DataEgress.AllowsModel(_config, profile, _configService.ResolveSecret)) throw new DataEgressBlockedException(DataEgress.BlockedMessageFor(profile));
        }, status, () => dispatchUi.Send(_ =>
        {
            if (turn == _turn) DocumentsDispatched();
            if (profile.IsExternal) PostStream(turn, new { type = "cloud_used" });
        }, null),
            entities: ct => ReadPrivacyEntitiesOnUiAsync(dispatchUi, ct), audit: _privacyAudit);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_replay.Running)
            OnlyWorkspaceLog.Logger.Information("Pane work cancelled with its Outlook window: {Action}", _replay.Action);
        CancelActiveTask();
        _control.ControlReady -= OnControlReady;
        _control.MessageReceived -= OnMessageReceived;
        PaneJobs.Changed -= PostOtherJobs;
        _modelStatus.Changed -= PostModelStatus;
        PaneJobs.Unregister(_jobId);
    }
}
