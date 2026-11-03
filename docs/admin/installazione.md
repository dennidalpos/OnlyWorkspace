# Installazione

Il setup [NSIS](../../installer/OnlyWorkspaceSetup.nsi) installa l'add-in COM gestito (senza VSTO Runtime) per Outlook classico 2016 e successivi, a 32 e 64 bit, su Windows x64. Prerequisiti: .NET Framework 4.8 e WebView2 Evergreen, rilevato dal valore `pv` delle chiavi documentate da Microsoft (versione maggiore di 0.0.0.0; la sola cartella non basta). WebView2 assente viene scaricato e accettato solo con firma Microsoft valida, e il setup prosegue solo se dopo il bootstrapper il runtime risulta registrato. Se `icacls`, `certutil` o `RegAsm` falliscono (codice diverso da 0, avvio non riuscito) il setup si ferma con codice di uscita 2: la configurazione esistente resta com'era e un'installazione fermata dopo la copia dei file resta disinstallabile da App e funzionalità.

## Setup firmato

`.\scripts\build-installer.ps1 [-SkipTests] [-OutputDirectory <nuova-cartella>]` compila, esegue i test (salvo `-SkipTests`), prepara `publish\` nella nuova cartella del pacchetto, firma binari e disinstallatore, compila NSIS e firma `dist\OnlyWorkspaceSetup.exe` nella stessa cartella. La destinazione viene stampata al termine. Senza `-OutputDirectory` viene creata una cartella univoca sotto gli artifact centrali, se configurati, altrimenti sotto `dist\` del checkout. Una destinazione già esistente è rifiutata; i pacchetti precedenti e i tentativi falliti restano conservati. Dettagli: [Output locali](../tecnico/output-locali.md).

- Richiede .NET SDK, NSIS 3.13, `signtool.exe`, `certs\OnlyApps.pfx` e `OnlyApps.cer`: senza la coppia la build si ferma e non genera un certificato nuovo. Ogni firma ha la marca temporale RFC 3161; se il server non risponde la firma si ferma (`sign-binaries.ps1 -AllowNoTimestamp` solo per prove locali).
- La password del PFX sta solo nella variabile `ONLYAPPS_PFX_PASSWORD`, senza parametro alternativo: se manca, `sign-binaries.ps1` e `create-code-signing-cert.ps1` si fermano con un errore (il file ignorato `.local-secrets/onlyapps-signing.ps1` la imposta nella sessione; non mostrarne il contenuto). Non rigenerare il certificato già distribuito.
- Se `signtool verify` segnala una radice non attendibile, confrontare l'impronta con `certs\OnlyApps.cer` e importare **solo quel certificato pubblico** in `Cert:\CurrentUser\Root` di chi compila.
- La versione è in [Directory.Build.props](../../Directory.Build.props).

## Installare

Chiudere Outlook. Il setup chiede l'elevazione, copia i file, valida o crea `%ProgramData%\OnlyWorkspace\config.json` e solo dopo installa il certificato pubblico (Root e TrustedPublisher), registra `OnlyWorkspace.Outlook.dll` con `regasm` a 32 e 64 bit e scrive la chiave `Addins\OnlyWorkspace.Addin` (`LoadBehavior=3`). Una configurazione non valida ferma il setup senza lasciare un add-in registrato a metà. Per l'utente che installa toglie un'eventuale disattivazione in HKCU; gli altri usano **Ripara add-in** nel ConfigTool.

| Parametro | Effetto |
| --- | --- |
| `/S` | Silenzioso: codice 0 successo, 2 stop previsto (per esempio Outlook aperto) |
| `/CONFIG=<file>` | Importa una configurazione sorgente con segreti in chiaro (proteggere e poi rimuovere il file) |
| `/BLOCKNEWOUTLOOK=0` | Non applica le policy che nascondono «Prova il nuovo Outlook» (componente selezionato per default) |

Nel setup silenzioso il ConfigTool/tray in esecuzione viene chiuso e ne viene richiesto il riavvio tramite Explorer con l'utente collegato; senza desktop interattivo ripartirà al prossimo accesso. Il setup interattivo chiede invece di chiuderlo e propone alla fine l'avvio della Configurazione, che al primo avvio apre la [procedura guidata](configtool.md#primo-avvio).

Script per lo sviluppo: [register-addin.ps1](../../scripts/register-addin.ps1) (`-CheckOnly`), [unregister-addin.ps1](../../scripts/unregister-addin.ps1), [block-new-outlook.ps1](../../scripts/block-new-outlook.ps1) (`-Action Check|Apply|Revert`; `Revert` toglie solo i valori creati da `Apply`, annotati in `Software\OnlyWorkspace\NewOutlookBlock` dello stesso hive). Modificano il PC.

## Disinstallare

La disinstallazione interattiva chiede se mantenere configurazione e segreti in ProgramData (default **Sì**) e se eliminare l'archivio dell'utente corrente (default **No**); la pagina finale elenca i dati che restano (`%LOCALAPPDATA%\OnlyWorkspace`, `%APPDATA%\OnlyWorkspace\preferences.json`). `/S /PURGE` elimina configurazione, vault (anche le copie non leggibili in `%LOCALAPPDATA%\OnlyWorkspace\config-non-leggibile`) e archivio dell'utente che esegue: solo dopo un backup verificato. Con o senza `/PURGE`, per l'utente che disinstalla viene rimossa la voce `DoNotDisableAddinList\OnlyWorkspace.Addin` scritta da **Ripara add-in**. Vale solo `/PURGE` senza valore: `/PURGE=1` o altre varianti conservano i dati. Gli archivi degli altri utenti Windows e le email in Outlook non vengono toccati. Rimuove solo le policy del nuovo Outlook create dal setup. Gli esiti di `RegAsm /u` (a 64 bit solo su Windows x64) e `certutil -delstore` restano nel log della disinstallazione, che prosegue anche se uno fallisce.

## Aggiornare

Tray e add-in vanno aggiornati insieme (pipe v15). I formati 11 e 12 vengono migrati al formato 13 soltanto dopo un backup cifrato verificato, con chiave DPAPI e manifest SHA-256. I dati IA pregressi e le chiusure delle pratiche ritirate sono rimossi, mentre originali, allegati, fatti, indici e scelte sono conservati. Le copie rimangono in `%LOCALAPPDATA%\OnlyWorkspace\archive-migration-backups`. I formati sconosciuti sono rifiutati senza ricostruzioni distruttive. Per tornare indietro ripristinare insieme build precedente e backup verificato dell'archivio. Dettagli: [Motore dell'archivio](../tecnico/archivio-motore.md#formato-conservazione-e-ricostruzione).
