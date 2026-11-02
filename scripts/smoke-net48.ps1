<#
.SYNOPSIS
    Carica l'add-in in un host .NET Framework senza binding redirect, come avviene dentro Outlook.
.DESCRIPTION
    Da eseguire dopo un aggiornamento di librerie: in Outlook le dipendenze (System.Text.Json, Serilog...)
    passano solo dall'AssemblyResolve dell'add-in. Va lanciato con Windows PowerShell 5.1, a 64 e a 32 bit:
      powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-net48.ps1
      %WINDIR%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-net48.ps1
    Non tocca configurazione, storico o Outlook: lavora in una cartella temporanea.
.PARAMETER Dir
    Cartella con OnlyWorkspace.Outlook.dll (default: output Release risolto da MSBuild).
#>
param([string]$Dir = "")
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -ne 5) { throw "Serve Windows PowerShell 5.1 (CLR di .NET Framework), non pwsh." }
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
. (Join-Path $PSScriptRoot "build-paths.ps1")
Set-OnlyWorkspaceVerificationEnvironment -RepositoryRoot $repoRoot
if (!$Dir) { $Dir = Get-OnlyWorkspaceOutputDirectory -RepositoryRoot $repoRoot }
"Host: CLR $([Environment]::Version), 64 bit: $([Environment]::Is64BitProcess) - $Dir"

[void][Reflection.Assembly]::LoadFrom("$Dir\OnlyWorkspace.Outlook.dll")
$addin = New-Object OnlyWorkspace.Outlook.OnlyWorkspaceAddin   # il costruttore statico registra AssemblyResolve

# Ribbon: Office chiama i callback per nome e ignora in silenzio quelli mancanti e l'XML non valido.
foreach ($ribbonId in 'Microsoft.Outlook.Explorer', 'Microsoft.Outlook.Mail.Read', 'Microsoft.Outlook.Mail.Compose') {
    $nodes = ([xml]$addin.GetCustomUI($ribbonId)).SelectNodes('//*')
    foreach ($attr in 'id', 'keytip') {
        $dup = @($nodes | ForEach-Object { $_.GetAttribute($attr) } | Where-Object { $_ } | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
        if ($dup) { throw "Ribbon ${ribbonId}: $attr duplicati: $($dup -join ', ')" }
    }
    $actions = @($nodes | ForEach-Object { $node = $_; foreach ($callback in 'onAction','getEnabled','getVisible','onLoad') { $node.GetAttribute($callback) } } | Where-Object { $_ } | Sort-Object -Unique)
    $missing = @($actions | Where-Object { -not $addin.GetType().GetMethod($_) })
    if ($missing) { throw "Ribbon ${ribbonId}: callback senza metodo pubblico: $($missing -join ', ')" }
    "Ribbon ${ribbonId}: $($nodes.Count) elementi, $($actions.Count) callback"
}
if ($addin.GetCustomUI('Microsoft.Outlook.Appointment')) { throw "Ribbon: atteso vuoto fuori dalle finestre di posta." }

# Real net48 dynamic dispatch and timer lifetime, without an Office window or configuration.
Add-Type -TypeDefinition 'public sealed class SyntheticRibbonUi { public int Calls; public string Last; public void InvalidateControl(string id) { Calls++; Last=id; } }'
$ribbon = New-Object SyntheticRibbonUi
$addin.OnRibbonLoad($ribbon)
$addin.OnRibbonLoad($ribbon)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$addin.GetType().GetMethod('InvalidateClearChat',$flags).Invoke($addin,@())
if ($ribbon.Calls -ne 1 -or $ribbon.Last -ne 'btnNewChat') { throw 'Ribbon: enabled-state invalidation failed or duplicated.' }
if ($addin.GetCanClearChat($null)) { throw 'Ribbon: clear chat enabled with no pane.' }
if ($addin.GetCanActOnEmail($null) -or !$addin.GetNeedsEmailSelection($null)) { throw 'Ribbon: email actions exposed without an Outlook context.' }
foreach ($callback in 'OnSummaryClicked','OnReplyAcceptClicked','OnReplyDeclineClicked','OnReplyCustomClicked','OnSecurityClicked','OnActionItemsClicked','OnExtractDataClicked') {
    $addin.$callback($null)
}
if ($null -ne $addin.GetType().GetField('_paneManager',$flags).GetValue($addin)) { throw 'Ribbon: unavailable email action created a pane.' }
$timer = $addin.GetType().GetField('_ribbonStateTimer',$flags).GetValue($addin)
$addin.GetType().GetMethod('OnOutlookQuit',$flags).Invoke($addin,@())
if ($timer.Enabled) { throw 'Ribbon: timer still active after Quit.' }
$custom = [Array]@()
$addin.OnDisconnection([OnlyWorkspace.Outlook.ext_DisconnectMode]0,[ref]$custom)
if ($null -ne $addin.GetType().GetField('_ribbonStateTimer',$flags).GetValue($addin)) { throw 'Ribbon: timer not disposed on disconnect.' }
'Ribbon: targeted invalidation, disabled empty chat, Quit and disconnect OK'
# PowerShell cerca i tipi solo negli assembly gia' caricati; le loro dipendenze restano da risolvere.
[void][Reflection.Assembly]::LoadFrom("$Dir\OnlyWorkspace.Core.dll")
[void][Reflection.Assembly]::LoadFrom("$Dir\OnlyWorkspace.Infrastructure.dll")

$tmp = Join-Path $env:TEMP ("ow_smoke_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    Set-Content "$tmp\config.json" -Encoding ascii -Value @'
{ "schemaVersion": 1, "defaultProfile": "local-ollama",
  "profiles": [
    { "id": "local-ollama", "type": "ollama", "baseUrl": "http://127.0.0.1:11434", "model": "smoke" },
    { "id": "cloud-ai", "type": "openai", "baseUrl": "https://api.openai.com/v1", "model": "smoke" } ] }
'@
    $svc = New-Object OnlyWorkspace.Infrastructure.Configuration.FileConfigurationService($tmp, $tmp, $null)
    $cfg = $svc.LoadConfiguration()
    $svc.SetUserFlag('smoke', $true)
    "Configurazione: $($cfg.Profiles.Count) profili, predefinito $($cfg.DefaultProfile), preferenza scritta: $($svc.GetUserFlag('smoke'))"

    $messages = [OnlyWorkspace.Core.Prompts.PromptCatalog]::CreateSummaryPrompt("Testo </email> di prova")
    "Prompt: $(@($messages).Count) messaggi"

    # Exercise the offline PSL and MIME parsers under Outlook's assembly resolution.
    $securityInput = New-Object OnlyWorkspace.Core.Security.EmailSecurityInput -Property @{
        TransportHeaders = "Authentication-Results: receiver; dmarc=fail`r`n"
        HtmlBody = '<a href="https://b.github.io">https://a.github.io</a>'
    }
    $securityReport = [OnlyWorkspace.Core.Security.EmailSecurityAnalyzer]::Analyze($securityInput)
    $verdict = [OnlyWorkspace.Core.Security.SecurityVerdict]::Decide($securityReport)
    if ($verdict.Outcome.ToString() -ne 'Suspicious' -or @($securityReport.Links).Count -ne 1) {
        throw "Security: offline PSL / MIME parsing failed."
    }
    $securitySources = [OnlyWorkspace.Core.Security.SecurityContentAnalysis]::CreateSources('Smoke', 'Sender', 's@example.com', 'Test body', $securityReport)
    [OnlyWorkspace.Core.Security.SecurityContentAnalysis]::Parse('{"findings":[]}', $securitySources).AddTo($securityReport)
    $view = [OnlyWorkspace.Core.Security.SecurityReportView]::From($securityReport, [OnlyWorkspace.Core.Security.SecurityCheckState]::Done, '')
    if ($view.Outcome -ne 'suspicious') { throw "Security: local evidence was lowered by an empty reading." }
    # The JSON names come from System.Text.Json attributes: they must load under Outlook's resolution.
    $name = [OnlyWorkspace.Core.Security.SecurityReportData].GetProperty('Outcome').GetCustomAttributes($true) | Where-Object { $_.GetType().Name -eq 'JsonPropertyNameAttribute' }
    if ($name.Name -ne 'outcome') { throw "Security: report JSON names not loaded." }
    $inspection = New-Object OnlyWorkspace.Core.Security.AttachmentInspection
    [OnlyWorkspace.Core.Security.ScriptStaticRules]::Inspect('Start-Process notepad.exe', [OnlyWorkspace.Core.Security.ScriptStaticRules+Language]::PowerShell, '', $inspection)
    if (@($inspection.Findings).Count -ne 1) { throw "Security: static script rules failed." }
    "Security: offline PSL, MIME headers/HTML, local verdict, report JSON and static rules OK"

    # Senza un server Ollama in ascolto risponde healthy=False: conta che la chiamata non lanci eccezioni di caricamento.
    $client = [OnlyWorkspace.Infrastructure.Llm.LlmClientFactory]::CreateClient($cfg.Profiles[0], $null, $null)
    $health = $client.CheckHealthAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    "Client Ollama: healthy=$($health.IsHealthy)"

    $data = [OnlyWorkspace.Core.Agents.StructuredDataParser]::Parse('{"type":"fattura","fields":[{"name":"Totale","value":"=1+1"}],"lines":[]}')
    "CSV: $($data.ToCsv(';').Trim() -replace "`r?`n", ' | ')"

    $history = New-Object OnlyWorkspace.Infrastructure.Security.DpapiHistoryStorageService("$tmp\history")
    $history.SaveConversationAsync((New-Object OnlyWorkspace.Core.Models.ConversationHistory)).GetAwaiter().GetResult()
    "Storico: $(@($history.ListConversationsAsync().GetAwaiter().GetResult()).Count) conversazione"

    # Archivio: SQLite cifrato con la DLL nativa della stessa architettura del processo (32 o 64 bit).
    $archive = [OnlyWorkspace.Infrastructure.Archive.ArchiveDb]::Open("$tmp\archive", 'smoke')
    try {
        $folder = $archive.GetOrAddFolder('store', 'inbox', 'Posta in arrivo')
        $header = New-Object OnlyWorkspace.Core.Archive.ArchiveHeader -Property @{
            EntryId = 'e1'; Subject = 'Specifiche di posa'; SenderAddress = 'mario@acme.it'; ReceivedUtc = [DateTime]::UtcNow; Preview = 'modalità operative' }
        [void]$archive.AddHeaders($folder, [OnlyWorkspace.Core.Archive.ArchiveHeader[]]@($header), $true)
        $query = New-Object OnlyWorkspace.Core.Archive.ArchiveQuery -Property @{ Text = 'modalita operativa' }
        $hits = @($archive.Search($query))
        if ($hits.Count -ne 1) { throw "Archivio: attesa 1 email, trovate $($hits.Count)." }
        "Archivio: $($hits.Count) email trovata, $($archive.FileSizeBytes) byte cifrati"
    }
    finally { $archive.Dispose() }

    # Real named pipe, SID checks and encrypted storage under the net48 host.
    $engine = New-Object OnlyWorkspace.Infrastructure.Archive.ArchiveEngine("$tmp\engine", $null)
    try {
        [void]$engine.Ready.GetAwaiter().GetResult()
        $remote = New-Object OnlyWorkspace.Infrastructure.Archive.ArchiveClient('smoke', "$tmp\engine", [Threading.CancellationToken]::None)
        $remote.OpenAsync($true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $remoteFolder = $remote.GetOrAddFolderAsync('store', 'inbox', 'Inbox', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $added = $remote.AddHeadersAsync($remoteFolder, [OnlyWorkspace.Core.Archive.ArchiveHeader[]]@($header), $true, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $remoteHits = $remote.SearchAsync($query, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if ($added -ne 1 -or $remoteHits.Count -ne 1 -or !$remoteFolder.Done) { throw 'Archive IPC: batch/search/bookmark failed.' }
        $state = $remote.EngineStatusAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if ($state.OutlookConnected -or $state.LastCheckUtc) { throw 'Archive IPC: unverified state presented as verified.' }
        "Archive IPC: encrypted batch/search with SID validation OK"
    }
    finally { $engine.Dispose() }

}
finally {
    if (!(Test-Path Env:ONLYWORKSPACE_TEST_ARTIFACTS)) { Remove-Item -LiteralPath $tmp -Recurse -Force }
}

"Assembly caricati dalla cartella dell'add-in:"
[AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.Location -like "$Dir*" } |
    ForEach-Object { '  {0} {1}' -f $_.GetName().Name, $_.GetName().Version } | Sort-Object
"OK"
