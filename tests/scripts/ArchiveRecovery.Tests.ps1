param([string]$Dir = '')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Run with Windows PowerShell 5.1.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot "../../scripts/build-paths.ps1")
Set-OnlyWorkspaceVerificationEnvironment -RepositoryRoot $repo
if (!$Dir) { $Dir = Get-OnlyWorkspaceOutputDirectory -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('OnlyWorkspace_Recovery_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$child = Join-Path $scratch 'writer.ps1'
$fixture = Join-Path $scratch 'archive'
$process = $null
try {
    $source = @'
param([string]$Dir, [string]$Fixture)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Outlook.dll'))
$addin = New-Object OnlyWorkspace.Outlook.OnlyWorkspaceAddin
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Infrastructure.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Core.dll'))
$db = [OnlyWorkspace.Infrastructure.Archive.ArchiveDb]::Open($Fixture, 'recovery')
$db.SetMeta('committed', 'yes')
$key = [OnlyWorkspace.Infrastructure.Archive.ArchiveKey]::LoadOrCreate((Join-Path $Fixture 'archive.key'))
$builder = New-Object Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
$builder.DataSource = $db.FilePath
$builder.Password = $key
$builder.Pooling = $false
$connection = New-Object Microsoft.Data.Sqlite.SqliteConnection($builder.ToString())
$connection.Open()
$transaction = $connection.BeginTransaction()
$command = $connection.CreateCommand()
$command.Transaction = $transaction
$command.CommandText = "insert into meta(key,value) values('uncommitted','must-not-survive')"
[void]$command.ExecuteNonQuery()
[IO.File]::WriteAllText((Join-Path $Fixture 'ready'), 'transaction-open')
while ($true) { Start-Sleep -Seconds 1 }
'@
    [IO.File]::WriteAllText($child, $source, (New-Object Text.UTF8Encoding($false)))
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe')
    $info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $child + '" -Dir "' + $Dir + '" -Fixture "' + $fixture + '"'
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($info)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (!(Test-Path -LiteralPath (Join-Path $fixture 'ready'))) {
        if ($process.HasExited) { throw "Fixture writer exited early ($($process.ExitCode))." }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Fixture writer did not reach the transaction.' }
        Start-Sleep -Milliseconds 50
    }
    # Only this script's synthetic writer is terminated.
    $process.Kill()
    $process.WaitForExit()
    [void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Outlook.dll'))
    $addin = New-Object OnlyWorkspace.Outlook.OnlyWorkspaceAddin
    [void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Infrastructure.dll'))
    $db = [OnlyWorkspace.Infrastructure.Archive.ArchiveDb]::Open($fixture, 'recovery')
    try {
        if ($db.GetMeta('committed') -ne 'yes') { throw 'Committed WAL content was lost.' }
        if ($null -ne $db.GetMeta('uncommitted')) { throw 'Uncommitted transaction survived the process crash.' }
        $db.SetMeta('resumed', 'yes')
    } finally { $db.Dispose() }
    $db = [OnlyWorkspace.Infrastructure.Archive.ArchiveDb]::Open($fixture, 'recovery')
    try { if ($db.GetMeta('resumed') -ne 'yes') { throw 'Archive did not resume writing.' } }
    finally { $db.Dispose() }
    'PASS archive recovery: committed WAL preserved, uncommitted transaction rolled back, owner released, writes resumed.'
} finally {
    if ($process) { if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() }
    $resolved = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('OnlyWorkspace_Recovery_')) { throw 'Unexpected recovery scratch path.' }
    if (!(Test-Path Env:ONLYWORKSPACE_TEST_ARTIFACTS) -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
