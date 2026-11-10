# Generate runsettings with XML escaping; the variables affect only the VSTest child process.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SettingsPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{12}$')][string]$CheckoutKey
)
$ErrorActionPreference = 'Stop'
$central = Split-Path $env:ONLYWORKSPACE_RUNTIME_ROOT -Parent
$key = $CheckoutKey.Substring(0, 8)
$day = Get-Date -Format yyyyMMdd
# Keep net48/NSIS temporary paths short; every fixture below these roots has a unique name.
$temp = Join-Path $central "tmp-$day-$key"
$fixtures = Join-Path $central "fixture-$day-$key"
foreach ($directory in @($temp, $fixtures, (Split-Path $SettingsPath -Parent))) {
    [void][IO.Directory]::CreateDirectory($directory)
}
$xml = [xml]'<RunSettings><RunConfiguration><EnvironmentVariables /></RunConfiguration></RunSettings>'
foreach ($entry in @{ TEMP = $temp; TMP = $temp; ONLYWORKSPACE_TEST_ARTIFACTS = $fixtures }.GetEnumerator()) {
    $node = $xml.CreateElement($entry.Key)
    $node.InnerText = $entry.Value
    [void]$xml.SelectSingleNode('//EnvironmentVariables').AppendChild($node)
}
$xml.Save($SettingsPath)
