# Shared by pwsh and Windows PowerShell 5.1. Resolve the evaluated graph, never a previous publish.
function Get-OnlyWorkspaceBuildPaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [string]$Project = 'OnlyWorkspace.Outlook',
        [string]$Configuration = 'Release',
        [string]$Framework = 'net48'
    )
    if ($env:ONLYWORKSPACE_RUNTIME_ROOT -and
        (![IO.Path]::IsPathRooted($env:ONLYWORKSPACE_RUNTIME_ROOT) -or
         !(Test-Path -LiteralPath $env:ONLYWORKSPACE_RUNTIME_ROOT -PathType Container))) {
        throw 'ONLYWORKSPACE_RUNTIME_ROOT deve indicare una cartella assoluta esistente.'
    }
    $category = if ($Project -eq 'OnlyWorkspace.Tests') { 'tests' } else { 'src' }
    $projectFile = Join-Path $RepositoryRoot "$category/$Project/$Project.csproj"
    $json = & dotnet msbuild $projectFile -nologo "-p:Configuration=$Configuration" "-p:TargetFramework=$Framework" '-getProperty:TargetPath,TargetDir,ProjectAssetsFile,ArtifactsPath,OnlyWorkspaceCheckoutKey'
    if ($LASTEXITCODE -ne 0) { throw "Impossibile risolvere gli output di $Project." }
    ($json | Out-String | ConvertFrom-Json).Properties
}

function Get-OnlyWorkspaceOutputDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [string]$Project = 'OnlyWorkspace.Outlook',
        [string]$Configuration = 'Release',
        [string]$Framework = 'net48'
    )
    $paths = Get-OnlyWorkspaceBuildPaths @PSBoundParameters
    if (!(Test-Path -LiteralPath $paths.TargetPath -PathType Leaf)) {
        throw "Output mancante: $($paths.TargetPath). Compilare prima il progetto."
    }
    # A trailing backslash escapes the closing quote in a native Windows command line.
    $paths.TargetDir.TrimEnd('\', '/')
}

function Set-OnlyWorkspaceVerificationEnvironment {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
    if (!$env:ONLYWORKSPACE_RUNTIME_ROOT) { return }
    $paths = Get-OnlyWorkspaceBuildPaths -RepositoryRoot $RepositoryRoot
    $settings = Join-Path $paths.ArtifactsPath 'script.runsettings'
    & (Join-Path $PSScriptRoot 'test-environment.ps1') -SettingsPath $settings -CheckoutKey $paths.OnlyWorkspaceCheckoutKey
    $xml = [xml](Get-Content -Raw -LiteralPath $settings)
    foreach ($node in $xml.SelectSingleNode('//EnvironmentVariables').ChildNodes) {
        [Environment]::SetEnvironmentVariable($node.Name, $node.InnerText, 'Process')
    }
}
