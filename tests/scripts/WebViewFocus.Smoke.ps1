param([int]$TimeoutSeconds = 30, [string]$EvidenceDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "../../scripts/build-paths.ps1")
Set-OnlyWorkspaceVerificationEnvironment -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path
$output = Get-OnlyWorkspaceOutputDirectory -RepositoryRoot (Resolve-Path "$PSScriptRoot/../..").Path
Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom((Join-Path $output 'OnlyWorkspace.Outlook.dll'))
$addin = New-Object OnlyWorkspace.Outlook.OnlyWorkspaceAddin
Add-Type -Path (Join-Path $output 'Microsoft.Web.WebView2.Core.dll')
Add-Type -Path (Join-Path $output 'Microsoft.Web.WebView2.WinForms.dll')
$architecture = if ([Environment]::Is64BitProcess) {'win-x64'} else {'win-x86'}
[Microsoft.Web.WebView2.Core.CoreWebView2Environment]::SetLoaderDllFolderPath((Join-Path $output "runtimes\$architecture\native"))
Add-Type -ReferencedAssemblies @((Join-Path $output 'Microsoft.Web.WebView2.Core.dll')) -TypeDefinition @'
using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
public sealed class FocusEnvironmentFactory {
    public readonly TaskCompletionSource<CoreWebView2Environment> Ready = new TaskCompletionSource<CoreWebView2Environment>();
    public Func<Task<CoreWebView2Environment>> Callback { get { return () => Ready.Task; } }
}
'@
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('OnlyWorkspace-WebViewFocus-' + [Guid]::NewGuid().ToString('N'))
$browserFolder = Join-Path $scratch 'browser'
$flags = [Reflection.BindingFlags]'NonPublic,Instance'
$type = [OnlyWorkspace.Outlook.UI.TaskPaneControl]
$constructor = $type.GetConstructors($flags) | Where-Object {$_.GetParameters().Count -eq 1}
$focus = $type.GetMethod('RequestContentFocus', $flags)
$form = New-Object Windows.Forms.Form
$form.StartPosition='Manual'; $form.Location=New-Object Drawing.Point(-10000,-10000); $form.Size=New-Object Drawing.Size(600,500)
$button = New-Object Windows.Forms.Button
$button.Text='Synthetic ribbon'; $button.Dock='Top'
$panes = @()
function Wait-Until([scriptblock]$condition, [string]$stage) {
    $until=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (!(& $condition)) {
        if ([DateTime]::UtcNow -gt $until) { throw "Timeout: $stage" }
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 15
    }
}
function Field($pane, [string]$name) { return $type.GetField($name,$flags).GetValue($pane) }
function Script($pane, [string]$code) {
    $task=(Field $pane '_webView').CoreWebView2.ExecuteScriptAsync($code)
    Wait-Until {$task.IsCompleted} 'script result'
    return $task.GetAwaiter().GetResult()
}
$serilog = [Reflection.Assembly]::LoadFrom((Join-Path $output 'Serilog.dll'))
Add-Type -ReferencedAssemblies $serilog.Location, netstandard -TypeDefinition @'
using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;
public sealed class FocusDiagnosticLog : ILogEventSink {
    public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
    public void Emit(LogEvent entry) { Lines.Enqueue(entry.RenderMessage()); }
}
'@
$diagnosticLog = New-Object FocusDiagnosticLog
$previousLogger = [Serilog.Log]::Logger
$fixtureLogger = (New-Object Serilog.LoggerConfiguration).WriteTo.Sink($diagnosticLog).CreateLogger()
[Serilog.Log]::Logger = $fixtureLogger
try {
    $factory = New-Object FocusEnvironmentFactory
    $pane = $constructor.Invoke(@($factory.Callback)); $panes += $pane
    $pane.Dock='Fill'; $form.Controls.Add($pane); $form.Controls.Add($button)
    $form.Show(); $form.Activate()
    [void]$focus.Invoke($pane,@())
    $pane.PostMessage(@{type='show_input';mode='chat';prompt=''})
    $envTask=[Microsoft.Web.WebView2.Core.CoreWebView2Environment]::CreateAsync($null,$browserFolder)
    Wait-Until {$envTask.IsCompleted} 'environment'
    $factory.Ready.SetResult($envTask.GetAwaiter().GetResult())
    Wait-Until {Field $pane '_pageReady'} 'first page ready'
    Wait-Until {$pane.ContainsFocus -and (Script $pane 'document.hasFocus() && document.activeElement.id === "userPrompt"') -eq 'true'} 'queued explicit focus'

    foreach ($screen in @(@('toggle_info','infoTitle'),@('toggle_history','historyTitle'))) {
        [void]$button.Focus()
        if (!$button.Focused) { throw 'Synthetic ribbon did not own focus' }
        [void]$focus.Invoke($pane,@())
        $pane.PostMessage(@{type=$screen[0];toggle=$false})
        $expected=$screen[1]
        Wait-Until {$pane.ContainsFocus -and (Script $pane "document.hasFocus() && document.activeElement.id === '$expected'") -eq 'true'} "explicit $expected"
    }
    [void]$button.Focus()
    $pane.PostMessage(@{type='context_update';hasSelection=$false})
    [void](Script $pane 'document.activeElement.id')
    if (!$button.Focused -or $pane.ContainsFocus) { throw 'Background update stole focus' }

    $waiting = New-Object FocusEnvironmentFactory
    $second = $constructor.Invoke(@($waiting.Callback)); $panes += $second
    $pane.Hide(); $second.Dock='Fill'; $form.Controls.Add($second); $second.BringToFront()
    [void]$focus.Invoke($second,@())
    [void]$button.Focus()
    $second.PostMessage(@{type='show_input';mode='chat';prompt=''})
    $waiting.Ready.SetResult($envTask.GetAwaiter().GetResult())
    Wait-Until {Field $second '_pageReady'} 'second page ready after focus left'
    [void](Script $second 'document.activeElement.id')
    if (!$button.Focused -or $second.ContainsFocus) { throw 'Delayed initialization stole focus back' }
    [void]$focus.Invoke($second,@())
    $second.PostMessage(@{type='show_input';mode='chat';prompt=''})
    Wait-Until {$second.ContainsFocus -and (Script $second 'document.hasFocus() && document.activeElement.id === "userPrompt"') -eq 'true'} 'explicit focus after initialization'
    # A212: real DOM and keyboard handling, using only synthetic headers and an isolated bridge spy.
    [void](Script $second 'window.selectionMessages=[];window.selectionPost=chrome.webview.postMessage;chrome.webview.postMessage=function(m){window.selectionMessages.push(m);}')
    $second.PostMessage(@{type='context_update';hasEmail=$false})
    Wait-Until {(Script $second 'document.getElementById("emailGroup").hidden && !document.getElementById("emailSelectionHint").hidden && document.getElementById("btnSummary").disabled && !document.getElementById("btnBriefing").disabled') -eq 'true'} 'no-selection actions'
    [void](Script $second '["s","r","v"].forEach(function(k){document.dispatchEvent(new KeyboardEvent("keydown",{key:k,altKey:true,bubbles:true,cancelable:true}));});')
    if ((Script $second 'window.selectionMessages.some(function(m){return ["summary","reply","security","custom_reply"].indexOf(m.type)>=0;})') -ne 'false') {throw 'A hidden email action remained reachable by keyboard'}
    $second.PostMessage(@{type='context_update';hasEmail=$true;entryId='fixture-email';storeId='fixture-store';subject='<b>Offerta</b>'})
    Wait-Until {(Script $second '!document.getElementById("emailGroup").hidden && document.getElementById("selectedEmailSubject").textContent === "<b>Offerta</b>" && document.getElementById("selectedEmailSubject").children.length === 0 && !document.getElementById("btnSummary").disabled') -eq 'true'} 'selected literal subject'
    [void](Script $second 'document.getElementById("btnReplyMenu").click();document.querySelector("[data-reply=custom]").click();document.getElementById("userPrompt").value="Istruzione conservata";')
    if ((Script $second '!document.getElementById("composerMode").hidden') -ne 'true') {throw 'Reply mode was not armed for the selected email'}
    $second.PostMessage(@{type='context_update';hasEmail=$false})
    Wait-Until {(Script $second 'document.getElementById("composerMode").hidden && document.getElementById("userPrompt").value === "Istruzione conservata"') -eq 'true'} 'selection loss clears reply mode without losing text'
    $second.PostMessage(@{type='context_update';hasEmail=$true;entryId='original';storeId='fixture-store';subject='Originale';compose=@{subject='Re: Offerta';kind='reply'}})
    Wait-Until {(Script $second 'document.getElementById("emailGroup").hidden && !document.getElementById("composeGroup").hidden && document.getElementById("composeEmailSubject").textContent === "Re: Offerta" && document.getElementById("btnSummary").disabled && !document.getElementById("btnTranslateMenu").disabled && !document.getElementById("btnBriefing").disabled') -eq 'true'} 'inline compose actions'
    $second.PostMessage(@{type='context_update';hasEmail=$true;entryId='fixture-email';storeId='fixture-store';subject='<b>Offerta</b>'})
    $second.PostMessage(@{type='toggle_analysis';preset='summary'})
    Wait-Until {(Script $second '!document.getElementById("analysisDrawer").hidden') -eq 'true'} 'analysis form'
    [void](Script $second 'document.querySelector("input[name=anScope][value=thread]").click();')
    $requestId = [int](Script $second 'window.selectionMessages.filter(function(m){return m.type === "analysis_open";}).slice(-1)[0].requestId')
    $second.PostMessage(@{type='analysis_context';requestId=$requestId;selectionScope='thread';hasEmail=$true;entryId='fixture-email';storeId='fixture-store';subject='<b>Offerta</b>';stores=@(@{id='fixture-store';name='Sintetico'});attachmentsAllowed=$true;maxEmails=20;followUpDays=7})
    Wait-Until {(Script $second 'document.getElementById("anThreadLabel").textContent.indexOf("<b>Offerta</b>") >= 0 && document.getElementById("anThreadLabel").children.length === 0') -eq 'true'} 'conversation subject'
    [void](Script $second 'chrome.webview.postMessage=window.selectionPost;document.dispatchEvent(new KeyboardEvent("keydown",{key:"Escape",bubbles:true,cancelable:true}));document.getElementById("userPrompt").value="";')
    'PASS A212 real WebView: no selection, literal subjects, reply selection loss, inline draft, keyboard guards and conversation source'
    # A208: real form, local preset with no configured AI, source bound on dispatch and summary opt-in.
    [void](Script $second 'chrome.webview.postMessage=function(m){window.selectionMessages.push(m);}')
    $second.PostMessage(@{type='context_update';hasEmail=$true;entryId='practice-email';storeId='practice-store';subject='<b>Pratica sintetica</b>'})
    $second.PostMessage(@{type='toggle_analysis';preset='reconstruct'})
    Wait-Until {(Script $second '!document.getElementById("analysisDrawer").hidden') -eq 'true'} 'reconstruction form'
    $requestId = Script $second 'window.selectionMessages.filter(function(m){return m.type === "analysis_open";}).slice(-1)[0].requestId'
    $second.PostMessage(@{type='analysis_context';requestId=[int]$requestId;selectionScope='flow';hasEmail=$true;entryId='practice-email';storeId='practice-store';subject='<b>Pratica sintetica</b>';archive=@{active=$true;emails=2};stores=@()})
    Wait-Until {(Script $second 'document.getElementById("anSeedSubject").textContent === "<b>Pratica sintetica</b>" && !document.getElementById("anStart").disabled') -eq 'true'} 'reconstruction source'
    $second.PostMessage(@{type='config_error';message='Profilo IA sintetico assente'})
    Wait-Until {(Script $second 'document.getElementById("srStatus").textContent === "Errore di configurazione: Profilo IA sintetico assente"') -eq 'true'} 'configuration error announcement'
    if ((Script $second 'document.getElementById("anStart").disabled') -ne 'false') {throw 'Local reconstruction requires an AI profile'}
    [void](Script $second 'document.getElementById("anStart").click();')
    if ((Script $second 'JSON.stringify(window.selectionMessages.filter(function(m){return m.type === "mail_analysis";}).slice(-1)[0].scope)') -notmatch 'practice-email') {throw 'Reconstruction lost its explicit source'}
    $chainRequest = Script $second 'window.selectionMessages.filter(function(m){return m.type === "mail_analysis";}).slice(-1)[0].requestId'
    $second.PostMessage(@{type='analysis_reconstruction';requestId=[int]$chainRequest;seed=@{entryId='practice-email';storeId='practice-store';subject='<b>Pratica sintetica</b>'};items=@(@{entryId='practice-email';storeId='practice-store';subject='Email sintetica';bodyState='indexed'});coverage=@()})
    Wait-Until {(Script $second '!document.getElementById("anReconstruction").hidden && document.getElementById("flowList").children.length === 1 && document.getElementById("flowAnalyze").disabled && !document.getElementById("flowDrawer")') -eq 'true'} 'local reconstruction in analysis'
    'PASS A208 real WebView local preset without AI, literal source and explicit synthesis'
    [void](Script $second 'chrome.webview.postMessage=window.selectionPost;')
    # A201: actual WebView layout at a splitter width Office may temporarily permit. This is
    # isolated layout evidence, not a qualification of monitor DPI or the installed Outlook splitter.
    $savedSize = $form.ClientSize
    foreach ($width in @(380, 190, 120)) {
        $form.ClientSize = New-Object Drawing.Size($width, 600)
        [Windows.Forms.Application]::DoEvents()
        $layout = Script $second 'JSON.stringify({viewport:innerWidth,page:document.body.getBoundingClientRect().width,scroll:document.documentElement.scrollWidth,overflow:getComputedStyle(document.documentElement).overflowX})' | ConvertFrom-Json | ConvertFrom-Json
        $metricsTask = $second.GetType().GetMethod('ReadLayoutMetricsAsync', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($second, @())
        Wait-Until {$metricsTask.IsCompleted} 'numeric layout diagnostics'
        $metrics = $metricsTask.GetAwaiter().GetResult() | ConvertFrom-Json
        if ($metrics.cssWidth -ne $layout.viewport -or $metrics.pageWidth -ne $layout.page -or $metrics.devicePixelRatio -le 0) {throw 'A201 numeric layout diagnostics disagree with the real viewport'}
        if ($layout.page -lt 340 -or $layout.overflow -ne 'auto') { throw "Page collapsed at width $width" }
        if ($width -lt 340 -and $layout.scroll -le $layout.viewport) { throw "Narrow viewport is not horizontally scrollable at $width" }
    }
    $form.ClientSize = $savedSize
    'PASS A201 real WebView layout: minimum 340 CSS pixels and horizontal reachability at 380/190/120 px'
    # A210: Stato opens on its essentials, with the diagnostics folded away, and fits the page width at 100% and 200%.
    $second.PostMessage(@{type='toggle_info';toggle=$false})
    Wait-Until {(Script $second '!document.getElementById("infoDrawer").hidden') -eq 'true'} 'Stato drawer'
    foreach ($width in @(380, 190)) {
        $form.ClientSize = New-Object Drawing.Size($width, 600)
        [Windows.Forms.Application]::DoEvents()
        $stato = Script $second 'JSON.stringify({open:document.getElementById("statusAdvanced").open,fits:(function(b){return b.scrollWidth<=b.clientWidth+1;})(document.querySelector("#infoDrawer .drawer-body"))})' | ConvertFrom-Json | ConvertFrom-Json
        if ($stato.open) { throw "Stato opens with Avanzate unfolded at width $width" }
        if (!$stato.fits) { throw "Stato essentials need horizontal scrolling at width $width" }
    }
    $form.ClientSize = $savedSize
    'PASS A210 real WebView Stato: Avanzate closed by default, essentials fit at 380 and 190 px'
    # A191/A194: real browser layout, keyboard focus and emulated contrast, without Outlook.
    $view = Field $second '_webView'
    $layoutEvidence = @()
    foreach ($sample in @(@{width=380;height=800;zoom=1}, @{width=190;height=400;zoom=1}, @{width=190;height=400;zoom=2})) {
        $view.ZoomFactor = $sample.zoom
        $form.ClientSize = New-Object Drawing.Size(($sample.width*$sample.zoom), ($sample.height*$sample.zoom+$button.Height))
        $second.PostMessage(@{type='toggle_analysis';preset='summary';toggle=$false})
        Wait-Until {(Script $second 'document.body.dataset.page === "analysisDrawer"') -eq 'true'} 'A191 Posta'
        [void](Script $second 'document.getElementById("anPerson").value="persona.sintetica@example.test"; document.getElementById("anSubject").value="Ordine sintetico"; document.getElementById("anDays").value="0"; document.getElementById("anRole").value="recipient";')
        # Keep the last field active at the bottom: a long error toast must leave it reachable.
        [void](Script $second 'document.getElementById("anDays").focus(); document.getElementById("anDays").scrollIntoView({block:"end"});')
        $second.PostMessage(@{type='toast';kind='error';message=('Messaggio sintetico lungo da leggere. '*14)})
        Wait-Until {(Script $second 'parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--toast-space")) > 0') -eq 'true'} 'A191 toast space'
        [void](Script $second 'document.getElementById("anDays").scrollIntoView({block:"end"});')
        $metrics = Script $second @'
JSON.stringify((function(){
 var drawer=document.getElementById('analysisDrawer'), toast=document.getElementById('toast');
 var days=document.getElementById('anDays'), rect=days.getBoundingClientRect(), t=toast.getBoundingClientRect();
 var body=drawer.querySelector('.drawer-body'), field=document.getElementById('anSubject'), label=field.previousElementSibling;
 var canvas=document.createElement('canvas'), painter=canvas.getContext('2d');
 var selectionsFit=['anDays','anRole'].every(function(id){var select=document.getElementById(id), style=getComputedStyle(select); painter.font=style.font; return painter.measureText(select.selectedOptions[0].textContent).width+parseFloat(style.paddingLeft)+parseFloat(style.paddingRight)+20<=select.clientWidth;});
 return {viewport:innerWidth,height:innerHeight,page:document.body.getBoundingClientRect().width,
  header:body.getBoundingClientRect().top,fieldWidth:field.getBoundingClientRect().width,
  stacked:field.getBoundingClientRect().top>=label.getBoundingClientRect().bottom,
  drawerBottom:drawer.getBoundingClientRect().bottom,toastTop:t.top,fieldBottom:rect.bottom,
  active:document.activeElement.id,role:document.getElementById('anRole').value,period:days.value,selectionsFit:selectionsFit};
})())
'@ | ConvertFrom-Json | ConvertFrom-Json
        $metrics | Add-Member -NotePropertyName zoom -NotePropertyValue $sample.zoom
        $layoutEvidence += $metrics
        "A191 layout: $($metrics | ConvertTo-Json -Compress)"
        if ([Math]::Abs($metrics.viewport-$sample.width) -gt 1 -or [Math]::Abs($metrics.height-$sample.height) -gt 2) {throw "A191 unexpected viewport: $($metrics | ConvertTo-Json -Compress)"}
        if (!$metrics.stacked -or !$metrics.selectionsFit -or $metrics.fieldWidth -lt $metrics.viewport-40 -or $metrics.page -lt 340 -or $metrics.role -ne 'recipient' -or $metrics.period -ne '0') {throw 'A191 filters collapsed or lost selected values'}
        if ($metrics.header -gt $metrics.height/3) {throw "A191 header exceeds one third: $($metrics | ConvertTo-Json -Compress)"}
        if ($metrics.drawerBottom -gt $metrics.toastTop -or $metrics.fieldBottom -gt $metrics.toastTop -or $metrics.active -ne 'anDays') {throw "A191 toast covers the active field: $($metrics | ConvertTo-Json -Compress)"}
        # The job bar adds its own space below both the drawer and toast.
        [void](Script $second 'document.body.classList.add("has-job"); document.getElementById("jobBar").hidden=false; document.getElementById("anDays").scrollIntoView({block:"end"});')
        $jobFits = Script $second 'document.getElementById("analysisDrawer").getBoundingClientRect().bottom <= document.getElementById("toast").getBoundingClientRect().top && document.getElementById("anDays").getBoundingClientRect().bottom <= document.getElementById("toast").getBoundingClientRect().top'
        if ($jobFits -ne 'true') {throw 'A191 job bar and toast cover the active field'}
        if ($EvidenceDirectory -and $sample.zoom -eq 2) {
            [void][IO.Directory]::CreateDirectory($EvidenceDirectory)
            $capture = $view.CoreWebView2.CallDevToolsProtocolMethodAsync('Page.captureScreenshot','{"format":"png"}')
            Wait-Until {$capture.IsCompleted} 'A191 synthetic screenshot'
            $png = $capture.GetAwaiter().GetResult() | ConvertFrom-Json
            [IO.File]::WriteAllBytes((Join-Path $EvidenceDirectory "posta-zoom200-$architecture.png"), [Convert]::FromBase64String($png.data))
        }
        [void](Script $second 'document.body.classList.remove("has-job"); document.getElementById("jobBar").hidden=true; document.getElementById("toastClose").click();')
        $second.PostMessage(@{type='toggle_info';toggle=$false})
        Wait-Until {(Script $second 'document.body.dataset.page === "infoDrawer"') -eq 'true'} 'A191 Stato'
        if ((Script $second '(function(b){return b.scrollWidth<=b.clientWidth+1;})(document.querySelector("#infoDrawer .drawer-body"))') -ne 'true') {throw 'A191 Stato overflow'}
    }
    if ($EvidenceDirectory) {
        [void][IO.Directory]::CreateDirectory($EvidenceDirectory)
        $layoutEvidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory "layout-$architecture.json") -Encoding UTF8
    }
    $view.ZoomFactor = 1; $form.ClientSize = $savedSize
    # Browser forced-colors emulation qualifies CSS only, not a Windows contrast theme.
    $contrast = $view.CoreWebView2.CallDevToolsProtocolMethodAsync('Emulation.setEmulatedMedia','{"features":[{"name":"forced-colors","value":"active"}]}')
    Wait-Until {$contrast.IsCompleted} 'contrast emulation'; [void]$contrast.GetAwaiter().GetResult()
    $second.PostMessage(@{type='toggle_analysis';preset='summary';toggle=$false})
    Wait-Until {(Script $second 'document.body.dataset.page === "analysisDrawer"') -eq 'true'} 'A194 contrast Posta'
    $contrastState = Script $second 'JSON.stringify((function(){var chip=document.querySelector(".chip[aria-checked=true]"), nav=document.querySelector(".section-nav [aria-current=page]"); return {active:matchMedia("(forced-colors: active)").matches,chip:chip ? getComputedStyle(chip).textDecorationLine : "missing",nav:getComputedStyle(nav).textDecorationLine,role:document.querySelector("nav.section-nav").getAttribute("aria-label")};})())' | ConvertFrom-Json | ConvertFrom-Json
    if (!$contrastState.active -or $contrastState.chip -ne 'underline' -or $contrastState.nav -ne 'underline' -or $contrastState.role -ne 'Azioni di Posta') {throw "A194 contrast selection missing: $($contrastState | ConvertTo-Json -Compress)"}
    $contrast = $view.CoreWebView2.CallDevToolsProtocolMethodAsync('Emulation.setEmulatedMedia','{"features":[]}')
    Wait-Until {$contrast.IsCompleted} 'end contrast emulation'; [void]$contrast.GetAwaiter().GetResult()
    # Drive the real DOM and host message handler; all history content is synthetic.
    [void](Script $second 'window.historyPosts=[]; window.historyOriginalPost=chrome.webview.postMessage; chrome.webview.postMessage=function(m){window.historyPosts.push(m);};')
    $second.PostMessage(@{type='toggle_history';toggle=$false})
    Wait-Until {(Script $second 'document.body.dataset.page === "historyDrawer"') -eq 'true'} 'A194 history'
    $historyItems = @(@{id='a194-a';title='Synthetic A';messageCount=1},@{id='a194-b';title='Synthetic B';messageCount=1},@{id='a194-c';title='Synthetic C';messageCount=1})
    $historyRead = Script $second 'JSON.stringify(historyPosts.filter(function(m){return m.type==="list_history";}).pop())' | ConvertFrom-Json | ConvertFrom-Json
    $second.PostMessage(@{type='history_list';items=$historyItems;requestId=$historyRead.requestId})
    try {
        Wait-Until {(Script $second 'document.querySelectorAll("#historyList .history-item").length===3') -eq 'true'} 'A194 history rows'
    } catch {
        Script $second 'JSON.stringify({rows:document.querySelectorAll("#historyList .history-item").length,posts:historyPosts,toast:document.getElementById("toastText").textContent})'
        throw
    }
    [void](Script $second 'window.historyDelete=document.querySelector("[data-history-id=\"a194-b\"][data-history-action=delete]"); historyDelete.focus(); historyDelete.click(); historyDelete.click();')
    $deleteRequest = Script $second 'JSON.stringify(historyPosts.filter(function(m){return m.type==="delete_history";}).pop())' | ConvertFrom-Json | ConvertFrom-Json
    if (!$deleteRequest.requestId -or $deleteRequest.id -ne 'a194-b') {throw 'A194 delete lost identity'}
    $second.PostMessage(@{type='new_chat_ready';preservePage=$true})
    $second.PostMessage(@{type='history_list';items=@($historyItems[0],$historyItems[2]);deleted=$true;requestId=$deleteRequest.requestId})
    Wait-Until {(Script $second 'document.body.dataset.page === "historyDrawer" && document.activeElement.dataset.historyId === "a194-c" && document.getElementById("srStatus").textContent === "Conversazione eliminata."') -eq 'true'} 'A194 next row and announcement'
    # Browser keyboard input must advance from the restored row, not from body or Chat.
    $tab = $view.CoreWebView2.CallDevToolsProtocolMethodAsync('Input.dispatchKeyEvent','{"type":"keyDown","key":"Tab","code":"Tab","windowsVirtualKeyCode":9,"nativeVirtualKeyCode":9}')
    Wait-Until {$tab.IsCompleted} 'A194 keyboard Tab'; [void]$tab.GetAwaiter().GetResult()
    $tab = $view.CoreWebView2.CallDevToolsProtocolMethodAsync('Input.dispatchKeyEvent','{"type":"keyUp","key":"Tab","code":"Tab","windowsVirtualKeyCode":9,"nativeVirtualKeyCode":9}')
    Wait-Until {$tab.IsCompleted} 'A194 keyboard Tab up'; [void]$tab.GetAwaiter().GetResult()
    Wait-Until {(Script $second 'document.activeElement.dataset.historyId === "a194-c" && document.activeElement.dataset.historyAction === "resume"') -eq 'true'} 'A194 keyboard resumes beside restored focus'
    $second.PostMessage(@{type='history_list';items=$historyItems;requestId=$historyRead.requestId})
    Wait-Until {(Script $second 'document.querySelectorAll("#historyList .history-item").length===2') -eq 'true'} 'A194 stale pre-delete read rejected'
    $second.PostMessage(@{type='history_changed'})
    Wait-Until {(Script $second 'historyPosts.filter(function(m){return m.type==="list_history";}).length>=2') -eq 'true'} 'A194 correlated refresh'
    $historyRead = Script $second 'JSON.stringify(historyPosts.filter(function(m){return m.type==="list_history";}).pop())' | ConvertFrom-Json | ConvertFrom-Json
    $second.PostMessage(@{type='history_list';items=@($historyItems[2],$historyItems[0]);requestId=$historyRead.requestId})
    Wait-Until {(Script $second 'document.activeElement.dataset.historyId === "a194-c" && document.activeElement.dataset.historyAction === "resume" && document.querySelector("#historyList .history-open").dataset.historyId === "a194-c"') -eq 'true'} 'A194 refresh preserves focused identity'
    [void](Script $second 'var b=document.getElementById("btnDeleteAll"); b.focus(); b.click(); b.click();')
    $deleteAllRequest = Script $second 'JSON.stringify(historyPosts.filter(function(m){return m.type==="delete_all_history";}).pop())' | ConvertFrom-Json | ConvertFrom-Json
    if (!$deleteAllRequest.requestId) {throw 'A194 delete all lost request identity'}
    $second.PostMessage(@{type='new_chat_ready';preservePage=$true})
    $second.PostMessage(@{type='history_list';items=@();deleted=$true;requestId=$deleteAllRequest.requestId})
    Wait-Until {(Script $second 'document.body.dataset.page === "historyDrawer" && document.activeElement.id === "historyTitle" && document.getElementById("srStatus").textContent === "Tutte le conversazioni eliminate."') -eq 'true'} 'A194 delete all stays in empty history'
    $second.PostMessage(@{type='history_changed'})
    Wait-Until {(Script $second 'historyPosts.filter(function(m){return m.type==="list_history";}).length>=3') -eq 'true'} 'A194 next history read'
    $historyRead = Script $second 'JSON.stringify(historyPosts.filter(function(m){return m.type==="list_history";}).pop())' | ConvertFrom-Json | ConvertFrom-Json
    $second.PostMessage(@{type='history_list';items=$historyItems;requestId=$historyRead.requestId})
    Wait-Until {(Script $second 'document.querySelectorAll("#historyList .history-item").length===3') -eq 'true'} 'A194 navigation fixture'
    [void](Script $second 'var b=document.querySelector("[data-history-id=\"a194-b\"][data-history-action=delete]"); b.focus(); b.click(); b.click(); document.getElementById("btnChatPage").click(); document.getElementById("btnChatPage").focus();')
    $deleteRequest = Script $second 'JSON.stringify(historyPosts.filter(function(m){return m.type==="delete_history";}).pop())' | ConvertFrom-Json | ConvertFrom-Json
    $second.PostMessage(@{type='new_chat_ready';preservePage=$true})
    $second.PostMessage(@{type='history_list';items=@($historyItems[0],$historyItems[2]);deleted=$true;requestId=$deleteRequest.requestId})
    Wait-Until {(Script $second 'document.body.dataset.page === "chat" && document.activeElement.id === "btnChatPage"') -eq 'true'} 'A194 late completion does not reopen history or steal focus'
    [void](Script $second 'chrome.webview.postMessage=window.historyOriginalPost;')
    'PASS A191/A194 real WebView: 380x800/190x400 and zoom 200%, stacked filters, compact header, toast/job clearance, Stato, emulated contrast, history focus and keyboard'
    # Actual contextual help dialog: modal semantics, wrapping, Escape and focus restoration.
    $second.PostMessage(@{type='toggle_analysis';preset='summary';toggle=$false})
    Wait-Until {(Script $second '!document.getElementById("analysisDrawer").hidden') -eq 'true'} 'analysis help'
    [void](Script $second 'document.getElementById("anMaxNote").textContent="Descrizione sintetica del limite di ricerca e dei dati protetti."; document.getElementById("anMaxNote").hidden=false;')
    Wait-Until {(Script $second '!document.querySelector("button[aria-label=\"Guida al limite di ricerca\"]").hidden') -eq 'true'} 'context help button'
    [void](Script $second 'window.contextHelpTrigger=document.querySelector("button[aria-label=\"Guida al limite di ricerca\"]"); contextHelpTrigger.focus(); contextHelpTrigger.click();')
    Wait-Until {(Script $second '!!document.querySelector("dialog:modal") && document.activeElement.textContent === "Chiudi"') -eq 'true'} 'modal help and keyboard focus'
    $modalFits = Script $second '(function(){var d=document.querySelector("dialog:modal"); return d.scrollWidth <= d.clientWidth + 1;})()'
    if ($modalFits -ne 'true') {throw 'Context help needs horizontal scrolling'}
    [void]$focus.Invoke($second,@())
    [void](Script $second 'document.querySelector("dialog button").focus();')
    # Browser input on the isolated, deliberately offscreen WebView; SendKeys is not reliable for this host.
    $escapeDown = (Field $second '_webView').CoreWebView2.CallDevToolsProtocolMethodAsync('Input.dispatchKeyEvent','{"type":"keyDown","key":"Escape","code":"Escape","windowsVirtualKeyCode":27,"nativeVirtualKeyCode":27}')
    Wait-Until {$escapeDown.IsCompleted} 'browser Escape down'
    [void]$escapeDown.GetAwaiter().GetResult()
    $escapeUp = (Field $second '_webView').CoreWebView2.CallDevToolsProtocolMethodAsync('Input.dispatchKeyEvent','{"type":"keyUp","key":"Escape","code":"Escape","windowsVirtualKeyCode":27,"nativeVirtualKeyCode":27}')
    Wait-Until {$escapeUp.IsCompleted} 'browser Escape up'
    [void]$escapeUp.GetAwaiter().GetResult()
    try {
        Wait-Until {(Script $second '!document.querySelector("dialog") && document.activeElement === window.contextHelpTrigger') -eq 'true'} 'Escape closes help and restores focus'
    } catch {
        $helpState = Script $second 'JSON.stringify({modal:!!document.querySelector("dialog"),focus:document.activeElement.outerHTML,hasFocus:document.hasFocus(),trigger:window.contextHelpTrigger.outerHTML,drawer:document.getElementById("analysisDrawer").hidden})'
        throw "Context help failure: $helpState"
    }
    'PASS real WebView contextual help: modal, keyboard focus, Escape, focus return and wrapping'
    # A192: malformed host data triggers the actual page boundary, accessible feedback and native log.
    $second.PostMessage(@{type='archive_status';available=$true;active=$true;metrics=@{invalid='fixture'};stores=@()})
    Wait-Until {(Script $second '!document.getElementById("toast").hidden && document.getElementById("toastText").textContent.indexOf("Il riquadro non") === 0') -eq 'true'} 'translated page failure'
    Wait-Until {@($diagnosticLog.Lines.ToArray() | Where-Object {$_ -like '*Pane page update failed:*TypeError*'}).Count -gt 0} 'native page diagnostic log'
    $feedback = Script $second 'document.getElementById("toastText").textContent'
    if ($feedback -match 'TypeError|forEach|Dettaglio:') {throw 'Raw JavaScript exception reached the toast'}
    # Send a deliberately oversized diagnostic: native code independently enforces its own bound.
    [void](Script $second 'window.chrome.webview.postMessage({type:"page_error",detail:"BOUND192"+"x".repeat(5000)})')
    Wait-Until {@($diagnosticLog.Lines.ToArray() | Where-Object {$_ -like '*BOUND192*'}).Count -gt 0} 'bounded native diagnostic'
    $bounded = @($diagnosticLog.Lines.ToArray() | Where-Object {$_ -like '*BOUND192*'})[-1]
    if ($bounded.Length -gt 2100) {throw 'Native page diagnostic was not bounded'}
    'PASS A192 real WebView: Italian failure toast, original JavaScript exception in native log, independent 2048-character bound'
    'PASS real WebView2 focus: queued and ready ribbon actions, Chat/Status/History, no background or delayed focus theft'
    "Runtime=$((Field $second '_environment').BrowserVersionString); SDK=$((Get-Item (Join-Path $output 'Microsoft.Web.WebView2.Core.dll')).VersionInfo.FileVersion)"
    Add-Type -ReferencedAssemblies (Join-Path $output 'OnlyWorkspace.Outlook.dll') -TypeDefinition @'
using OnlyWorkspace.Outlook.Interop;
public sealed class WidthFixturePane : _CustomTaskPane {
    public string Title { get { return "Synthetic width"; } }
    public object Application { get { return null; } }
    public object Window { get { return null; } }
    public bool Visible { get; set; }
    public object ContentControl { get { return null; } }
    public int Height { get; set; }
    private int width = 120;
    public int Attempts;
    public int IgnoreWrites = 1;
    public int Width { get { return width; } set { Attempts++; if (IgnoreWrites-- <= 0) width = value; } }
    public MsoCTPDockPosition DockPosition { get; set; }
    public MsoCTPDockPositionRestrict DockPositionRestrict { get; set; }
    public void Delete() { }
}
'@
    $widthFixture = New-Object WidthFixturePane -Property @{Visible=$true}
    $coordinator = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([OnlyWorkspace.Outlook.UI.TaskPaneCoordinator])
    $entry = New-Object OnlyWorkspace.Outlook.Services.TaskPaneEntry((New-Object object),$widthFixture,$second,$coordinator,540)
    try {
        $entryType=$entry.GetType()
        [void]$entryType.GetMethod('KeepMinimumWidth',$flags).Invoke($entry,@($null,[EventArgs]::Empty))
        $timer=$entryType.GetField('_widthCheck',$flags).GetValue($entry)
        # A resize never writes the width from inside Office's splitter drag: only the timer, once the pane is still.
        if ($widthFixture.Attempts -ne 0 -or !$timer.Enabled) {throw 'The width was written during the resize instead of after it'}
        $tick=$entryType.GetMethod('OnWidthCheckTick',$flags)
        [void]$tick.Invoke($entry,@($null,[EventArgs]::Empty))
        if ($widthFixture.Attempts -ne 1 -or !$timer.Enabled) {throw 'Ignored minimum width was not retried'}
        [void]$tick.Invoke($entry,@($null,[EventArgs]::Empty))
        if ($widthFixture.Attempts -ne 2 -or $widthFixture.Width -ne 540 -or $timer.Enabled) {throw 'Minimum width retry did not stop after acceptance'}
        # An Office that never accepts the width gets a few attempts per resize, not an endless fight.
        $widthFixture.IgnoreWrites=0; $widthFixture.Width=120
        $widthFixture.IgnoreWrites=1000; $widthFixture.Attempts=0
        [void]$entryType.GetMethod('KeepMinimumWidth',$flags).Invoke($entry,@($null,[EventArgs]::Empty))
        for ($i=0; $i -lt 10; $i++) {[void]$tick.Invoke($entry,@($null,[EventArgs]::Empty))}
        if ($widthFixture.Attempts -ne 4 -or $timer.Enabled) {throw "Width retries not bounded: $($widthFixture.Attempts) attempts, timer enabled $($timer.Enabled)"}
        'PASS A201 native width guard: no write during the drag, ignored Office write retried, bounded attempts, timer stops after acceptance'
    } finally {$entry.Dispose()}
}
finally {
    [Serilog.Log]::Logger = $previousLogger
    $fixtureLogger.Dispose()
    $form.Close(); $form.Dispose()
    foreach ($pane in $panes) {$pane.Dispose()}
    $processes=@(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object {$_.CommandLine -and $_.CommandLine.Contains($browserFolder)})
    foreach ($process in $processes) {Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue; Wait-Process -Id $process.ProcessId -Timeout 10 -ErrorAction SilentlyContinue}
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    $resolved=[IO.Path]::GetFullPath($scratch)
    if (!$resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or ![IO.Path]::GetFileName($resolved).StartsWith('OnlyWorkspace-WebViewFocus-')) {throw 'Unsafe fixture cleanup path'}
    if (!(Test-Path Env:ONLYWORKSPACE_TEST_ARTIFACTS) -and (Test-Path -LiteralPath $resolved)) {Remove-Item -LiteralPath $resolved -Recurse -Force}
}
