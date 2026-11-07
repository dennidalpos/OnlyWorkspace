#requires -Version 5.1
param(
    [Parameter(Mandatory)][ValidateSet('Check', 'Deploy', 'RetireLegacy', 'CheckUninstall', 'Uninstall')][string]$Operation,
    [Parameter(Mandatory)][string]$Destination
)

$ErrorActionPreference = 'Stop'
$registryPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\OnlyWorkspace'
$registryHive = [Microsoft.Win32.RegistryHive]::LocalMachine
$inventoryValue = 'ProgramInventory'
$payloadRoot = Join-Path $PSScriptRoot 'payload'
$backupRoot = Join-Path $PSScriptRoot 'program-backup'
$directoryHandles = [Collections.Generic.Dictionary[string, Microsoft.Win32.SafeHandles.SafeFileHandle]]::new([StringComparer]::OrdinalIgnoreCase)

function Get-PathAttributes([string]$Path) {
    try { [IO.File]::GetAttributes($Path) }
    catch {
        if ($_.Exception.InnerException -isnot [IO.FileNotFoundException] -and
            $_.Exception.InnerException -isnot [IO.DirectoryNotFoundException]) { throw }
    }
}

function Test-ProgramDirectory([string]$Path, [switch]$Create) {
    if ($directoryHandles.ContainsKey($Path)) { return $true }
    $parent = [IO.Path]::GetDirectoryName($Path)
    if ($parent -and !(Test-ProgramDirectory $parent -Create:$Create)) { return $false }
    $attributes = Get-PathAttributes $Path
    if ($null -eq $attributes) {
        if (!$Create) { return $false }
        [void][IO.Directory]::CreateDirectory($Path)
    }
    # Open the entry itself. Keep its name and reparse state stable while accessing its children.
    $handle = [OnlyWorkspace.SetupDirectory]::CreateFile($Path, 0x80, 1, [IntPtr]::Zero, 3, 0x02200000, [IntPtr]::Zero)
    if ($handle.IsInvalid) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $handle.Dispose()
        throw "Impossibile aprire la cartella $Path (Win32 $errorCode)."
    }
    $directoryHandles.Add($Path, $handle)
    $attributes = [IO.File]::GetAttributes($Path)
    if (!($attributes -band [IO.FileAttributes]::Directory) -or ($attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Destinazione non valida: $Path"
    }
    return $true
}

function Get-ProgramPath([string]$Root, [string]$Relative) {
    # Registry and staged package paths use a canonical relative Windows file name.
    foreach ($component in $Relative.Split('\')) {
        if ($component -match '^(?:\s*$|.*[. ]$|(?:CON|PRN|AUX|NUL|(?:COM|LPT)[0-9\u00B9\u00B2\u00B3])(?:\.|$))' -or
            $component.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $component -match '[/\\]') {
            throw "Percorso non valido nell'inventario: $Relative"
        }
    }
    [IO.Path]::Combine($Root, $Relative)
}

function Test-ProgramFile([string]$Path) {
    if (!(Test-ProgramDirectory ([IO.Path]::GetDirectoryName($Path)))) { return $false }
    $attributes = Get-PathAttributes $Path
    if ($null -eq $attributes) { return $false }
    if ($attributes -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) {
        throw "Il percorso di un file di programma e' occupato da una cartella o un collegamento: $Path"
    }
    return $true
}

function Clear-EmptyProgramDirectories([string]$Root, [string[]]$Files) {
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$directories.Add($Root)
    foreach ($relative in $Files) {
        $parent = [IO.Path]::GetDirectoryName((Get-ProgramPath $Root $relative))
        while ($directories.Add($parent)) { $parent = [IO.Path]::GetDirectoryName($parent) }
    }
    foreach ($directory in ($directories | Sort-Object { $_.Length } -Descending)) {
        if (!(Test-ProgramDirectory $directory)) { continue }
        if ([IO.Directory]::EnumerateFileSystemEntries($directory) | Select-Object -First 1) { continue }
        $directoryHandles[$directory].Dispose()
        [void]$directoryHandles.Remove($directory)
        [IO.Directory]::Delete($directory, $false)
    }
}

function Copy-NewProgramFile([string]$Source, [string]$Target, [Collections.Generic.List[string]]$Created) {
    [void](Test-ProgramDirectory ([IO.Path]::GetDirectoryName($Target)) -Create)
    $inputStream = [IO.File]::OpenRead($Source)
    try {
        $outputStream = [IO.File]::Open($Target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $Created.Add($Target)
            $inputStream.CopyTo($outputStream)
        } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
}

function Rename-LegacyUninstaller($Legacy, [string]$CurrentRoot) {
    if ($Legacy.root -notmatch '^[a-zA-Z]:\\[^\\]' -or
        [IO.Path]::GetFullPath($Legacy.root) -ine $Legacy.root -or $Legacy.root -ieq $CurrentRoot) {
        throw 'Percorso del disinstallatore precedente non valido.'
    }
    $source = Join-Path $Legacy.root 'Uninstall.exe'
    if (!(Test-ProgramFile $source)) { return }
    if ($Legacy.command -ine ('"' + $source + '"')) {
        throw "Il comando registrato non identifica il disinstallatore precedente: $source"
    }

    # Request GENERIC_READ and DELETE without sharing either writes or deletes. Verify bytes
    # from this handle and rename that same entry. Never run or overwrite an executable.
    $handle = [OnlyWorkspace.SetupDirectory]::CreateFile($source, [uint32]2147549184, 1, [IntPtr]::Zero, 3, 0x00200000, [IntPtr]::Zero)
    try {
        if ($handle.IsInvalid) {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "Impossibile aprire il disinstallatore precedente (Win32 $errorCode): $source"
        }
        $snapshot = Join-Path $PSScriptRoot 'legacy-signature-check.exe'
        $inputStream = [IO.FileStream]::new($handle, [IO.FileAccess]::Read)
        try {
            $outputStream = [IO.File]::Open($snapshot, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
            $expected = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $payloadRoot 'OnlyApps.cer'))
            try {
                Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1')
                $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $snapshot
                $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($snapshot)
                # The certificate is trusted as a publisher only, not as a root: the chain then ends in an untrusted root
                # (UnknownError). Hash, signer thumbprint and product metadata are still checked; HashMismatch, NotSigned and
                # the other statuses stay refused.
                if ($signature.Status -notin @('Valid', 'UnknownError') -or $signature.SignerCertificate.Thumbprint -ne $expected.Thumbprint -or
                    $version.ProductName -cne 'OnlyWorkspace' -or $version.CompanyName -cne 'OnlyApps') {
                    throw "Identita' o firma del disinstallatore precedente non verificata: $source"
                }
            } finally { $expected.Dispose() }
            [OnlyWorkspace.SetupDirectory]::RenameWithoutReplacing($handle, "$source.disabled")
            Write-Output "Disinstallatore precedente rinominato: $source.disabled"
        } finally { $inputStream.Dispose() }
    } finally { $handle.Dispose() }
}

$baseKey = $null
$key = $null
try {
    Add-Type -Namespace OnlyWorkspace -Name SetupDirectory -ReferencedAssemblies ([Uri].Assembly.Location) -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
    string path, uint access, uint share, System.IntPtr security, uint mode, uint flags, System.IntPtr template);

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
private static extern bool SetFileInformationByHandle(
    Microsoft.Win32.SafeHandles.SafeFileHandle file, int informationClass, byte[] information, int size);

public static void RenameWithoutReplacing(Microsoft.Win32.SafeHandles.SafeFileHandle file, string destination)
{
    // FILE_RENAME_INFO: zero ReplaceIfExists and RootDirectory, then byte length and UTF-16 name.
    int lengthOffset = 2 * System.IntPtr.Size;
    int nameOffset = lengthOffset + sizeof(int);
    byte[] name = System.Text.Encoding.Unicode.GetBytes(destination + "\0");
    byte[] info = new byte[nameOffset + name.Length];
    System.Buffer.BlockCopy(System.BitConverter.GetBytes(name.Length - sizeof(char)), 0, info, lengthOffset, sizeof(int));
    System.Buffer.BlockCopy(name, 0, info, nameOffset, name.Length);
    if (!SetFileInformationByHandle(file, 3, info, info.Length))
        throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
}
'@

    if ($Destination -notmatch '^[a-zA-Z]:\\') { throw 'Scegliere una cartella su un disco locale.' }
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if ($root -notmatch '^[a-zA-Z]:\\[^\\]' -or $root -match '[. ](?:\\|$)') {
        throw 'Scegliere una cartella su un disco locale.'
    }
    $rootExists = Test-ProgramDirectory $root
    $view = if ([Environment]::Is64BitOperatingSystem) { [Microsoft.Win32.RegistryView]::Registry64 } else { [Microsoft.Win32.RegistryView]::Registry32 }
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($registryHive, $view)
    $key = $baseKey.OpenSubKey($registryPath, $Operation -in @('Deploy', 'RetireLegacy'))
    $inventory = $null
    $registeredRoot = $null
    if ($key) {
        $registeredRoot = $key.GetValue('InstallLocation')
        $inventoryJson = $key.GetValue($inventoryValue)
        if ($inventoryJson) { $inventory = ConvertFrom-Json -InputObject $inventoryJson }
    }
    $previous = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($inventory) {
        if ($inventory.version -ne 1) { throw 'Formato inventario di OnlyWorkspace non riconosciuto.' }
        if ($inventory.root -ine $root) { throw 'Usare la cartella di OnlyWorkspace registrata per questo pacchetto.' }
        foreach ($relative in $inventory.files) {
            if ($relative -isnot [string] -or !$previous.Add($relative)) { throw 'Inventario di OnlyWorkspace non valido.' }
            [void](Get-ProgramPath $root $relative)
        }
        if (!$previous.Contains('Uninstall.exe')) { throw 'Inventario di OnlyWorkspace incompleto.' }
    }

    if ($Operation -eq 'RetireLegacy') {
        if (!$inventory) { throw 'Inventario di OnlyWorkspace assente.' }
        if ($inventory.legacyUninstaller) {
            if (!$registeredRoot -or [IO.Path]::GetFullPath($registeredRoot).TrimEnd('\') -ine $root) {
                throw 'Il nuovo percorso non risulta registrato.'
            }
            Rename-LegacyUninstaller $inventory.legacyUninstaller $root
            $inventory.PSObject.Properties.Remove('legacyUninstaller')
            $key.SetValue($inventoryValue, (ConvertTo-Json -Compress -Depth 4 -InputObject $inventory), [Microsoft.Win32.RegistryValueKind]::String)
        }
        exit 0
    }

    if ($Operation -in @('CheckUninstall', 'Uninstall')) {
        if (!$inventory) { throw 'Inventario di OnlyWorkspace assente. La cartella viene conservata.' }
        if ($Operation -eq 'CheckUninstall') {
            # RegAsm runs before cleanup. Do not allow a redirected DLL.
            [void](Test-ProgramFile (Join-Path $root 'OnlyWorkspace.Outlook.dll'))
        } else {
            $failed = $false
            foreach ($relative in ($previous | Where-Object { $_ -ine 'Uninstall.exe' })) {
                try {
                    $target = Get-ProgramPath $root $relative
                    if (Test-ProgramFile $target) { [IO.File]::Delete($target) }
                } catch {
                    [Console]::Error.WriteLine($_.Exception.Message)
                    $failed = $true
                }
            }
            if ($failed) { exit 3 }
            $uninstaller = Join-Path $root 'Uninstall.exe'
            Clear-EmptyProgramDirectories $root @($previous)
            if (Test-ProgramFile $uninstaller) { [IO.File]::Delete($uninstaller) }
            Clear-EmptyProgramDirectories $root @()
        }
        exit 0
    }

    if (!$inventory -and (($registeredRoot -and ([IO.Path]::GetFullPath($registeredRoot).TrimEnd('\') -ieq $root)) -or
        ($rootExists -and ([IO.Directory]::EnumerateFileSystemEntries($root) | Select-Object -First 1)))) {
        throw "Scegliere una cartella nuova o vuota. I file dell'installazione precedente senza inventario vengono conservati."
    }
    $files = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($payloadRoot.Length + 1)
    })
    if ($files -inotcontains 'Uninstall.exe') { throw 'Pacchetto incompleto.' }
    foreach ($relative in $files) {
        if ((Test-ProgramFile (Get-ProgramPath $root $relative)) -and !$previous.Contains($relative)) {
            throw "Un file estraneo occupa un percorso del pacchetto: $relative"
        }
    }
    if ($Operation -eq 'Check') { exit 0 }

    $nextInventory = @{version=1; root=$root; files=$files}
    if ($inventory -and $inventory.legacyUninstaller) {
        $nextInventory.legacyUninstaller = $inventory.legacyUninstaller
    } elseif (!$inventory -and $registeredRoot) {
        $nextInventory.legacyUninstaller = @{
            root = [IO.Path]::GetFullPath($registeredRoot).TrimEnd('\')
            command = $key.GetValue('UninstallString')
        }
    }

    $saved = [Collections.Generic.List[string]]::new()
    $removed = [Collections.Generic.List[string]]::new()
    $created = [Collections.Generic.List[string]]::new()
    foreach ($relative in $previous) {
        $target = Get-ProgramPath $root $relative
        if (Test-ProgramFile $target) {
            $backup = Get-ProgramPath $backupRoot $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup))
            [IO.File]::Copy($target, $backup)
            $saved.Add($relative)
        }
    }
    try {
        foreach ($relative in $saved) {
            [IO.File]::Delete((Get-ProgramPath $root $relative))
            $removed.Add($relative)
        }
        foreach ($relative in $files) {
            Copy-NewProgramFile (Get-ProgramPath $payloadRoot $relative) (Get-ProgramPath $root $relative) $created
        }
        if (!$key) { $key = $baseKey.CreateSubKey($registryPath) }
        $key.SetValue($inventoryValue, (ConvertTo-Json -Compress -Depth 4 -InputObject $nextInventory), [Microsoft.Win32.RegistryValueKind]::String)
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        $failed = $false
        foreach ($target in $created) {
            try { [IO.File]::Delete($target) } catch {
                [Console]::Error.WriteLine("Rimozione del nuovo file non riuscita: $($_.Exception.Message)")
                $failed = $true
            }
        }
        foreach ($relative in $removed) {
            try {
                [IO.File]::Copy((Get-ProgramPath $backupRoot $relative), (Get-ProgramPath $root $relative))
            } catch {
                [Console]::Error.WriteLine("Ripristino del file precedente non riuscito: $($_.Exception.Message)")
                $failed = $true
            }
        }
        if ($failed) { exit 3 }
        if (!$rootExists) {
            try { Clear-EmptyProgramDirectories $root $files } catch {
                [Console]::Error.WriteLine("Pulizia della nuova cartella incompleta: $($_.Exception.Message)")
                exit 3
            }
        }
        exit 2
    }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    if ($Operation -like '*Uninstall' -or $Operation -eq 'RetireLegacy') { exit 3 }
    exit 2
} finally {
    if ($key) { $key.Dispose() }
    if ($baseKey) { $baseKey.Dispose() }
    foreach ($handle in $directoryHandles.Values) { $handle.Dispose() }
}
