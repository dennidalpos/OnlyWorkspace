<#
.SYNOPSIS
Measures literal local facts on the retained synthetic corpus, without providers or configuration.
#>
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$CorpusPath = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (!$CorpusPath) { $CorpusPath = Join-Path $repo 'tests\fixtures\fidelity\corpus.json' }
$framework = if ($PSVersionTable.PSVersion.Major -lt 7) { 'net48' } else { 'net10.0-windows' }
$bin = Join-Path $repo "tests\OnlyWorkspace.Tests\bin\Release\$framework"
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
$archivedCorpus = Join-Path $output 'corpus.json'
if (Test-Path -LiteralPath $archivedCorpus) {
    if ((Get-FileHash -LiteralPath $archivedCorpus).Hash -ne (Get-FileHash -LiteralPath $CorpusPath).Hash) {
        throw 'Output directory belongs to different corpus bytes.'
    }
} else { Copy-Item -LiteralPath $CorpusPath -Destination $archivedCorpus }
$runtime = Join-Path $output ('runtime-' + $framework)
[void][IO.Directory]::CreateDirectory($runtime)
Get-ChildItem -LiteralPath $bin -File -Filter '*.dll' | Copy-Item -Destination $runtime -Force
foreach ($dll in 'Serilog.dll', 'OnlyWorkspace.Core.dll', 'OnlyWorkspace.Infrastructure.dll', 'OnlyWorkspace.Tests.dll') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $runtime $dll))
}
[OnlyWorkspace.Tests.Fidelity.FidelityBench]::WriteOffline($archivedCorpus, $output)
$assemblies = @('OnlyWorkspace.Core.dll', 'OnlyWorkspace.Infrastructure.dll', 'OnlyWorkspace.Tests.dll') | ForEach-Object {
    $path = Join-Path $runtime $_
    [pscustomobject]@{ Path = $path; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
[ordered]@{ Mode = 'Offline'; Host = $PSVersionTable.PSVersion.ToString(); CorpusSha256 = (Get-FileHash -LiteralPath $archivedCorpus -Algorithm SHA256).Hash; Assemblies = $assemblies } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'manifest-offline.json') -Encoding UTF8
Write-Output "Local facts measurement complete: $output"
