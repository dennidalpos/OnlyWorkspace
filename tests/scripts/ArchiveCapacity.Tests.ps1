param([string]$Dir = '', [int]$Emails = 10000)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Run with Windows PowerShell 5.1.' }
if ($Emails -lt 100 -or $Emails -gt 100000 -or $Emails % 100 -ne 0) { throw 'Emails must be a multiple of 100 between 100 and 100000.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot "../../scripts/build-paths.ps1")
Set-OnlyWorkspaceVerificationEnvironment -RepositoryRoot $repo
if (!$Dir) { $Dir = Get-OnlyWorkspaceOutputDirectory -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path }
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Outlook.dll'))
$addin = New-Object OnlyWorkspace.Outlook.OnlyWorkspaceAddin
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Infrastructure.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Core.dll'))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('OnlyWorkspace_Capacity_' + [guid]::NewGuid().ToString('N'))
$db = $null
try {
    $before = [Diagnostics.Process]::GetCurrentProcess().PrivateMemorySize64
    $db = [OnlyWorkspace.Infrastructure.Archive.ArchiveDb]::Open($scratch, 'synthetic-capacity')
    $folder = $db.GetOrAddFolder('synthetic', 'inbox', 'Inbox')
    $timer = [Diagnostics.Stopwatch]::StartNew()
    for ($offset = 0; $offset -lt $Emails; $offset += 100) {
        $rows = New-Object 'Collections.Generic.List[OnlyWorkspace.Core.Archive.ArchiveHeader]'
        for ($i = $offset; $i -lt $offset + 100; $i++) {
            $row = New-Object OnlyWorkspace.Core.Archive.ArchiveHeader
            $row.EntryId = 'synthetic-' + $i
            $row.Subject = 'capacityprobe synthetic invoice ' + $i
            $row.SenderAddress = 'sender@example.com'
            $row.ReceivedUtc = [DateTime]::UtcNow.AddSeconds(-$i)
            $rows.Add($row)
        }
        [void]$db.AddHeaders($folder, $rows, ($offset + 100 -eq $Emails), $true)
    }
    $timer.Stop()
    $writeMs = $timer.ElapsedMilliseconds
    if ($db.GetStats().Emails -ne $Emails) { throw 'Synthetic inventory count differs.' }
    $query = New-Object OnlyWorkspace.Core.Archive.ArchiveQuery
    $query.Text = 'capacityprobe'
    $query.Offset = $Emails - 50
    $query.Max = 50
    $timer.Restart()
    $hits = $db.Search($query)
    $timer.Stop()
    if ($hits.Count -ne 50) { throw 'Synthetic search pagination differs.' }
    $searchMs = $timer.ElapsedMilliseconds
    $dbBytes = $db.FileSizeBytes
    $memoryDelta = [Diagnostics.Process]::GetCurrentProcess().PrivateMemorySize64 - $before
    $backup = Join-Path $scratch 'synthetic-backup.db'
    $timer.Restart()
    $db.Backup($backup)
    $timer.Stop()
    $backupMs = $timer.ElapsedMilliseconds
    $db.Dispose(); $db = $null
    $db = [OnlyWorkspace.Infrastructure.Archive.ArchiveDb]::Open($scratch, 'synthetic-capacity')
    if ($db.GetStats().Emails -ne $Emails -or $db.Search($query).Count -ne 50) { throw 'Inventory or pagination differs after restart.' }
    "PASS synthetic archive: emails=$Emails; batchWriteMs=$writeMs; lastPageSearchMs=$searchMs; backupMs=$backupMs; dbWithWalBytes=$dbBytes; privateMemoryDeltaBytes=$memoryDelta."
    'Synthetic headers only: this does not qualify 80 GB mailboxes, provider latency, Outlook UI or power loss.'
} finally {
    if ($db) { $db.Dispose() }
    $resolved = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('OnlyWorkspace_Capacity_')) { throw 'Unexpected capacity scratch path.' }
    if (!(Test-Path Env:ONLYWORKSPACE_TEST_ARTIFACTS) -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
