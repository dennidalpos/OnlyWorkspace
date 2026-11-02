param([int]$TimeoutSeconds = 30)
$ErrorActionPreference = 'Stop'
$output = Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path 'src\OnlyWorkspace.Outlook\bin\Release\net48'
Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom((Join-Path $output 'OnlyWorkspace.Outlook.dll'))
$addin = New-Object OnlyWorkspace.Outlook.OnlyWorkspaceAddin
Add-Type -Path (Join-Path $output 'Microsoft.Web.WebView2.Core.dll')
Add-Type -Path (Join-Path $output 'Microsoft.Web.WebView2.WinForms.dll')
$architecture = if ([Environment]::Is64BitProcess) {'win-x64'} else {'win-x86'}
[Microsoft.Web.WebView2.Core.CoreWebView2Environment]::SetLoaderDllFolderPath((Join-Path $output "runtimes\$architecture\native"))
Add-Type -ReferencedAssemblies @((Join-Path $output 'Microsoft.Web.WebView2.Core.dll'), (Join-Path $output 'OnlyWorkspace.Outlook.dll'), 'System.Windows.Forms') -TypeDefinition @'
using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
public sealed class RecoveryEnvironmentFactory {
    public string Folder;
    public int Failures;
    public int Calls;
    public Task<CoreWebView2Environment> Create() {
        Calls++;
        if (Failures-- > 0) return Task.FromException<CoreWebView2Environment>(new InvalidOperationException("Synthetic initialization failure"));
        return CoreWebView2Environment.CreateAsync(null, Folder);
    }
    public Func<Task<CoreWebView2Environment>> Callback { get { return Create; } }
}
'@
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('OnlyWorkspace-WebViewRecovery-' + [Guid]::NewGuid().ToString('N'))
$factory = New-Object RecoveryEnvironmentFactory
$factory.Folder = Join-Path $scratch 'browser'
$factory.Failures = 1
$type = [OnlyWorkspace.Outlook.UI.TaskPaneControl]
$flags = [Reflection.BindingFlags]'NonPublic,Instance'
$constructor = $type.GetConstructors($flags) | Where-Object {$_.GetParameters().Count -eq 1}
$form = New-Object Windows.Forms.Form
$form.StartPosition='Manual'; $form.Location=New-Object Drawing.Point(-10000,-10000); $form.Size=New-Object Drawing.Size(600,400)
$panes = @()
$browserPid = $null
function Wait-Until([scriptblock]$condition, [string]$stage) {
    $until=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (!(& $condition)) {
        if ([DateTime]::UtcNow -gt $until) { throw "Timeout: $stage" }
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 15
    }
}
function Field($pane, [string]$name) { return $type.GetField($name,$flags).GetValue($pane) }
try {
    $first = $constructor.Invoke(@($factory.Callback))
    $panes += $first
    $first.Dock='Left'; $first.Width=300; $form.Controls.Add($first)
    $form.Show()
    Wait-Until { (Field $first '_errorPanel') -ne $null } 'initialization failure'
    if ($factory.Calls -ne 1) { throw 'Initialization failure retried automatically' }
    $retry = (Field $first '_errorPanel').Controls | Where-Object {$_ -is [Windows.Forms.Button]}
    $retry.PerformClick()
    Wait-Until { Field $first '_pageReady' } 'explicit retry ready'
    $second = $constructor.Invoke(@($factory.Callback))
    $panes += $second
    $second.Dock='Fill'; $form.Controls.Add($second)
    Wait-Until { Field $second '_pageReady' } 'second pane ready'
    $oldViews = @($panes | ForEach-Object { Field $_ '_webView' })
    $browserPid = $oldViews[0].CoreWebView2.BrowserProcessId
    if ($oldViews[1].CoreWebView2.BrowserProcessId -ne $browserPid) { throw 'Panes do not share the isolated browser' }
    $browser = Get-CimInstance Win32_Process -Filter "ProcessId=$browserPid"
    if (!$browser -or $browser.Name -ne 'msedgewebview2.exe' -or !$browser.CommandLine.Contains($factory.Folder)) { throw 'Refuse to crash a browser outside the exact isolated data folder' }
    $callsBeforeCrash = $factory.Calls
    $script:queuedSeen = 0
    $environment = Field $first '_environment'
    $environment.add_BrowserProcessExited({
        param($eventSender, $eventData)
        if ($eventData.BrowserProcessExitKind -eq [Microsoft.Web.WebView2.Core.CoreWebView2BrowserProcessExitKind]::Failed) {
            foreach ($pane in $panes) {
                $pane.PostMessage(@{type='toast';message='Synthetic queued recovery';kind='info'})
                $script:queuedSeen += (Field $pane '_pendingMessages').Count
            }
        }
    })
    Stop-Process -Id $browserPid -Force
    Wait-Until {
        (Field $first '_pageReady') -and (Field $second '_pageReady') -and
        ![object]::ReferenceEquals($oldViews[0], (Field $first '_webView')) -and
        ![object]::ReferenceEquals($oldViews[1], (Field $second '_webView'))
    } 'both panes recreated and ready'
    if ($factory.Calls -ne $callsBeforeCrash + 2) { throw 'Recovery was not exactly once per affected pane' }
    if ($script:queuedSeen -lt 1) { throw 'No pending message was exercised during browser recovery' }
    foreach ($pane in $panes) {
        if ((Field $pane '_pendingMessages').Count -ne 0) { throw 'Recovery did not flush pending messages' }
        $view = Field $pane '_webView'
        if ($view.CoreWebView2.Source -ne 'about:blank') { throw 'Recovered page has an unexpected source' }
        $queued = $view.CoreWebView2.ExecuteScriptAsync('document.body.textContent.includes("Synthetic queued recovery")')
        Wait-Until {$queued.IsCompleted} 'queued toast delivery'
        if ($queued.GetAwaiter().GetResult() -ne 'true') { throw 'Queued message did not survive recovery' }
        $setup = $view.CoreWebView2.ExecuteScriptAsync('window.__recoveryMessages=[];chrome.webview.addEventListener("message",e=>window.__recoveryMessages.push(e.data));')
        Wait-Until {$setup.IsCompleted} 'dispatch probe setup'
        [void]$setup.GetAwaiter().GetResult()
        $pane.PostMessage(@{type='recovery_probe';value='synthetic'})
        $probe = $view.CoreWebView2.ExecuteScriptAsync('JSON.stringify(window.__recoveryMessages)')
        Wait-Until {$probe.IsCompleted} 'recovered dispatch'
        if (!$probe.GetAwaiter().GetResult().Contains('synthetic')) { throw 'Recovered dispatch was not delivered' }
    }
    # Existing boundary still blocks arbitrary navigation after recovery.
    $view = Field $first '_webView'
    $view.CoreWebView2.Navigate('data:text/html,<b>untrusted</b>')
    $probe = $view.CoreWebView2.ExecuteScriptAsync('document.location.href')
    Wait-Until {$probe.IsCompleted} 'blocked navigation'
    if ($probe.GetAwaiter().GetResult() -ne '"about:blank"') { throw 'Recovery relaxed the navigation boundary' }
    $script:rendererReloaded = $false
    $first.add_ControlReady({$script:rendererReloaded = $true})
    $rendererCrash = $view.CoreWebView2.CallDevToolsProtocolMethodAsync('Page.crash', '{}')
    Wait-Until {$script:rendererReloaded -and (Field $first '_pageReady')} 'renderer page reload'
    if (![object]::ReferenceEquals($view, (Field $first '_webView'))) { throw 'Renderer crash unnecessarily replaced the control' }
    'PASS real WebView2: failed init/manual retry, two-pane isolated browser crash, one recovery each, queued delivery, dispatch, navigation boundary and renderer reload'
    "Runtime=$((Field $first '_environment').BrowserVersionString); SDK=$((Get-Item (Join-Path $output 'Microsoft.Web.WebView2.Core.dll')).VersionInfo.FileVersion)"
}
finally {
    $form.Close(); $form.Dispose()
    foreach ($pane in $panes) { $pane.Dispose() }
    # Close only the process group created with this exact fixture folder, then remove its data.
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object {$_.CommandLine -and $_.CommandLine.Contains($factory.Folder)})
    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $process.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($scratch)
    if (!$resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or ![IO.Path]::GetFileName($resolved).StartsWith('OnlyWorkspace-WebViewRecovery-')) { throw 'Unsafe fixture cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
