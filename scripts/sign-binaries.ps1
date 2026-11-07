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

# signtool takes the PFX password only on its command line, where any process can read it. The PFX is imported with the
# password as a SecureString and signtool selects the certificate by thumbprint; a certificate and key this script
# added are removed again, one that was already in the store is left alone.
$store = 'Cert:\CurrentUser\My'
$securePassword = ConvertTo-SecureString -String $pfxPassword -AsPlainText -Force
$pfxThumbprint = [Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath, $securePassword).Thumbprint
$alreadyImported = Test-Path -LiteralPath "$store\$pfxThumbprint"
$imported = Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation $store -Password $securePassword
if ($imported.Thumbprint -ne $pfxThumbprint) { throw "Importazione del PFX inattesa: $($imported.Thumbprint)." }

function Remove-ImportedSigningCertificate {
    if ($alreadyImported) { return }
    $item = Get-Item -LiteralPath "$store\$pfxThumbprint" -ErrorAction SilentlyContinue
    if (!$item) { return }
    try {
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($item)
        if ($rsa -is [Security.Cryptography.RSACng]) { $rsa.Key.Delete() }
        elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) { $rsa.PersistKeyInCsp = $false; $rsa.Clear() }
    } catch { Write-Warning "Chiave privata importata non rimossa: $($_.Exception.Message)" }
    Remove-Item -LiteralPath "$store\$pfxThumbprint" -Force -ErrorAction SilentlyContinue
}

try {
foreach ($file in $Files) {
    Write-Host "--> Firma file: $file ..." -ForegroundColor Cyan
    
    & $signtool sign /fd SHA256 /tr $TimestampServer /td SHA256 /sha1 $pfxThumbprint /s My $file 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        if (!$AllowNoTimestamp) {
            throw "Firma con marca temporale fallita per file: $file (ExitCode: $LASTEXITCODE). Verificare la connessione a $TimestampServer; -AllowNoTimestamp firma senza marca solo per prove locali."
        }
        Write-Warning "Firma con marca temporale fallita per ${file}: firma senza marca (-AllowNoTimestamp)."
        & $signtool sign /fd SHA256 /sha1 $pfxThumbprint /s My $file 2>&1 | Out-Null
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

}
finally { Remove-ImportedSigningCertificate }

Write-Host "==> Tutte le firme completate con successo." -ForegroundColor Green
