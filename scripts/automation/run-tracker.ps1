# Esegue i task autonomi del tracker OnlyWorkspace, uno per sessione fresca, fino a errore o quota.
# Uso (dalla radice del repo): pwsh -NoProfile -File scripts\automation\run-tracker.ps1 [-OnlyId '#138'] [-MaxTasks 20] [-Skip '#110','#118'] [-DryRun]
# Richiede SECONDBRAIN_HOME (variabile utente) e la CLI claude. Non fa commit ne push: le modifiche restano nel working tree.
# -Skip: numeri (#NNN) o id esclusi; il default elenca i task del tracker che chiedono upgrade firmato, UAC reale, LongPaths o il repo SecondBrain.
# -NoQualification: numeri o id che ricevono il prompt normale invece di quello di qualifica.
# -DryRun: mostra la selezione e il prompt senza avviare sessioni. -Tracker e -Claude servono ai test con tracker e CLI sintetici.
param(
  [string]$OnlyId = '',
  [int]$MaxTasks = 20,
  [string[]]$Skip = @('#110', '#118', '#119', '#036'),
  [string[]]$NoQualification = @('#137', '#138', '#139'),
  [switch]$DryRun,
  [string]$Tracker = '',
  [string]$Claude = 'claude',
  [string]$LogDirectory = (Join-Path $env:TEMP 'OnlyWorkspace-tracker-run')
)
$ErrorActionPreference = 'Stop'
# con pwsh -File gli array arrivano come una stringa sola: accetta anche '#110,#118'
$Skip = @($Skip -split ',' | ForEach-Object Trim | Where-Object { $_ })
$NoQualification = @($NoQualification -split ',' | ForEach-Object Trim | Where-Object { $_ })
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sb = [Environment]::GetEnvironmentVariable('SECONDBRAIN_HOME', 'User')
if (-not $Tracker) {
  if (-not $sb) { throw 'SECONDBRAIN_HOME non impostata (dossier non raggiungibile).' }
  $Tracker = Join-Path $sb '01-projects\OnlyWorkspace\tracker.json'
}
# Read/Grep/Glob fuori dal repo sono negati in -p: il dossier (report in history/) e i browser di Playwright entrano con --add-dir.
# I percorsi vengono dalla workstation, non dal file settings versionato.
$addDirs = @(@($sb, (Join-Path $env:LOCALAPPDATA 'ms-playwright')) | Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { '--add-dir'; $_ })
$set  = Join-Path $PSScriptRoot 'run-tracker.settings.json'
New-Item -ItemType Directory -Force $LogDirectory | Out-Null
$log  = Join-Path $LogDirectory ("run-{0}.log" -f (Get-Date -f yyyyMMdd-HHmmss))
function L($m) { $l = "[{0}] {1}" -f (Get-Date -f HH:mm:ss), $m; $l | Tee-Object $log -Append }
# Numero del titolo (#NNN) o id: chiave usata da -OnlyId, -Skip e -NoQualification.
function Keys($task) { @($task.id) + @(if ($task.title -match '^(#\d+)\b') { $Matches[1] }) }
function InList($task, $list) { [bool](Keys $task | Where-Object { $list -contains $_ }) }
# Testo degli errori di limite: usato solo per classificare il motivo, perche ogni errore ferma comunque il giro.
$limitPattern = '(?i)usage limit|rate.?limit|limit reached|hit your limit|quota|credit balance|overloaded|\b429\b'
# tieni sveglio il PC
if (-not $DryRun) {
  Add-Type -Namespace W -Name P -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint f);'
  [void][W.P]::SetThreadExecutionState([uint32]2147483651)  # ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED; 0x80000003 sarebbe un Int32 negativo
}
Set-Location $repo
$done = @{}; $n = 0; $stopped = $false
try {
  while ($n -lt $MaxTasks) {
    $t = Get-Content $Tracker -Raw | ConvertFrom-Json
    # bloccano solo i task autonomi ancora aperti (le dipendenze da task utente, es. #136, non bloccano le prove con fixture)
    $auto = @($t.tasks | Where-Object execution -eq 'autonomous' | ForEach-Object id)
    $next = $t.tasks | Where-Object {
      $_.execution -eq 'autonomous' -and $_.status -ne 'done' -and -not $done[$_.id] -and
      ($OnlyId -eq '' -or (Keys $_) -contains $OnlyId) -and
      ($OnlyId -ne '' -or -not (InList $_ $Skip)) -and
      -not ($_.dependencies | Where-Object { $auto -contains $_.id -and -not $done[$_.id] })
    } | Select-Object -First 1
    if (-not $next) { L 'Nessun task eseguibile: fine.'; break }
    $isQual = -not (InList $next $NoQualification)
    $done[$next.id] = $true; $n++
    L "START $($next.title) (qualifica: $isQual)"
    $qualNote = if ($isQual) { @"

QUESTO E UN TASK DI QUALIFICA, SENZA UTENTE PRESENTE: computer-use e negato e non va richiesto. Per ogni criterio: (1) riproduci il caso con una fixture sintetica (PST/profili/configurazione di prova creati in cartella temporanea, mai dati reali); (2) dove e una pagina del riquadro usa playwright-cli su TaskPane.html a 100% e 200% (viewport/zoom), tastiera, focus, Esc; (3) dove e Outlook, puoi avviarlo senza interfaccia e leggere il registro Application come da memoria collaudo-outlook-howto; (4) cloud reale (QwenCloud) solo entro quota e con dati sintetici, chiave dall'archivio utente, mai nei rapporti. Non toccare impostazioni di sistema, registro, UAC o account. Chiudi un criterio solo se verificato davvero sulla build/fixture indicata dal criterio; una prova su fixture o su pagina isolata NON equivale a prova sulla build installata a schermo: registrala come tale e lascia il task in_progress con stateReason aggiornato e l'elenco di cio che resta a schermo. Report in history/.
"@ } else { '' }
    $prompt = @"
Esegui SOLO il task "$($next.title)" del tracker OnlyWorkspace (leggi descrizione e criteri di accettazione con knowledge.ps1 o get_task). Rispetta AGENTS.md e CLAUDE.md.
Vincoli: NON fare commit, push, installazioni, cancellazioni, ne modifiche a impostazioni di sistema. Non toccare altri task. Build con -p:ArtifactsPath=<cartella temporanea di sessione>. Verifica solo con strumenti funzionanti (dotnet build 0 avvisi, test net48 e net10 pertinenti, check-code.ps1).
Comandi: nessuna approvazione e possibile in questa sessione. Bash rifiuta i comandi che non puo controllare prima (variabili di shell, cicli, sostituzioni, cd in catena, git -C): un comando semplice per chiamata dalla radice del repo; ogni sequenza va in uno script .ps1 nella scratchpad, eseguito con pwsh -NoProfile -File. Gli script SecondBrain richiedono pwsh, non powershell 5.1. Dossier e file fuori dal repo: Read/Grep/Glob o gli strumenti MCP secondbrain.
A fine lavoro: se i criteri sono tutti verificati, chiudi il task con la procedura del tracker e scrivi history/<data>-<id>/report.md; altrimenti lascialo aperto con motivazione aggiornata. Modifiche al repo lasciate non committate. $qualNote`nUltima riga della risposta: RISULTATO: done | partial | blocked.
"@
    if ($DryRun) { L "DRYRUN $($next.id)"; $prompt | Out-File $log -Append; continue }
    # json: un solo oggetto con is_error, subtype, api_error_status, result e permission_denials.
    $out = & $Claude -p $prompt --settings $set --permission-mode acceptEdits --output-format json @addDirs 2>&1 | Out-String
    $code = $LASTEXITCODE
    $out | Out-File $log -Append
    $r = $null; try { $r = $out | ConvertFrom-Json } catch { $r = $null }
    if ($r -and $r.permission_denials) {
      L ("Permessi negati ({0}): {1}" -f @($r.permission_denials).Count, (($r.permission_denials | ForEach-Object {
        $i = $_.tool_input
        "$($_.tool_name): $(@($i.command, $i.file_path, $i.path, $i.skill) | Where-Object { $_ } | Select-Object -First 1)"
      }) -join ' | '))
    }
    L "END $($next.id) exit=$code subtype=$($r.subtype) is_error=$($r.is_error)"
    $failed = $code -ne 0 -or -not $r -or $r.is_error -or $r.subtype -ne 'success' -or $r.api_error_status
    if ($failed) {
      $text = if ($r) { "$($r.result) $($r.api_error_status)" } else { $out }
      $why = if ($text -match $limitPattern) { 'quota o limite' } else { 'errore' }
      L "STOP: $why ($($text.Trim() -replace '\s+', ' '))"
      $stopped = $true; break
    }
  }
} finally {
  if (-not $DryRun) { [void][W.P]::SetThreadExecutionState([uint32]2147483648) }
  L "Fine run. Log: $log"
}
if ($stopped) { exit 2 }
