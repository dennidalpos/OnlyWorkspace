param([string]$Dir = '', [string]$EvidenceDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "../../scripts/build-paths.ps1")
Set-OnlyWorkspaceVerificationEnvironment -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path
if (!$Dir) { $Dir = Get-OnlyWorkspaceOutputDirectory -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path }
$buildOutput = (Get-OnlyWorkspaceBuildPaths -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path).TargetDir
$fromBuildGraph = [IO.Path]::GetFullPath($Dir).TrimEnd('\', '/') -ieq [IO.Path]::GetFullPath($buildOutput).TrimEnd('\', '/')
if (!$fromBuildGraph -and (!(Test-Path -LiteralPath (Join-Path $Dir 'extractor/OnlyWorkspace.AttachmentExtractor.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $Dir 'OnlyWorkspace.ConfigTool.exe')))) {
    throw 'An external candidate must include its own extractor and ConfigTool; components from another build are not substituted.'
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('OnlyWorkspace-PanePolicy-' + [Guid]::NewGuid().ToString('N'))
# The real reader needs the installed extractor layout. Stage build outputs, preserving the supplied candidate directory.
if (!(Test-Path -LiteralPath (Join-Path $Dir 'extractor/OnlyWorkspace.AttachmentExtractor.exe'))) {
    $candidateDir = $Dir
    $helperDir = Get-OnlyWorkspaceOutputDirectory -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path -Project OnlyWorkspace.AttachmentExtractor
    if (!(Test-Path -LiteralPath (Join-Path $helperDir 'OnlyWorkspace.AttachmentExtractor.exe'))) { throw 'Build the complete solution before PanePolicy: extractor output is missing.' }
    $Dir = Join-Path $scratch 'runtime'
    [void][IO.Directory]::CreateDirectory($Dir)
    Get-ChildItem -LiteralPath $candidateDir | Copy-Item -Destination $Dir -Recurse
    $stagedHelper = Join-Path $Dir 'extractor'
    [void][IO.Directory]::CreateDirectory($stagedHelper)
    Get-ChildItem -LiteralPath $helperDir | Copy-Item -Destination $stagedHelper -Recurse
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Net.Http
Add-Type -ReferencedAssemblies System.Net.Http, System.Core -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public sealed class CompletionSequenceHandler : HttpMessageHandler {
    public string[] Responses;
    public int Requests;
    public string LastRequest;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        LastRequest = await request.Content.ReadAsStringAsync();
        if (Requests >= Responses.Length) throw new InvalidOperationException("Unexpected continuation request");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Responses[Requests++]) };
    }
}
// One real HTTP request on loopback; never contacts a model or the user's Outlook data.
public sealed class BriefingCaptureServer : IDisposable {
    private readonly System.Net.Sockets.TcpListener listener;
    public readonly string Address;
    public bool Pending { get { return listener.Pending(); } }
    public BriefingCaptureServer() {
        listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    public async Task<string> ServeAsync(string json) {
        using (var socket = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
        using (var stream = socket.GetStream()) {
            var header = new System.Text.StringBuilder();
            var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) {
                if (header.Length > 65536 || await stream.ReadAsync(single, 0, 1).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Invalid fixture HTTP headers");
                header.Append((char)single[0]);
            }
            int length = 0;
            foreach (string line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.Substring(15).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            if (length < 1 || length > 262144) throw new InvalidOperationException("Invalid fixture HTTP length");
            var payload = new byte[length];
            for (int offset = 0; offset < length;) {
                int count = await stream.ReadAsync(payload, offset, length - offset).ConfigureAwait(false);
                if (count == 0) throw new InvalidOperationException("Incomplete fixture HTTP body");
                offset += count;
            }
            string body = System.Text.Encoding.UTF8.GetString(payload);
            if (body.Contains("[FATTO_")) {
                var references = new System.Collections.Generic.List<string>();
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(body, @"\[FATTO_\d+\]"))
                    if (!references.Contains(match.Value)) references.Add(match.Value);
                string facts = "{\"facts\":[\"" + string.Join("\",\"", references) + "\"]}";
                json = "{\"message\":{\"content\":\"" + facts.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"},\"done\":true,\"done_reason\":\"stop\"}";
            }
            var response = System.Text.Encoding.UTF8.GetBytes(json);
            var responseHeader = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + response.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeader, 0, responseHeader.Length).ConfigureAwait(false);
            await stream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(payload);
        }
    }
    public void Dispose() { listener.Stop(); }
}
// The coordinator's private header/coverage types stay private; only Outlook collection is substituted.
public static class CalendarFixtureResolver {
    public static Delegate Constant(Type delegateType, object value) {
        return System.Linq.Expressions.Expression.Lambda(delegateType,
            System.Linq.Expressions.Expression.Constant(value, delegateType.GetMethod("Invoke").ReturnType)).Compile();
    }
    public static Delegate Create(Type delegateType, object headers, Action onResolve = null) {
        var invoke = delegateType.GetMethod("Invoke");
        var resultType = invoke.ReturnType.GetGenericArguments()[0];
        var task = typeof(Task).GetMethod("FromResult").MakeGenericMethod(resultType).Invoke(null, new[] { headers });
        var parameters = Array.ConvertAll(invoke.GetParameters(), p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name));
        System.Linq.Expressions.Expression body = System.Linq.Expressions.Expression.Constant(task, invoke.ReturnType);
        if (onResolve != null) body = System.Linq.Expressions.Expression.Block(
            System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(onResolve)), body);
        return System.Linq.Expressions.Expression.Lambda(delegateType, body, parameters).Compile();
    }
}
'@
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Outlook.dll'))
$addin = New-Object OnlyWorkspace.Outlook.OnlyWorkspaceAddin
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Core.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Infrastructure.dll'))
$configToolAssembly = Join-Path $Dir 'OnlyWorkspace.ConfigTool.exe'
if (!(Test-Path -LiteralPath $configToolAssembly)) {
    $configToolAssembly = (Get-OnlyWorkspaceBuildPaths -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path -Project OnlyWorkspace.ConfigTool).TargetPath
}
[void][Reflection.Assembly]::LoadFrom($configToolAssembly)
# The pane logs through OnlyWorkspaceLog: capture it in memory so the user's log under %LOCALAPPDATA% stays untouched.
$serilog = [Reflection.Assembly]::LoadFrom((Join-Path $Dir 'Serilog.dll'))
Add-Type -ReferencedAssemblies $serilog.Location, netstandard -TypeDefinition @'
using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;
public sealed class CapturedLog : ILogEventSink {
    public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
    public readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();
    public void Emit(LogEvent e) {
        Lines.Enqueue(e.RenderMessage());
        if (e.Exception != null) Errors.Enqueue(e.Exception.ToString());
    }
}
'@
$capturedLog = New-Object CapturedLog
[Serilog.Log]::Logger = (New-Object Serilog.LoggerConfiguration).WriteTo.Sink($capturedLog).CreateLogger()
$logInitialized = [OnlyWorkspace.Infrastructure.Logging.OnlyWorkspaceLog].GetField('_initialized', [Reflection.BindingFlags]'NonPublic,Static')
$logInitialized.SetValue($null, $true)
$flags = [Reflection.BindingFlags]'NonPublic,Instance'
$type = [OnlyWorkspace.Outlook.UI.TaskPaneCoordinator]
$control = New-Object OnlyWorkspace.Outlook.UI.TaskPaneControl
function Assert-Pass([bool]$condition, [string]$label) { if (!$condition) { throw $label } }
function PaneMessage([hashtable]$fields) {
    $message = New-Object 'Collections.Generic.Dictionary[string,object]'
    foreach ($key in $fields.Keys) { $message.Add($key, $fields[$key]) }
    return ,$message
}
function Set-Field([string]$name, $value) {
    if ($null -ne $value -and $value -is [psobject]) { $value = $value.PSObject.BaseObject }
    $type.GetField($name, $flags).SetValue($script:coordinator, $value)
}
function Call([string]$name, [object[]]$arguments) {
    for ($i = 0; $i -lt $arguments.Count; $i++) {
        if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject }
    }
    # Overloads (AddWebEvidenceAsync with a turn or a search already started): the one the arguments fit.
    $method = $type.GetMethods(($flags -bor [Reflection.BindingFlags]::Public)) | Where-Object {
        $parameters = $_.GetParameters()
        $fits = $_.Name -eq $name -and $parameters.Count -eq $arguments.Count
        for ($i = 0; $fits -and $i -lt $arguments.Count; $i++) {
            if ($null -ne $arguments[$i] -and !$parameters[$i].ParameterType.IsInstanceOfType($arguments[$i])) { $fits = $false }
        }
        $fits
    } | Select-Object -First 1
    if ($null -eq $method) { throw "Pane method missing: $name" }
    # Office invokes pane actions on its UI STA. Install that context for the call itself:
    # the PowerShell host can restore its own context between pipeline invocations.
    $callerContext = [Threading.SynchronizationContext]::Current
    try {
        [Threading.SynchronizationContext]::SetSynchronizationContext((New-Object Windows.Forms.WindowsFormsSynchronizationContext))
        $result = $method.Invoke($script:coordinator, $arguments)
    } finally { [Threading.SynchronizationContext]::SetSynchronizationContext($callerContext) }
    if ($null -ne $result) { return ,$result }
}
function Call-Static([string]$name, [object[]]$arguments) {
    for ($i = 0; $i -lt $arguments.Count; $i++) {
        if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject }
    }
    return ,$type.GetMethod($name, [Reflection.BindingFlags]'NonPublic,Static').Invoke($null, $arguments)
}
function Wait-PaneTask($task) {
    $until = [DateTime]::UtcNow.AddSeconds(30)
    while (!$task.IsCompleted) {
        if ([DateTime]::UtcNow -gt $until) { throw 'Pane task timeout' }
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 10
    }
    return ,$task.GetAwaiter().GetResult()
}
try {
    [IO.Directory]::CreateDirectory($scratch) | Out-Null
    $config = New-Object OnlyWorkspace.Core.Models.WorkspaceConfig
    $config.DefaultProfile = 'same-id'
    $config.History.Enabled = $false
    $llmProfile = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Id='same-id'; Model='synthetic'; AllowAttachmentUpload=$true}
    $config.Profiles.Add($llmProfile)
    $service = New-Object OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService($scratch, $scratch)
    $service.SaveConfiguration($config)
    [void]$service.LoadConfiguration()
    $script:coordinator = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($type)
    $privacySource = $type.GetField('_privacyEntitySource', $flags)
    $privacyEntities = New-Object 'System.Collections.Generic.List[OnlyWorkspace.Core.Privacy.PrivacyEntity]'
    $script:privacySourceThread = -1
    $privacyOwnerThread = [Threading.Thread]::CurrentThread.ManagedThreadId
    $privacyRead = [Action] { $script:privacySourceThread = [Threading.Thread]::CurrentThread.ManagedThreadId }
    $privacySource.SetValue($script:coordinator, [CalendarFixtureResolver]::Create($privacySource.FieldType, $privacyEntities, $privacyRead))
    $type.GetField('_privacyAudit', $flags).SetValue($script:coordinator,
        [OnlyWorkspace.Infrastructure.Security.PseudonymizationAuditStore]::new((Join-Path $scratch 'privacy-audit'), $null))
    Set-Field '_control' $control
    Set-Field '_config' $config
    Set-Field '_configService' $service
    Set-Field '_contextBuilder' (New-Object OnlyWorkspace.Core.Context.ContextBuilder)
    Set-Field '_operations' ([Activator]::CreateInstance($type.Assembly.GetType('OnlyWorkspace.Outlook.UI.PaneOperations'), $true))
    Set-Field '_actionItemSaves' ([Activator]::CreateInstance($type.Assembly.GetType('OnlyWorkspace.Outlook.UI.ActionItemSaves'), $true))
    Set-Field '_replay' ([Activator]::CreateInstance($type.Assembly.GetType('OnlyWorkspace.Outlook.UI.PaneJobReplay'), $true))
    Set-Field '_mailSourcesForTurn' (New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Models.HistoryContextReference]')
    Set-Field '_inputSourcesForTurn' (New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Models.HistoryContextReference]')
    Set-Field '_documentsForTurn' (New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Agents.DocumentOutcome]')
    Set-Field '_actionFactsForTurn' (New-Object 'Collections.Generic.Dictionary[string,Collections.Generic.List[OnlyWorkspace.Core.Archive.ArchiveFact]]')
    Set-Field '_webForTurn' ([Activator]::CreateInstance($type.Assembly.GetType('OnlyWorkspace.Outlook.UI.TurnWebSources'), $true))
    $storage = New-Object OnlyWorkspace.Infrastructure.Security.DpapiHistoryStorageService((Join-Path $scratch 'history'))
    Set-Field '_historyStorage' $storage
    $pending = $control.GetType().GetField('_pendingMessages', $flags).GetValue($control)

    # Document identity is the physical mail and source ordinal, never the filename alone.
    $documentFixtures = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Agents.DocumentOutcome]'
    foreach ($ordinal in @('1','2')) {
        $documentFixtures.Add((New-Object OnlyWorkspace.Core.Agents.DocumentOutcome -Property @{Name='same.pdf';EntryId='doc-entry';StoreId='doc-store';SourceId=$ordinal;Status=[OnlyWorkspace.Core.Agents.DocumentStatus]::Reference}))
    }
    $null = Call 'TrackDocuments' @(,$documentFixtures)
    $readDocument = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Agents.DocumentOutcome]'
    $readDocument.Add((New-Object OnlyWorkspace.Core.Agents.DocumentOutcome -Property @{Name='same.pdf';EntryId='doc-entry';StoreId='doc-store';SourceId='1';Status=[OnlyWorkspace.Core.Agents.DocumentStatus]::Read}))
    $null = Call 'TrackDocuments' @(,$readDocument)
    $documentSnapshot = Call 'DocumentSnapshot' @()
    Assert-Pass ($documentSnapshot.Count -eq 2 -and @($documentSnapshot | Where-Object WasRead).Count -eq 1) 'Same-name documents lost their distinct identities or reading outcomes'
    'PASS document report: physical source identities, duplicate names and local reading evidence'

    # A192: force a real non-COM failure inside the writing handler. The UI gets recovery
    # instructions, while the original NullReferenceException remains in the captured log.
    Set-Field '_dataService' $null
    $writingMessage = [System.Text.Json.JsonDocument]::Parse('{"mode":"new_mail","text":"Testo sintetico"}')
    try { $null = Call 'ApplyWrittenText' @($writingMessage.RootElement) } finally { $writingMessage.Dispose() }
    $writingError = @($pending | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object type -eq 'toast')[-1]
    Assert-Pass ($writingError.kind -eq 'error' -and $writingError.message -eq "Inserimento non riuscito. Verifica che l'email sia ancora aperta in scrittura e riprova.") 'Writing failure leaked exception text or lost recovery instructions'
    Assert-Pass (@($capturedLog.Errors.ToArray() | Where-Object { $_ -match 'NullReferenceException' }).Count -gt 0) 'Writing failure lost its technical log'
    $pending.Clear()
    'PASS A192 real writing failure: Italian recovery message and original exception in log'

    # A181: production security finalization. Analyzer failure and history policy failure must not
    # prevent the terminal message; an existing local report stays visible even when saving fails.
    Set-Field '_turnText' (New-Object Text.StringBuilder)
    $securityEmail = New-Object OnlyWorkspace.Outlook.Services.SelectedEmailInfo -Property @{Subject='Synthetic security'; EntryId='security-email'; StoreId='security-store'}
    $securityFiles = New-Object 'Collections.Generic.List[Collections.Generic.KeyValuePair[OnlyWorkspace.Core.Security.AttachmentFacts,string]]'
    foreach ($ending in @('stream_end', 'stream_error', 'stream_cancelled')) {
        foreach ($failure in @('none', 'analyze', 'history')) {
            $cts = Call 'BeginOperation' @()
            try {
                $turn = $type.GetField('_turn', $flags).GetValue($script:coordinator)
                $null = Call 'PostStream' @($turn, (PaneMessage @{type='stream_start'; action='Verifica sicurezza'}))
                $analysisInput = New-Object OnlyWorkspace.Core.Security.EmailSecurityInput -Property @{Body='Synthetic body'}
                $report = [OnlyWorkspace.Core.Security.EmailSecurityAnalyzer]::Analyze($analysisInput)
                $securityProfile = $null
                if ($failure -eq 'analyze') { $analysisInput = $null; $report = $null }
                if ($failure -eq 'history') {
                    $securityProfile = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Id='same-id'; BaseUrl='https://security.example.invalid'; Model='synthetic'}
                    $config.Profiles.Clear(); $config.Profiles.Add($securityProfile)
                }
                $data = New-Object OnlyWorkspace.Outlook.Services.EmailSecurityData($securityEmail, $analysisInput, '', $securityFiles)
                $state = [OnlyWorkspace.Core.Security.SecurityCheckState]::Done
                if ($ending -eq 'stream_cancelled') {
                    $state = [OnlyWorkspace.Core.Security.SecurityCheckState]::Stopped
                    $null = Call 'StopActiveTask' @()
                }
                $before = @($pending).Count
                $finish = Call 'FinishSecurityCheckAsync' @($data, $report, $securityProfile, $cts, $turn, $state, 'Synthetic status', 'Synthetic limit', '', (PaneMessage @{type=$ending}))
                [void](Wait-PaneTask $finish)
                $messages = @($pending | Select-Object -Skip $before | ForEach-Object { $_ | ConvertFrom-Json })
                $terminals = @($messages | Where-Object { $_.type -in @('stream_end', 'stream_error', 'stream_cancelled') })
                $expected = $ending
                if ($failure -ne 'none' -and $ending -ne 'stream_cancelled') { $expected = 'stream_error' }
                Assert-Pass ($terminals.Count -eq 1 -and $terminals[0].type -eq $expected) "Security $ending/$failure did not terminate exactly once"
                if ($failure -ne 'analyze') {
                    $views = @($messages | Where-Object { $_.type -eq 'security_report' })
                    Assert-Pass ($views.Count -gt 0 -and $views[-1].report.limits -contains 'Synthetic limit') "Security $ending/$failure lost the existing report"
                    if ($failure -eq 'history') {
                        Assert-Pass ($views[-1].report.limits -contains 'Risultato non conservato nello Storico.') 'Security history failure was not declared'
                    }
                }
                $replay = $type.GetField('_replay', $flags).GetValue($script:coordinator)
                Assert-Pass (!$replay.Running) "Security $ending/$failure remained running in replay"
            } finally {
                $null = Call 'EndOperation' @($cts)
                $config.Profiles.Clear(); $config.Profiles.Add($llmProfile)
            }
        }
    }
    Assert-Pass (@($capturedLog.Lines.ToArray() | Where-Object { $_.Contains('Failed to finalize security check') }).Count -eq 6) 'Security finalization failures were not exercised'

    # A157: pane creation (inside the ribbon callback) loads configuration and IA indicator only; profiles, context
    # and history wait for the page. _dataService stays null: an Object Model read would log a context failure.
    Set-Field '_modelStatus' (New-Object OnlyWorkspace.Infrastructure.Llm.ModelServiceStatus)
    $pending = $control.GetType().GetField('_pendingMessages', $flags).GetValue($control)
    $postedBefore = @($pending).Count
    Set-Field '_config' $null
    $null = Call 'LoadInitialConfiguration' @()
    $loadedConfig = $type.GetField('_config', $flags).GetValue($script:coordinator)
    $status = $type.GetField('_modelStatus', $flags).GetValue($script:coordinator).Current
    Assert-Pass ($null -ne $loadedConfig -and $loadedConfig.DefaultProfile -eq 'same-id') 'Pane creation did not load the configuration'
    Assert-Pass ($status.Label -eq 'non verificato') "Pane creation left the IA indicator at '$($status.Label)'"
    Assert-Pass (@($pending).Count -eq $postedBefore) 'Pane creation posted messages before the page exists'
    Assert-Pass (!($capturedLog.Lines.ToArray() | Where-Object { $_.Contains('email context') })) 'Pane creation read the Outlook context'
    Set-Field '_configService' (New-Object OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService((Join-Path $scratch 'missing'), (Join-Path $scratch 'missing')))
    $null = Call 'LoadInitialConfiguration' @()
    $status = $type.GetField('_modelStatus', $flags).GetValue($script:coordinator).Current
    Assert-Pass ($null -eq $type.GetField('_config', $flags).GetValue($script:coordinator) -and $status.Label -eq 'non configurato') 'A missing configuration at pane creation was not reported'
    Assert-Pass (@($pending).Count -eq $postedBefore) 'A failed load at pane creation posted messages'
    Set-Field '_configService' $service
    Set-Field '_config' $config

    $old =[OnlyWorkspace.Core.Privacy.ConversationPolicy]::Create($llmProfile)
    $old.Messages.Add((New-Object OnlyWorkspace.Core.Models.HistoryChatMessage -Property @{Role='user';Content='Leggi allegato sintetico'}))
    $old.Messages.Add((New-Object OnlyWorkspace.Core.Models.HistoryChatMessage -Property @{Role='assistant';Content='SYNTHETIC-ATTACHMENT-FACT'}))
    [void]$storage.SaveConversationAsync($old).GetAwaiter().GetResult()
    Set-Field '_currentConversation' $old
    $llmProfile.AllowAttachmentUpload = $false
    $null = Call 'BeginConversationTurn' @($llmProfile.PSObject.BaseObject, 'Continua', $null)
    $messages = [OnlyWorkspace.Core.Prompts.PromptCatalog]::CreateChatPrompt('Continua')
    $sent = Call 'WithPriorTurns' @($messages, 'Continua', [int]0)
    Assert-Pass (!((($sent | ForEach-Object {$_.Content}) -join "`n").Contains('SYNTHETIC-ATTACHMENT-FACT'))) 'Prior attachment fact escaped into the new request'
    $next = $type.GetField('_currentConversation', $flags).GetValue($script:coordinator)
    Assert-Pass ($next.Id -ne $old.Id) 'Policy change reused the conversation identity'
    Assert-Pass ([OnlyWorkspace.Core.Privacy.ConversationPolicy]::Matches($next, $llmProfile)) 'New conversation lacks its policy'
    $reopened = $storage.LoadConversationAsync($old.Id).GetAwaiter().GetResult()
    Assert-Pass ($reopened.Messages[1].Content -eq 'SYNTHETIC-ATTACHMENT-FACT') 'Previous history was changed'

    # Same policy continues through the real BeginConversationTurn/WithPriorTurns path.
    $next.Messages.Add((New-Object OnlyWorkspace.Core.Models.HistoryChatMessage -Property @{Role='assistant';Content='SAFE-CURRENT-FACT'}))
    $null = Call 'BeginConversationTurn' @($llmProfile.PSObject.BaseObject, 'Ancora', $null)
    $sent = Call 'WithPriorTurns' @($messages, 'Ancora', [int]0)
    Assert-Pass ((($sent | ForEach-Object {$_.Content}) -join "`n").Contains('SAFE-CURRENT-FACT')) 'Unchanged policy lost its history'

    # Restored legacy history is displayed with an explicit policy warning, then starts separately.
    $old.PolicyKey = $null
    [void]$storage.SaveConversationAsync($old).GetAwaiter().GetResult()
    $load = Call 'LoadHistoryAsync' @($old.Id)
    [void](Wait-PaneTask $load)
    $pending = $control.GetType().GetField('_pendingMessages', $flags).GetValue($control)
    $loaded = @($pending | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object {$_.type -eq 'history_loaded'})[-1]
    Assert-Pass $loaded.policyChanged 'Legacy history was presented as safe to continue'
    $null = Call 'BeginConversationTurn' @($llmProfile.PSObject.BaseObject, 'Nuova', $null)
    Assert-Pass ($type.GetField('_currentConversation', $flags).GetValue($script:coordinator).Id -ne $old.Id) 'Legacy history reused'

    # Complete request over budget: body alone fits, but schema/roles must stop the real pane dispatch before HTTP.
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $oldUrl = $llmProfile.BaseUrl; $oldBudget = $llmProfile.ContextBudgetTokens
    $budgetClient = $null
    try {
        $llmProfile.BaseUrl = 'http://127.0.0.1:' + $listener.LocalEndpoint.Port
        $llmProfile.ContextBudgetTokens = 100
        $budgetClient = Call 'CreateLlmClient' @($llmProfile.PSObject.BaseObject, $true)
        $bounded = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Models.ChatMessage]'
        $bounded.Add([OnlyWorkspace.Core.Models.ChatMessage]::User(('x' * 350)))
        $overflow = $false
        try { [void]$budgetClient.CompleteStructuredAsync($bounded, '{"type":"object"}', [Threading.CancellationToken]::None).GetAwaiter().GetResult() }
        catch { $overflow = $_.Exception.ToString().Contains('ContextTooLongException') }
        Assert-Pass $overflow 'The real pane dispatch did not reject full-request overflow'
        Assert-Pass (!$listener.Pending()) 'HTTP was reached for an oversized request'
        $beforeWeb = $false
        try { $task = Call 'AddWebEvidenceAsync' @($llmProfile.PSObject.BaseObject, $bounded, [int]0, [Threading.CancellationToken]::None, '{"type":"object"}'); [void]$task.GetAwaiter().GetResult() }
        catch { $beforeWeb = $_.Exception.ToString().Contains('ContextTooLongException') }
        Assert-Pass $beforeWeb 'Pre-web check did not include the structured schema'
    }
    finally {
        if ($null -ne $budgetClient) { $budgetClient.Dispose() }
        $listener.Stop(); $llmProfile.BaseUrl = $oldUrl; $llmProfile.ContextBudgetTokens = $oldBudget
    }

    # Parallel web search (A150): started only for an action the profile searches the web for, and the data-egress
    # notice still stops it when it does not cover the web host (ollama.com for a local Ollama model).
    $type.GetField('_turn', $flags).SetValue($script:coordinator, [int]7)
    $llmProfile.WebSearchEnabled = $true
    try {
        Set-Field '_webActionForTurn' 'summary'
        $none = Call 'StartWebSearch' @($llmProfile.PSObject.BaseObject, 'Novita normative', [int]7, [Threading.CancellationToken]::None)
        Assert-Pass ($null -eq $none) 'Web search started for an action the profile does not search'
        Set-Field '_webActionForTurn' 'chat'
        $started = Call 'StartWebSearch' @($llmProfile.PSObject.BaseObject, 'Novita normative', [int]7, [Threading.CancellationToken]::None)
        Assert-Pass ($null -ne $started) 'Web search not started for a chat action'
        $refused = $false
        try { $task = Call 'AddWebEvidenceAsync' @($llmProfile.PSObject.BaseObject, $messages, $started, [Threading.CancellationToken]::None, $null); [void](Wait-PaneTask $task) }
        catch { $refused = $_.Exception.ToString().Contains('DataEgressBlockedException') }
        Assert-Pass $refused 'The parallel web search joined the request without the data-egress notice'
        $sources = $type.GetField('_webForTurn', $flags).GetValue($script:coordinator)
        Assert-Pass ($sources.Sources.Count -eq 0) 'A refused web search left sources in the turn'
    }
    finally { $llmProfile.WebSearchEnabled = $false; Set-Field '_webActionForTurn' $null }

    # A cloud profile at level 1 with no notice is refused for the notice, not for the level (A163): raising the level would not help.
    $cloud = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Id='cloud-l1';Model='synthetic';BaseUrl='https://remote.example.test/v1';External=$true;Exposure=[OnlyWorkspace.Core.Privacy.ExposureLevel]::Minimal}
    $reason = $null
    try { $null = Call 'CreateLlmClient' @($cloud, $true) } catch { $reason = $_.Exception.ToString() }
    Assert-Pass ($null -ne $reason -and $reason.Contains([OnlyWorkspace.Core.Privacy.DataEgress]::BlockedMessage)) 'A cloud profile without notice was refused for its level instead of the notice'

    # A189: the real pane factory must not report a cloud transfer while the client is idle.
    # A real dispatcher/client then reaches an in-memory HTTP handler, after the notification.
    $noticeConfig = New-Object OnlyWorkspace.Core.Models.WorkspaceConfig
    $noticeProfile = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Id='synthetic-cloud';Model='synthetic';External=$true}
    $noticeConfig.Profiles.Add($noticeProfile); $noticeConfig.DefaultProfile=$noticeProfile.Id
    $signer = New-Object OnlyWorkspace.Core.Privacy.DataEgressSigner -Property @{Company='Synthetic';FirstName='Test';LastName='OnlyWorkspace';Role='Test';Email='test@example.invalid';Date=[DateTime]::Today.ToString('yyyy-MM-dd');LegalBasis=[OnlyWorkspace.Core.Privacy.DataEgress]::LegalBases[2];Dpia='Synthetic';WorkersInformed=$true;AiLiteracy=$true}
    $providers = [OnlyWorkspace.Core.Privacy.DataEgress]::ProvidersFor($noticeConfig)
    foreach ($provider in $providers) {
        $provider.Provider='Synthetic'; $provider.ProcessingAgreement=$true; $provider.Region='UE';$provider.TransferBasis=[OnlyWorkspace.Core.Privacy.DataEgress]::TransferBases[0];$provider.NoTraining=$true;$provider.Retention='none'
    }
    [void][OnlyWorkspace.Core.Privacy.DataEgress]::Accept($noticeConfig,'test','test',[DateTime]::UtcNow,$null)
    $noticeClient=$null; $noticeHttp=$null
    try {
        Set-Field '_config' $noticeConfig
        $beforeCloud=@($pending).Count
        $noticeClient=Call 'CreateLlmClient' @($noticeProfile,$false)
        Assert-Pass (@($pending).Count -eq $beforeCloud) 'Idle cloud client incorrectly marked a transfer'
        $noticeHandler=New-Object CompletionSequenceHandler
        $noticeHandler.Responses=[string[]]@('{"message":{"content":"{}"},"done":true}')
        $noticeHttp=New-Object Net.Http.HttpClient($noticeHandler)
        $noticeHttp.BaseAddress=New-Object Uri(($noticeProfile.BaseUrl.TrimEnd('/')+'/'))
        $inner=$noticeClient.GetType().GetField('_client',$flags).GetValue($noticeClient)
        $inner.GetType().GetField('_httpClient',$flags).GetValue($inner).Dispose()
        $inner.GetType().GetField('_httpClient',$flags).SetValue($inner,$noticeHttp)
        $blocked = $false
        try { [void](Wait-PaneTask ($noticeClient.CompleteStructuredAsync($messages,'{}',[Threading.CancellationToken]::None))) }
        catch { $blocked = $_.Exception.ToString().Contains('DataEgressBlockedException') }
        Assert-Pass ($blocked -and $noticeHandler.Requests -eq 0) 'Unverified cloud text reached HTTP'
        $probe = [OnlyWorkspace.Core.Privacy.CloudRequestGuard]::Probe($true,$false)
        $task=$noticeClient.CompleteStructuredAsync($probe,[OnlyWorkspace.Core.Privacy.CloudRequestGuard]::ProbeSchema,[Threading.CancellationToken]::None)
        [void](Wait-PaneTask $task)
        $cloudMessages=@($pending | Select-Object -Skip $beforeCloud | ForEach-Object {$_ | ConvertFrom-Json} | Where-Object type -eq 'cloud_used')
        Assert-Pass ($noticeHandler.Requests -eq 1 -and $cloudMessages.Count -eq 1) 'Real dispatch did not mark the cloud request exactly once'
        Assert-Pass ($script:privacySourceThread -eq $privacyOwnerThread) 'A221 identity snapshot did not run on the captured UI thread'
    } finally {
        if ($noticeClient) {$noticeClient.Dispose()}; if ($noticeHttp) {$noticeHttp.Dispose()}
        Set-Field '_config' $config
    }

    # A213: production briefing completion, HTTP transport, DPAPI history, reopening and subsequent context.
    # Only collection from Outlook is replaced by plain synthetic data; the real rendering/dispatch/storage run.
    if ($EvidenceDirectory) { [IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null }
    $fixtureAppointments = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Models.AppointmentInfo]'
    $fixtureAppointments.Add((New-Object OnlyWorkspace.Core.Models.AppointmentInfo -Property @{
        Subject='CALENDARSENTINELPRIVATE'; Location='LOCATIONSENTINELPRIVATE'; Body='APPOINTMENTBODYPRIVATE'
        Start=[DateTime]::Today.AddHours(9); End=[DateTime]::Today.AddHours(10)
    }))
    $fixtureAgenda = [OnlyWorkspace.Core.Context.BriefingContext]::FormatAgenda($fixtureAppointments)
    $fixtureUnread = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Models.EmailSearchResult]'
    $fixtureUnread.Add((New-Object OnlyWorkspace.Core.Models.EmailSearchResult -Property @{Subject='Ordine sintetico';SenderName='Fixture';SenderEmail='fixture@example.invalid';ReceivedTime=[DateTime]::Today}))
    $fixtureCategories = New-Object 'Collections.Generic.List[string]'
    $fixtureEmails = [OnlyWorkspace.Core.Context.BriefingContext]::Email(1, $fixtureUnread[0], 'Scadenza pagamento: 15/11/2026', '', $true)
    $fixtureJson = '{"message":{"content":"{\"summary\":\"TRIAGESUMMARY\",\"emails\":[{\"id\":1,\"priority\":\"media\",\"category\":\"\",\"reason\":\"Scadenza locale\"}]}"},"done":true,"done_reason":"stop"}'
    foreach ($mode in @('Minimal','Standard','Extended','Local')) {
        $capture = New-Object BriefingCaptureServer
        $followClient = $null
        try {
            $briefingProfile = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{
                Id='briefing-fixture'; Model='synthetic'; BaseUrl=$capture.Address; External=($mode -ne 'Local')
                Exposure=[OnlyWorkspace.Core.Privacy.ExposureLevel]::Minimal
            }
            if ($mode -eq 'Standard') { $briefingProfile.Exposure=[OnlyWorkspace.Core.Privacy.ExposureLevel]::Standard }
            if ($mode -eq 'Extended') { $briefingProfile.Exposure=[OnlyWorkspace.Core.Privacy.ExposureLevel]::Extended; $briefingProfile.AllowAttachmentUpload=$true }
            $briefingConfig = New-Object OnlyWorkspace.Core.Models.WorkspaceConfig
            $briefingConfig.DefaultProfile=$briefingProfile.Id; $briefingConfig.Profiles.Add($briefingProfile)
            $providers = [OnlyWorkspace.Core.Privacy.DataEgress]::ProvidersFor($briefingConfig)
            foreach ($provider in $providers) {
                $provider.Provider='Synthetic'; $provider.ProcessingAgreement=$true; $provider.Region='UE'; $provider.TransferBasis=[OnlyWorkspace.Core.Privacy.DataEgress]::TransferBases[0]; $provider.NoTraining=$true; $provider.Retention='none'
            }
            if ($mode -ne 'Local') { [void][OnlyWorkspace.Core.Privacy.DataEgress]::Accept($briefingConfig,'test','test',[DateTime]::UtcNow,$null) }
            Set-Field '_config' $briefingConfig
            $briefingConversation = [OnlyWorkspace.Core.Privacy.ConversationPolicy]::Create($briefingProfile)
            Set-Field '_currentConversation' $briefingConversation
            $briefingUnread = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Models.EmailSearchResult]'
            $briefingUnread.Add($fixtureUnread[0])
            if ($mode -eq 'Minimal') {
                # This email has no local facts and therefore does not enter the prepared email context.
                $briefingUnread.Add((New-Object OnlyWorkspace.Core.Models.EmailSearchResult -Property @{Subject='LOCALUNREADPRIVATE';SenderName='LOCALSENDERPRIVATE'}))
            }
            $cts = Call 'BeginOperation' @()
            try {
                $turn = $type.GetField('_turn',$flags).GetValue($script:coordinator)
                $null = Call 'PostStream' @($turn,(PaneMessage @{type='stream_start';action='Briefing di oggi'}))
                $localDisplay = New-Object Text.StringBuilder
                [void]$localDisplay.Append("**Agenda**`n" + $fixtureAgenda + "`nLOCALNOTICEPRIVATE`n")
                $before = @($pending).Count
                if ($mode -ne 'Local') {
                    $refused = $false
                    try { [void](Wait-PaneTask (Call 'CompleteBriefingAsync' @($briefingProfile,$fixtureAgenda,$localDisplay,$fixtureEmails,$briefingUnread,$fixtureCategories,$turn,[Diagnostics.Stopwatch]::StartNew(),$cts.Token))) }
                    catch { $refused = $_.Exception.ToString().Contains('DataEgressBlockedException') }
                    $newMessages = @($pending | Select-Object -Skip $before | ForEach-Object { $_ | ConvertFrom-Json })
                    Assert-Pass (($refused -or !!($newMessages | Where-Object type -eq 'stream_error')) -and !$capture.Pending -and !($newMessages | Where-Object type -eq 'cloud_used')) "Briefing $mode leaked unverifiable text"
                    continue
                }
                $capturedRequest = $capture.ServeAsync($fixtureJson)
                $task = Call 'CompleteBriefingAsync' @($briefingProfile,$fixtureAgenda,$localDisplay,$fixtureEmails,$briefingUnread,$fixtureCategories,$turn,[Diagnostics.Stopwatch]::StartNew(),$cts.Token)
                [void](Wait-PaneTask $task)
                $payload = Wait-PaneTask $capturedRequest
                if ($EvidenceDirectory) { [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "briefing-$mode.json"),$payload) }
                $includesAgenda = $mode -ne 'Minimal'
                Assert-Pass ($payload.Contains('CALENDARSENTINELPRIVATE') -eq $includesAgenda) "Briefing $mode agenda payload violated policy"
                Assert-Pass ($payload.Contains('LOCATIONSENTINELPRIVATE') -eq $includesAgenda) "Briefing $mode location payload violated policy"
                Assert-Pass (!$payload.Contains('APPOINTMENTBODYPRIVATE') -and !$payload.Contains('LOCALNOTICEPRIVATE')) "Briefing $mode included unprepared local data"
                Assert-Pass (!$payload.Contains('LOCALUNREADPRIVATE') -and !$payload.Contains('LOCALSENDERPRIVATE')) "Briefing $mode included email metadata absent from permitted facts"
                Assert-Pass ($payload.Contains('15/11/2026')) "Briefing $mode lost its permitted local fact"
                $shown = @($pending | Select-Object -Skip $before | ForEach-Object {$_ | ConvertFrom-Json} | Where-Object type -eq 'stream_chunk')
                Assert-Pass ($shown.Count -eq 1 -and $shown[0].chunk.Contains('CALENDARSENTINELPRIVATE') -and $shown[0].chunk.Contains('TRIAGESUMMARY')) "Briefing $mode lost local agenda or triage on screen: $($shown | ConvertTo-Json -Depth 5 -Compress)"
                $saved = Wait-PaneTask ($storage.LoadConversationAsync($briefingConversation.Id))
                Assert-Pass ($saved.Messages[1].Content.Contains('CALENDARSENTINELPRIVATE')) "Briefing $mode history lost the local agenda"
                if ($mode -eq 'Minimal') {
                    Assert-Pass (!$saved.Messages[1].ModelContent.Contains('CALENDARSENTINELPRIVATE') -and !$saved.Messages[1].ModelContent.Contains('LOCALNOTICEPRIVATE')) 'Minimal history did not separate local-only display'
                    Assert-Pass ($saved.Messages[1].Content.Contains('LOCALUNREADPRIVATE') -and !$saved.Messages[1].ModelContent.Contains('LOCALUNREADPRIVATE')) 'Minimal history reused locally listed email metadata'
                } else { Assert-Pass ($null -eq $saved.Messages[1].ModelContent) "Briefing $mode changed reusable agenda context" }
            } finally { $null = Call 'EndOperation' @($cts) }

            [void](Wait-PaneTask (Call 'LoadHistoryAsync' @($briefingConversation.Id)))
            $loaded = @($pending | ForEach-Object {$_ | ConvertFrom-Json} | Where-Object type -eq 'history_loaded')[-1]
            Assert-Pass (!$loaded.policyChanged -and $loaded.messages[1].content.Contains('CALENDARSENTINELPRIVATE')) "Briefing $mode reopening lost local display or current policy: $($loaded | ConvertTo-Json -Depth 5 -Compress)"
            $null = Call 'BeginConversationTurn' @($briefingProfile,'Continua senza aggiungere dati',$null)
            $prompt = [OnlyWorkspace.Core.Prompts.PromptCatalog]::CreateChatPrompt('Continua senza aggiungere dati')
            $nextMessages = Call 'WithPriorTurns' @($prompt,'Continua senza aggiungere dati',[int]0)
            $followClient = Call 'CreateLlmClient' @($briefingProfile,$false)
            $followRequest = $capture.ServeAsync('{"message":{"content":"{}"},"done":true}')
            [void](Wait-PaneTask ($followClient.CompleteStructuredAsync($nextMessages,'{}',[Threading.CancellationToken]::None)))
            $followPayload = Wait-PaneTask $followRequest
            if ($EvidenceDirectory) { [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "briefing-followup-$mode.json"),$followPayload) }
            Assert-Pass ($followPayload.Contains('CALENDARSENTINELPRIVATE') -eq ($mode -ne 'Minimal')) "Briefing $mode agenda escaped policy through reopened history"
            Assert-Pass ($followPayload.Contains('LOCATIONSENTINELPRIVATE') -eq ($mode -ne 'Minimal')) "Briefing $mode location escaped policy through reopened history"
            Assert-Pass ($followPayload.Contains('TRIAGESUMMARY')) "Briefing $mode lost the permitted previous answer"
            if ($mode -eq 'Minimal') {
                Assert-Pass (!$followPayload.Contains('LOCALNOTICEPRIVATE')) 'Local-only notice escaped through reopened history'
                Assert-Pass (!$followPayload.Contains('LOCALUNREADPRIVATE') -and !$followPayload.Contains('LOCALSENDERPRIVATE')) 'Locally listed email metadata escaped through reopened history'
                # An explicit empty context is distinct from a legacy null: never fall back to display text.
                $activeConversation = $type.GetField('_currentConversation',$flags).GetValue($script:coordinator)
                $activeConversation.Messages[1].ModelContent=''
                [void](Wait-PaneTask ($storage.SaveConversationAsync($activeConversation)))
                [void](Wait-PaneTask (Call 'LoadHistoryAsync' @($activeConversation.Id)))
                $nextMessages = Call 'WithPriorTurns' @($prompt,'Continua senza aggiungere dati',[int]0)
                Assert-Pass (!(($nextMessages | ForEach-Object {$_.Content}) -join "`n").Contains('TRIAGESUMMARY')) 'Empty model context fell back to the complete display text'
            }
        } finally {
            if ($followClient) { $followClient.Dispose() }
            $capture.Dispose()
            Set-Field '_config' $config
        }
    }
    Write-Host 'OK: Briefing Minimal/Standard/local, real HTTP captures, local display, DPAPI history, reopening and subsequent context'

    # Reload cancels both pending work and a client created under the old epoch, before any HTTP request.
    $client = Call 'CreateLlmClient' @($llmProfile.PSObject.BaseObject, $true)
    $operations = $type.GetField('_operations', $flags).GetValue($script:coordinator)
    $operation = $operations.GetType().GetMethod('Begin').Invoke($operations, @())
    # Svuota chat must leave a running turn and its conversation intact, also from the ribbon/host boundary.
    $beforeConversation = $type.GetField('_currentConversation', $flags).GetValue($script:coordinator)
    $null = Call 'ClearChat' @()
    Assert-Pass (-not $operation.IsCancellationRequested) 'Clear chat cancelled a running turn'
    Assert-Pass ([object]::ReferenceEquals($beforeConversation, $type.GetField('_currentConversation', $flags).GetValue($script:coordinator))) 'Clear chat discarded the running conversation'

    $changed = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Id='same-id';Model='synthetic';BaseUrl='https://remote.example.test/v1'}
    $config.Profiles.Clear(); $config.Profiles.Add($changed)
    # Preserve the old in-memory config while writing the replacement to the isolated directory.
    $oldConfig = New-Object OnlyWorkspace.Core.Models.WorkspaceConfig
    $oldConfig.DefaultProfile='same-id'; $oldConfig.History.Enabled=$false; $oldConfig.Profiles.Add($llmProfile)
    Set-Field '_config' $oldConfig
    $service.SaveConfiguration($config)
    $null = Call 'ReloadConfiguration' @()
    Assert-Pass $operation.IsCancellationRequested 'Reload did not cancel pending work'
    $blocked = $false
    try { [void](Wait-PaneTask ($client.CompleteStructuredAsync($messages, '{}', [Threading.CancellationToken]::None))) }
    catch { $blocked = $_.Exception.ToString().Contains('policy del profilo') }
    finally { $client.Dispose(); $operation.Dispose() }
    Assert-Pass $blocked 'A client from the old policy was still dispatched'

    # Credential disappearance with an unchanged profile must revoke captured clients and the running turn.
    $config = $oldConfig
    $service.SaveConfiguration($config)
    $llmProfile.SecretRef = 'synthetic-key'
    $service.SaveConfiguration($config)
    $service.StoreSecret('synthetic-key', 'SYNTHETIC-OLD-KEY')
    $null = Call 'ReloadConfiguration' @()
    $client = Call 'CreateLlmClient' @($llmProfile, $true)
    $operation = $operations.GetType().GetMethod('Begin').Invoke($operations, @())
    $vaultBackup = [IO.File]::ReadAllBytes((Join-Path $scratch 'secrets.bin'))
    [IO.File]::Delete((Join-Path $scratch 'secrets.bin'))
    $null = Call 'ReloadConfiguration' @()
    Assert-Pass $operation.IsCancellationRequested 'Missing vault did not cancel the real pane turn'
    Assert-Pass ($null -eq $service.ResolveSecret('synthetic-key')) 'Missing vault retained the key'
    $blocked = $false
    try { [void](Wait-PaneTask ($client.CompleteStructuredAsync($messages, '{}', [Threading.CancellationToken]::None))) }
    catch { $blocked = $_.Exception.ToString().Contains('policy del profilo') }
    finally { $client.Dispose(); $operation.Dispose() }
    Assert-Pass $blocked 'Client captured before vault removal remained dispatchable'
    $null = Call 'ReloadConfiguration' @()
    Assert-Pass ($null -eq $service.ResolveSecret('synthetic-key')) 'Second reload revived the removed key'
    [IO.File]::WriteAllBytes((Join-Path $scratch 'secrets.bin'), $vaultBackup)
    $null = Call 'ReloadConfiguration' @()
    Assert-Pass ($service.ResolveSecret('synthetic-key') -eq 'SYNTHETIC-OLD-KEY') 'Valid restored vault did not reload'

    # Both partial-result labels use the real history and PostStream/EndOperation methods.
    $config.History.Enabled = $true
    Set-Field '_config' $config
    Set-Field '_turnText' (New-Object Text.StringBuilder)
    foreach ($label in @('Analisi (Riepilogo): fixture', 'Appuntamenti e scadenze: fixture')) {
        $conversation = [OnlyWorkspace.Core.Privacy.ConversationPolicy]::Create($llmProfile)
        Set-Field '_currentConversation' $conversation
        $cts = Call 'BeginOperation' @()
        $turn = $type.GetField('_turn', $flags).GetValue($script:coordinator)
        $start = New-Object 'Collections.Generic.Dictionary[string,object]'
        $start.Add('type', 'stream_start'); $start.Add('action', $label)
        $null = Call 'PostStream' @($turn, $start)
        foreach ($i in 1..3) {
            $null = Call 'StopActiveTask' @()
            Assert-Pass ($operations.GetType().GetMethod('IsCurrent').Invoke($operations, @($cts))) 'Repeated Stop lost current identity'
        }
        $resultToken = $operations.GetType().GetMethod('ResultToken').Invoke($operations, @($cts))
        $save = Call 'RecordActionInHistoryAsync' @($llmProfile, $label, 'SYNTHETIC-PARTIAL-ONCE', $resultToken, $null, $null, $null, $null)
        [void](Wait-PaneTask $save)
        $null = Call 'StopActiveTask' @()
        $finish = New-Object 'Collections.Generic.Dictionary[string,object]'
        $finish.Add('type', 'stream_end')
        $null = Call 'PostStream' @($turn, $finish)
        $null = Call 'EndOperation' @($cts)
        $saved = $storage.LoadConversationAsync($conversation.Id).GetAwaiter().GetResult()
        Assert-Pass ($saved.Messages.Count -eq 2 -and $saved.Messages[1].Content -eq 'SYNTHETIC-PARTIAL-ONCE') 'Partial result was not saved once'
        $replay = $type.GetField('_replay', $flags).GetValue($script:coordinator)
        Assert-Pass (!$replay.Running -and $null -eq $replay.Restore()) 'Completed partial turn remained in replay'
    }

    # E7/A71: production finalizer with real Stop/history/PostStream/EndOperation: the list on screen stays the result.
    $finalizer = $type.Assembly.GetType('OnlyWorkspace.Outlook.UI.AnalysisFinalization').GetMethod('RunAsync', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($stops in @(1, 3)) {
        $conversation = [OnlyWorkspace.Core.Privacy.ConversationPolicy]::Create($llmProfile)
        Set-Field '_currentConversation' $conversation
        $cts = Call 'BeginOperation' @()
        $turn = $type.GetField('_turn', $flags).GetValue($script:coordinator)
        $null = Call 'PostStream' @($turn, (PaneMessage @{type='stream_start';action=('Analisi finale '+$stops)}))
        $result = New-Object Text.StringBuilder('SYNTHETIC-LIST')
        $null = Call 'PostStream' @($turn, (PaneMessage @{type='stream_chunk';chunk='SYNTHETIC-LIST'}))
        $answer = [Func[Threading.CancellationToken,Threading.Tasks.Task]] {
            param($token)
            foreach ($i in 1..$stops) { $null = Call 'StopActiveTask' @() }
            return [Threading.Tasks.Task]::FromCanceled($token)
        }
        $append = [Action[string]] { param($chunk); $null = Call 'PostStream' @($turn, (PaneMessage @{type='stream_chunk';chunk=$chunk})) }
        $parameters = @($operations, $cts, $result, $answer, $append)
        for ($i = 0; $i -lt $parameters.Count; $i++) { $parameters[$i] = $parameters[$i].PSObject.BaseObject }
        $task = $finalizer.Invoke($null, $parameters)
        Assert-Pass ($task.GetAwaiter().GetResult()) 'Final Stop did not retain the result'
        Assert-Pass ($result.ToString().StartsWith('SYNTHETIC-LIST') -and $result.ToString().Contains('Analisi interrotta prima della risposta finale')) 'Final Stop lost the list'
        $resultToken = $operations.GetType().GetMethod('ResultToken').Invoke($operations, @($cts))
        $save = Call 'RecordActionInHistoryAsync' @($llmProfile, ('Analisi finale '+$stops), $result.ToString(), $resultToken, $null, $null, $null, $null)
        [void](Wait-PaneTask $save)
        $null = Call 'PostStream' @($turn, (PaneMessage @{type='stream_end'}))
        $null = Call 'EndOperation' @($cts)
        $saved = $storage.LoadConversationAsync($conversation.Id).GetAwaiter().GetResult()
        Assert-Pass ($saved.Messages.Count -eq 2 -and $saved.Messages[1].Content -eq $result.ToString()) 'Final partial was not saved once'
        Assert-Pass (!$replay.Running -and $null -eq $replay.Restore()) 'Final partial remained in replay'
    }

    # E7: the actual net48 answer is one structured request; an invented amount is marked, a cut answer is not continued.
    $pack = New-Object OnlyWorkspace.Core.Agents.MailEvidencePack
    $mail = New-Object OnlyWorkspace.Core.Archive.ChatMailItem -Property @{Subject='Sollecito'; SenderName='Fornitore'; Received=[DateTime]'2026-10-20T09:00:00'; EntryId='e1'; StoreId='s'}
    $pack.Items.Add((New-Object OnlyWorkspace.Core.Agents.MailEvidence -Property @{Mail=$mail; Text='Fattura FT-2026/045 di 1.250,00 EUR'; Whole=$true}))
    $content = '{"sections":[{"title":"","points":[{"text":"Fattura FT-2026/045 da 1.250,00 EUR con penale di 9.999,00 EUR","sources":["M1"]}]}],"gaps":[]}'
    foreach ($finish in @('stop', 'length')) {
        $handler = New-Object CompletionSequenceHandler
        $handler.Responses = @((@{choices=@(@{message=@{content=$content};finish_reason=$finish})} | ConvertTo-Json -Depth 10 -Compress))
        $http = New-Object Net.Http.HttpClient($handler)
        $http.BaseAddress = 'https://fixture.example.test/'
        $answerClient = New-Object OnlyWorkspace.Infrastructure.Llm.OpenAIClient((New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Type='localai';BaseUrl='http://127.0.0.1:12345/v1';Model='synthetic'}), $null, $http)
        try {
            $prompt = [OnlyWorkspace.Core.Prompts.PromptCatalog]::CreateMailAnswerPrompt('Stato della fattura', $pack.Render())
            $task = Call 'AnswerOnEvidenceAsync' @($answerClient, $prompt, $pack, [int]0, [Threading.CancellationToken]::None, $null)
            if ($finish -eq 'stop') {
                $answered = Wait-PaneTask $task
                Assert-Pass ($answered.Item1.Contains('[M1]') -and $answered.Item1.Contains('non verificato nelle email citate: 9.999,00*')) "Invented amount was not marked: $($answered.Item1)"
                Assert-Pass ($answered.Item1.Contains('1 punto non verificato')) 'Coverage did not count the unverified point'
            } else {
                $cut = $false
                try { [void](Wait-PaneTask $task) } catch { $cut = $_.Exception.ToString().Contains('InvalidModelOutputException') }
                Assert-Pass $cut 'A cut structured answer was accepted'
            }
            Assert-Pass ($handler.Requests -eq 1) "The final answer used $($handler.Requests) requests"
        } finally { $answerClient.Dispose(); $http.Dispose() }
    }

    # A114: only the latest analysis keeps its emails; Nuova chat and another conversation drop them.
    $evidenceField = $type.GetField('_analysisEvidence', $flags)
    $kept = [ValueTuple[string,OnlyWorkspace.Core.Agents.MailEvidencePack]]::new('a114', $pack)
    $evidenceField.SetValue($script:coordinator, $kept)
    $null = Call 'StartNewChat' @()
    Assert-Pass ($null -eq $evidenceField.GetValue($script:coordinator)) 'Nuova chat kept the analysis emails'
    $question = Call 'ExecuteAnalysisQuestionAsync' @('Quanto resta?', 'a114')
    [void](Wait-PaneTask $question)
    Assert-Pass ($null -eq $evidenceField.GetValue($script:coordinator)) 'A dropped analysis came back'
    $evidenceField.SetValue($script:coordinator, $kept)
    [void](Wait-PaneTask (Call 'LoadHistoryAsync' @($conversation.Id)))
    Assert-Pass ($null -eq $evidenceField.GetValue($script:coordinator)) 'Opening another conversation kept the analysis emails'

    # A194: persistence outcomes from the production coordinator, including a missing/locked file.
    $historyFixtureStorage = New-Object OnlyWorkspace.Infrastructure.Security.DpapiHistoryStorageService((Join-Path $scratch 'a194-history'))
    Set-Field '_historyStorage' $historyFixtureStorage
    $historyBefore = $type.GetField('_currentConversation', $flags).GetValue($script:coordinator)
    $syntheticHistory = New-Object OnlyWorkspace.Core.Models.ConversationHistory -Property @{Id='a194-current';Title='Synthetic history'}
    [void](Wait-PaneTask ($historyFixtureStorage.SaveConversationAsync($syntheticHistory)))
    Set-Field '_currentConversation' $syntheticHistory
    $beforeDelete = $pending.Count
    [void](Wait-PaneTask (Call 'DeleteHistoryAsync' @($syntheticHistory.Id, 'a194-single')))
    $deletedMessages = @($pending | Select-Object -Skip $beforeDelete | ForEach-Object {$_ | ConvertFrom-Json})
    Assert-Pass ($null -eq (Wait-PaneTask ($historyFixtureStorage.LoadConversationAsync($syntheticHistory.Id)))) 'History deletion did not persist'
    Assert-Pass (@($deletedMessages | Where-Object {$_.type -eq 'new_chat_ready' -and $_.preservePage}).Count -eq 1) 'Deleting current history changed the page'
    Assert-Pass (@($deletedMessages | Where-Object {$_.type -eq 'history_list' -and $_.deleted -and $_.requestId -eq 'a194-single'}).Count -eq 1) 'History completion lost its request identity'
    $beforeDelete = $pending.Count
    [void](Wait-PaneTask (Call 'DeleteHistoryAsync' @('a194-missing', 'a194-failure')))
    $deletedMessages = @($pending | Select-Object -Skip $beforeDelete | ForEach-Object {$_ | ConvertFrom-Json})
    Assert-Pass (@($deletedMessages | Where-Object {$_.type -eq 'history_delete_failed' -and $_.requestId -eq 'a194-failure'}).Count -eq 1) 'Missing history reported success'
    Assert-Pass (@($deletedMessages | Where-Object {$_.type -eq 'history_list' -and $_.deleted}).Count -eq 0) 'Failure announced a persisted deletion'
    # A deleted ID is permanently revoked; a separate conversation exercises partial failure/retry.
    $syntheticHistory.Id = 'a194-locked'
    [void](Wait-PaneTask ($historyFixtureStorage.SaveConversationAsync($syntheticHistory)))
    Set-Field '_currentConversation' $syntheticHistory
    $historyPath = Join-Path (Join-Path $scratch 'a194-history') 'a194-locked.bin'
    $heldHistory = [IO.File]::Open($historyPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $beforeDelete = $pending.Count
        [void](Wait-PaneTask (Call 'DeleteAllHistoryAsync' @('a194-all-failed')))
        $deletedMessages = @($pending | Select-Object -Skip $beforeDelete | ForEach-Object {$_ | ConvertFrom-Json})
        Assert-Pass (@($deletedMessages | Where-Object type -eq 'history_delete_failed').Count -eq 1) 'Partial deletion reported success'
        Assert-Pass (@($deletedMessages | Where-Object type -eq 'new_chat_ready').Count -eq 0) 'Failed deletion reset the current conversation'
    } finally {$heldHistory.Dispose()}
    $beforeDelete = $pending.Count
    [void](Wait-PaneTask (Call 'DeleteAllHistoryAsync' @('a194-all-retry')))
    $deletedMessages = @($pending | Select-Object -Skip $beforeDelete | ForEach-Object {$_ | ConvertFrom-Json})
    Assert-Pass (@($deletedMessages | Where-Object {$_.type -eq 'history_list' -and $_.deleted -and $_.items.Count -eq 0}).Count -eq 1) 'Retry did not delete all history'
    Assert-Pass (@($deletedMessages | Where-Object {$_.type -eq 'new_chat_ready' -and $_.preservePage}).Count -eq 1) 'Delete all changed the page'
    Set-Field '_currentConversation' $historyBefore
    Set-Field '_historyStorage' $storage
    'PASS A194 history: persisted deletion, preserve page, request identity, missing file, partial failure and retry'

    # Actual pane render/export/history and calendar extraction, beyond the former 40/200/10 limits.
    $staticFlags = [Reflection.BindingFlags]'NonPublic,Static'
    $email = New-Object OnlyWorkspace.Outlook.Services.SelectedEmailInfo -Property @{Subject='Synthetic'; ReceivedTime=[DateTime]::Now}
    $dataJson = @{type='ordine';fields=@(1..41 | ForEach-Object {@{name="field$_";value="value$_"}}) + @(42);lines=@(1..201 | ForEach-Object {@{description="row$_";amount='=UNTRUSTED()'}}) + @($null)} | ConvertTo-Json -Depth 8 -Compress
    $rendered = Call-Static 'RenderStructuredData' @($dataJson, $email)
    Assert-Pass ($rendered.Item2.table.fields.Count -eq 41 -and $rendered.Item2.table.lines.Count -eq 201) 'Pane discarded valid table entries'
    Assert-Pass ($rendered.Item1.Contains('Risultato parziale') -and $rendered.Item1.Contains('2 voci')) 'Pane lost validation diagnostics'
    Assert-Pass ($rendered.Item2.table.csv.Contains('row201') -and $rendered.Item2.table.tsv.Contains("'=UNTRUSTED()")) 'Pane export lost final row or formula protection'
    $historyText = Call-Static 'StructuredHistoryText' @($rendered.Item1, $rendered.Item2)
    $config.History.Enabled = $true
    Set-Field '_config' $config
    $conversation = [OnlyWorkspace.Core.Privacy.ConversationPolicy]::Create($llmProfile)
    Set-Field '_currentConversation' $conversation
    $save = Call 'RecordActionInHistoryAsync' @($llmProfile, 'Estrazione dati: synthetic', $historyText, [Threading.CancellationToken]::None, $null, $null, $null, $null)
    [void](Wait-PaneTask $save)
    $saved = $storage.LoadConversationAsync($conversation.Id).GetAwaiter().GetResult()
    Assert-Pass (($saved.Messages | Where-Object Role -eq 'assistant' | Select-Object -Last 1).Content.Contains('row201')) 'Saved extraction history lost the final row'
    Assert-Pass (($saved.Messages | Where-Object Role -eq 'assistant' | Select-Object -Last 1).Content.Contains('Risultato parziale')) 'Saved extraction history lost diagnostics'
    $actionsJson = @{items=@(1..11 | ForEach-Object {@{type='task';title="task$_";due=[DateTime]::Today.AddDays(1).ToString('yyyy-MM-dd')}}) + @(42)} | ConvertTo-Json -Depth 8 -Compress
    $actions = Call-Static 'RenderActionItems' @($actionsJson, $email)
    Assert-Pass ($actions.Item2.items.Count -eq 11 -and $actions.Item1.Contains('task11') -and $actions.Item1.Contains('Risultato parziale')) 'Pane discarded actions or diagnostics'
    Assert-Pass ($actions.Item2.export.tsv.Contains('task11') -and $actions.Item2.export.csv.Contains('Risultato parziale')) 'Action export lost entries or diagnostics'
    # A129: an unread attachment of one email is declared by the pane with its reason, whatever the model says.
    $read = New-Object OnlyWorkspace.Core.Agents.DocumentOutcome -Property @{Name='offerta.docx'}
    $read.ApplyExtraction((New-Object OnlyWorkspace.Core.Security.AttachmentExtraction -Property @{Format='docx'; Text='Importo 1.250,00 EUR'}))
    $scan = New-Object OnlyWorkspace.Core.Agents.DocumentOutcome -Property @{Name='scansione.pdf'}
    $scan.ApplyExtraction((New-Object OnlyWorkspace.Core.Security.AttachmentExtraction -Property @{Format='pdf'; Problem='ocr-off'}))
    $documents = [OnlyWorkspace.Core.Agents.DocumentOutcome[]]@($read, $scan)
    $emailAttachments = [Activator]::CreateInstance($type.GetNestedType('EmailAttachments', $staticFlags), @('', $documents))
    $notice = Call-Static 'AttachmentNoticeFor' @($llmProfile, $emailAttachments)
    Assert-Pass ($notice.Contains('`scansione.pdf`') -and $notice.Contains('scansione o immagine: OCR disattivato nelle impostazioni') -and !$notice.Contains('`offerta.docx`')) 'Pane did not declare the unread attachment with its reason'
    Assert-Pass ($notice.Contains('verificali sul documento') -eq !$llmProfile.IsExternal) 'Pane caution on read attachments changed'
    # A220: real common reader of selected-email and draft attachments, including a saved copy supplied despite a refusal.
    $attachmentRoot = Join-Path $scratch 'extended-attachments'
    [void][IO.Directory]::CreateDirectory($attachmentRoot)
    $attachmentFile = Join-Path $attachmentRoot 'documento.txt'
    [IO.File]::WriteAllText($attachmentFile, 'ATTACHMENT-WHOLE-SYNTHETIC anna@example.invalid')
    $blockedFile = Join-Path $attachmentRoot 'blocked.txt'
    [IO.File]::WriteAllText($blockedFile, 'BLOCKED-CONTENT-MUST-STAY-LOCAL')
    $attachmentFacts = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Security.AttachmentFacts]'
    $attachmentFacts.Add((New-Object OnlyWorkspace.Core.Security.AttachmentFacts('documento.txt', 100)))
    $attachmentFacts.Add((New-Object OnlyWorkspace.Core.Security.AttachmentFacts('blocked.txt', 100)))
    $attachmentFacts[1].ScanStatus = [OnlyWorkspace.Core.Security.AttachmentScanStatus]::Threat
    $attachmentFiles = New-Object 'Collections.Generic.List[Collections.Generic.KeyValuePair[OnlyWorkspace.Core.Security.AttachmentFacts,string]]'
    $attachmentFiles.Add((New-Object 'Collections.Generic.KeyValuePair[OnlyWorkspace.Core.Security.AttachmentFacts,string]'($attachmentFacts[0], $attachmentFile)))
    $attachmentFiles.Add((New-Object 'Collections.Generic.KeyValuePair[OnlyWorkspace.Core.Security.AttachmentFacts,string]'($attachmentFacts[1], $blockedFile)))
    # The fixture owns these retained files; the production reader owns no temp directory here.
    $attachmentCopies = New-Object OnlyWorkspace.Outlook.Services.AttachmentCopies('', $attachmentFacts, $attachmentFiles)
    $attachmentMethod = $type.GetMethods($flags) | Where-Object { $_.Name -eq 'BuildAttachmentContextAsync' -and $_.GetParameters().Count -eq 4 -and $_.GetParameters()[0].ParameterType.Name.StartsWith('Func') }
    $copyFixture = [CalendarFixtureResolver]::Create($attachmentMethod.GetParameters()[0].ParameterType, $attachmentCopies)
    foreach ($mode in @('LocalOnly','Minimal','Standard','ExtendedDisabled','Extended','Local')) {
        $attachmentProfile = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Id='attachment-fixture'; Model='synthetic'; External=($mode -ne 'Local'); AllowAttachmentUpload=($mode -ne 'ExtendedDisabled')}
        $attachmentProfile.Exposure = if ($mode -eq 'ExtendedDisabled') { [OnlyWorkspace.Core.Privacy.ExposureLevel]::Extended } elseif ($mode -eq 'Local') { [OnlyWorkspace.Core.Privacy.ExposureLevel]::Standard } else { [Enum]::Parse([OnlyWorkspace.Core.Privacy.ExposureLevel], $mode) }
        $attachmentRead = Wait-PaneTask (Call 'BuildAttachmentContextAsync' @($copyFixture, $attachmentProfile, $type.GetField('_turn', $flags).GetValue($script:coordinator), [Threading.CancellationToken]::None))
        $allowed = $mode -in @('Extended','Local')
        Assert-Pass ($attachmentRead.Text.Contains('ATTACHMENT-WHOLE-SYNTHETIC') -eq $allowed) "A220 attachment exposure mismatch: $mode"
        Assert-Pass (!$attachmentRead.Text.Contains('BLOCKED-CONTENT-MUST-STAY-LOCAL')) "A220 known threat included: $mode"
        Assert-Pass ($attachmentRead.Documents[1].Reason.Contains('minaccia')) "A220 known threat lacked reason: $mode"
    }
    'PASS A220 selected/draft shared reader: explicit level and upload permission, local extraction, known-threat exclusion, no text from refused saved copies'
    $attachmentConfigBefore = $type.GetField('_config', $flags).GetValue($script:coordinator)
    $attachmentCapture = New-Object BriefingCaptureServer
    $attachmentClient = $null
    try {
        $attachmentProfile.External = $true
        $attachmentProfile.Exposure = [OnlyWorkspace.Core.Privacy.ExposureLevel]::Extended
        $attachmentProfile.BaseUrl = $attachmentCapture.Address
        $attachmentConfig = New-Object OnlyWorkspace.Core.Models.WorkspaceConfig
        $attachmentConfig.DefaultProfile = $attachmentProfile.Id; $attachmentConfig.Profiles.Add($attachmentProfile)
        Set-Field '_config' $attachmentConfig
        $refused = $false
        try { $attachmentClient = Call 'CreateLlmClient' @($attachmentProfile, $false) }
        catch { $refused = $_.Exception.ToString().Contains('DataEgressBlockedException') }
        Assert-Pass ($refused -and !$attachmentCapture.Pending) 'A220 invalid consent allowed dispatch'
        $attachmentProviders = [OnlyWorkspace.Core.Privacy.DataEgress]::ProvidersFor($attachmentConfig)
        foreach ($provider in $attachmentProviders) {
            $provider.Provider='Synthetic'; $provider.ProcessingAgreement=$true; $provider.Region='UE'; $provider.TransferBasis=[OnlyWorkspace.Core.Privacy.DataEgress]::TransferBases[0]; $provider.NoTraining=$true; $provider.Retention='none'
        }
        [void][OnlyWorkspace.Core.Privacy.DataEgress]::Accept($attachmentConfig,'test','test',[DateTime]::UtcNow,$null)
        $attachmentClient = Call 'CreateLlmClient' @($attachmentProfile, $false)
        $attachmentMessages = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Models.ChatMessage]'
        $attachmentMessages.Add([OnlyWorkspace.Core.Models.ChatMessage]::User($attachmentRead.Text))
        $refused = $false
        try { [void](Wait-PaneTask ($attachmentClient.CompleteStructuredAsync($attachmentMessages, '{}', [Threading.CancellationToken]::None))) }
        catch { $refused = $_.Exception.ToString().Contains('DataEgressBlockedException') }
        Assert-Pass ($refused -and !$attachmentCapture.Pending) 'Unverified attachment text reached HTTP'
        # A224: an already-created real client must recheck a revoked grant before any loopback connection.
        $attachmentClient.Dispose()
        $attachmentClient = Call 'CreateLlmClient' @($attachmentProfile, $false)
        [OnlyWorkspace.Core.Privacy.DataEgress]::Revoke($attachmentConfig,'test','test',[DateTime]::UtcNow)
        $refused = $false
        try { [void](Wait-PaneTask ($attachmentClient.CompleteStructuredAsync($attachmentMessages, '{}', [Threading.CancellationToken]::None))) }
        catch { $refused = $_.Exception.ToString().Contains('DataEgressBlockedException') }
        Assert-Pass ($refused -and !$attachmentCapture.Pending) 'A224 revoked consent reached HTTP through a captured client'
        [void][OnlyWorkspace.Core.Privacy.DataEgress]::Accept($attachmentConfig,'test','test',[DateTime]::UtcNow,$null)
        $attachmentConfig.DataEgress.Consent.Profiles.Clear()
        $refused = $false
        try { $null = Call 'CreateLlmClient' @($attachmentProfile, $false) }
        catch { $refused = $_.Exception.ToString().Contains('DataEgressBlockedException') }
        Assert-Pass ($refused -and !$attachmentCapture.Pending) 'A224 legacy host-only v4 consent inherited a profile grant'

        # Both actual keys change under the same reference, through the real isolated DPAPI vault.
        $egressServiceBefore = $service
        $egressService = New-Object OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService((Join-Path $scratch 'a224-keys'), (Join-Path $scratch 'a224-keys'))
        $attachmentProfile.SecretRef='a224-model'; $attachmentProfile.WebSearchSecretRef='a224-web'
        $egressService.StoreSecret('a224-model','SYNTHETIC-A224-MODEL')
        $egressService.StoreSecret('a224-web','SYNTHETIC-A224-WEB')
        $egressResolver = [Delegate]::CreateDelegate([Func[string,string]], $egressService, 'ResolveSecret')
        Set-Field '_configService' $egressService
        try {
            foreach ($credential in @('a224-model','a224-web')) {
                [void][OnlyWorkspace.Core.Privacy.DataEgress]::Accept($attachmentConfig,'test','test',[DateTime]::UtcNow,$egressResolver)
                $egressService.SaveConfiguration($attachmentConfig); [void]$egressService.LoadConfiguration()
                $attachmentClient.Dispose()
                $attachmentClient = Call 'CreateLlmClient' @($attachmentProfile, $false)
                $egressService.StoreSecret($credential,('SYNTHETIC-ROTATED-' + $credential))
                $refused = $false
                try { [void](Wait-PaneTask ($attachmentClient.CompleteStructuredAsync($attachmentMessages, '{}', [Threading.CancellationToken]::None))) }
                catch { $refused = $_.Exception.ToString().Contains('policy del profilo') }
                Assert-Pass ($refused -and !$attachmentCapture.Pending) ('A224 rotated credential reached HTTP: ' + $credential)
                [void]$egressService.LoadConfiguration()
            }
        } finally { Set-Field '_configService' $egressServiceBefore }
        if ($EvidenceDirectory) { [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'a224-guards.txt'), 'PASS: revoked pending client, legacy v4, actual model and web key rotations; zero pending HTTP connections') }
    }
    finally {
        if ($attachmentClient -is [IDisposable]) { $attachmentClient.Dispose() }
        $attachmentCapture.Dispose()
        Set-Field '_config' $attachmentConfigBefore
    }
    'PASS strict pane attachment boundary: extraction stays local; unverified text, revoked grants, legacy grants and rotated keys blocked before HTTP'


    # Calendar scan (A109): one request on the whole pack, every valid item kept, dates checked in the cited email.
    $due = [DateTime]::Today.AddDays(1).ToString('yyyy-MM-dd')
    $scanJson = @{items=@(1..11 | ForEach-Object {@{source="M$((($_ - 1) % 2) + 1)";type='task';title="task$_";date_text='entro domani';due=$due}}) + @(@{source='M2';type='task';title='invented';date_text='entro il 31/12/2099';due='2099-12-31'}, 42)} | ConvertTo-Json -Depth 8 -Compress
    $handler = New-Object CompletionSequenceHandler
    $handler.Responses = @((@{choices=@(@{message=@{content=$scanJson};finish_reason='stop'})} | ConvertTo-Json -Depth 10 -Compress))
    $http = New-Object Net.Http.HttpClient($handler)
    $http.BaseAddress = 'https://fixture.example.test/'
    $extractionProfile = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Type='localai';BaseUrl='http://127.0.0.1:12345/v1';Model='synthetic'}
    $extractionClient = New-Object OnlyWorkspace.Infrastructure.Llm.OpenAIClient($extractionProfile, $null, $http)
    try {
        $scanEvidence = foreach ($n in 1, 2) {
            $mail = New-Object OnlyWorkspace.Core.Archive.ChatMailItem -Property @{Subject="Synthetic $n"; SenderName='Fixture'; Received=[DateTime]::Now.AddMinutes(-$n); EntryId="e$n"; StoreId='s'}
            New-Object OnlyWorkspace.Core.Agents.MailEvidence -Property @{Mail=$mail; Text='Consegnate il modulo entro domani.'; Whole=$true}
        }
        $scanPack = [OnlyWorkspace.Core.Agents.MailEvidencePack]::Chronological([OnlyWorkspace.Core.Agents.MailEvidence[]]$scanEvidence)
        $task = Call 'ExtractCalendarItemsAsync' @($extractionClient, $scanPack, [Threading.CancellationToken]::None)
        $calendarItems = Wait-PaneTask $task
        Assert-Pass ($handler.Requests -eq 1 -and $calendarItems.Count -eq 12 -and $calendarItems.Diagnostics.DiscardedCount -eq 1) 'Calendar scan sent more than one request or discarded actions/diagnostics'
        $flagged = @($calendarItems | Where-Object { $_.Unverified.Count -gt 0 })
        Assert-Pass ($flagged.Count -eq 1 -and $flagged[0].Title -eq 'invented') 'Calendar scan did not mark the date missing from the cited email'
        Assert-Pass (@($calendarItems | Where-Object { $_.Notes -notmatch '^Da email .Synthetic [12]. di Fixture' }).Count -eq 0) 'Calendar scan items lost the email they come from'
    } finally { $extractionClient.Dispose(); $http.Dispose() }

    # Actual pane archive queries, using an isolated engine and an indexer already connected to it.
    $scopeEngine = New-Object OnlyWorkspace.Infrastructure.Archive.ArchiveEngine((Join-Path $scratch 'scope'))
    $currentField = [OnlyWorkspace.Outlook.Services.ArchiveIndexer].GetField('<Current>k__BackingField', $staticFlags)
    $previousIndexer = $currentField.GetValue($null)
    try {
        [void]$scopeEngine.Ready.GetAwaiter().GetResult()
        $scopeDb = New-Object OnlyWorkspace.Infrastructure.Archive.ArchiveClient('pane-scope', (Join-Path $scratch 'scope'))
        [void]$scopeDb.OpenAsync($true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        foreach ($store in @('a','b')) {
            $folder = $scopeDb.GetOrAddFolderAsync($store, 'inbox', 'Inbox', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            $headers = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Archive.ArchiveHeader]'
            $headers.Add((New-Object OnlyWorkspace.Core.Archive.ArchiveHeader -Property @{EntryId=$store;MessageId="<$store@example.test>";ReceivedUtc=[DateTime]::UtcNow;Subject=$store}))
            [void]$scopeDb.AddHeadersAsync($folder, $headers, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        $scopeIndexerType = [OnlyWorkspace.Outlook.Services.ArchiveIndexer]
        $scopeIndexer = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($scopeIndexerType)
        $scopeConfig = New-Object OnlyWorkspace.Core.Models.WorkspaceConfig
        $scopeConfig.Profiles.Add((New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Id='p';Model='synthetic'})); $scopeConfig.DefaultProfile='p'
        $scopeConfig.Archive.Enabled=$true; $scopeConfig.Archive.IndexAttachmentText=$false
        $scopeService = New-Object OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService((Join-Path $scratch 'scope-config'), (Join-Path $scratch 'scope-config'))
        $scopeService.SaveConfiguration($scopeConfig); [void]$scopeService.LoadConfiguration()
        foreach ($pair in @(@('_db',$scopeDb), @('_configService',$scopeService), @('_directory',(Join-Path $scratch 'scope')), @('_operations',(New-Object Threading.SemaphoreSlim(1,1))))) {
            $scopeIndexerType.GetField($pair[0], $flags).SetValue($scopeIndexer, $pair[1])
        }
        $currentField.SetValue($null, $scopeIndexer)
        Set-Field '_config' $scopeConfig
        foreach ($selection in @('[]','["a"]')) {
            [void]$scopeDb.SetMetaAsync('stores', $selection, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            $query = New-Object OnlyWorkspace.Core.Archive.ArchiveQuery
            $coverage = [Activator]::CreateInstance($type.GetNestedType('MailSearchCoverage', [Reflection.BindingFlags]::NonPublic), $true)
            $task = Call 'SearchArchiveAsync' @($scopeDb, $query, $false, $coverage, [Threading.CancellationToken]::None, [int]0)
            $hits = Wait-PaneTask $task
            $expected = if ($selection -eq '[]') {2} else {1}
            Assert-Pass ($hits.Count -eq $expected) 'Actual pane search confused all and explicit stores'
            $seed = New-Object OnlyWorkspace.Core.Archive.ArchiveFlowSeed -Property @{StoreId='a';EntryId='a'}
            $task = Call 'ReadFlowAsync' @($seed, $null, $false, [Threading.CancellationToken]::None, $false)
            [void](Wait-PaneTask $task)
            Assert-Pass (($selection -eq '[]' -and $null -eq $seed.AllowedStoreIds) -or ($selection -ne '[]' -and $seed.AllowedStoreIds.Count -eq 1)) 'Actual pane flow projected a different scope'
        }
        # A208: actual dispatcher + encrypted archive/pipe, no configured AI profile or Outlook application.
        $profiles = @($scopeConfig.Profiles)
        $scopeConfig.Profiles.Clear()
        Set-Field '_dataService' $null
        $pending.Clear()
        $localRequest = New-Object OnlyWorkspace.Core.Agents.AnalysisRequest -Property @{Kind='flow';EntryId='a';StoreId='a';RequestId=208}
        $localRequest.Goal.Preset = 'reconstruct'
        $localTask = Call 'ExecuteMailAnalysisAsync' @($localRequest, '', $false)
        [void](Wait-PaneTask $localTask)
        $localMessages = @($pending | ForEach-Object { $_ | ConvertFrom-Json })
        $localResult = @($localMessages | Where-Object type -eq 'analysis_reconstruction')
        Assert-Pass ($localResult.Count -eq 1 -and $localResult[0].items.Count -eq 1 -and $localResult[0].seed.entryId -eq 'a' -and $localResult[0].requestId -eq 208) 'Local reconstruction did not use its explicit archive seed'
        Assert-Pass (!($localMessages | Where-Object { $_.type -eq 'cloud_used' -or $_.type -eq 'stream_start' -or $_.type -eq 'config_error' })) 'Local reconstruction used the model path'
        if ($EvidenceDirectory) { [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'reconstruction-local.json'),($localMessages | ConvertTo-Json -Depth 12)) }
        $localRequest.EntryId = 'missing'
        $pending.Clear()
        [void](Wait-PaneTask (Call 'ExecuteMailAnalysisAsync' @($localRequest, '', $false)))
        $missingResult = @($pending | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object type -eq 'analysis_reconstruction')
        Assert-Pass ($missingResult.Count -eq 1 -and $missingResult[0].items.Count -eq 0 -and $missingResult[0].refused) 'Missing reconstruction seed was silently replaced'
        $localRequest.EntryId = 'b'; $localRequest.StoreId = 'b'
        $pending.Clear()
        [void](Wait-PaneTask (Call 'ExecuteMailAnalysisAsync' @($localRequest, '', $false)))
        $deniedResult = @($pending | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object type -eq 'analysis_reconstruction')
        Assert-Pass ($deniedResult.Count -eq 1 -and $deniedResult[0].items.Count -eq 0 -and $deniedResult[0].refused.Contains('esclusa')) 'Local reconstruction crossed the mailbox boundary'
        foreach ($savedProfile in $profiles) { $scopeConfig.Profiles.Add($savedProfile) }
        'PASS A208 actual local reconstruction without AI, exact seed, missing seed and excluded mailbox'
        # A162: the chat's reference parser feeds the actual pane archive search without request words.
        $all = New-Object OnlyWorkspace.Core.Archive.ArchiveQuery
        $seedHits = $scopeDb.SearchAsync($all, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        foreach ($hit in $seedHits) {
            $reference = if ($hit.StoreId -eq 'a') {'4471'} else {'4472'}
            $body = New-Object OnlyWorkspace.Core.Archive.ArchiveBody -Property @{Text="Ordine $reference. Conferma ricevuta."}
            [void]$scopeDb.SaveBodyAsync($hit.Id, $body, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        foreach ($question in @("Riassumi le email sull'ordine 4471", "Cosa chiede l'email sull'ordine 4471 e chi la manda?")) {
            $query = New-Object OnlyWorkspace.Core.Archive.ArchiveQuery
            $query.Text = [OnlyWorkspace.Core.Archive.ArchiveSearchText]::ParseChat($question, $query.FactFilters)
            Assert-Pass ([string]::IsNullOrWhiteSpace($query.Text) -and $query.FactFilters.Count -eq 1) 'Chat reference parser retained request words'
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                $coverage = [Activator]::CreateInstance($type.GetNestedType('MailSearchCoverage', [Reflection.BindingFlags]::NonPublic), $true)
                $hits = Wait-PaneTask (Call 'SearchArchiveAsync' @($scopeDb, $query, $false, $coverage, [Threading.CancellationToken]::None, [int]0))
                if ($hits.Count -gt 0) { break }
                Start-Sleep -Milliseconds 50
            } while ([DateTime]::UtcNow -lt $deadline)
            Assert-Pass ($hits.Count -eq 1 -and $hits[0].EntryId -eq 'a') 'Actual pane reference search lost its email or included a different reference'
        }
        # A160: the actual local-fact reader uses exact copies, scoped stores and current local dates.
        $source = $seedHits | Where-Object StoreId -eq 'a' | Select-Object -First 1
        Assert-Pass ($null -ne $source -and $source.Id -gt 0) 'Local-fact fixture lost its source'
        $body = New-Object OnlyWorkspace.Core.Archive.ArchiveBody -Property @{Text=('CORPO NON INVIATO.' + (' contesto generico.' * 30) + "`nScadenza pagamento: 15/11/2026")}
        [void]$scopeDb.SaveBodyAsync($source.Id, $body, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        Assert-Pass ($null -ne $scopeDb.GetOriginalAsync('a', 'a', [Threading.CancellationToken]::None).GetAwaiter().GetResult()) 'Local-fact fixture original was not saved'
        $bodyVersion = $scopeDb.GetBodyVersionAsync($source.Id, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $untilFacts = [DateTime]::UtcNow.AddSeconds(15)
        while ($scopeDb.GetFactProgressAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult().Pending -gt 0) {
            if ([DateTime]::UtcNow -gt $untilFacts) { throw 'Local facts did not complete' }
            Start-Sleep -Milliseconds 25
        }
        $copies = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Archive.ArchiveEmailRef]'
        $copies.Add((New-Object OnlyWorkspace.Core.Archive.ArchiveEmailRef -Property @{EntryId='a';StoreId='a'}))
        $actionFacts = Wait-PaneTask (Call 'ActionFactsAsync' @($copies, $false, [Threading.CancellationToken]::None))
        Assert-Pass ($actionFacts.Count -eq 1) "Expected one scoped facts block; got $($actionFacts.Count)"
        $text = $actionFacts["a`na"]
        Assert-Pass ($text.Contains('15/11/2026') -and !$text.Contains('CORPO NON INVIATO')) 'Action evidence lost local dates or included a paid card/original body'
        $handler = New-Object CompletionSequenceHandler
        $handler.Responses = @('{"choices":[{"message":{"content":"{\"items\":[]}"},"finish_reason":"stop"}]}')
        $http = New-Object Net.Http.HttpClient($handler)
        $http.BaseAddress = 'https://fixture.example.test/'
        $actionClient = New-Object OnlyWorkspace.Infrastructure.Llm.OpenAIClient($extractionProfile, $null, $http)
        try {
            $evidence = New-Object OnlyWorkspace.Core.Agents.MailEvidence -Property @{Mail=(New-Object OnlyWorkspace.Core.Archive.ChatMailItem -Property @{EntryId='a';StoreId='a'});Text=$text}
            $actionPack = [OnlyWorkspace.Core.Agents.MailEvidencePack]::Chronological([OnlyWorkspace.Core.Agents.MailEvidence[]]@($evidence))
            [void](Wait-PaneTask (Call 'ExtractCalendarItemsAsync' @($actionClient, $actionPack, [Threading.CancellationToken]::None)))
            Assert-Pass ($handler.Requests -eq 1 -and $handler.LastRequest.Contains('15/11/2026') -and !$handler.LastRequest.Contains('CORPO NON INVIATO')) 'Calendar transport did not use minimized evidence'
        } finally { $actionClient.Dispose(); $http.Dispose() }
        [void]$scopeDb.SetMetaAsync('stores', '["b"]', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $deniedFacts = Wait-PaneTask (Call 'ActionFactsAsync' @($copies, $false, [Threading.CancellationToken]::None))
        Assert-Pass ($deniedFacts.Count -eq 0) 'Action evidence crossed the mailbox scope'

        # A214: the complete production scan, including client factory, local facts, filtering and HTTP.
        # The service has no Outlook application: only the empty profile-name fallback can run.
        # Minimal must skip originals/PEC/attachments, including absent/out-of-scope copies.
        $scopeIndexerType.GetField('_attachmentReads', $flags).SetValue($scopeIndexer, (New-Object 'Collections.Generic.List[Threading.CancellationTokenSource]'))
        $calendarData = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([OnlyWorkspace.Outlook.Services.OutlookDataService])
        $calendarData.GetType().GetField('_uiThreadId', $flags).SetValue($calendarData, [Threading.Thread]::CurrentThread.ManagedThreadId)
        [void]$scopeDb.SetMetaAsync('stores', '["a"]', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $calendarDate = [DateTime]::Today.AddDays(20).ToString('dd/MM/yyyy')
        $calendarIso = [DateTime]::Today.AddDays(20).ToString('yyyy-MM-dd')
        $otherDate = [DateTime]::Today.AddDays(21).ToString('dd/MM/yyyy')
        foreach ($hit in $seedHits) {
            $date = if ($hit.StoreId -eq 'a') {$calendarDate} else {$otherDate}
            $body = New-Object OnlyWorkspace.Core.Archive.ArchiveBody -Property @{Text="Scadenza pagamento: $date. ARCHIVEDBODYPRIVATE"}
            [void]$scopeDb.SaveBodyAsync($hit.Id, $body, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        $untilFacts = [DateTime]::UtcNow.AddSeconds(15)
        while ($scopeDb.GetFactProgressAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult().Pending -gt 0) {
            if ([DateTime]::UtcNow -gt $untilFacts) { throw 'Calendar local facts did not complete' }
            Start-Sleep -Milliseconds 25
        }
        $calendarHeaderType = $type.GetNestedType('AnalysisEmail', [Reflection.BindingFlags]::NonPublic)
        $calendarHeaders = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($calendarHeaderType))
        foreach ($entry in @('a', 'no-facts', 'b')) {
            $header = [Activator]::CreateInstance($calendarHeaderType, $true)
            $calendarHeaderType.GetField('EntryId').SetValue($header, $entry)
            $calendarHeaderType.GetField('StoreId').SetValue($header, $entry)
            $calendarHeaderType.GetField('MessageId').SetValue($header, "<$entry@example.invalid>")
            $calendarHeaderType.GetField('Subject').SetValue($header, 'SUBJECTPRIVATE')
            $calendarHeaderType.GetField('Sender').SetValue($header, 'SENDERPRIVATE')
            $calendarHeaderType.GetField('Received').SetValue($header, [DateTime]::Today)
            $calendarHeaderType.GetField('Body').SetValue($header, $(if ($entry -eq 'a') {"Scadenza pagamento: $calendarDate. BODYPRIVATE"} else {$null}))
            $calendarHeaderType.GetField('OutsideFilters').SetValue($header, $true)
            $calendarHeaderType.GetField('LinkReasons').GetValue($header).Add('LINKPRIVATE')
            $calendarHeaders.Add($header)
        }
        $calendarMethod = $type.GetMethod('ExecuteCalendarScanAsync', $flags)
        $calendarOp = New-Object 'Collections.Generic.Dictionary[string,string]'
        $calendarOp.Add('type', 'calendar-fixture')
        foreach ($mode in @('Minimal','Standard','Extended','Local')) {
            $capture = New-Object BriefingCaptureServer
            try {
                $calendarProfile = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{
                    Id='calendar-fixture'; Model='synthetic'; BaseUrl=$capture.Address; External=($mode -ne 'Local')
                    Exposure=[OnlyWorkspace.Core.Privacy.ExposureLevel]::Minimal
                }
                if ($mode -eq 'Standard') { $calendarProfile.Exposure=[OnlyWorkspace.Core.Privacy.ExposureLevel]::Standard }
                if ($mode -eq 'Extended') { $calendarProfile.Exposure=[OnlyWorkspace.Core.Privacy.ExposureLevel]::Extended; $calendarProfile.AllowAttachmentUpload=$true }
                $calendarConfig = New-Object OnlyWorkspace.Core.Models.WorkspaceConfig
                $calendarConfig.DefaultProfile=$calendarProfile.Id; $calendarConfig.Profiles.Add($calendarProfile)
                $providers = [OnlyWorkspace.Core.Privacy.DataEgress]::ProvidersFor($calendarConfig)
                foreach ($provider in $providers) {
                    $provider.Provider='Synthetic'; $provider.ProcessingAgreement=$true; $provider.Region='UE'; $provider.TransferBasis=[OnlyWorkspace.Core.Privacy.DataEgress]::TransferBases[0]; $provider.NoTraining=$true; $provider.Retention='none'
                }
                if ($mode -ne 'Local') { [void][OnlyWorkspace.Core.Privacy.DataEgress]::Accept($calendarConfig,'test','test',[DateTime]::UtcNow,$null) }
                Set-Field '_config' $calendarConfig
                Set-Field '_currentConversation' ([OnlyWorkspace.Core.Privacy.ConversationPolicy]::Create($calendarProfile))
                Set-Field '_dataService' $calendarData
                # Whole-email modes use just the preloaded source; Minimal must also skip unavailable/out-of-scope copies.
                $modeHeaders = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($calendarHeaderType))
                if ($mode -ne 'Local') { foreach ($header in $calendarHeaders) { $modeHeaders.Add($header) } }
                else { $modeHeaders.Add($calendarHeaders[0]) }
                $resolver = [CalendarFixtureResolver]::Create($calendarMethod.GetParameters()[4].ParameterType, $modeHeaders)
                $before = @($pending).Count
                $modelItems = if ($mode -eq 'Minimal') {
                    @{items=@(
                        @{source='M1';type='task';title='FUTURETASK';date_text=$calendarDate;due=$calendarIso},
                        @{source='M2';type='task';title='UNVERIFIEDTASK';date_text=$calendarDate;due=$calendarIso}
                    )} | ConvertTo-Json -Depth 5 -Compress
                } else {'{"items":[]}'}
                $response = @{message=@{content=$modelItems};done=$true;done_reason='stop'} | ConvertTo-Json -Depth 5 -Compress
                $capturedRequest = $capture.ServeAsync($response)
                [void](Wait-PaneTask (Call 'ExecuteCalendarScanAsync' @($calendarOp,'Appuntamenti sintetici','fixture',($mode -eq 'Minimal'),$resolver)))
                $messages = @($pending | Select-Object -Skip $before | ForEach-Object {$_ | ConvertFrom-Json})
                if ($EvidenceDirectory) { [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "calendar-messages-$mode.json"),($messages | ConvertTo-Json -Depth 12)) }
                Assert-Pass (!($messages | Where-Object type -eq 'stream_error')) "Calendar $mode failed: $($capturedLog.Errors.ToArray() | Select-Object -Last 1)"
                Assert-Pass (@($messages | Where-Object type -eq 'stream_end').Count -eq 1) "Calendar $mode lost terminal result"
                $payload = Wait-PaneTask $capturedRequest
                if ($EvidenceDirectory) { [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "calendar-$mode.json"),$payload) }
                Assert-Pass (($payload.Contains($calendarDate) -eq ($mode -eq 'Local')) -and !$payload.Contains($otherDate)) "Calendar $mode lost its date or included a different mailbox"
                Assert-Pass ($payload.Contains('BODYPRIVATE') -eq ($mode -eq 'Local')) "Calendar $mode violated whole-body policy"
                Assert-Pass ($payload.Contains('SUBJECTPRIVATE') -eq ($mode -eq 'Local')) "Calendar $mode violated subject policy"
                Assert-Pass ($payload.Contains('SENDERPRIVATE') -eq ($mode -eq 'Local')) "Calendar $mode violated sender policy"
                Assert-Pass ($payload.Contains('LINKPRIVATE') -eq ($mode -eq 'Local')) "Calendar $mode violated link-reason policy"
                Assert-Pass (!$payload.Contains('ARCHIVEDBODYPRIVATE')) 'Calendar retrieved unrequested archived body text'
                $inputs = $type.GetField('_inputSourcesForTurn',$flags).GetValue($script:coordinator)
                Assert-Pass ($inputs.Count -eq 1 -and $inputs[0].EntryId -eq 'a' -and $inputs[0].StoreId -eq 'a') "Calendar $mode lost exact source tracking"
                $end = @($messages | Where-Object type -eq 'stream_end')[0]
                Assert-Pass ($end.mailSources.Count -eq 1 -and $end.mailSources[0].EntryId -eq 'a' -and $end.mailSources[0].StoreId -eq 'a') "Calendar $mode lost local source links"
                if ($mode -eq 'Minimal') {
                    Assert-Pass ($end.items.Count -ge 1 -and $end.items[0].verified) 'Calendar lost date/source verification'
                    Assert-Pass ($end.items[0].source.entryId -eq 'a' -and $end.items[0].source.storeId -eq 'a') 'Calendar lost exact item source'
                    Assert-Pass (!!($messages | Where-Object {$_.type -eq 'stream_chunk' -and $_.chunk.Contains('2 email senza fatti locali')})) 'Minimal omitted its missing-facts coverage'
                    $calendarConversation = $type.GetField('_currentConversation',$flags).GetValue($script:coordinator)
                    $saved = Wait-PaneTask ($storage.LoadConversationAsync($calendarConversation.Id))
                    Assert-Pass ($saved.Messages[1].Content.Contains('SUBJECTPRIVATE') -and !$saved.Messages[1].ModelContent.Contains('SUBJECTPRIVATE')) 'Calendar history reused local source labels'
                    [void](Wait-PaneTask (Call 'LoadHistoryAsync' @($calendarConversation.Id)))
                    $null = Call 'BeginConversationTurn' @($calendarProfile,'Continua senza nuovi dati',$null)
                    $nextPrompt = [OnlyWorkspace.Core.Prompts.PromptCatalog]::CreateChatPrompt('Continua senza nuovi dati')
                    $nextMessages = Call 'WithPriorTurns' @($nextPrompt,'Continua senza nuovi dati',[int]0)
                    $followClient = Call 'CreateLlmClient' @($calendarProfile,$false)
                    try {
                        $refused = $false
                        try { [void](Wait-PaneTask ($followClient.CompleteStructuredAsync($nextMessages,'{}',[Threading.CancellationToken]::None))) }
                        catch { $refused = $_.Exception.ToString().Contains('DataEgressBlockedException') }
                        Assert-Pass ($refused -and !$capture.Pending) 'Calendar follow-up reused unverified history'
                    } finally { $followClient.Dispose() }
                }
            } finally { $capture.Dispose(); Set-Field '_config' $scopeConfig }
        }
        # Real method, no HTTP listener accepting requests: every guard must terminate before any connection.
        foreach ($guard in @('Notice','LocalOnly','Budget','NoFacts','ScopedOut','Cancelled')) {
            $capture = New-Object BriefingCaptureServer
            try {
                $guardProfile = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{
                    Id='calendar-guard'; Model='synthetic'; BaseUrl=$capture.Address; External=$true
                    Exposure=[OnlyWorkspace.Core.Privacy.ExposureLevel]::Minimal
                }
                if ($guard -eq 'LocalOnly') { $guardProfile.Exposure=[OnlyWorkspace.Core.Privacy.ExposureLevel]::LocalOnly }
                if ($guard -eq 'Budget') { $guardProfile.ContextBudgetTokens=1 }
                $guardConfig = New-Object OnlyWorkspace.Core.Models.WorkspaceConfig
                $guardConfig.DefaultProfile=$guardProfile.Id; $guardConfig.Profiles.Add($guardProfile)
                if ($guard -notin @('Notice','LocalOnly')) {
                    $providers = [OnlyWorkspace.Core.Privacy.DataEgress]::ProvidersFor($guardConfig)
                    foreach ($provider in $providers) {
                        $provider.Provider='Synthetic'; $provider.ProcessingAgreement=$true; $provider.Region='UE'; $provider.TransferBasis=[OnlyWorkspace.Core.Privacy.DataEgress]::TransferBases[0]; $provider.NoTraining=$true; $provider.Retention='none'
                    }
                    [void][OnlyWorkspace.Core.Privacy.DataEgress]::Accept($guardConfig,'test','test',[DateTime]::UtcNow,$null)
                }
                Set-Field '_config' $guardConfig
                Set-Field '_currentConversation' ([OnlyWorkspace.Core.Privacy.ConversationPolicy]::Create($guardProfile))
                $guardHeaders = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($calendarHeaderType))
                $guardHeaders.Add($calendarHeaders[$(if ($guard -eq 'NoFacts') {1} else {0})])
                $stores = if ($guard -eq 'ScopedOut') {'["b"]'} else {'["a"]'}
                [void]$scopeDb.SetMetaAsync('stores',$stores,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
                $onResolve = if ($guard -eq 'Cancelled') {[Action]{ $null = Call 'StopActiveTask' @() }} else {$null}
                $resolver = [CalendarFixtureResolver]::Create($calendarMethod.GetParameters()[4].ParameterType,$guardHeaders,$onResolve)
                $before = @($pending).Count
                [void](Wait-PaneTask (Call 'ExecuteCalendarScanAsync' @($calendarOp,'Appuntamenti sintetici','fixture',$true,$resolver)))
                $messages = @($pending | Select-Object -Skip $before | ForEach-Object {$_ | ConvertFrom-Json})
                Assert-Pass (!$capture.Pending -and !($messages | Where-Object type -eq 'cloud_used')) "Calendar $guard contacted the model"
                $expected = if ($guard -in @('Notice','LocalOnly')) {'stream_error'} elseif ($guard -eq 'Cancelled') {'stream_cancelled'} else {'stream_end'}
                Assert-Pass (@($messages | Where-Object type -eq $expected).Count -eq 1) "Calendar $guard ended incorrectly: $($messages | ConvertTo-Json -Depth 5 -Compress)"
                if ($EvidenceDirectory) { [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "calendar-guard-$guard.json"),($messages | ConvertTo-Json -Depth 10)) }
            } finally { $capture.Dispose(); Set-Field '_config' $scopeConfig }
        }
        'PASS A214 complete calendar scan Minimal/Standard/local: scoped facts, no Minimal original reads, real HTTP, sources, verification, DPAPI history and six dispatch guards'

        # A224: the complete Chat action can answer from archive facts/listing without any consent or client.
        $localCapture = New-Object BriefingCaptureServer
        $localConfig = New-Object OnlyWorkspace.Core.Models.WorkspaceConfig
        $localProfile = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Id='a224-local-answer';Type='openai';Model='synthetic';External=$true;BaseUrl=$localCapture.Address;Exposure=[OnlyWorkspace.Core.Privacy.ExposureLevel]::Standard}
        $localConfig.Profiles.Add($localProfile); $localConfig.DefaultProfile=$localProfile.Id; $localConfig.History.Enabled=$false
        $localConfig.Archive.Enabled=$true
        [void]$scopeDb.SetMetaAsync('stores', '["a"]', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $localBody = New-Object OnlyWorkspace.Core.Archive.ArchiveBody -Property @{Text='Ordine 4471. Importo: 125,00 EUR.'}
        [void]$scopeDb.SaveBodyAsync($source.Id, $localBody, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $localDeadline=[DateTime]::UtcNow.AddSeconds(15)
        while ($scopeDb.GetFactProgressAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult().Pending -gt 0) {
            if ([DateTime]::UtcNow -gt $localDeadline) { throw 'A224 local facts did not complete' }
            Start-Sleep -Milliseconds 25
        }
        $folderSource=$type.GetField('_chatSearchFoldersSource',$flags)
        $emptyFolders=New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Models.MailFolderRef]'
        $folderSource.SetValue($script:coordinator,[CalendarFixtureResolver]::Constant($folderSource.FieldType,$emptyFolders))
        Set-Field '_contextWindow' (New-Object object)
        Set-Field '_dataService' $calendarData
        Set-Field '_configService' $scopeService
        Set-Field '_config' $localConfig
        try {
            foreach ($question in @("Qual è l'importo dell'ordine 4471?", "Elenca le email sull'ordine 4471", "Elenca le email sull'ordine 9999")) {
                Set-Field '_currentConversation' ([OnlyWorkspace.Core.Privacy.ConversationPolicy]::Create($localProfile))
                $pending.Clear()
                [void](Wait-PaneTask (Call 'ExecuteChatAsync' @($question,$null,$false)))
                $localMessages=@($pending | ForEach-Object { $_ | ConvertFrom-Json })
                Assert-Pass (@($localMessages | Where-Object type -eq 'stream_end').Count -eq 1) ('A224 local Chat did not finish: ' + ($localMessages | ConvertTo-Json -Depth 8 -Compress))
                Assert-Pass (!($localMessages | Where-Object { $_.type -eq 'stream_error' -or $_.type -eq 'cloud_used' }) -and !$localCapture.Pending) 'A224 local Chat required consent or reached HTTP'
                if ($question.StartsWith('Qual')) { Assert-Pass (($localMessages | Where-Object type -eq 'stream_chunk' | ForEach-Object chunk) -join '' -match '125') 'A224 local Chat lost the amount' }
                if ($EvidenceDirectory) { [IO.File]::WriteAllText((Join-Path $EvidenceDirectory ('a224-local-' + $(if($question.StartsWith('Qual')){'facts'}elseif($question.Contains('9999')){'empty-list'}else{'list'}) + '.json')),($localMessages | ConvertTo-Json -Depth 12)) }
            }
        } finally {
            $folderSource.SetValue($script:coordinator,$null)
            Set-Field '_contextWindow' $null
            Set-Field '_config' $scopeConfig
            $localCapture.Dispose()
        }
        'PASS A224 complete Chat local amount and list without consent: no cloud signal or HTTP'

        # A105: an excluded mailbox and an archive turned off are refused with distinct reasons.
        [void]$scopeDb.SetMetaAsync('stores', '["b"]', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        foreach ($off in @($false, $true)) {
            if ($off) { $scopeConfig.Archive.Enabled = $false; $scopeService.SaveConfiguration($scopeConfig) }
            $seed = New-Object OnlyWorkspace.Core.Archive.ArchiveFlowSeed -Property @{StoreId='a';EntryId='a'}
            $task = Call 'ReadFlowAsync' @($seed, $null, $false, [Threading.CancellationToken]::None, $false)
            try { [void](Wait-PaneTask $task); throw 'Actual pane flow crossed the archive scope' }
            catch [InvalidOperationException] {
                Assert-Pass ($_.Exception.GetType().Name -eq 'FlowRefusedException' -and $_.Exception.ArchiveOff -eq $off) "Flow refusal reason (archive off: $off) was $($_.Exception.Message)"
            }
        }
    } finally {
        $currentField.SetValue($null, $previousIndexer)
        $scopeEngine.Dispose()
        Set-Field '_config' $config
    }

    # Real CLI import, isolated and offline: remote local connector must remain gated afterwards.
    $config.History.Enabled = $false
    $config.Profiles.Clear(); $config.Profiles.Add($changed)
    $service.SaveConfiguration($config)
    $importTarget = Join-Path $scratch 'imported'
    $entry = [OnlyWorkspace.ConfigTool.Forms.MainConfigForm].Assembly.GetType('OnlyWorkspace.ConfigTool.Program').GetMethod('Main')
    $exitCode = $entry.Invoke($null, (,([string[]]@('import', (Join-Path $scratch 'config.json'), $importTarget, '--skip-test'))))
    Assert-Pass ($exitCode -eq 0) 'Isolated CLI import failed'
    $imported = (New-Object OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService($importTarget, $importTarget)).LoadConfiguration()
    Assert-Pass $imported.Profiles[0].IsExternal 'Import lost the effective remote classification'
    Assert-Pass (![OnlyWorkspace.Core.Privacy.DataEgress]::AllowsModel($imported, $imported.Profiles[0])) 'Imported profile bypassed notice'
    # A205: retired card settings are ignored; Full is read as Standard without losing the saved key.
    $secretBefore = (New-Object OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService($importTarget, $importTarget)).ResolveSecret($imported.Profiles[0].SecretRef)
    $legacy = Get-Content -LiteralPath (Join-Path $importTarget 'config.json') -Raw | ConvertFrom-Json
    $legacy.archive | Add-Member -NotePropertyName cardsEnabled -NotePropertyValue $true -Force
    $legacy.archive | Add-Member -NotePropertyName cardsDailyTokens -NotePropertyValue -1 -Force
    $legacy.profiles[0] | Add-Member -NotePropertyName exposure -NotePropertyValue 'Full' -Force
    $legacyPath = Join-Path $scratch 'legacy-cards.json'
    [IO.File]::WriteAllText($legacyPath, ($legacy | ConvertTo-Json -Depth 30))
    $exitCode = $entry.Invoke($null, (,([string[]]@('import', $legacyPath, $importTarget, '--skip-test'))))
    Assert-Pass ($exitCode -eq 0) 'Retired card fields blocked the import'
    $serviceAfter = New-Object OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService($importTarget, $importTarget)
    $configAfter = $serviceAfter.LoadConfiguration()
    Assert-Pass ($configAfter.Profiles[0].Exposure -eq [OnlyWorkspace.Core.Privacy.ExposureLevel]::Standard) 'Retired Full level was not restricted to Standard'
    Assert-Pass ($secretBefore -eq $serviceAfter.ResolveSecret($configAfter.Profiles[0].SecretRef)) 'Legacy import changed the saved key'
    $savedAfter = Get-Content -LiteralPath (Join-Path $importTarget 'config.json') -Raw
    Assert-Pass (!$savedAfter.Contains('cardsEnabled') -and !$savedAfter.Contains('cardsDailyTokens')) 'Retired generation fields were written back'
    # A212: the production message handler refuses stale selection-based clicks before a turn starts.
    $selectionConfig = New-Object OnlyWorkspace.Core.Models.WorkspaceConfig
    $selectionProfile = New-Object OnlyWorkspace.Core.Models.LlmProfileConfig -Property @{Id='selection-fixture';Type='ollama';Model='synthetic'}
    $selectionConfig.Profiles.Add($selectionProfile); $selectionConfig.DefaultProfile = $selectionProfile.Id
    $selectionRoot = Join-Path $scratch 'selection'
    $selectionService = New-Object OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService($selectionRoot,$selectionRoot)
    $selectionService.SaveConfiguration($selectionConfig); [void]$selectionService.LoadConfiguration()
    Set-Field '_configService' $selectionService
    Set-Field '_config' $selectionConfig
    Set-Field '_currentConversation' ([OnlyWorkspace.Core.Privacy.ConversationPolicy]::Create($selectionProfile))
    Set-Field '_dataService' $null
    $beforeSelectionTurn = $type.GetField('_turn',$flags).GetValue($script:coordinator)
    $beforeSelectionMessages = @($pending).Count
    foreach ($action in @('summary','email_followup','reply','security','action_items','extract_data','custom_reply')) {
        $document = [System.Text.Json.JsonDocument]::Parse((@{type=$action;instruction='Sintetico'} | ConvertTo-Json -Compress))
        try {$null = Call 'OnMessageReceived' @($document.RootElement)} finally {$document.Dispose()}
    }
    $selectionMessages = @($pending | Select-Object -Skip $beforeSelectionMessages | ForEach-Object {$_ | ConvertFrom-Json})
    Assert-Pass ($beforeSelectionTurn -eq $type.GetField('_turn',$flags).GetValue($script:coordinator)) 'Unavailable selection started an operation'
    Assert-Pass (@($selectionMessages | Where-Object type -eq 'toast').Count -eq 7) 'Unavailable email action lacked feedback'
    Assert-Pass (!($selectionMessages | Where-Object {$_.type -eq 'context_update' -and $_.hasEmail})) 'Unavailable email action kept stale context'
    'PASS A212 real coordinator: seven selection-based actions refused without starting a turn'
    $logged = $capturedLog.Lines.ToArray()
    Assert-Pass ($logged.Count -gt 0) 'Pane log was not captured in memory'
    Assert-Pass (!($logged | Where-Object { $_.Contains('logging initialized') })) 'Reload opened the file log'
    'PASS security finalization success/error/Stop with analyzer/history failures (A181), pane creation without page messages or context (A157), real pane history/reload/vault revocation, parallel web search policy (A150), repeated Stop partial saves/replay, single checked final answer, latest analysis only (A114), unread attachment reasons (A129), full-request guard, isolated CLI import and in-memory log'
}
finally {
    $control.Dispose()
    [Serilog.Log]::Logger = [Serilog.Core.Logger]::None
    $logInitialized.SetValue($null, $false)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($scratch)
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        ![IO.Path]::GetFileName($resolved).StartsWith('OnlyWorkspace-PanePolicy-', [StringComparison]::Ordinal)) { throw 'Unsafe fixture cleanup path' }
    if (!(Test-Path Env:ONLYWORKSPACE_TEST_ARTIFACTS) -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
