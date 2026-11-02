# Analisi statica

Da PowerShell 7, su Windows con .NET SDK 10, Node.js e Python 3:

```powershell
Install-Module PSScriptAnalyzer -RequiredVersion 1.25.0 -Scope CurrentUser -Repository PSGallery
pwsh -NoProfile -File scripts/check-code.ps1
```

Il comando compila Release con gli avvisi trattati come errori, esegue gli
analizzatori SDK e verifica le correzioni disponibili con `dotnet format analyzers`.
`Directory.Build.props` abilita gli analizzatori anche per net48 e fissa il livello
10.0. Non aggiunge pacchetti Roslyn. Non abilita indiscriminatamente tutte le regole.

`PSScriptAnalyzerSettings.psd1` seleziona dieci regole di correttezza: variabili
automatiche, Invoke-Expression, parametri riservati o username/password, confronti
con null, operatori sospetti, backtick, switch con valore predefinito e hash deboli.
Errori di parsing e rilievi delle regole selezionate fanno fallire il comando.
Le regole stilistiche del preset predefinito non fanno parte di questo gate.

Node verifica la sintassi dei file JavaScript e dello script incorporato in
TaskPane.html. Python compila in memoria i generatori senza importarli, eseguirli o
scrivere bytecode. Sono controlli di sintassi, non analisi del flusso o regole di
stile JavaScript/Python. Vengono verificati anche JSON e XML tecnici; YAML, NSIS,
SVG e CSS non hanno un linter dedicato in questo comando. Packaging e comportamento
del riquadro restano coperti dai test a script esistenti.

`.editorconfig` dà indicazioni agli editor e ai formattatori. Per formattare una
modifica C# circoscritta, su una copia verificabile del checkout:

```powershell
dotnet format whitespace OnlyWorkspace.sln --no-restore --include src/Percorso/File.cs
```

Il percorso è un segnaposto da sostituire con quello del file modificato. Il gate
non impone una riformattazione generale e non applica correzioni automatiche.
Per i controlli locali con SecondBrain, usare una copia sotto `.runtime`, con
TEMP e fixture nella stessa area centrale: la build ordinaria usa bin/obj nel
checkout su cui viene eseguita. L'esclusione di bin/obj e directory di cache dalla
scansione non ne centralizza la destinazione.

Per una revisione PowerShell più ampia, distinta dal gate:

```powershell
Invoke-ScriptAnalyzer -Path scripts -Recurse -Severity Error,Warning
Invoke-ScriptAnalyzer -Path tests/scripts -Recurse -Severity Error,Warning
```

Le dieci regole selezionate non includono i controlli sulle interfacce password
PFX degli script di firma: per valutarle usare anche la revisione più ampia.
Il gate non è una verifica completa di sicurezza o di funzionamento in Outlook.

Riferimenti: [analizzatori .NET](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview),
[configurazione delle regole](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/configuration-options),
[PSScriptAnalyzer](https://learn.microsoft.com/en-us/powershell/utility-modules/psscriptanalyzer/using-scriptanalyzer),
[Node --check](https://nodejs.org/api/cli.html#-c---check),
[Python compile](https://docs.python.org/3.13/library/functions.html#compile).
