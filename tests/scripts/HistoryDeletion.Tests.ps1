# Cross-process DPAPI history revocations. All files are synthetic and retained in managed verification.
param([string]$Dir = '', [switch]$Writer, [string]$Fixture, [string]$Signal)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/build-paths.ps1')
if (!$Dir) { $Dir = Get-OnlyWorkspaceOutputDirectory -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path }
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Core.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Infrastructure.dll'))

if ($Writer) {
    $ready = [Threading.EventWaitHandle]::OpenExisting($Signal + '-ready')
    $release = [Threading.EventWaitHandle]::OpenExisting($Signal + '-release')
    try {
        $storage = New-Object OnlyWorkspace.Infrastructure.Security.DpapiHistoryStorageService($Fixture)
        $pending = New-Object 'Threading.Tasks.TaskCompletionSource[bool]'
        $field = $storage.GetType().GetField('_saves', [Reflection.BindingFlags]'Instance,NonPublic')
        $field.SetValue($storage, $pending.Task)
        $conversation = New-Object OnlyWorkspace.Core.Models.ConversationHistory -Property @{Id='TARGET';Title='Stale independent process'}
        $save = $storage.SaveConversationAsync($conversation)
        [void]$ready.Set()
        if (!$release.WaitOne(20000)) { throw 'Writer release timed out.' }
        $pending.SetResult($true)
        $rejected = $false
        try { $save.GetAwaiter().GetResult() } catch { $rejected = $_.Exception.ToString().Contains('InvalidOperationException') }
        if (!$rejected) { throw 'Independent writer recreated a revoked conversation.' }
        'PASS independent writer rejected'
    } finally { $ready.Dispose(); $release.Dispose() }
    exit 0
}

Set-OnlyWorkspaceVerificationEnvironment -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('OnlyWorkspace-HistoryDeletion-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
foreach ($all in @($false, $true)) {
    $folder = Join-Path $root ([string]$all)
    $storage = New-Object OnlyWorkspace.Infrastructure.Security.DpapiHistoryStorageService($folder)
    $conversation = New-Object OnlyWorkspace.Core.Models.ConversationHistory -Property @{Id='target';Title='Persisted'}
    [void]$storage.SaveConversationAsync($conversation).GetAwaiter().GetResult()
    $signal = 'Local\OnlyWorkspace-HistoryTest-' + [guid]::NewGuid().ToString('N')
    $ready = New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::ManualReset, ($signal + '-ready'))
    $release = New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::ManualReset, ($signal + '-release'))
    $child = $null
    try {
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath), '-Writer', '-Dir', ('"{0}"' -f $Dir), '-Fixture', ('"{0}"' -f $folder), '-Signal', $signal)
        $child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root "$all-out.log") -RedirectStandardError (Join-Path $root "$all-error.log")
        $null = $child.Handle
        if (!$ready.WaitOne(20000)) { throw 'Independent writer did not queue its save.' }
        if ($all) { [void]$storage.DeleteAllConversationsAsync().GetAwaiter().GetResult() }
        elseif (!$storage.DeleteConversationAsync('target').GetAwaiter().GetResult()) { throw 'Single deletion failed.' }
        [void]$release.Set()
        if (!$child.WaitForExit(20000)) { throw 'Independent writer did not finish.' }
        if ($child.ExitCode -ne 0) { throw "Writer failed: see $root" }
        $reopened = New-Object OnlyWorkspace.Infrastructure.Security.DpapiHistoryStorageService($folder)
        if ($null -ne $reopened.LoadConversationAsync('target').GetAwaiter().GetResult()) { throw 'Deleted identity reopened.' }
        $fresh = New-Object OnlyWorkspace.Core.Models.ConversationHistory -Property @{Id='fresh'}
        [void]$reopened.SaveConversationAsync($fresh).GetAwaiter().GetResult()
        if ($reopened.ListConversationsAsync().GetAwaiter().GetResult().Count -ne 1) { throw 'New conversation was not retained.' }
    } finally {
        [void]$release.Set()
        if ($child) { [void]$child.WaitForExit(22000); $child.Dispose() }
        $ready.Dispose(); $release.Dispose()
    }
}
"PASS history single/all: queued independent process, case-insensitive revocation, reopen and fresh identity ($root)"
