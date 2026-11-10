# Test di scripts\automation\run-tracker.ps1 con un tracker sintetico e una CLI claude finta (nessuna sessione reale).
# Uso: pwsh -NoProfile -File tests\scripts\RunTracker.Tests.ps1
$ErrorActionPreference = 'Stop'
$script = (Resolve-Path (Join-Path $PSScriptRoot '..\..\scripts\automation\run-tracker.ps1')).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ("ow-runtracker-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $root | Out-Null
$tracker = Join-Path $root 'tracker.json'
@{ tasks = @(
    @{ id = 'T-SKIP'; title = '#110 - escluso'; execution = 'autonomous'; status = 'todo'; dependencies = @() },
    @{ id = 'T-USER'; title = '#200 - utente'; execution = 'user'; status = 'todo'; dependencies = @() },
    @{ id = 'T-A'; title = '#201 - primo'; execution = 'autonomous'; status = 'todo'; dependencies = @() },
    @{ id = 'T-B'; title = '#202 - secondo'; execution = 'autonomous'; status = 'in_progress'; dependencies = @(@{ id = 'T-A' }) },
    @{ id = 'T-C'; title = '#139 - non qualifica'; execution = 'autonomous'; status = 'todo'; dependencies = @() }
) } | ConvertTo-Json -Depth 5 | Set-Content $tracker -Encoding utf8
# CLI finta: risponde con il json di $env:RT_REPLY e registra il titolo ricevuto.
$stub = Join-Path $root 'claude-stub.ps1'
@'
$p = $args[1]
if ($p -match 'task "([^"]+)"') { Add-Content $env:RT_CALLS $Matches[1] }
if ($p -match 'TASK DI QUALIFICA') { Add-Content $env:RT_CALLS 'QUAL' }
Write-Output $env:RT_REPLY
exit [int]$env:RT_EXIT
'@ | Set-Content $stub -Encoding utf8
$failures = 0
function Check($name, $ok) { if ($ok) { Write-Host "ok   $name" } else { Write-Host "FAIL $name" -ForegroundColor Red; $script:failures++ } }
function Run([string]$reply, [int]$exit, [string[]]$extra) {
  $env:RT_CALLS = Join-Path $root ("calls-" + [Guid]::NewGuid().ToString('N') + '.txt')
  $env:RT_REPLY = $reply; $env:RT_EXIT = $exit
  $logDir = Join-Path $root ([Guid]::NewGuid().ToString('N'))
  & pwsh -NoProfile -File $script -Tracker $tracker -Claude $stub -LogDirectory $logDir @extra | Out-Null
  [pscustomobject]@{ Code = $LASTEXITCODE; Calls = @(if (Test-Path $env:RT_CALLS) { Get-Content $env:RT_CALLS }); Log = (Get-Content (Get-ChildItem $logDir -Filter *.log)[0].FullName -Raw) }
}
try {
  # Risposta riuscita che cita la parola quota nel testo: non deve fermare il giro.
  $ok = '{"type":"result","subtype":"success","is_error":false,"api_error_status":null,"result":"fatto entro quota\nRISULTATO: done","permission_denials":[]}'
  $r = Run $ok 0 @()
  Check 'selezione in ordine, esclusi skip e task utente' (($r.Calls -join ',') -eq '#201 - primo,QUAL,#202 - secondo,QUAL,#139 - non qualifica')
  Check 'quota nel testo di un successo non ferma' ($r.Code -eq 0 -and $r.Log -notmatch 'STOP')
  # -Skip sostituisce la lista di default; con -File la lista arriva separata da virgole.
  $r = Run $ok 0 @('-Skip', '#110,#201')
  Check '-Skip configurabile, dipendenza autonoma aperta blocca' ((($r.Calls | Where-Object { $_ -ne 'QUAL' }) -join ',') -eq '#139 - non qualifica')
  $r = Run $ok 0 @('-OnlyId', '#110')
  Check '-OnlyId vince su -Skip' (($r.Calls -join ',') -eq '#110 - escluso,QUAL')
  $r = Run $ok 0 @('-OnlyId', 'T-C')
  Check '-OnlyId per id, prompt normale' (($r.Calls -join ',') -eq '#139 - non qualifica')
  $r = Run $ok 0 @('-DryRun')
  Check '-DryRun non chiama la CLI' ($r.Calls.Count -eq 0 -and $r.Log -match 'DRYRUN T-A')
  # Errore di limite: is_error con testo di quota e exit 1.
  $quota = '{"type":"result","subtype":"success","is_error":true,"api_error_status":429,"result":"You''ve hit your limit","permission_denials":[]}'
  $r = Run $quota 1 @()
  Check 'quota ferma al primo task' ($r.Code -eq 2 -and $r.Calls.Count -eq 2 -and $r.Log -match 'STOP: quota o limite')
  $r = Run $quota 0 @()
  Check 'is_error ferma anche con exit 0' ($r.Code -eq 2 -and $r.Log -match 'STOP: quota o limite')
  $r = Run 'non json' 0 @()
  Check 'uscita non json ferma come errore' ($r.Code -eq 2 -and $r.Log -match 'STOP: errore')
  $denied = '{"type":"result","subtype":"success","is_error":false,"api_error_status":null,"result":"ok","permission_denials":[{"tool_name":"Bash","tool_input":{"command":"git commit -m x"}},{"tool_name":"Read","tool_input":{"file_path":"D:/fuori/report.md"}}]}'
  $r = Run $denied 0 @('-OnlyId', '#201')
  Check 'permessi negati registrati con comando o percorso' ($r.Log -match 'Permessi negati \(2\): Bash: git commit -m x \| Read: D:/fuori/report.md')
} finally {
  Remove-Item Env:RT_CALLS, Env:RT_REPLY, Env:RT_EXIT -ErrorAction SilentlyContinue
}
Write-Host "Cartella di prova conservata: $root"
if ($failures) { exit 1 }
