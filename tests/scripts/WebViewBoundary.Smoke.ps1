param([int]$TimeoutSeconds = 20)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = Join-Path $root 'src/OnlyWorkspace.Outlook/bin/Release/net48'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path (Join-Path $output 'Microsoft.Web.WebView2.Core.dll')
Add-Type -Path (Join-Path $output 'Microsoft.Web.WebView2.WinForms.dll')

$architecture = if ([Environment]::Is64BitProcess) { 'win-x64' } else { 'win-x86' }
$loader = Join-Path $output "runtimes/$architecture/native"
[Microsoft.Web.WebView2.Core.CoreWebView2Environment]::SetLoaderDllFolderPath($loader)
$userData = Join-Path ([IO.Path]::GetTempPath()) ('OnlyWorkspace-WebViewSmoke-' + [Guid]::NewGuid().ToString('N'))
$form = [System.Windows.Forms.Form]::new()
$view = [Microsoft.Web.WebView2.WinForms.WebView2]::new()
$form.StartPosition = 'Manual'
$form.Location = [Drawing.Point]::new(-10000, -10000)
$form.Size = [Drawing.Size]::new(200, 200)
$view.Dock = 'Fill'
$form.Controls.Add($view)

function Wait-WebView([scriptblock]$condition, [string]$stage) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (& $condition)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Timed out waiting for $stage; navigations=$($script:navigations -join ','); source=$($view.CoreWebView2.Source)"
        }
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 20
    }
}

try {
    $form.Show()
    $environmentTask = [Microsoft.Web.WebView2.Core.CoreWebView2Environment]::CreateAsync($null, $userData)
    Wait-WebView { $environmentTask.IsCompleted } 'environment'
    $environment = $environmentTask.GetAwaiter().GetResult()
    $initTask = $view.EnsureCoreWebView2Async($environment)
    Wait-WebView { $initTask.IsCompleted } 'control initialization'
    $null = $initTask.GetAwaiter().GetResult()

    $script:pending = $true
    $script:navigations = [Collections.Generic.List[string]]::new()
    $script:messageSource = $null
    $script:currentSource = $null
    $script:cancelled = 0
    $html = "<html><body>à`n<script>chrome.webview.postMessage('ready')</script></body></html>"
    $script:expectedDataUri = 'data:text/html;charset=utf-8;base64,' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($html))
    $view.CoreWebView2.add_NavigationStarting({
        param($eventSender, $eventData)
        $script:navigations.Add($eventData.Uri)
        if ($script:pending -and ($eventData.Uri -eq 'about:blank' -or $eventData.Uri -ceq $script:expectedDataUri)) {
            $script:pending = $false
        } else {
            $eventData.Cancel = $true
            $script:cancelled++
        }
    })
    $view.CoreWebView2.add_WebMessageReceived({
        param($eventSender, $eventData)
        $script:messageSource = $eventData.Source
        $script:currentSource = $eventSender.Source
    })
    $view.NavigateToString($html)
    Wait-WebView { $null -ne $script:messageSource } 'embedded message'
    if ($script:navigations.Count -ne 1 -or $script:navigations[0] -cne $script:expectedDataUri -or
        $script:messageSource -ne 'about:blank' -or $script:currentSource -ne 'about:blank') {
        throw "Unexpected embedded URI: message=$script:messageSource, current=$script:currentSource"
    }

    $view.CoreWebView2.Navigate('data:text/html,<script>chrome.webview.postMessage("spoof")</script>')
    Wait-WebView { $script:navigations.Count -ge 2 } 'blocked data navigation'
    if ($script:cancelled -ne 1 -or $script:messageSource -ne 'about:blank' -or $view.CoreWebView2.Source -ne 'about:blank') {
        throw 'Blocked navigation changed the embedded document'
    }
    Write-Output 'PASS WebView2 embedded navigation, message source, and blocked data navigation'
}
finally {
    $browserPid = if ($null -ne $view.CoreWebView2) { $view.CoreWebView2.BrowserProcessId } else { $null }
    $form.Close()
    $view.Dispose()
    $form.Dispose()
    # The browser releases the user data folder only after its processes exit, some time after Dispose.
    if ($browserPid) { Wait-Process -Id $browserPid -Timeout $TimeoutSeconds -ErrorAction SilentlyContinue }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (@(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($userData) }).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($userData)
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        ![IO.Path]::GetFileName($resolved).StartsWith('OnlyWorkspace-WebViewSmoke-', [StringComparison]::Ordinal)) { throw 'Unsafe fixture cleanup path' }
    try { if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force } }
    catch { $cleanupError = "WebView2 profile left behind: $resolved ($($_.Exception.Message))"; Write-Warning $cleanupError }
}
if ($cleanupError) { throw $cleanupError }
