# Output locali di build e verifica

`ONLYWORKSPACE_RUNTIME_ROOT` è una configurazione facoltativa della postazione: deve indicare una cartella assoluta esistente. Nei checkout gestiti la cartella risiede sotto SecondBrain `.runtime`, con nome `work-<yyyyMMdd>-<slug>`. Il valore non è incluso nel repository. Dopo una modifica delle variabili utente, riaprire terminali e IDE affinché ereditino il nuovo ambiente.

`Directory.Build.props` imposta `ArtifactsPath` sotto questa radice, separando i checkout con i primi 12 caratteri SHA-256 del percorso canonico in minuscolo. Il layout SDK separa progetti, configurazioni, framework e RID. Un `ArtifactsPath` esplicito conserva la precedenza. Senza la variabile rimane il layout SDK ordinario; una radice configurata ma invalida produce errore. I vecchi `bin/obj` restano conservati ed esclusi dagli input sorgente.

`scripts/build-paths.ps1` risolve `TargetPath`, `TargetDir` e il file degli asset NuGet tramite MSBuild. Harness, copie dell'extractor e packaging usano i percorsi valutati; un output mancante non viene sostituito da un vecchio publish. I test ricevono anche i percorsi del repository e dell'add-in come metadati generati durante la build.

Con `-Dir` esterno al grafo corrente, `PanePolicy.Tests.ps1` richiede extractor e ConfigTool già presenti nella cartella del candidato: non sostituisce componenti mancanti con quelli di un'altra build.

Le cache NuGet possono essere instradate con `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`, `NUGET_PLUGINS_CACHE_PATH` e `NUGET_SCRATCH`. Le impostazioni utente interessano anche gli altri repository della postazione: conservare i valori precedenti e le cache esistenti prima di cambiarle. I percorsi effettivi si controllano con `dotnet nuget locals all --list`.

Con la centralizzazione attiva, gli harness e le impostazioni VSTest generate applicano TEMP/TMP e `ONLYWORKSPACE_TEST_ARTIFACTS` soltanto ai processi di verifica. Le fixture hanno nomi univoci sotto radici centrali brevi `tmp-<yyyyMMdd>-<checkout>` e `fixture-<yyyyMMdd>-<checkout>`, per i limiti di percorso net48/NSIS. I finalizzatori conservano le fixture gestite; le rimozioni che simulano assenze o errori restano parte dei test. La conservazione delle fixture non modifica i dati dell'applicazione installata.

Comandi dalla radice:

```powershell
dotnet build OnlyWorkspace.sln -c Release --nologo
dotnet test OnlyWorkspace.sln -c Release --no-build --nologo
pwsh -NoProfile -File scripts/test-scripts.ps1
```

I due target di test restano sequenziali. Gli script WebView e `smoke-net48.ps1` richiedono Windows PowerShell 5.1; gli output predefiniti vengono risolti dalla build corrente. `BuildPaths.Tests.ps1` verifica configurazione assente o invalida, percorsi con spazi, isolamento dei checkout e rifiuto dei binari precedenti. Le prove isolate restano distinte dalla qualifica della build installata.

Riferimenti: [layout artifact SDK](https://learn.microsoft.com/en-us/dotnet/core/sdk/artifacts-output), [proprietà MSBuild](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties), [cache NuGet](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders).
