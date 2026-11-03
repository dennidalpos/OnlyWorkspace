<#
.SYNOPSIS
    Esegue i test a script di tests\scripts: riquadro (node), pipeline di firma e NSIS (pwsh), archivio e riquadro net48 (Windows PowerShell 5.1: ArchiveRecovery, ArchiveCapacity, PanePolicy, EndpointInitialization).
.DESCRIPTION
    Da lanciare con pwsh dalla radice, dopo il Build Release:
      pwsh -NoProfile -File scripts\test-scripts.ps1
    Richiede node e NSIS in C:\Program Files (x86)\NSIS. Quattro script esclusi, da eseguire a mano:
    OllamaLive.Tests.ps1 con pwsh (serve Ollama sul loopback);
    WebViewBoundary.Smoke.ps1, WebViewRecovery.Smoke.ps1 e WebViewFocus.Smoke.ps1
    con Windows PowerShell 5.1 -STA (serve il runtime WebView2); focus anche con PowerShell x86.
    PanePolicy.Tests.ps1 verifica il dispatch reale net48 e l'import CLI con configurazioni sintetiche isolate; il suo log resta in memoria.
    Esce con 1 se uno script fallisce, dopo averli eseguiti tutti.
.PARAMETER Dir
    Cartella con OnlyWorkspace.Outlook.dll per gli script net48 (default: l'output Release).
#>
param([string]$Dir = '')
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Serve pwsh 7: Packaging.Tests.ps1 usa API di .NET moderno.' }
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$tests = Join-Path $repoRoot 'tests\scripts'
. (Join-Path $PSScriptRoot "build-paths.ps1")
Set-OnlyWorkspaceVerificationEnvironment -RepositoryRoot $repoRoot
if (!$Dir) { $Dir = Get-OnlyWorkspaceOutputDirectory -RepositoryRoot $repoRoot }
if (!(Test-Path "$Dir\OnlyWorkspace.Outlook.dll")) { throw "OnlyWorkspace.Outlook.dll non trovato in ${Dir}: eseguire prima il Build Release." }
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'

$runs = @(Get-ChildItem "$tests\*.Tests.cjs" | Sort-Object Name | ForEach-Object { , @($_.Name, 'node', @($_.FullName)) })
if ($runs.Count -eq 0) { throw "Nessuno script node in $tests." }
$runs += , @('Packaging.Tests.ps1', 'pwsh', @('-NoProfile', '-File', "$tests\Packaging.Tests.ps1"))
$runs += , @('BuildPaths.Tests.ps1', 'pwsh', @('-NoProfile', '-File', "$tests\BuildPaths.Tests.ps1"))
foreach ($name in 'ArchiveRecovery.Tests.ps1', 'ArchiveCapacity.Tests.ps1', 'PanePolicy.Tests.ps1', 'EndpointInitialization.Tests.ps1', 'HistoryDeletion.Tests.ps1') {
    $runs += , @($name, $windowsPowerShell, @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$tests\$name", '-Dir', $Dir))
}

$failed = @()
foreach ($run in $runs) {
    $name, $exe, $arguments = $run
    Write-Host "==> $name" -ForegroundColor Cyan
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { $failed += "$name (codice $LASTEXITCODE)" }
}
if ($failed) {
    Write-Host "Script falliti: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
"OK: $($runs.Count) script"
