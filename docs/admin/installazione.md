# Installazione

Il setup [NSIS](../../installer/OnlyWorkspaceSetup.nsi) installa l'add-in COM gestito (senza VSTO Runtime) per Outlook classico 2016 e successivi, a 32 e 64 bit, su Windows x64. Prerequisiti: .NET Framework 4.8 e WebView2 Evergreen, rilevato dal valore `pv` delle chiavi documentate da Microsoft (versione maggiore di 0.0.0.0; la sola cartella non basta). WebView2 assente viene scaricato e accettato solo con firma Microsoft valida, e il setup prosegue solo se dopo il bootstrapper il runtime risulta registrato. Il setup valida la configurazione prima di distribuire i file nella destinazione. Se `certutil` o `RegAsm` falliscono, il setup esce con codice 2 e la voce in App e funzionalità resta disponibile.

## Setup firmato

`.\scripts\build-installer.ps1 [-SkipTests] [-OutputDirectory <nuova-cartella>]` compila, esegue i test (salvo `-SkipTests`), prepara `publish\` nella nuova cartella del pacchetto, firma binari e disinstallatore, compila NSIS e firma `dist\OnlyWorkspaceSetup.exe` nella stessa cartella. La destinazione viene stampata al termine. Senza `-OutputDirectory` viene creata una cartella univoca sotto gli artifact centrali, se configurati, altrimenti sotto `dist\` del checkout. Una destinazione già esistente è rifiutata; i pacchetti precedenti e i tentativi falliti restano conservati. Dettagli: [Output locali](../tecnico/output-locali.md).

- Richiede .NET SDK, NSIS 3.13, `signtool.exe`, `certs\OnlyApps.pfx` e `OnlyApps.cer`: senza la coppia la build si ferma e non genera un certificato nuovo. Ogni firma ha la marca temporale RFC 3161; se il server non risponde la firma si ferma (`sign-binaries.ps1 -AllowNoTimestamp` solo per prove locali).
- La password del PFX sta solo nella variabile `ONLYAPPS_PFX_PASSWORD`, senza parametro alternativo: se manca, `sign-binaries.ps1` e `create-code-signing-cert.ps1` si fermano con un errore (il file ignorato `.local-secrets/onlyapps-signing.ps1` la imposta nella sessione; non mostrarne il contenuto). `sign-binaries.ps1` non passa la password a `signtool`: importa il PFX in `Cert:\CurrentUser\My` con la password come SecureString, firma per impronta (`/sha1`) e poi rimuove certificato e chiave importati (se il certificato era già nell'archivio lo lascia). Non rigenerare il certificato già distribuito.
- Se `signtool verify` segnala una radice non attendibile, confrontare l'impronta con `certs\OnlyApps.cer` e importare **solo quel certificato pubblico** in `Cert:\CurrentUser\Root` del computer di build: i PC con il setup ricevono il certificato solo in TrustedPublisher, e il setup accetta la firma dei file con radice non attendibile solo se l'impronta coincide con `OnlyApps.cer`.
- La versione è in [Directory.Build.props](../../Directory.Build.props).

## Installare

Chiudere Outlook. Il setup chiede l'elevazione, copia i file, valida o crea `%ProgramData%\OnlyWorkspace\config.json` e solo dopo installa il certificato pubblico solo in TrustedPublisher (una voce Root lasciata da versioni precedenti viene rimossa), registra `OnlyWorkspace.Outlook.dll` con `regasm` a 32 e 64 bit e scrive la chiave `Addins\OnlyWorkspace.Addin` (`LoadBehavior=3`). Una configurazione non valida ferma il setup senza lasciare un add-in registrato a metà. Per l'utente che installa toglie un'eventuale disattivazione in HKCU; gli altri usano **Ripara add-in** nel ConfigTool.

| Parametro | Effetto |
| --- | --- |
| `/S` | Silenzioso: codice 0 successo, 2 stop previsto (per esempio Outlook aperto) |
| `/CONFIG=<file>` | Importa una configurazione sorgente con segreti in chiaro (proteggere e poi rimuovere il file) |
| `/BLOCKNEWOUTLOOK=0` | Non applica le policy che nascondono «Prova il nuovo Outlook» (componente selezionato per default) |

Nel setup silenzioso il ConfigTool/tray in esecuzione viene chiuso e ne viene richiesto il riavvio tramite Explorer con l'utente collegato; senza desktop interattivo ripartirà al prossimo accesso. Il setup interattivo chiede invece di chiuderlo e propone alla fine l'avvio della Configurazione, che al primo avvio apre la [procedura guidata](configtool.md#primo-avvio).

Alla prima installazione o al passaggio da un setup senza inventario, scegliere una cartella nuova o vuota. La cartella del vecchio setup resta sul disco. In modalità silenziosa, indicare la nuova destinazione con `/D=C:\Percorso\OnlyWorkspace` come ultimo argomento, senza virgolette. Il setup salva l'inventario dei file nella chiave di disinstallazione del registro di sistema. Gli aggiornamenti successivi usano la cartella registrata, rimuovono i file del pacchetto precedente ritirati e si fermano se un nuovo file del pacchetto sovrascriverebbe un file estraneo. In caso di errore durante la copia, il rollback ripristina i file precedenti. Se il ripristino è incompleto, il setup restituisce 3. [Parametri NSIS](https://nsis.sourceforge.io/Docs/Chapter3.html)

Dopo aver registrato la nuova installazione, il setup verifica che la vecchia voce indichi `Uninstall.exe` nella cartella precedente e controlla i metadati OnlyWorkspace e la firma OnlyApps. Rinomina quel file in `Uninstall.exe.disabled` e conserva gli altri file della vecchia cartella. Se il file è bloccato, la verifica fallisce o il nuovo nome è già occupato, il setup segnala il codice 3 e la voce corrente in App e funzionalità resta disponibile. Ripetere il setup dopo aver risolto il problema segnalato. Per disinstallare, usare la voce corrente.

Il disinstallatore rimuove i file presenti nell'inventario e il collegamento OnlyWorkspace dal menu Start. Conserva gli altri file e rifiuta i percorsi di programma reindirizzati. Una pulizia incompleta è segnalata con codice 3 dal processo del disinstallatore. Se un file programma è bloccato, la voce in App e funzionalità resta disponibile per un nuovo tentativo.

Script per lo sviluppo: [register-addin.ps1](../../scripts/register-addin.ps1) (`-CheckOnly`), [unregister-addin.ps1](../../scripts/unregister-addin.ps1), [block-new-outlook.ps1](../../scripts/block-new-outlook.ps1) (`-Action Check|Apply|Revert`; `Revert` toglie solo i valori creati da `Apply`, annotati in `Software\OnlyWorkspace\NewOutlookBlock` dello stesso hive). Modificano il PC.

## Disinstallare

La disinstallazione interattiva chiede se mantenere configurazione e segreti in ProgramData (default **Sì**) e se eliminare l'archivio dell'utente corrente (default **No**). La pagina finale elenca i dati che restano (`%LOCALAPPDATA%\OnlyWorkspace`, `%APPDATA%\OnlyWorkspace\preferences.json`). `/S /PURGE` richiede la rimozione di configurazione, vault (incluse le copie non leggibili in `%LOCALAPPDATA%\OnlyWorkspace\config-non-leggibile`) e archivio dell'utente che esegue. Fare prima un backup verificato e controllare le cartelle dopo la disinstallazione. La rimozione usa percorsi Win32 estesi e rimuove le junction come collegamenti, lasciandone intatta la destinazione. Se la rimozione è incompleta, il processo del disinstallatore restituisce 3. Il lanciatore NSIS standard restituisce l'esito dell'avvio. [Verifica e limiti](../tecnico/verifica.md)

Vale solo `/PURGE` senza valore. `/PURGE=1` e altre varianti conservano questi dati. Gli archivi degli altri utenti Windows e le email in Outlook sono fuori dal perimetro della rimozione. La disinstallazione rimuove la voce `DoNotDisableAddinList\OnlyWorkspace.Addin` scritta da **Ripara add-in** per l'utente che la esegue e le sole policy del nuovo Outlook create dal setup. Gli esiti di `RegAsm /u` (a 64 bit solo su Windows x64) e `certutil -delstore` restano nel log della disinstallazione, che prosegue anche se uno fallisce.

## Aggiornare

Tray e add-in vanno aggiornati insieme (pipe v15). I nuovi archivi nascono in formato 13. I formati precedenti, inclusi 11 e 12, sono rifiutati con un errore esplicito e i dati restano disponibili per il recupero. Per recuperare un archivio precedente usare la build compatibile e una copia verificata. [Motore dell'archivio](../tecnico/archivio-motore.md#formato-conservazione-e-ricostruzione)
