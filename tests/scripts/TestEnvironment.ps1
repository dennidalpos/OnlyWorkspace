# Test-only environment, dot-sourced by the harnesses in this folder. Build paths come from scripts/build-paths.ps1.
. (Join-Path $PSScriptRoot '../../scripts/build-paths.ps1')

function Set-OnlyWorkspaceVerificationEnvironment {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
    if (!$env:ONLYWORKSPACE_RUNTIME_ROOT) { return }
    $paths = Get-OnlyWorkspaceBuildPaths -RepositoryRoot $RepositoryRoot
    $settings = Join-Path $paths.ArtifactsPath 'script.runsettings'
    & (Join-Path $PSScriptRoot 'New-RunSettings.ps1') -SettingsPath $settings -CheckoutKey $paths.OnlyWorkspaceCheckoutKey
    $xml = [xml](Get-Content -Raw -LiteralPath $settings)
    foreach ($node in $xml.SelectSingleNode('//EnvironmentVariables').ChildNodes) {
        [Environment]::SetEnvironmentVariable($node.Name, $node.InnerText, 'Process')
    }
}
