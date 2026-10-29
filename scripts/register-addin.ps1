<#
.SYNOPSIS
    Registers the OnlyWorkspace COM add-in for Outlook on a development machine, or checks the registration.
.DESCRIPTION
    Same registration as the installer: RegAsm /codebase (32 and 64 bit) plus the OnlyWorkspace.Addin key in both
    registry views, so it loads whatever the bitness of Outlook. Registering requires elevation and Outlook closed.
.PARAMETER AssemblyPath
    OnlyWorkspace.Outlook.dll to register. Default: the Release build output.
.PARAMETER CheckOnly
    Only reports the registration in every registry view; exit code 0 when all of them are complete.
#>
[CmdletBinding()]
param(
    [string]$AssemblyPath = "$PSScriptRoot\..\src\OnlyWorkspace.Outlook\bin\Release\net48\OnlyWorkspace.Outlook.dll",
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

$progId = "OnlyWorkspace.Addin"
$addinKey = "Software\Microsoft\Office\Outlook\Addins\$progId"
$views = if ([Environment]::Is64BitOperatingSystem) {
    @(
        @{ Name = "64 bit"; View = [Microsoft.Win32.RegistryView]::Registry64; RegAsm = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" },
        @{ Name = "32 bit"; View = [Microsoft.Win32.RegistryView]::Registry32; RegAsm = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\regasm.exe" }
    )
} else {
    @(@{ Name = "32 bit"; View = [Microsoft.Win32.RegistryView]::Registry32; RegAsm = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\regasm.exe" })
}

function Get-ViewStatus($view) {
    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view.View)
    try {
        $addin = $hklm.OpenSubKey($addinKey)
        $clsid = $hklm.OpenSubKey("Software\Classes\$progId\CLSID")
        $clsidValue = if ($clsid) { $clsid.GetValue("") }
        $server = if ($clsidValue) { $hklm.OpenSubKey("Software\Classes\CLSID\$clsidValue\InprocServer32") }
        $codeBase = if ($server) { $server.GetValue("CodeBase") }
        [pscustomobject]@{
            View         = $view.Name
            LoadBehavior = if ($addin) { $addin.GetValue("LoadBehavior") }
            CodeBase     = $codeBase
            Complete     = ($null -ne $addin) -and ($addin.GetValue("LoadBehavior") -eq 3) -and
                           ($null -ne $codeBase) -and (Test-Path -LiteralPath ([Uri]$codeBase).LocalPath)
        }
    } finally {
        $hklm.Dispose()
    }
}

if ($CheckOnly) {
    $c2r = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Configuration" -ErrorAction SilentlyContinue
    Write-Host "Office Click-to-Run platform: $(if ($c2r.Platform) { $c2r.Platform } else { 'not detected (MSI or not installed)' })"

    $allComplete = $true
    foreach ($view in $views) {
        $status = Get-ViewStatus $view
        if ($status.Complete) {
            Write-Host "[$($status.View)] REGISTERED: LoadBehavior=$($status.LoadBehavior), $($status.CodeBase)" -ForegroundColor Green
        } else {
            $allComplete = $false
            Write-Host "[$($status.View)] NOT REGISTERED or incomplete: LoadBehavior=$($status.LoadBehavior), CodeBase=$($status.CodeBase)" -ForegroundColor Yellow
        }
    }

    # Outlook stores here the per-user state: anything other than 3 means the user (or Outlook) disabled the add-in.
    $user = Get-ItemProperty -Path "HKCU:\$addinKey" -ErrorAction SilentlyContinue
    if ($null -ne $user -and $null -ne $user.LoadBehavior -and $user.LoadBehavior -ne 3) {
        Write-Host "[current user] LoadBehavior=$($user.LoadBehavior): add-in disabled for this user." -ForegroundColor Yellow
    }

    exit $(if ($allComplete) { 0 } else { 1 })
}

$assembly = (Resolve-Path $AssemblyPath).Path
foreach ($view in $views) {
    & $view.RegAsm /codebase /nologo $assembly
    if ($LASTEXITCODE -ne 0) { throw "RegAsm ($($view.Name)) failed with exit code $LASTEXITCODE." }

    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view.View)
    try {
        $key = $hklm.CreateSubKey($addinKey)
        $key.SetValue("FriendlyName", "OnlyWorkspace")
        $key.SetValue("Description", "OnlyWorkspace AI Assistant for Microsoft Outlook")
        $key.SetValue("LoadBehavior", 3, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue("CommandLineSafe", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.Dispose()
    } finally {
        $hklm.Dispose()
    }
}

Write-Host "Registered $progId ($($views.Name -join ' and ')): $assembly" -ForegroundColor Green
