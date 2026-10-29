# Istruzioni del repository: OnlyWorkspace

<!-- secondbrain:repository:begin -->
Direttive comuni applicate: revisione **4.0** — 2026-10-29. <!-- secondbrain:source global=7585B84CED201216722B7E6646C3D2845045FEEE7E44C889E92E68A5E1975A4A workstation=2BC7FB0217FB690B8CEAB7E3DD85AAB916BA4CF3016533BC9E1D02477CE9321B -->SecondBrain: integrazione opzionale della conoscenza e delle direttive comuni. Risolvi SECONDBRAIN_HOME dallʼambiente utente della postazione; nessun percorso personale è conservato in questo repository. Se il clone privato non è disponibile, prosegui con le istruzioni tecniche locali e segnala lʼintegrazione non configurata; non creare memorie o stub sostitutivi. Dal clone SecondBrain usa scripts/prompts.ps1 -Context -RepositoryPath <checkout-corrente> per risolvere il dossier e il suo index.md. Carica indice, contesto sintetico, memoria essenziale e proiezione task pertinente; approfondimenti e storico soltanto quando servono. Memorie private, tracker interni, backup e output operativi risiedono in SecondBrain. Conserva qui codice, guide e asset pubblicabili. Le direttive non autorizzano commit, push, cancellazioni di artefatti o task applicativi impliciti.
<!-- secondbrain:repository:end -->

Revisione specifica del progetto 2.11 — 2026-10-26. Vincoli specifici del progetto; i contenuti comuni sono gestiti separatamente.

## Contesto

- **Scopo**: Add-in COM gestito per Microsoft Outlook classico (2016 e successivi) con analisi IA e archivio locale cifrato della posta.
- **Modalità**: Greenfield (semplificazione aggressiva per architettura minimale; no retrocompatibilità o shim legacy non concordati)
- **Target Applicativo**: Desktop Windows x86 e x64 (.NET Framework 4.8 / WinForms COM Add-in + WebView2 UI); ConfigTool e riquadro anche su display secondari a DPI 100/125/150%.
- **Stack & Toolchain**: C# (`net48`, `net10.0-windows`), Windows Forms/WebView2, SQLite3MC, xUnit, `dotnet`, PowerShell (`pwsh` e Windows PowerShell 5.1 per lo smoke net48) e NSIS (`installer/OnlyWorkspaceSetup.nsi`).

## Vincoli specifici

  * **Segreti**: Nessuna credenziale o token nel codice o nei documenti. Il progetto non usa `.env`: le chiavi dei provider stanno nel vault DPAPI `secrets.bin` (ignorato da Git), la password del PFX solo nella variabile `ONLYAPPS_PFX_PASSWORD`.
  * Preservare `.local-secrets/onlyapps-signing.ps1` ignorato da Git (firma installer); non leggerne, stampare, sovrascrivere o committare il contenuto.
  * Accesso a Outlook Object Model consentito ESCLUSIVAMENTE sul thread UI STA.
  * Raccolta posta solo dagli store aperti in Outlook; nessun connettore Graph/IMAP/OAuth.
  * Compatibilità binaria e di runtime sia x86 che x64; gestione DPI scaling senza artefatti.

## Manutenzione, fonti e sicurezza

- **Test**: mantenere i test aggiornati al comportamento previsto; rimuovere quelli legacy relativi a funzionalità eliminate o requisiti superati, preservando la copertura dei comportamenti ancora supportati. Aggiornare anche fixture, script e riferimenti coinvolti.
- **Legacy**: rimuovere codice, dipendenze e percorsi legacy nel perimetro autorizzato, dopo averne verificato utilizzi e dipendenze; non introdurre shim o compatibilità non concordati. Preservare dati persistenti e lavoro locale estraneo.
- **Fonti**: documentarsi sempre su fonti primarie autorevoli e aggiornate, compatibili con le versioni effettive del progetto; citare i riferimenti che sostengono le scelte e dichiarare ciò che non è verificabile.
- **Sicurezza informatica**: applicare le best practice e le direttive di sicurezza pertinenti allo stack e ai flussi modificati, usando riferimenti ufficiali aggiornati; verificare privilegi, segreti, input non fidati, confini tra WebView2 e codice nativo, accesso ai dati e dipendenze secondo il rischio della modifica.
- **Dubbi**: fare domande quando restano dubbi sostanziali su requisiti, perimetro o scelte tecniche che i file e le fonti non risolvono; esplicitare le assunzioni a basso rischio e procedere.

## Comandi e verifica

Comandi verificati staticamente contro i file del progetto; questa revisione delle direttive non certifica l'esecuzione di build o test applicativi. Consultare anche i gate della CI, quando presenti.

| Attività | Comando | Ambiente | Evidenza e limiti |
|---|---|---|---|
| **Restore** | `dotnet restore OnlyWorkspace.sln` | pwsh / root | Ripristino dipendenze NuGet |
| **Build Release** | `dotnet build OnlyWorkspace.sln -c Release --nologo` | pwsh / root | Compilazione soluzione in configurazione Release |
| **Targeted checks** | `dotnet test OnlyWorkspace.sln -c Release --filter "FullyQualifiedName~Target" --nologo` | pwsh / root | Test rapidi mirati all'ambito modificato |
| **Full checks** | `dotnet test OnlyWorkspace.sln -c Release --nologo && dotnet build OnlyWorkspace.sln -c Release --nologo && pwsh -NoProfile -File scripts\test-scripts.ps1` | pwsh / root | **Gate obbligatorio pre-commit** (deve uscire con exit code 0) |
| **Script tests** | `pwsh -NoProfile -File scripts\test-scripts.ps1` | pwsh / root (richiede node, NSIS 3.13 e Windows PowerShell 5.1) | Dopo il Build Release: script node del riquadro, `Packaging.Tests.ps1`, `ArchiveRecovery`, `ArchiveCapacity` e `PanePolicy` (riquadro reale net48 e import isolato); come in CI. Esclusi `OllamaLive.Tests.ps1`, `WebViewBoundary.Smoke.ps1`, `WebViewRecovery.Smoke.ps1` e `WebViewFocus.Smoke.ps1` da lanciare a mano: `pwsh` per OllamaLive (Ollama sul loopback), Windows PowerShell 5.1 `-STA` per i tre WebView2 (runtime richiesto); focus anche x86 |
| **Smoke test .NET 4.8** | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-net48.ps1 -Dir src\OnlyWorkspace.Outlook\bin\Release\net48` e lo stesso con `& "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe"` al posto di `powershell.exe` | pwsh / root (avvia Windows PowerShell 5.1 x64 e x86) | Caricamento dell'add-in senza binding redirect, dopo il Build Release. Senza `-Dir` prova `publish\`, che può essere una build vecchia |
| **Vulnerabilità** | `dotnet package list --project OnlyWorkspace.sln --vulnerable --include-transitive --format json` | pwsh / root | Esce 0 anche con risultati: fallisce se il JSON contiene `"vulnerabilities"` |

## Riferimenti e collaborazione

- Consultare README e documentazione effettivamente presenti; versioni dai manifest/lockfile, non da ipotesi sullo stack.
- 02-areas/products/OnlyWorkspace/tracker.json (contratto tracker v1 di SecondBrain, `config/tracker.schema.json`) contiene soltanto lavoro aperto in `tasks[]`, ciascuno con `id`, `title`, `status`, `execution`, `priority`, `dependencies`, `acceptance`, `evidence`, `provenance` ed `extensions`; i task parziali o bloccati restano con `status` e motivo in `extensions.stateReason`. Rimuovere i task conclusi solo dopo le verifiche e registrarne l'esito in un rapporto `history/<data>-<id>/report.md` del dossier, referenziato con SHA-256 in `history[]`; censire i residui confermati come nuovi task. docs/tecnico/verifica.md descrive solo comandi, limiti e stato di qualifica correnti, senza cronache di lotto. Alla chiusura riallineare documenti, skill, dossier SecondBrain e direttive solo dove il comportamento o lo stato cambiano, sostituendo informazioni superate e accorpando duplicati invece di aggiungere cronache.
- 02-areas/products/OnlyWorkspace/memory.md e 02-areas/products/OnlyWorkspace/context.md (nel dossier SecondBrain) conservano decisioni tecniche durevoli, gotcha e architettura; aggiornarli a fine task.
- Skill locali in .agents/skills/<nome>/SKILL.md: leggere quella pertinente; il caricamento automatico dipende dallo strumento.
- Eseguire i controlli previsti per la modifica prima di un commit richiesto. Nessun commit o push automatico.
- Preservare dati persistenti e configurazioni esterne; non estendere la manutenzione a operazioni distruttive non autorizzate.
