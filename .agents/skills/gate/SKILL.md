---
name: gate
description: Run the complete OnlyWorkspace verification gate (static analysis and build, net48 and net10 tests, x64/x86 smoke, script tests, NuGet audit) and report counts and outcomes in the receipt format. Invoke explicitly before a commit; it does not qualify Outlook, installed builds or cloud services.
---

# Complete gate

Commands match the table in `docs/tecnico/verifica.md` and `.github/workflows/ci.yml`. Run them from the repository root, **one after the other** (parallel runs hit MSB3021/MSB3026 on the shared output tree), stopping at the first failure to report it in full.

1. Output location: if `ONLYWORKSPACE_RUNTIME_ROOT` is unset in the user environment, say so and use the repository default; never invent a root (see `docs/tecnico/output-locali.md`).
2. `pwsh -NoProfile -File scripts/check-code.ps1` — analyzers, PSScriptAnalyzer, JavaScript/skill-bridge syntax, Release build.
3. `dotnet test tests/OnlyWorkspace.Tests -c Release --no-build -f net48`, then `-f net10.0-windows`. Record the passed/failed/skipped totals of each runtime. If a test fails right after a build on a loaded machine (A234, `ProcessRunnerTests`, `HttpErrorCancellationTests`; task #065), run `dotnet build-server shutdown`, wait about 15 s and repeat once; report both outcomes, never only the green one.
4. `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-net48.ps1`, then the same script with `%WINDIR%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe`.
5. `pwsh -NoProfile -File scripts\test-scripts.ps1` — record the number of scripts run.
6. `dotnet package list --project OnlyWorkspace.sln --vulnerable --include-transitive --format json`: the command exits 0 either way, so fail if the report contains `"vulnerabilities"`.

Summary, in the style of the receipts in `docs/tecnico/verifica.md`: one paragraph with test totals per runtime, scripts run, smoke x64/x86, NuGet result and any retry with its reason. A failure that disappears on rerun stays reported (A234 is the known case). State what was **not** run: manual smokes (`WebView*.Smoke.ps1`, `OllamaLive.Tests.ps1`), installed build, live Outlook, real cloud. A green gate is not a qualification of those.
