<#
.SYNOPSIS
    Genera certificato autofirmato per Code Signing OnlyApps e ne esporta chiavi pubblica (.cer) e protetta (.pfx).
.DESCRIPTION
    Conforme ai requisiti di firma OnlyWorkspace (docs/admin/sicurezza.md):
    - Subject: CN=OnlyApps Code Signing
    - EKU: Code Signing (1.3.6.1.5.5.7.3.3)
    - Chiave RSA 3072 bit, validità configurabile (default 3 anni)
    - Esporta OnlyApps.cer per inclusione nell'installer e importazione in Root/TrustedPublisher
    - Esporta OnlyApps.pfx protetto dalla password in ONLYAPPS_PFX_PASSWORD (conservato offline o in archivio protetto)
#>

[CmdletBinding()]
param(
    [string]$Subject = "CN=OnlyApps Code Signing",
    [string]$OutputDir = "$PSScriptRoot\..\certs",
    [string]$CertName = "OnlyApps",
    [string]$PasswordPlain = $env:ONLYAPPS_PFX_PASSWORD,
    [int]$ValidYears = 3,
    [switch]$InstallToMachineStores
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PasswordPlain)) {
    throw "Password del PFX mancante: impostare la variabile d'ambiente ONLYAPPS_PFX_PASSWORD (o passare -PasswordPlain)."
}

if (!(Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$cerPath = Join-Path $OutputDir "$CertName.cer"
$pfxPath = Join-Path $OutputDir "$CertName.pfx"

Write-Host "==> Generazione certificato Code Signing: $Subject (RSA 3072 bit, $ValidYears anni)..." -ForegroundColor Cyan

$securePwd = ConvertTo-SecureString -String $PasswordPlain -AsPlainText -Force

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeySpec Signature `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -FriendlyName "OnlyApps Code Signing"

try {
    # Esporta certificato pubblico (.cer)
    [System.IO.File]::WriteAllBytes($cerPath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Write-Host "--> Certificato pubblico esportato: $cerPath" -ForegroundColor Green

    # Esporta certificato e chiave privata protetta (.pfx)
    [System.IO.File]::WriteAllBytes($pfxPath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $securePwd))
    Write-Host "--> Chiave privata protetta esportata: $pfxPath" -ForegroundColor Green

    if ($InstallToMachineStores) {
        Write-Host "==> Importazione certificato pubblico negli archivi macchina..." -ForegroundColor Cyan
        & certutil.exe -addstore -f "Root" $cerPath
        if ($LASTEXITCODE -ne 0) { throw "Importazione Root fallita con codice $LASTEXITCODE" }
        & certutil.exe -addstore -f "TrustedPublisher" $cerPath
        if ($LASTEXITCODE -ne 0) { throw "Importazione TrustedPublisher fallita con codice $LASTEXITCODE" }
        Write-Host "--> Certificato importato con successo in Root e TrustedPublisher." -ForegroundColor Green
    }
}
finally {
    # Rimuove il certificato temporaneo da Cert:\CurrentUser\My per evitare proliferazione
    Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
}

Write-Host "==> Operazione completata con successo." -ForegroundColor Green
