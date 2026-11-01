# Isolated fixtures and command doubles; no real signing, build, or certificate-store changes.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scratchRoot = Join-Path ([IO.Path]::GetTempPath()) ('OnlyWorkspace-packaging-' + [guid]::NewGuid())
$savedPassword = $env:ONLYAPPS_PFX_PASSWORD
$savedExitCode = $global:LASTEXITCODE
$env:ONLYAPPS_PFX_PASSWORD = 'test-placeholder'
$rsa = [Security.Cryptography.RSA]::Create(2048)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=Packaging fixture', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
    [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
$global:packagingfixtureThumbprint = $certificate.Thumbprint

try {
    New-Item -ItemType Directory -Path $scratchRoot | Out-Null
    foreach ($global:packagingscenario in @('build', 'tests', 'certificate', 'binary', 'binary-signature', 'wrong-signer', 'nsis', 'setup', 'setup-signature', 'success')) {
        & {
            $global:packagingfixture = Join-Path $scratchRoot $global:packagingscenario
            foreach ($folder in @('scripts', 'certs', 'installer', 'src\OnlyWorkspace.Outlook\bin\Release\net48', 'src\OnlyWorkspace.ConfigTool\bin\Release\net48', 'src\OnlyWorkspace.AttachmentExtractor\bin\Release\net48')) {
                New-Item -ItemType Directory -Path (Join-Path $global:packagingfixture $folder) -Force | Out-Null
            }
            Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\build-installer.ps1') -Destination (Join-Path $global:packagingfixture 'scripts')
            [IO.File]::WriteAllText((Join-Path $global:packagingfixture 'Directory.Build.props'), '<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>')
            foreach ($file in @('src\OnlyWorkspace.Outlook\bin\Release\net48\OnlyWorkspace.Outlook.dll', 'src\OnlyWorkspace.Outlook\bin\Release\net48\fixture.pdb', 'src\OnlyWorkspace.ConfigTool\bin\Release\net48\OnlyWorkspace.ConfigTool.exe', 'src\OnlyWorkspace.AttachmentExtractor\bin\Release\net48\OnlyWorkspace.AttachmentExtractor.exe')) {
                if ($file.EndsWith('.pdb')) { [IO.File]::WriteAllText((Join-Path $global:packagingfixture $file), 'fixture') }
                else { Copy-Item -LiteralPath (Join-Path $repoRoot 'src\OnlyWorkspace.Core\bin\Release\net48\OnlyWorkspace.Core.dll') -Destination (Join-Path $global:packagingfixture $file) }
            }
            [IO.File]::WriteAllText((Join-Path $global:packagingfixture 'src\OnlyWorkspace.Outlook\bin\Release\net48\Microsoft.Office.Interop.Outlook.dll'), 'stale PIA')
            if ($global:packagingscenario -ne 'certificate') {
                [IO.File]::WriteAllBytes((Join-Path $global:packagingfixture 'certs\OnlyApps.cer'), $certificate.RawData)
                [IO.File]::WriteAllText((Join-Path $global:packagingfixture 'certs\OnlyApps.pfx'), 'fixture')
            }
            $global:packagingcalls = [Collections.Generic.List[string]]::new()
            $global:packagingmessages = [Collections.Generic.List[string]]::new()
            function Write-Host { param($Object, $ForegroundColor) $global:packagingmessages.Add([string]$Object) }
            function Test-Path {
                param($Path, $LiteralPath, $PathType)
                if ($Path -eq 'C:\Program Files (x86)\NSIS\makensis.exe') { return $true }
                if ($LiteralPath) { Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath }
                else { Microsoft.PowerShell.Management\Test-Path -Path $Path }
            }
            function dotnet {
                $stage = if ($args[0] -eq 'build') { 'build' } else { 'tests' }
                $global:packagingcalls.Add($stage)
                $global:LASTEXITCODE = if ($global:packagingscenario -eq $stage) { 23 } else { 0 }
            }
            function pwsh {
                param($ExecutionPolicy, $File, $Files)
                $stage = if ($File.EndsWith('create-code-signing-cert.ps1')) { 'certificate' }
                    elseif ([string]$Files -like '*OnlyWorkspaceSetup.exe') { 'setup' } else { 'binary' }
                $global:packagingcalls.Add($stage)
                $global:LASTEXITCODE = if ($global:packagingscenario -eq $stage) { 23 } else { 0 }
            }
            function Get-AuthenticodeSignature {
                param($LiteralPath)
                $stage = if ($LiteralPath.EndsWith('OnlyWorkspaceSetup.exe')) { 'setup-signature' } else { 'binary-signature' }
                $global:packagingcalls.Add($stage)
                [pscustomobject]@{
                    Status = if ($global:packagingscenario -eq $stage) { 'NotSigned' } else { 'Valid' }
                    SignerCertificate = [pscustomobject]@{ Thumbprint = if ($global:packagingscenario -eq 'wrong-signer') { 'OTHER' } else { $global:packagingfixtureThumbprint } }
                }
            }
            Set-Item -Path 'Function:local:C:\Program Files (x86)\NSIS\makensis.exe' -Value {
                $global:packagingcalls.Add('nsis')
                $global:LASTEXITCODE = if ($global:packagingscenario -eq 'nsis') { 23 } else { 0 }
                [IO.File]::WriteAllText((Join-Path $global:packagingfixture 'dist\OnlyWorkspaceSetup.exe'), 'fixture')
            }
            $failure = $null
            try { & (Join-Path $global:packagingfixture 'scripts\build-installer.ps1') }
            catch { $failure = $_ }
            if ($global:packagingscenario -eq 'success') {
                if ($failure -or $global:packagingcalls[-1] -ne 'setup-signature') { throw "Success path failed: $failure" }
                if (Test-Path -LiteralPath (Join-Path $global:packagingfixture 'publish\Microsoft.Office.Interop.Outlook.dll')) { throw 'Stale PIA packaged despite No-PIA verification' }
            }
            else {
                # A missing certificate pair stops the build before signing, without generating a new identity.
                $expected = if ($global:packagingscenario -eq 'wrong-signer') { 'binary-signature' } elseif ($global:packagingscenario -eq 'certificate') { 'tests' } else { $global:packagingscenario }
                if (!$failure -or $global:packagingcalls[-1] -ne $expected) { throw "Failure did not stop at ${expected}: $failure; calls=$global:packagingcalls" }
                if ($global:packagingscenario -eq 'certificate' -and ($global:packagingcalls -contains 'certificate' -or $failure.ToString() -notlike '*certs\OnlyApps.cer*')) { throw "Missing certificate handled wrongly: $failure; calls=$global:packagingcalls" }
                if ($global:packagingmessages -match 'completato con successo') { throw 'Pipeline reported success after failure.' }
            }
        }
        Write-Output "PASS pipeline: $global:packagingscenario"
    }

    # Run the real signing script with a fake SDK tool and isolated input files.
    foreach ($global:packagingscenario in @('missing', 'sign', 'timestamp', 'timestamp-allowed', 'verify', 'success')) {
        & {
            $global:packagingcalls = [Collections.Generic.List[string]]::new()
            $inputPath = Join-Path $scratchRoot 'signing-input.exe'
            [IO.File]::WriteAllText($inputPath, 'fixture')
            if ($global:packagingscenario -eq 'missing') { $inputPath = Join-Path $scratchRoot 'absent.exe' }
            $pfxPath = Join-Path $scratchRoot 'fixture.pfx'
            [IO.File]::WriteAllText($pfxPath, 'fixture')
            function Test-Path {
                param($Path, $LiteralPath, $PathType)
                if ($Path -like '*\signtool.exe') { return $true }
                if ($LiteralPath) { Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath }
                else { Microsoft.PowerShell.Management\Test-Path -Path $Path }
            }
            function Write-Host { }
            function Write-Warning { }
            Set-Item -Path 'Function:local:C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' -Value {
                $global:packagingcalls.Add($args[0])
                $timestampFails = $global:packagingscenario -like 'timestamp*' -and $args -contains '/tr'
                $global:LASTEXITCODE = if ($global:packagingscenario -eq $args[0] -or $timestampFails) { 23 } else { 0 }
                if ($global:packagingscenario -eq 'verify' -and $args[0] -eq 'verify') { 'Fixture: untrusted root' }
            }
            $failure = $null
            $allow = @{ AllowNoTimestamp = $global:packagingscenario -eq 'timestamp-allowed' }
            try { & (Join-Path $repoRoot 'scripts\sign-binaries.ps1') -Files $inputPath -PfxPath $pfxPath @allow }
            catch { $failure = $_ }
            if (($global:packagingscenario -in @('success', 'timestamp-allowed')) -eq [bool]$failure) { throw "Unexpected signing outcome: $global:packagingscenario; $failure" }
            if ($global:packagingscenario -eq 'missing' -and $global:packagingcalls.Count -ne 0) { throw 'Missing file reached signtool.' }
            if ($global:packagingscenario -in @('sign', 'timestamp') -and ($global:packagingcalls -join ',') -ne 'sign') { throw "A signature without timestamp was attempted or verified: $($global:packagingcalls -join ',') $failure" }
            if ($global:packagingscenario -eq 'timestamp-allowed' -and ($global:packagingcalls -join ',') -ne 'sign,sign,verify') { throw 'The explicit option did not sign without timestamp.' }
            if ($global:packagingscenario -in @('verify', 'success') -and ($global:packagingcalls -join ',') -ne 'sign,verify') { throw 'Signature verification was not executed.' }
            if ($global:packagingscenario -eq 'verify' -and $failure.ToString() -notlike '*Fixture: untrusted root*') { throw 'Verification diagnostics were hidden.' }
        }
        Write-Output "PASS signing: $global:packagingscenario"
    }

    # Compile the production uninstaller hook with an actual failing child process.
    $nsis = 'C:\Program Files (x86)\NSIS\makensis.exe'
    $hook = Get-Content (Join-Path $repoRoot 'installer\OnlyWorkspaceSetup.nsi') | Where-Object { $_ -like '!uninstfinalize *' }
    $nsisFixture = Join-Path $scratchRoot 'nsis'
    New-Item -ItemType Directory -Path (Join-Path $nsisFixture 'scripts') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $nsisFixture 'installer') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $nsisFixture 'scripts\sign-binaries.ps1'), 'exit 23')
    $nsiPath = Join-Path $nsisFixture 'installer\fixture.nsi'
    [IO.File]::WriteAllText($nsiPath, "Name 'Fixture'`nOutFile '..\fixture.exe'`n$hook`nSection`nWriteUninstaller '`$TEMP\fixture-uninstall.exe'`nSectionEnd`nSection 'Uninstall'`nSectionEnd`n")
    & $nsis /V2 $nsiPath 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'NSIS ignored the failed uninstaller signing process.' }
    Write-Output 'PASS NSIS: failed uninstaller signature stops compilation'

    # Every critical setup command is checked, and WebView2 counts only through the documented pv keys.
    $setupLines = @(Get-Content (Join-Path $repoRoot 'installer\OnlyWorkspaceSetup.nsi'))
    if ($setupLines -match 'EdgeWebView\\Application') { throw 'The WebView2 folder alone still counts as an installed runtime.' }
    $criticalCommands = 0
    for ($i = 0; $i -lt $setupLines.Count; $i++) {
        if ($setupLines[$i] -notmatch "nsExec::ExecToLog '.*(certutil\.exe -addstore|regasm\.exe`" /codebase|icacls\.exe)") { continue }
        $criticalCommands++
        if ($setupLines[$i + 1] -notmatch '^\s*!insertmacro CheckExecResult ') { throw "Unchecked setup command: $($setupLines[$i].Trim())" }
    }
    if ($criticalCommands -ne 6) { throw "Expected 6 critical setup commands, found $criticalCommands." }
    # Uninstall: every command result is read and logged; the 64-bit regasm runs only on a 64-bit system.
    $uninstall = [array]::FindIndex([string[]]$setupLines, [Predicate[string]] { param($line) $line -like 'Section "Uninstall"*' })
    $uninstallCommands = 0
    for ($i = $uninstall; $i -lt $setupLines.Count -and $setupLines[$i] -notlike 'SectionEnd*'; $i++) {
        if ($setupLines[$i] -notmatch "nsExec::ExecToLog") { continue }
        $uninstallCommands++
        if ($setupLines[$i + 1] -notmatch '^\s*!insertmacro LogExecResult ') { throw "Unlogged uninstall command: $($setupLines[$i].Trim())" }
        if ($setupLines[$i] -match 'Framework64' -and $setupLines[$i - 1] -notmatch '\$\{If\} \$\{RunningX64\}') { throw 'The 64-bit regasm runs on 32-bit systems too.' }
    }
    if ($uninstallCommands -ne 4) { throw "Expected 4 uninstall commands, found $uninstallCommands." }
    $bootstrapper = [array]::FindIndex([string[]]$setupLines, [Predicate[string]] { param($line) $line -like '*MicrosoftEdgeWebview2Setup.exe" /silent /install*' })
    if ($bootstrapper -lt 0 -or -not ($setupLines[($bootstrapper + 1)..($bootstrapper + 4)] -match 'Call WebView2RuntimeInstalled')) { throw 'The WebView2 bootstrapper result is not followed by a new runtime check.' }
    Write-Output 'PASS NSIS: critical setup commands are checked'

    # Shared setup checks (installer\SetupChecks.nsh) in a user-level silent fixture with synthetic data only.
    $checksFixture = Join-Path $scratchRoot 'setupchecks'
    New-Item -ItemType Directory -Path $checksFixture -Force | Out-Null
    $checksNsi = Join-Path $checksFixture 'setupchecks.nsi'
    [IO.File]::WriteAllText($checksNsi, @'
Unicode True
RequestExecutionLevel user
SilentInstall silent
Name "Setup checks fixture"
OutFile "setupchecks.exe"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!include "@CHECKS@"
Var Out
Var Case
Var Arg
!macro Record TEXT
    FileOpen $9 $Out a
    FileSeek $9 0 END
    FileWrite $9 "${TEXT}$\r$\n"
    FileClose $9
!macroend
Function FixtureCleanup
    !insertmacro Record "cleanup"
FunctionEnd
Section
    ${GetParameters} $R0
    ${GetOptions} $R0 "/OUT=" $Out
    ${GetOptions} $R0 "/CASE=" $Case
    ${GetOptions} $R0 "/ARG=" $Arg
    ${If} $Case == "version"
        Push $Arg
        Call WebView2VersionUsable
        Pop $1
        !insertmacro Record "version=$1"
    ${ElseIf} $Case == "exec"
        ${If} $Arg == "missing"
            nsExec::ExecToLog '"$EXEDIR\missing-command.exe"'
            !insertmacro CheckExecResult "Fixture" "" ""
        ${Else}
            ${If} $Arg == "ok"
                nsExec::ExecToLog '"$SYSDIR\cmd.exe" /c exit 0'
            ${ElseIf} $Arg == "fail"
                nsExec::ExecToLog '"$SYSDIR\cmd.exe" /c exit 5'
            ${Else}
                nsExec::ExecToLog /TIMEOUT=300 '"$SYSDIR\cmd.exe" /c ping -n 4 127.0.0.1 >nul'
            ${EndIf}
            !insertmacro CheckExecResult "Fixture" "" FixtureCleanup
        ${EndIf}
        !insertmacro Record "reached"
    ${ElseIf} $Case == "purge"
        !insertmacro PurgeRequested $R0 $1
        !insertmacro Record "purge=$1"
        !insertmacro PurgeUserData $1 "$Arg\pd" "$Arg\lad"
        !insertmacro Record "done"
    ${ElseIf} $Case == "logexec"
        Push "sentinel"
        ${If} $Arg == "missing"
            nsExec::ExecToLog '"$EXEDIR\missing-command.exe"'
        ${Else}
            nsExec::ExecToLog '"$SYSDIR\cmd.exe" /c exit 5'
        ${EndIf}
        !insertmacro LogExecResult "Fixture"
        Pop $1
        !insertmacro Record "stack=$1"
    ${ElseIf} $Case == "resiliency"
        !insertmacro RemoveAddinResiliency $Arg
        !insertmacro Record "done"
    ${EndIf}
SectionEnd
'@.Replace('@CHECKS@', (Join-Path $repoRoot 'installer\SetupChecks.nsh')))
    & $nsis /V2 $checksNsi 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The setup checks fixture did not compile.' }
    $checksExe = Join-Path $checksFixture 'setupchecks.exe'
    function Invoke-SetupChecks([string]$Arguments) {
        $out = Join-Path $checksFixture ('out-' + [guid]::NewGuid().ToString('N') + '.txt')
        $process = Start-Process -FilePath $checksExe -ArgumentList "$Arguments /OUT=`"$out`"" -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw "Setup checks fixture timed out: $Arguments" }
        [pscustomobject]@{ ExitCode = $process.ExitCode; Lines = @(if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out }) }
    }

    foreach ($case in @(@('', '0'), @('0.0.0.0', '0'), @('abc', '0'), @('.1', '0'), @('1.x.0.0', '0'), @('130.0.2849.80', '1'), @('155.0.4283.62', '1'))) {
        $result = Invoke-SetupChecks "/CASE=version /ARG=$($case[0])"
        if ($result.ExitCode -ne 0 -or ($result.Lines -join ',') -ne "version=$($case[1])") { throw "WebView2 pv '$($case[0])': exit $($result.ExitCode), $($result.Lines -join ',')" }
    }
    Write-Output 'PASS setup checks: WebView2 pv validation'

    foreach ($case in @(@('ok', 0, 'reached'), @('fail', 2, 'cleanup'), @('missing', 2, ''), @('timeout', 2, 'cleanup'))) {
        $result = Invoke-SetupChecks "/CASE=exec /ARG=$($case[0])"
        if ($result.ExitCode -ne $case[1] -or ($result.Lines -join ',') -ne $case[2]) { throw "Command '$($case[0])': exit $($result.ExitCode), $($result.Lines -join ',')" }
    }
    Write-Output 'PASS setup checks: failed, missing and timed-out commands stop the setup'

    foreach ($case in @('fail', 'missing')) {
        $result = Invoke-SetupChecks "/CASE=logexec /ARG=$case"
        if ($result.ExitCode -ne 0 -or ($result.Lines -join ',') -ne 'stack=sentinel') { throw "Uninstall command '$case': exit $($result.ExitCode), $($result.Lines -join ',')" }
    }
    Write-Output 'PASS setup checks: a failed uninstall command is logged and leaves the stack clean'

    $purgeFiles = @('pd\OnlyWorkspace\config.json', 'pd\OnlyWorkspace\secrets.bin', 'pd\Other\keep.txt', 'lad\OnlyWorkspace\archive\Outlook.engine.db', 'lad\OnlyWorkspace\config-non-leggibile\secrets.bin', 'lad\OnlyWorkspace\logs\addin.log', 'lad\Microsoft\Outlook\mail.ost', 'otheruser\OnlyWorkspace\archive\Outlook.engine.db', 'outside\OnlyWorkspace\config.json')
    $purgedFiles = @('pd\OnlyWorkspace\config.json', 'pd\OnlyWorkspace\secrets.bin', 'lad\OnlyWorkspace\archive\Outlook.engine.db', 'lad\OnlyWorkspace\config-non-leggibile\secrets.bin')
    foreach ($case in @(@('/S', '0'), @('/S /PURGE', '1'), @('/PURGE /S', '1'), @('/S /PURGE=1', '0'), @('/S /PURGEALL', '0'), @('/CONFIG=x.json /PURGE /S', '1'))) {
        $data = Join-Path $checksFixture ('data-' + [guid]::NewGuid().ToString('N'))
        foreach ($file in $purgeFiles) {
            New-Item -ItemType Directory -Path (Split-Path (Join-Path $data $file)) -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $data $file), 'synthetic')
        }
        $result = Invoke-SetupChecks "$($case[0]) /CASE=purge /ARG=$data"
        if ($result.ExitCode -ne 0 -or ($result.Lines -join ',') -ne "purge=$($case[1]),done") { throw "Purge '$($case[0])': exit $($result.ExitCode), $($result.Lines -join ',')" }
        foreach ($file in $purgeFiles) {
            $expectedGone = $case[1] -eq '1' -and $purgedFiles -contains $file
            if ((Test-Path -LiteralPath (Join-Path $data $file)) -eq $expectedGone) { throw "Purge '$($case[0])': unexpected state of $file" }
        }
        if ($case[1] -eq '1' -and (Test-Path -LiteralPath (Join-Path $data 'pd\OnlyWorkspace'))) { throw "Purge '$($case[0])': ProgramData folder kept" }
    }
    Write-Output 'PASS setup checks: /PURGE removes only the documented data'

    # A126: uninstall removes only our DoNotDisableAddinList value, on a synthetic HKCU key.
    $resiliency = 'Software\OnlyWorkspace-PackagingTest-' + [guid]::NewGuid().ToString('N')
    $keep = New-Item -Path "HKCU:\$resiliency\DoNotDisableAddinList" -Force
    try {
        New-ItemProperty -Path $keep.PSPath -Name 'OnlyWorkspace.Addin' -Value 1 -PropertyType DWord | Out-Null
        New-ItemProperty -Path $keep.PSPath -Name 'Other.Addin' -Value 1 -PropertyType DWord | Out-Null
        $result = Invoke-SetupChecks "/CASE=resiliency /ARG=$resiliency"
        $names = @((Get-Item -Path $keep.PSPath).Property)
        if ($result.ExitCode -ne 0 -or ($names -join ',') -ne 'Other.Addin') { throw "Resiliency entry: exit $($result.ExitCode), values $($names -join ',')" }
    }
    finally { Remove-Item -Path "HKCU:\$resiliency" -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Output 'PASS setup checks: uninstall removes only the add-in resiliency entry'

    # block-new-outlook.ps1 on a synthetic HKCU root: Revert removes only the values Apply created.
    $policyRoot = 'Software\OnlyWorkspace-PolicyTest-' + [guid]::NewGuid().ToString('N')
    $policyScript = Join-Path $repoRoot 'scripts\block-new-outlook.ps1'
    $preexisting = "HKCU:\$policyRoot\HKCU\Software\Microsoft\Office\16.0\Outlook\Preferences"
    try {
        New-Item -Path $preexisting -Force | Out-Null
        New-ItemProperty -Path $preexisting -Name 'UseNewOutlook' -Value 0 -PropertyType DWord | Out-Null
        & pwsh -NoProfile -File $policyScript -Action Apply -RegistryRoot $policyRoot | Out-Null
        & pwsh -NoProfile -File $policyScript -Action Check -RegistryRoot $policyRoot | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Policy check after Apply: exit $LASTEXITCODE" }
        & pwsh -NoProfile -File $policyScript -Action Revert -RegistryRoot $policyRoot | Out-Null
        $left = @(Get-ChildItem -Path "HKCU:\$policyRoot" -Recurse | ForEach-Object { foreach ($name in $_.Property) { $_.PSPath.Split('::')[-1].Replace("HKEY_CURRENT_USER\$policyRoot\", '') + '\' + $name } })
        if (($left -join ',') -ne 'HKCU\Software\Microsoft\Office\16.0\Outlook\Preferences\UseNewOutlook') { throw "Policy values after Revert: $($left -join ', ')" }
    }
    finally { Remove-Item -Path "HKCU:\$policyRoot" -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Output 'PASS policy script: Revert removes only the values Apply created'
}
finally {
    $certificate.Dispose()
    $rsa.Dispose()
    $env:ONLYAPPS_PFX_PASSWORD = $savedPassword
    $global:LASTEXITCODE = $savedExitCode
    $resolvedScratch = [IO.Path]::GetFullPath($scratchRoot)
    if ($resolvedScratch.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolvedScratch).StartsWith('OnlyWorkspace-packaging-')) {
        if (!(Test-Path Env:ONLYWORKSPACE_TEST_ARTIFACTS)) { Remove-Item -LiteralPath $resolvedScratch -Recurse -Force }
    }
}
