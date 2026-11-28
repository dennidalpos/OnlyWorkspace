<#
.SYNOPSIS
    Manages registry policies to hide and block the "Try the new Outlook" toggle.
.PARAMETER Action
    Check, Apply, or Revert. Apply records the values it creates under Software\OnlyWorkspace\NewOutlookBlock
    of the same hive; Revert removes only those, so a value set before (by a company policy or by the setup) stays.
#>
[CmdletBinding()]
param(
    [ValidateSet("Check", "Apply", "Revert")]
    [string]$Action = "Check",
    [switch]$Check,
    [switch]$Apply,
    [switch]$Revert,
    # Tests only: every value (HKLM ones too) and the markers under HKCU:\<RegistryRoot>, never the real policies.
    [string]$RegistryRoot = ""
)

if ($Check) { $Action = "Check" }
elseif ($Apply) { $Action = "Apply" }
elseif ($Revert) { $Action = "Revert" }

$ErrorActionPreference = "Stop"

$Targets = @(
    @{ Path = "HKCU:\Software\Microsoft\Office\16.0\Outlook\Options\General"; Name = "HideNewOutlookToggle"; Value = 1; Type = "DWord" },
    @{ Path = "HKCU:\Software\Microsoft\Office\16.0\Outlook\Preferences"; Name = "UseNewOutlook"; Value = 0; Type = "DWord" },
    @{ Path = "HKLM:\Software\Policies\Microsoft\Office\16.0\Outlook\Options\General"; Name = "HideNewOutlookToggle"; Value = 1; Type = "DWord" },
    @{ Path = "HKLM:\Software\Policies\Microsoft\Office\16.0\Outlook\Preferences"; Name = "UseNewOutlook"; Value = 0; Type = "DWord" }
)

if ($RegistryRoot) {
    foreach ($target in $Targets) { $target.Path = "HKCU:\$RegistryRoot\" + ($target.Path -replace '^(HK..):', '$1') }
}

# One marker per value created by Apply, in the hive of the value.
function Get-MarkerKey($target) {
    if ($RegistryRoot) { "HKCU:\$RegistryRoot\Markers" }
    elseif ($target.Path.StartsWith("HKLM:")) { "HKLM:\Software\OnlyWorkspace\NewOutlookBlock" }
    else { "HKCU:\Software\OnlyWorkspace\NewOutlookBlock" }
}
function Get-MarkerName($target) { ($target.Path -replace '^HK(LM|CU):\\', '') + '\' + $target.Name }

function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]$identity
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

switch ($Action) {
    "Check" {
        $allConfigured = $true
        foreach ($target in $Targets) {
            $currentVal = $null
            if (Test-Path $target.Path) {
                $prop = Get-ItemProperty -Path $target.Path -Name $target.Name -ErrorAction SilentlyContinue
                if ($null -ne $prop) {
                    $currentVal = $prop.$($target.Name)
                }
            }
            $status = if ($currentVal -eq $target.Value) { "OK (Match)" } else { "NOT SET ($currentVal)" }
            if ($currentVal -ne $target.Value) { $allConfigured = $false }
            Write-Host "[$status] $($target.Path)\$($target.Name) = $($target.Value)"
        }
        if ($allConfigured) {
            Write-Host "All New Outlook block policies are active." -ForegroundColor Green
            exit 0
        } else {
            Write-Host "One or more policies are not configured." -ForegroundColor Yellow
            exit 1
        }
    }

    "Apply" {
        $isElevated = Test-IsElevated
        foreach ($target in $Targets) {
            if ($target.Path.StartsWith("HKLM:") -and -not $isElevated) {
                Write-Warning "Skipping $($target.Path): administrator rights required."
                continue
            }
            if (-not (Test-Path $target.Path)) {
                New-Item -Path $target.Path -Force | Out-Null
            }
            if ($null -eq (Get-ItemProperty -Path $target.Path -Name $target.Name -ErrorAction SilentlyContinue)) {
                $marker = Get-MarkerKey $target
                if (-not (Test-Path $marker)) { New-Item -Path $marker -Force | Out-Null }
                Set-ItemProperty -Path $marker -Name (Get-MarkerName $target) -Value 1 -Type DWord -Force
            }
            Set-ItemProperty -Path $target.Path -Name $target.Name -Value $target.Value -Type $target.Type -Force
            Write-Host "Applied: $($target.Path)\$($target.Name) = $($target.Value)" -ForegroundColor Green
        }
    }

    "Revert" {
        $isElevated = Test-IsElevated
        foreach ($target in $Targets) {
            if ($target.Path.StartsWith("HKLM:") -and -not $isElevated) {
                Write-Warning "Skipping $($target.Path): administrator rights required."
                continue
            }
            $marker = Get-MarkerKey $target
            $markerName = Get-MarkerName $target
            if ($null -eq (Get-ItemProperty -Path $marker -Name $markerName -ErrorAction SilentlyContinue)) {
                Write-Host "Kept: $($target.Path)\$($target.Name) (not written by Apply)"
                continue
            }
            if (Test-Path $target.Path) {
                Remove-ItemProperty -Path $target.Path -Name $target.Name -ErrorAction SilentlyContinue
                Write-Host "Removed: $($target.Path)\$($target.Name)" -ForegroundColor Green
            }
            Remove-ItemProperty -Path $marker -Name $markerName -ErrorAction SilentlyContinue
        }
    }
}
