<#
.SYNOPSIS
    Compila la soluzione, prepara il pacchetto di distribuzione, firma i binari e genera l'installer NSIS.
.DESCRIPTION
    Conforme alle specifiche di distribuzione OnlyWorkspace (docs/admin/installazione.md).
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

function Assert-SignedFile([string]$Path, [string]$Thumbprint) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
        throw "Firma non valida o certificato inatteso: $Path (stato: $($signature.Status))."
    }
}

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$publishDir = Join-Path $repoRoot "publish"
$distDir = Join-Path $repoRoot "dist"
$certsDir = Join-Path $repoRoot "certs"
$nsisExe = "C:\Program Files (x86)\NSIS\makensis.exe"

# Gli script di firma leggono la password del PFX da questa variabile: meglio fermarsi prima di compilare.
if ([string]::IsNullOrWhiteSpace($env:ONLYAPPS_PFX_PASSWORD)) {
    throw "Variabile d'ambiente ONLYAPPS_PFX_PASSWORD non impostata (password di certs\OnlyApps.pfx)."
}

if (!(Test-Path $nsisExe)) {
    throw "NSIS non trovato in $nsisExe. Verificare l'installazione di NSIS 3.13."
}

Write-Host "==> 1. Compilazione soluzione ($Configuration)..." -ForegroundColor Cyan
& dotnet build "$repoRoot\OnlyWorkspace.sln" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Compilazione fallita con codice $LASTEXITCODE" }

if (!$SkipTests) {
    Write-Host "==> 2. Esecuzione test di regressione..." -ForegroundColor Cyan
    & dotnet test "$repoRoot\OnlyWorkspace.sln" -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "Test falliti con codice $LASTEXITCODE" }
}

Write-Host "==> 3. Preparazione cartella publish..." -ForegroundColor Cyan
# Antivirus/indicizzazione possono bloccare per qualche istante i file appena scritti: breve retry.
for ($attempt = 1; (Test-Path $publishDir); $attempt++) {
    try {
        Remove-Item $publishDir -Recurse -Force
    }
    catch {
        if ($attempt -ge 5) { throw }
        Write-Host "    publish bloccata, nuovo tentativo ($attempt/5)..." -ForegroundColor Yellow
        Start-Sleep -Seconds 2
    }
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
if (!(Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

# Copia file Outlook Add-in
$outlookBin = Join-Path $repoRoot "src\OnlyWorkspace.Outlook\bin\$Configuration\net48"
Copy-Item -Path "$outlookBin\*" -Destination $publishDir -Recurse -Force
# Fuori dal pacchetto: simboli di debug e loader WebView2 ARM64 (TaskPaneControl usa solo win-x64/win-x86)
Remove-Item -Path "$publishDir\*.pdb" -Force
Remove-Item -Path "$publishDir\runtimes\win-arm64" -Recurse -Force -ErrorAction SilentlyContinue

# Copia ConfigTool (versione net48 per ambiente Windows client)
$configToolBin = Join-Path $repoRoot "src\OnlyWorkspace.ConfigTool\bin\$Configuration\net48"
Copy-Item -Path "$configToolBin\OnlyWorkspace.ConfigTool.exe*" -Destination $publishDir -Force

# Copia estrattore allegati in sottocartella (dipendenze isolate da quelle dell'add-in)
$extractorBin = Join-Path $repoRoot "src\OnlyWorkspace.AttachmentExtractor\bin\$Configuration\net48"
$extractorDest = Join-Path $publishDir "extractor"
New-Item -ItemType Directory -Path $extractorDest -Force | Out-Null
Copy-Item -Path "$extractorBin\*" -Destination $extractorDest -Exclude "*.pdb" -Force

# Copia avvisi licenze di terze parti
# No-PIA is verified on every distributed managed assembly before removing stale interop DLLs.
# Read PE metadata without loading .NET Framework assemblies into the packaging process.
foreach ($managedBinary in Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object { $_.Extension -in '.dll','.exe' -and $_.Name -notin 'Microsoft.Office.Interop.Outlook.dll','Microsoft.Office.Interop.Word.dll' }) {
    $peStream = [IO.File]::OpenRead($managedBinary.FullName)
    $peReader = $null
    try {
        $peReader = [Reflection.PortableExecutable.PEReader]::new($peStream)
        if (!$peReader.HasMetadata) { continue }
        $metadataReader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
        foreach ($assemblyHandle in $metadataReader.AssemblyReferences) {
            $assemblyReference = $metadataReader.GetString($metadataReader.GetAssemblyReference($assemblyHandle).Name)
            if ($assemblyReference -like 'Microsoft.Office.Interop.*') { throw "PIA runtime ancora richiesta da $($managedBinary.Name): $assemblyReference" }
        }
    }
    finally { if ($peReader) { $peReader.Dispose() }; $peStream.Dispose() }
}
foreach ($piaName in @('Microsoft.Office.Interop.Outlook.dll','Microsoft.Office.Interop.Word.dll')) {
    $stagedPia = Join-Path $publishDir $piaName
    if (Test-Path -LiteralPath $stagedPia) { Remove-Item -LiteralPath $stagedPia -Force }
}

$noticesSrc = Join-Path $repoRoot "THIRD-PARTY-NOTICES.txt"
if (Test-Path $noticesSrc) {
    Copy-Item -Path $noticesSrc -Destination $publishDir -Force
}

# Copia configurazione template di base
$templateSrc = Join-Path $repoRoot "deployment\config.template.json"
if (Test-Path $templateSrc) {
    Copy-Item -Path $templateSrc -Destination $publishDir -Force
}

# La coppia del certificato distribuito: uno nuovo, generato qui, non sarebbe attendibile (signtool verify /pa)
# e firmerebbe con un'identità diversa da quella installata sui PC.
$cerPath = Join-Path $certsDir "OnlyApps.cer"
$pfxPath = Join-Path $certsDir "OnlyApps.pfx"
if (!(Test-Path $cerPath) -or !(Test-Path $pfxPath)) {
    throw "Certificato assente o incompleto: servono certs\OnlyApps.cer e certs\OnlyApps.pfx del certificato distribuito. Ripristinarli dal backup; scripts\create-code-signing-cert.ps1 crea un'identità nuova solo per un primo rilascio, da rendere attendibile prima della build."
}
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cerPath)
try { $signerThumbprint = $certificate.Thumbprint }
finally { $certificate.Dispose() }

Write-Host "==> 4. Firma digitale binari in publish..." -ForegroundColor Cyan
$binariesToSign = @(Get-ChildItem -Path $publishDir -Include "OnlyWorkspace.*.dll", "OnlyWorkspace.ConfigTool.exe", "OnlyWorkspace.AttachmentExtractor.exe" -Recurse -File | Select-Object -ExpandProperty FullName)
if ($binariesToSign.Count -eq 0) { throw "Nessun binario OnlyWorkspace da firmare in $publishDir." }
foreach ($bin in $binariesToSign) {
    & pwsh -ExecutionPolicy Bypass -File "$PSScriptRoot\sign-binaries.ps1" -Files @($bin)
    if ($LASTEXITCODE -ne 0) { throw "Firma binario fallita con codice ${LASTEXITCODE}: $bin" }
    Assert-SignedFile $bin $signerThumbprint
}

Write-Host "==> 5. Compilazione Installer NSIS..." -ForegroundColor Cyan
$nsiScript = Join-Path $repoRoot "installer\OnlyWorkspaceSetup.nsi"
# Versione del prodotto: unica fonte Directory.Build.props
$version = ([xml](Get-Content (Join-Path $repoRoot "Directory.Build.props") -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Versione non valida in Directory.Build.props: '$version' (atteso x.y.z)." }
& $nsisExe "/DPRODUCT_VERSION=$version" $nsiScript
if ($LASTEXITCODE -ne 0) { throw "Compilazione NSIS fallita con codice $LASTEXITCODE" }

$installerPath = Join-Path $distDir "OnlyWorkspaceSetup.exe"
if (!(Test-Path $installerPath)) {
    throw "Installer generato non trovato in $installerPath"
}

Write-Host "==> 6. Firma digitale dell'installer NSIS..." -ForegroundColor Cyan
& pwsh -ExecutionPolicy Bypass -File "$PSScriptRoot\sign-binaries.ps1" -Files $installerPath
if ($LASTEXITCODE -ne 0) { throw "Firma installer fallita con codice $LASTEXITCODE" }
Assert-SignedFile $installerPath $signerThumbprint

Write-Host "==> Pacchetto di installazione completato con successo:" -ForegroundColor Green
Write-Host "    $installerPath ($( [math]::Round((Get-Item $installerPath).Length / 1MB, 2) ) MB)" -ForegroundColor Green
