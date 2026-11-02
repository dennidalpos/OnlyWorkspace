<#
.SYNOPSIS
    Verifies that resolving an API path does not initialize TLS before an injected HTTP handler runs.
.DESCRIPTION
    Windows PowerShell 5.1, after the Release build. Each provider runs in a fresh process:
    LlmHttp has a net48 static initializer, so a previous request would hide an ordering regression.
    All requests use an in-memory handler; no provider, configuration or credential is accessed.
#>
param(
    [string]$Dir = '',
    [ValidateSet('openai', 'anthropic')][string]$Provider
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Use Windows PowerShell 5.1 for the net48 initialization test.' }
if (!$Dir) { $Dir = Join-Path (Resolve-Path "$PSScriptRoot/../..").Path 'src/OnlyWorkspace.Outlook/bin/Release/net48' }
$Dir = (Resolve-Path -LiteralPath $Dir).Path
if (!$Provider) {
    $shell = Join-Path $PSHOME 'powershell.exe'
    foreach ($family in 'openai', 'anthropic') {
        & $shell -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Dir $Dir -Provider $family
        if ($LASTEXITCODE -ne 0) { throw "Endpoint initialization failed: $family" }
    }
    return
}

[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Core.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $Dir 'OnlyWorkspace.Infrastructure.dll'))
Add-Type -ReferencedAssemblies @((Join-Path $Dir 'OnlyWorkspace.Core.dll'), (Join-Path $Dir 'OnlyWorkspace.Infrastructure.dll'), 'System.Net.Http', 'System', 'System.Core') -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Llm;
public sealed class EndpointInitializationProbe : HttpMessageHandler
{
    private const string Switch = "Switch.System.Net.DontEnableSystemDefaultTlsVersions";
    private bool _observed;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        AppContext.TryGetSwitch(Switch, out _observed);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
    }
    public static bool Run(string provider)
    {
        AppContext.SetSwitch(Switch, true);
        using (var handler = new EndpointInitializationProbe())
        using (var http = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/v1/") })
        {
            var profile = new LlmProfileConfig { Type = provider, BaseUrl = "https://fixture.invalid/v1", Model = "fixture" };
            ILlmClient client = provider == "openai" ? (ILlmClient)new OpenAIClient(profile, httpClient: http) : new AnthropicClient(profile, httpClient: http);
            try { client.ListModelsAsync().GetAwaiter().GetResult(); }
            finally { ((IDisposable)client).Dispose(); }
            return handler._observed;
        }
    }
}
'@
if (![EndpointInitializationProbe]::Run($Provider)) { throw 'TLS initialized before the injected HTTP handler.' }
'PASS injected HTTP endpoint preserves TLS initialization order: ' + $Provider
