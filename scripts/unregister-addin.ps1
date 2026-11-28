<#
.SYNOPSIS
    Removes the registration made by register-addin.ps1 (development machines): COM classes and add-in keys
    in both registry views, plus the per-user state. Requires elevation and Outlook closed.
.PARAMETER AssemblyPath
    OnlyWorkspace.Outlook.dll to unregister. Default: the Release build output.
#>
[CmdletBinding()]
param(
    [string]$AssemblyPath = ''
)

$ErrorActionPreference = "Stop"
if (!$AssemblyPath) {
    . (Join-Path $PSScriptRoot "build-paths.ps1")
    $AssemblyPath = (Get-OnlyWorkspaceBuildPaths -RepositoryRoot (Resolve-Path "$PSScriptRoot/..").Path).TargetPath
}

if (Test-Path $AssemblyPath) {
    $assembly = (Resolve-Path $AssemblyPath).Path
    foreach ($regasm in "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\regasm.exe", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\regasm.exe") {
        if (Test-Path $regasm) { & $regasm /u /nologo $assembly }
    }
} else {
    Write-Warning "Assembly not found, COM classes left registered: $AssemblyPath"
}

$keys = @(
    "HKCU:\Software\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin",
    "HKLM:\Software\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin",
    "HKLM:\Software\WOW6432Node\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin"
)
foreach ($key in $keys) {
    if (Test-Path $key) {
        Remove-Item -Path $key -Recurse -Force
        Write-Host "Removed add-in registration: $key" -ForegroundColor Green
    }
}
