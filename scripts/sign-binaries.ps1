<#
.SYNOPSIS
    Firma digitalmente i file indicati (binari OnlyWorkspace e installer) con signtool.
.DESCRIPTION
    Conforme ai requisiti di firma OnlyWorkspace (docs/admin/sicurezza.md).
    Firma con algoritmo di digest SHA256 e timestamp RFC 3161; senza marca temporale si ferma, salvo -AllowNoTimestamp.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Files,

    [string]$PfxPath = "",
    [string]$TimestampServer = "http://timestamp.digicert.com",

    # Solo per prove locali: senza marca temporale la firma smette di valere alla scadenza del certificato.
    [switch]$AllowNoTimestamp
)

$ErrorActionPreference = "Stop"

$pfxPassword = $env:ONLYAPPS_PFX_PASSWORD
if ([string]::IsNullOrWhiteSpace($pfxPassword)) {
    throw "Password del PFX mancante: impostare la variabile d'ambiente ONLYAPPS_PFX_PASSWORD."
}

if ([string]::IsNullOrWhiteSpace($PfxPath)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    if (!$scriptDir) { $scriptDir = $PSScriptRoot }
    $PfxPath = Join-Path $scriptDir "..\certs\OnlyApps.pfx"
}
$PfxPath = [System.IO.Path]::GetFullPath($PfxPath)

if (!(Test-Path $PfxPath)) {
    throw "File PFX non trovato nel percorso: $PfxPath. Ripristinare il PFX del certificato distribuito (non generarne uno nuovo)."
}

# Rilevamento signtool.exe
$signtoolPaths = @(
    "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe",
    "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe",
    "C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe",
    "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\signtool.exe"
)

$signtool = $null
foreach ($p in $signtoolPaths) {
    if (Test-Path $p) {
        $signtool = $p
        break
    }
}

if (!$signtool) {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        $signtool = $cmd.Source
    }
}

if (!$signtool) {
    throw "signtool.exe non trovato nel sistema. Verificare che Windows SDK sia installato."
}

Write-Host "==> Utilizzo signtool: $signtool" -ForegroundColor Cyan

foreach ($file in $Files) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "File non trovato da firmare: $file"
    }
}

foreach ($file in $Files) {
    Write-Host "--> Firma file: $file ..." -ForegroundColor Cyan
    
    & $signtool sign /fd SHA256 /tr $TimestampServer /td SHA256 /f $PfxPath /p $pfxPassword $file 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        if (!$AllowNoTimestamp) {
            throw "Firma con marca temporale fallita per file: $file (ExitCode: $LASTEXITCODE). Verificare la connessione a $TimestampServer; -AllowNoTimestamp firma senza marca solo per prove locali."
        }
        Write-Warning "Firma con marca temporale fallita per ${file}: firma senza marca (-AllowNoTimestamp)."
        & $signtool sign /fd SHA256 /f $PfxPath /p $pfxPassword $file 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Firma fallita per file: $file (ExitCode: $LASTEXITCODE)"
        }
    }

    $verificationOutput = & $signtool verify /pa /all $file 2>&1 | Out-String
    $verificationExitCode = $LASTEXITCODE
    if ($verificationExitCode -ne 0) {
        throw "Verifica firma fallita per file: $file (ExitCode: $verificationExitCode). Dettagli signtool:`n$verificationOutput"
    }
    Write-Host "--> File firmato con successo: $file" -ForegroundColor Green
}

Write-Host "==> Tutte le firme completate con successo." -ForegroundColor Green
