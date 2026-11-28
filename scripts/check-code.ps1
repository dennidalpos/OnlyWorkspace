<#
.SYNOPSIS
    Verifica analizzatori .NET, PowerShell e sintassi JavaScript, Python, JSON e XML.
.DESCRIPTION
    Richiede .NET SDK 10, pwsh 7, PSScriptAnalyzer 1.25.0, node e python.
    Non esegue generatori, script applicativi o formattazioni automatiche.
    ONLYWORKSPACE_RUNTIME_ROOT abilita gli output centrali anche per i comandi dotnet diretti.
#>
#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
foreach ($tool in 'dotnet', 'node', 'python') {
    $null = Get-Command $tool -ErrorAction Stop
}
Import-Module PSScriptAnalyzer -RequiredVersion 1.25.0 -ErrorAction Stop

function Get-CodeFile([string]$Directory) {
    foreach ($item in Get-ChildItem -LiteralPath $Directory) {
        if ($item.PSIsContainer) {
            if ($item.Name -notin @('bin', 'obj', 'node_modules', '__pycache__', 'TestResults')) {
                Get-CodeFile $item.FullName
            }
        } else {
            $item
        }
    }
}

Push-Location $repoRoot
try {
    & dotnet restore OnlyWorkspace.sln --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Restore bloccato fallito: i pacchetti non corrispondono a packages.lock.json. Dopo una modifica voluta di Directory.Packages.props eseguire dotnet restore --force-evaluate e includere i lockfile.' }
    & dotnet build OnlyWorkspace.sln -c Release --nologo -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Build/analizzatori .NET falliti. Con MSB3021/MSB3026 un altro build o dotnet test usa lo stesso albero: attendere la fine e ripetere in sequenza.' }
    & dotnet format analyzers OnlyWorkspace.sln --no-restore --verify-no-changes --severity warn
    if ($LASTEXITCODE -ne 0) { throw 'Verifica analizzatori dotnet format fallita.' }
    & dotnet format whitespace OnlyWorkspace.sln --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw 'Formattazione degli spazi non conforme: eseguire dotnet format whitespace OnlyWorkspace.sln --no-restore e includere le modifiche.' }

    $files = @(
        foreach ($directory in 'src', 'tests', 'scripts', 'prompts') {
            Get-CodeFile (Join-Path $repoRoot $directory)
        }
        Get-ChildItem -LiteralPath $repoRoot -File
    )
    $settings = Join-Path $repoRoot 'PSScriptAnalyzerSettings.psd1'
    $psFiles = @($files | Where-Object Extension -in '.ps1', '.psm1', '.psd1')
    $diagnostics = @(
        foreach ($file in $psFiles) {
            Invoke-ScriptAnalyzer -Path $file.FullName -Settings $settings
        }
    )
    if ($diagnostics.Count -gt 0) {
        $diagnostics | Format-Table ScriptName, Line, RuleName, Message -Wrap
        throw "PSScriptAnalyzer: $($diagnostics.Count) rilievi."
    }

    $jsFiles = @($files | Where-Object Extension -in '.js', '.cjs', '.mjs')
    foreach ($file in $jsFiles) {
        & node --check $file.FullName
        if ($LASTEXITCODE -ne 0) { throw "Sintassi JavaScript non valida: $($file.FullName)" }
    }
    # TaskPane.html currently has one inline classic script; reject an unexamined layout change.
    $html = [IO.File]::ReadAllText((Join-Path $repoRoot 'src/OnlyWorkspace.Outlook/UI/TaskPane.html'))
    $scripts = [regex]::Matches($html, '(?is)<script\b([^>]*)>(.*?)</script\s*>')
    if ($scripts.Count -ne 1 -or $scripts[0].Groups[1].Value.Trim()) {
        throw 'Aggiornare il controllo per il nuovo layout degli script di TaskPane.html.'
    }
    $scripts[0].Groups[2].Value | & node --check --input-type=commonjs
    if ($LASTEXITCODE -ne 0) { throw 'Sintassi JavaScript di TaskPane.html non valida.' }

    $pythonFiles = @($files | Where-Object Extension -eq '.py')
    if ($pythonFiles.Count -gt 0) {
        & python -c 'import pathlib, sys; [compile(p.read_text(encoding="utf-8-sig"), str(p), "exec") for p in map(pathlib.Path, sys.argv[1:])]' @($pythonFiles.FullName)
        if ($LASTEXITCODE -ne 0) { throw 'Sintassi Python non valida.' }
    }

    $jsonFiles = @($files | Where-Object Extension -eq '.json')
    foreach ($file in $jsonFiles) {
        $null = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($file.FullName))
    }
    $xmlFiles = @($files | Where-Object Extension -in '.csproj', '.props', '.targets', '.resx', '.manifest')
    $xmlSettings = [Xml.XmlReaderSettings]::new()
    $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $xmlSettings.XmlResolver = $null
    foreach ($file in $xmlFiles) {
        $reader = [Xml.XmlReader]::Create($file.FullName, $xmlSettings)
        try { while ($reader.Read()) {} } finally { $reader.Dispose() }
    }
    "OK: .NET; $($psFiles.Count) PowerShell; $($jsFiles.Count) JavaScript + TaskPane; $($pythonFiles.Count) Python; $($jsonFiles.Count) JSON; $($xmlFiles.Count) XML."
} finally {
    # Compiler servers left running slow the test run that follows (#131).
    try { & dotnet build-server shutdown *> $null } catch { }
    Pop-Location
}
