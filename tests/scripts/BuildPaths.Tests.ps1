# Evaluated SDK paths, two checkouts, spaces, and rejection of stale/invalid output locations.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
. (Join-Path $repo 'scripts/build-paths.ps1')
Set-OnlyWorkspaceVerificationEnvironment -RepositoryRoot $repo
$savedRuntime = $env:ONLYWORKSPACE_RUNTIME_ROOT
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('OnlyWorkspace-BuildPaths-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
try {
    $paths = @()
    foreach ($name in 'checkout one', 'checkout two') {
        $checkout = Join-Path $fixture $name
        $project = Join-Path $checkout 'src/OnlyWorkspace.Outlook'
        [void][IO.Directory]::CreateDirectory($project)
        Copy-Item -LiteralPath (Join-Path $repo 'Directory.Build.props') -Destination $checkout
        Copy-Item -LiteralPath (Join-Path $repo 'Directory.Build.targets') -Destination $checkout
        [IO.File]::WriteAllText((Join-Path $project 'OnlyWorkspace.Outlook.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>')
        $env:ONLYWORKSPACE_RUNTIME_ROOT = Join-Path $fixture 'central output'
        [void][IO.Directory]::CreateDirectory($env:ONLYWORKSPACE_RUNTIME_ROOT)
        $central = Get-OnlyWorkspaceBuildPaths -RepositoryRoot $checkout
        if (!$central.TargetPath.StartsWith($env:ONLYWORKSPACE_RUNTIME_ROOT, [StringComparison]::OrdinalIgnoreCase)) { throw 'Central output not selected.' }
        $paths += $central.TargetPath
        # Even a conventional stale binary must not satisfy a missing central output.
        $stale = Join-Path $project 'bin/Release/net48'
        [void][IO.Directory]::CreateDirectory($stale)
        [IO.File]::WriteAllText((Join-Path $stale 'OnlyWorkspace.Outlook.dll'), 'stale')
        $rejected = $false
        try { $null = Get-OnlyWorkspaceOutputDirectory -RepositoryRoot $checkout } catch { $rejected = $true }
        if (!$rejected) { throw 'Stale binary accepted.' }
        $env:ONLYWORKSPACE_RUNTIME_ROOT = $null
        $ordinary = Get-OnlyWorkspaceBuildPaths -RepositoryRoot $checkout
        if ($ordinary.TargetPath -ne (Join-Path $stale 'OnlyWorkspace.Outlook.dll')) { throw 'Ordinary SDK layout changed.' }
        if ((Get-OnlyWorkspaceOutputDirectory -RepositoryRoot $checkout) -ne $stale) { throw 'Output directory is unsafe to quote in a native command line.' }
        $notDirectory = Join-Path $fixture 'runtime-file'
        [IO.File]::WriteAllText($notDirectory, 'not a directory')
        foreach ($invalid in @('relative-output', (Join-Path $fixture 'missing-output'), $notDirectory)) {
            $env:ONLYWORKSPACE_RUNTIME_ROOT = $invalid
            $rejected = $false
            try { $null = Get-OnlyWorkspaceBuildPaths -RepositoryRoot $checkout } catch { $rejected = $true }
            if (!$rejected) { throw 'Invalid runtime root accepted by script.' }
            $validation = & dotnet msbuild (Join-Path $project 'OnlyWorkspace.Outlook.csproj') -nologo -t:ValidateOnlyWorkspaceRuntime 2>&1
            $validationCode = $LASTEXITCODE
            $validation | Set-Content -LiteralPath (Join-Path $checkout ('invalid-' + [IO.Path]::GetFileName($invalid) + '.log'))
            if ($validationCode -eq 0) { throw 'Invalid runtime root accepted by MSBuild.' }
        }
    }
    if ($paths[0] -eq $paths[1]) { throw 'Checkouts share their output.' }
    $env:ONLYWORKSPACE_RUNTIME_ROOT = $savedRuntime
    if ($savedRuntime) {
        $override = Join-Path $fixture 'explicit artifacts'
        $settings = & dotnet msbuild (Join-Path $repo 'tests/OnlyWorkspace.Tests/OnlyWorkspace.Tests.csproj') -nologo -verbosity:quiet -p:Configuration=Release -p:TargetFramework=net48 "-p:ArtifactsPath=$override" -t:PrepareTestEnvironment -getProperty:RunSettingsFilePath
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $settings)) { throw 'Runsettings failed with explicit ArtifactsPath.' }
        $xml = [xml](Get-Content -Raw -LiteralPath $settings)
        if (!$xml.SelectSingleNode('//TEMP').InnerText.StartsWith((Split-Path $savedRuntime -Parent), [StringComparison]::OrdinalIgnoreCase)) { throw 'Testhost TEMP escaped the central runtime.' }
    }
    $missingExtractor = Join-Path $fixture 'unbuilt graph'
    $validation = & dotnet msbuild (Join-Path $repo 'tests/OnlyWorkspace.Tests/OnlyWorkspace.Tests.csproj') -nologo -p:Configuration=Release -p:TargetFramework=net48 "-p:ArtifactsPath=$missingExtractor" -t:CopyExtractor 2>&1
    $validationCode = $LASTEXITCODE
    $validation | Set-Content -LiteralPath (Join-Path $fixture 'missing-extractor.log')
    if ($validationCode -eq 0 -or (Test-Path -LiteralPath (Join-Path $missingExtractor 'bin/OnlyWorkspace.Tests/release_net48/extractor'))) { throw 'Missing extractor did not stop copying.' }
    $helper = Get-OnlyWorkspaceBuildPaths -RepositoryRoot $repo -Project OnlyWorkspace.AttachmentExtractor
    if (Test-Path -LiteralPath $helper.TargetPath) {
        $foreign = Join-Path $fixture 'foreign candidate'
        [void][IO.Directory]::CreateDirectory($foreign)
        $before = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'OnlyWorkspace-PanePolicy-*' | Select-Object -ExpandProperty Name)
        $validation = & (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'PanePolicy.Tests.ps1') -Dir $foreign 2>&1
        $validationCode = $LASTEXITCODE
        $validation | Set-Content -LiteralPath (Join-Path $fixture 'foreign-candidate.log')
        $created = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'OnlyWorkspace-PanePolicy-*' | Where-Object { $_.Name -notin $before })
        if ($validationCode -eq 0 -or $created.Count) { throw 'An incomplete external candidate borrowed current-build components.' }
    }
    'OK: central/ordinary layout, spaces, checkout isolation, invalid roots, stale output rejection, missing-extractor guard.'
} finally {
    $env:ONLYWORKSPACE_RUNTIME_ROOT = $savedRuntime
    # Retain the fixture and both invalid-input command results can be reproduced from it.
}
