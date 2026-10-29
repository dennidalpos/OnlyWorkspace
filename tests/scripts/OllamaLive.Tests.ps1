param(
    [string]$BaseUrl = 'http://127.0.0.1:11434',
    [string]$Model = 'qwen3.5:4b',
    [int]$NumCtx = 8192,
    [int]$MaxOutputTokens = 256,
    [int]$ContextBudgetTokens = 0
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$bin = Join-Path $repo 'tests\OnlyWorkspace.Tests\bin\Release\net10.0-windows'
Add-Type -Path (Join-Path $bin 'OnlyWorkspace.Core.dll')
Add-Type -Path (Join-Path $bin 'OnlyWorkspace.Infrastructure.dll')

$profile = [OnlyWorkspace.Core.Models.LlmProfileConfig]::new()
$profile.BaseUrl = $BaseUrl
$profile.Model = $Model
$profile.NumCtx = $NumCtx
$profile.MaxOutputTokens = $MaxOutputTokens
$profile.ContextBudgetTokens = if ($ContextBudgetTokens -gt 0) { $ContextBudgetTokens } else { [OnlyWorkspace.Core.Models.ProviderCatalog]::ComputeBudget($NumCtx, $MaxOutputTokens) }
$profile.Temperature = 0
$profile.KeepAlive = '1m'
$profile.DisableThinking = $true
$profile.FirstTokenTimeoutSeconds = 120
$profile.IdleTimeoutSeconds = 60

$context = [OnlyWorkspace.Core.Context.ContextBuilder]::new()
$tooLong = 'x' * ([OnlyWorkspace.Core.Context.ContextBuilder]::MaxChars($profile.ContextBudgetTokens) + 1)
try {
    [void]$context.EnsureFits($tooLong, $profile.ContextBudgetTokens)
    throw 'The over-budget document was accepted.'
} catch [OnlyWorkspace.Core.Context.ContextTooLongException] {
    if ($_.Exception.BudgetTokens -ne $profile.ContextBudgetTokens) { throw }
}

$lines = for ($i = 1; $i -le 160; $i++) {
    'Riga {0:D3}: ordine sintetico; fornitura, data e importo sono segnaposto senza dati personali.' -f $i
}
$document = ($lines -join "`n") + "`nCODICE_FINALE: OMEGA-8421"
$prompt = $context.EnsureFits(('Copia nel campo marker esattamente il valore che segue CODICE_FINALE: nell ultima riga del documento. Imposta found=true. Non usare le parole found o true come marker.' + "`n" + $document), $profile.ContextBudgetTokens)
$schema = '{"type":"object","properties":{"marker":{"type":"string"},"found":{"type":"boolean"}},"required":["marker","found"],"additionalProperties":false}'
$messages = [OnlyWorkspace.Core.Models.ChatMessage[]]@([OnlyWorkspace.Core.Models.ChatMessage]::User($prompt))

$client = [OnlyWorkspace.Infrastructure.Llm.OllamaClient]::new($profile)
try {
    $health = $client.CheckHealthAsync().GetAwaiter().GetResult()
    if (!$health.IsHealthy) { throw "Ollama is unavailable: $($health.ErrorMessage)" }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $answer = $client.CompleteStructuredAsync($messages, $schema).GetAwaiter().GetResult() | ConvertFrom-Json
    $timer.Stop()
    if ($answer.marker -ne 'OMEGA-8421' -or !$answer.found) { throw "Unexpected structured answer: $($answer | ConvertTo-Json -Compress)" }
    $usage = $client.LastTokenUsage
    if ($null -eq $usage -or $client.LastCompletion.IsTruncated) { throw 'Structured response has missing usage or is truncated.' }
    "PASS structured stream: endpoint=$BaseUrl model=$Model chars=$($prompt.Length) elapsedMs=$($timer.ElapsedMilliseconds) promptTokens=$($usage.PromptTokens) outputTokens=$($usage.CompletionTokens)."

    $stop = [Threading.CancellationTokenSource]::new()
    try {
        $pending = $client.CompleteStructuredAsync($messages, $schema, $stop.Token)
        [Threading.Tasks.Task]::Delay(250).GetAwaiter().GetResult()
        if ($pending.IsCompleted) { throw 'Cancellation request completed before cancellation could be tested.' }
        $stop.Cancel()
        try {
            [void]$pending.GetAwaiter().GetResult()
            throw 'Cancellation did not stop the structured response.'
        } catch [OperationCanceledException] {
            'PASS in-flight cancellation: structured response stopped without a completed result.'
        }
    } finally { $stop.Dispose() }
    "PASS context limit: budgetTokens=$($profile.ContextBudgetTokens) maxChars=$([OnlyWorkspace.Core.Context.ContextBuilder]::MaxChars($profile.ContextBudgetTokens)); over-budget input rejected before send."
} finally { $client.Dispose() }
