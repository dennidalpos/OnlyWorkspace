; OnlyWorkspace - NSIS Setup Script
; Architettura auto-adattante 32/64 bit, verifica prerequisiti, importazione certificato e configurazione

!include "MUI2.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

; Informazioni Generali
Name "OnlyWorkspace"
!ifndef INSTALLER_PATH
    !define INSTALLER_PATH "..\dist\OnlyWorkspaceSetup.exe"
!endif
!ifndef PUBLISH_DIR
    !define PUBLISH_DIR "..\publish"
!endif
OutFile "${INSTALLER_PATH}"
RequestExecutionLevel admin
Unicode True
; Sharp text at 125/150% instead of a stretched bitmap.
ManifestDPIAware true

; Firma digitale disinstallatore (§7.4)
!uninstfinalize 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "..\scripts\sign-binaries.ps1" -Files "%1"' = 0

; Versione x.y.z passata da scripts\build-installer.ps1 (unica fonte: Directory.Build.props)
!ifndef PRODUCT_VERSION
    !error "PRODUCT_VERSION non definita: compilare con scripts\build-installer.ps1."
!endif

VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey "ProductName" "OnlyWorkspace"
VIAddVersionKey "CompanyName" "OnlyApps"
VIAddVersionKey "LegalCopyright" "Copyright (C) 2026 OnlyApps"
VIAddVersionKey "FileDescription" "OnlyWorkspace Outlook AI Add-in Setup"
VIAddVersionKey "FileVersion" "${PRODUCT_VERSION}.0"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"

; Percorsi
Var ConfigParam
Var ProgramDataDir
Var BlockNewOutlook
Var PurgeParam
Var RestartConfigTool
Var ProgramCleanupFailure

; Controlli di prerequisiti, comandi e /PURGE, provati anche da tests\scripts\Packaging.Tests.ps1
!include "SetupChecks.nsh"
!insertmacro DefinePurgeTree "un."

; Keep one installer or uninstaller active until its process exits.
!macro LockSetup
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Global\OnlyWorkspaceSetup") p.R9'
    System::Call 'kernel32::WaitForSingleObject(p R9, i 0) i.R8'
    ${If} $R8 != 0
    ${AndIf} $R8 != 128 ; WAIT_ABANDONED
        MessageBox MB_OK|MB_ICONSTOP "Un altro setup o disinstallatore di OnlyWorkspace e' in corso." /SD IDOK
        SetErrorLevel 2
        Quit
    ${EndIf}
!macroend

!macro RunProgramFiles OPERATION
    ; NSIS extracts its own System.dll in $PLUGINSDIR. Keep it out of the PowerShell compiler's working directory.
    SetOutPath "$SYSDIR"
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\ProgramFiles.ps1" -Operation ${OPERATION} -Destination "$INSTDIR\."'
    Pop $0
!macroend

!macro RequireProgramFiles OPERATION
    !insertmacro RunProgramFiles "${OPERATION}"
    ${If} $0 != 0
        MessageBox MB_OK|MB_ICONSTOP "Gestione dei file programma non riuscita (esito $0). Scegliere una cartella nuova o verificare il log del setup." /SD IDOK
        ${If} $0 == 3
            SetErrorLevel 3
        ${Else}
            SetErrorLevel 2
        ${EndIf}
        Quit
    ${EndIf}
!macroend

; Pagine Installer
!define MUI_ICON "..\src\OnlyWorkspace.ConfigTool\Assets\OnlyWorkspace.ico"
!define MUI_UNICON "..\src\OnlyWorkspace.ConfigTool\Assets\OnlyWorkspace.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "Il setup installa per tutti gli utenti di questo PC:$\r$\n$\r$\n\
- l'add-in OnlyWorkspace per Outlook classico (32 e 64 bit) e lo strumento di configurazione, in Programmi;$\r$\n\
- il certificato pubblico OnlyApps nell'archivio TrustedPublisher (editori attendibili) del computer, non tra le autorita' radice;$\r$\n\
- la configurazione condivisa in ProgramData\OnlyWorkspace (scrivibile solo dagli amministratori);$\r$\n\
- WebView2 Runtime di Microsoft, se manca (scaricato e verificato).$\r$\n$\r$\n\
Nella pagina dei componenti puoi scegliere se nascondere 'Prova il nuovo Outlook'. Chiudi Outlook prima di continuare."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\OnlyWorkspace.ConfigTool.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Apri Configurazione OnlyWorkspace"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchConfigTool
!insertmacro MUI_PAGE_FINISH

; Pagine Uninstaller
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "La disinstallazione di OnlyWorkspace e' terminata; le email in Outlook non sono state modificate.$\r$\n$\r$\n\
Restano sul PC, se presenti: per ogni utente Windows la cartella LocalAppData\OnlyWorkspace (chiavi API cifrate, storico, log, dati del riquadro e, salvo diversa scelta, l'archivio delle email) e il file AppData\OnlyWorkspace\preferences.json; la configurazione in ProgramData\OnlyWorkspace se hai scelto di conservarla. Si possono eliminare a mano."
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "Italian"

; Chiede di chiudere il processo e riprova; in modalita' silenziosa (/S) annulla subito (codice di uscita 2).
; tasklist in CSV senza intestazione: una riga che inizia con le virgolette se il processo esiste, altrimenti
; un messaggio informativo (tradotto). Niente cmd/find: le virgolette annidate facevano fallire find.
!macro EnsureProcessClosed IMAGE MESSAGE
    ${Do}
        nsExec::ExecToStack 'tasklist /FO CSV /NH /FI "IMAGENAME eq ${IMAGE}"'
        Pop $0
        Pop $1
        StrCpy $1 $1 1
        ${If} $1 != '"'
            ${ExitDo}
        ${EndIf}
        MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${MESSAGE}" /SD IDCANCEL IDRETRY +3
        ; Abort in .onInit esce con codice 0
        SetErrorLevel 2
        Quit
    ${Loop}
!macroend

; Modalita' silenziosa: il ConfigTool (di solito solo l'icona nell'area di notifica) viene chiuso senza chiedere;
; $RestartConfigTool = 1 se era in esecuzione. Eventuali modifiche non salvate nella sua finestra vanno perse.
!macro CloseConfigToolIfSilent
    ${If} ${Silent}
        nsExec::ExecToStack 'tasklist /FO CSV /NH /FI "IMAGENAME eq OnlyWorkspace.ConfigTool.exe"'
        Pop $0
        Pop $1
        StrCpy $1 $1 1
        ${If} $1 == '"'
            StrCpy $RestartConfigTool "1"
            nsExec::ExecToStack 'taskkill /F /IM OnlyWorkspace.ConfigTool.exe'
            Pop $0
            Pop $1
            Sleep 1500
        ${EndIf}
    ${EndIf}
!macroend

; Scarica URL in FILE e lo accetta solo con firma Authenticode valida di Microsoft: i prerequisiti vengono
; eseguiti con privilegi elevati. $0 = 0 solo se il file e' pronto per l'esecuzione.
; PSModulePath di macchina: se il setup parte da PowerShell 7 quello ereditato impedisce di caricare i moduli di 5.1.
!macro DownloadVerified URL FILE
    InitPluginsDir
    nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$$ErrorActionPreference = \"Stop\"; $$env:PSModulePath = [Environment]::GetEnvironmentVariable(\"PSModulePath\", \"Machine\"); [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; $$f = \"${FILE}\"; (New-Object Net.WebClient).DownloadFile(\"${URL}\", $$f); $$s = Get-AuthenticodeSignature -LiteralPath $$f; if ($$s.Status -ne \"Valid\" -or $$s.SignerCertificate.Subject -notlike \"*O=Microsoft Corporation*\") { Remove-Item -LiteralPath $$f -Force; Write-Output \"Firma Authenticode non valida o non di Microsoft.\"; exit 3 }"'
    Pop $0
!macroend

; Chiave Add-in COM nella vista del registro corrente (SetRegView).
!macro WriteAddinKey
    WriteRegStr HKLM "Software\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin" "FriendlyName" "OnlyWorkspace"
    WriteRegStr HKLM "Software\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin" "Description" "OnlyWorkspace AI Assistant for Microsoft Outlook"
    WriteRegDWORD HKLM "Software\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin" "LoadBehavior" 3
    WriteRegDWORD HKLM "Software\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin" "CommandLineSafe" 1
!macroend

!define POLICY_KEY "Software\Policies\Microsoft\Office\16.0\Outlook"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\OnlyWorkspace"
!define SETUP_RETRY_HINT "OnlyWorkspace resta disinstallabile da App e Funzionalita'; correggere la causa e ripetere il setup."

; Policy del nuovo Outlook: se il valore non esisteva lo annota in UNINST_KEY, cosi' la disinstallazione
; toglie solo cio' che ha scritto il setup e lascia una policy aziendale preesistente.
!macro WritePolicy SUBKEY NAME VALUE
    ClearErrors
    ReadRegDWORD $0 HKLM "${POLICY_KEY}\${SUBKEY}" "${NAME}"
    ${If} ${Errors}
        WriteRegDWORD HKLM "${UNINST_KEY}" "Policy${NAME}" 1
    ${EndIf}
    WriteRegDWORD HKLM "${POLICY_KEY}\${SUBKEY}" "${NAME}" ${VALUE}
!macroend

!macro un.RemovePolicy SUBKEY NAME
    ReadRegDWORD $0 HKLM "${UNINST_KEY}" "Policy${NAME}"
    ${If} $0 == 1
        DeleteRegValue HKLM "${POLICY_KEY}\${SUBKEY}" "${NAME}"
        DeleteRegKey /ifempty HKLM "${POLICY_KEY}\${SUBKEY}"
    ${EndIf}
!macroend

Function .onInit
    !insertmacro LockSetup
    ReadEnvStr $ProgramDataDir "ProgramData"
    ${If} $ProgramDataDir == ""
        StrCpy $ProgramDataDir "C:\ProgramData"
    ${EndIf}

    ${If} ${RunningX64}
        SetRegView 64
    ${Else}
        SetRegView 32
    ${EndIf}
    ${If} $INSTDIR == ""
        ReadRegStr $INSTDIR HKLM "${UNINST_KEY}" "InstallLocation"
        ${If} $INSTDIR == ""
            StrCpy $INSTDIR "$PROGRAMFILES\OnlyWorkspace"
            ${If} ${RunningX64}
                StrCpy $INSTDIR "$PROGRAMFILES64\OnlyWorkspace"
            ${EndIf}
        ${EndIf}
    ${EndIf}

    ; Lettura parametri da riga di comando (/CONFIG=... /BLOCKNEWOUTLOOK=...; /PURGE vale solo per il disinstallatore)
    ${GetParameters} $R0
    ${GetOptions} $R0 "/CONFIG=" $ConfigParam
    ${GetOptions} $R0 "/BLOCKNEWOUTLOOK=" $BlockNewOutlook

    ${If} $BlockNewOutlook == ""
        StrCpy $BlockNewOutlook "1"
    ${EndIf}

    ; Outlook e ConfigTool (anche nell'area di notifica) tengono aperti i file da sostituire
    !insertmacro EnsureProcessClosed "OUTLOOK.EXE" "Microsoft Outlook e' attualmente in esecuzione. Chiudere Outlook prima di procedere con l'installazione di OnlyWorkspace."
    !insertmacro CloseConfigToolIfSilent
    !insertmacro EnsureProcessClosed "OnlyWorkspace.ConfigTool.exe" "Lo strumento di configurazione di OnlyWorkspace e' in esecuzione (finestra o icona nell'area di notifica). Chiuderlo con Esci prima di procedere con l'installazione."

    ; Verifica Prerequisiti
    Call CheckDotNet48
    Call CheckAndInstallWebView2
FunctionEnd

Function CheckDotNet48
    ClearErrors
    ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
    ${If} ${Errors}
    ${OrIf} $0 < 528040
        MessageBox MB_OK|MB_ICONSTOP "Prerequisito mancante: e' richiesto .NET Framework 4.8 (build 528040 o successiva). Installare il runtime e riavviare l'installazione." /SD IDOK
        ; Abort in .onInit esce con codice 0
        SetErrorLevel 2
        Quit
    ${EndIf}
FunctionEnd

Function CheckAndInstallWebView2
    ; Runtime Evergreen rilevato dalle chiavi pv documentate da Microsoft (SetupChecks.nsh)
    Call WebView2RuntimeInstalled
    ${If} $0 == 1
        Return
    ${EndIf}

    DetailPrint "WebView2 Runtime non rilevato. Download ed installazione automatica bootstrapper Evergreen..."
    !insertmacro DownloadVerified "https://go.microsoft.com/fwlink/p/?LinkId=2124703" "$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe"
    ${If} $0 != 0
        MessageBox MB_OK|MB_ICONSTOP "Impossibile scaricare o verificare la firma digitale di Microsoft Edge WebView2 Runtime. Scaricarlo manualmente da Microsoft." /SD IDOK
        SetErrorLevel 2
        Quit
    ${EndIf}
    DetailPrint "Installazione silenziosa Microsoft Edge WebView2 Runtime in corso..."
    nsExec::ExecToLog '"$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe" /silent /install'
    Pop $2
    ; Conta il runtime effettivamente registrato, non il solo esito del bootstrapper
    Call WebView2RuntimeInstalled
    ${If} $0 != 1
        MessageBox MB_OK|MB_ICONSTOP "Microsoft Edge WebView2 Runtime non risulta installato dopo il bootstrapper (esito $2). Installarlo da Microsoft e ripetere il setup." /SD IDOK
        SetErrorLevel 2
        Quit
    ${EndIf}
FunctionEnd

; Short component names: the list on the components page cuts longer ones; the descriptions say the rest.
Section "Add-in OnlyWorkspace" SecOnlyWorkspace
    SectionIn RO
    ; Collegamenti per tutti gli utenti: con l'elevazione da un altro account quelli personali finirebbero nel suo profilo.
    SetShellVarContext all
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File "ProgramFiles.ps1"
    SetOutPath "$PLUGINSDIR\payload"
    ClearErrors
    File /r "${PUBLISH_DIR}\*.*"
    File "..\certs\OnlyApps.cer"
    WriteUninstaller "$PLUGINSDIR\payload\Uninstall.exe"
    CreateShortcut /NoWorkingDir "$PLUGINSDIR\payload\OnlyWorkspace Tray.lnk" "$INSTDIR\OnlyWorkspace.ConfigTool.exe" "--tray"
    ${If} ${Errors}
        SetErrorLevel 2
        Abort "Preparazione dei file di programma non riuscita."
    ${EndIf}
    !insertmacro RequireProgramFiles Check

    ; Configurazione prima di certificato e registrazioni: se non e' valida il setup si ferma senza
    ; lasciare un add-in registrato a meta'.
    Call SetupConfiguration
    !insertmacro RequireProgramFiles Deploy
    SetOutPath "$INSTDIR"

    ; Disinstallatore e voce in App e Funzionalita' prima di certificato e registrazioni: un setup fermato
    ; da uno dei comandi seguenti resta rimovibile.
    WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "OnlyWorkspace Outlook Add-in"
    WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
    WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "OnlyApps"
    WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\OnlyWorkspace.ConfigTool.exe,0"
    WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
    WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1

    ; Certificato pubblico solo tra gli editori attendibili: nessuna autorita' radice aggiunta al computer.
    ; Le versioni precedenti lo avevano messo anche in Root: lo si toglie (se manca, non e' un errore).
    DetailPrint "Rimozione del certificato OnlyApps dall'archivio Root lasciato da versioni precedenti..."
    nsExec::ExecToLog 'certutil.exe -delstore "Root" "OnlyApps Code Signing"'
    !insertmacro LogExecResult "Rimozione del certificato da Root (versioni precedenti)"
    DetailPrint "Importazione certificato pubblico OnlyApps tra gli editori attendibili..."
    nsExec::ExecToLog 'certutil.exe -addstore -f "TrustedPublisher" "$INSTDIR\OnlyApps.cer"'
    !insertmacro CheckExecResult "Importazione del certificato OnlyApps in TrustedPublisher" "${SETUP_RETRY_HINT}" ""

    ; Registrazione COM (RegAsm, IDTExtensibility2) e chiave Add-in in entrambe le viste del registro:
    ; la DLL e' AnyCPU e la bitness di Outlook non si ricava in modo affidabile (Office MSI non ha la
    ; chiave Click-to-Run). Outlook legge solo la vista della propria bitness.
    DetailPrint "Registrazione Add-in per Microsoft Outlook (32 bit)..."
    nsExec::ExecToLog '"$WINDIR\Microsoft.NET\Framework\v4.0.30319\regasm.exe" /codebase "$INSTDIR\OnlyWorkspace.Outlook.dll"'
    !insertmacro CheckExecResult "Registrazione COM a 32 bit (RegAsm)" "${SETUP_RETRY_HINT}" ""
    SetRegView 32
    !insertmacro WriteAddinKey
    ${If} ${RunningX64}
        DetailPrint "Registrazione Add-in per Microsoft Outlook (64 bit)..."
        nsExec::ExecToLog '"$WINDIR\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" /codebase "$INSTDIR\OnlyWorkspace.Outlook.dll"'
        !insertmacro CheckExecResult "Registrazione COM a 64 bit (RegAsm)" "${SETUP_RETRY_HINT}" ""
        SetRegView 64
        !insertmacro WriteAddinKey
    ${EndIf}

    ; Toglie un'eventuale disattivazione per l'utente che installa: vale allora il LoadBehavior=3 di HKLM.
    ; Gli altri utenti usano 'Ripara add-in' nella Configurazione.
    DeleteRegKey HKCU "Software\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin"

    ; Creazione collegamenti Menu Start
    CreateDirectory "$SMPROGRAMS\OnlyWorkspace"
    CreateShortcut "$SMPROGRAMS\OnlyWorkspace\Configurazione OnlyWorkspace.lnk" "$INSTDIR\OnlyWorkspace.ConfigTool.exe" "" "$INSTDIR\OnlyWorkspace.ConfigTool.exe" 0

    ; Riavvio del ConfigTool chiuso in modalita' silenziosa: tramite explorer parte con l'utente collegato,
    ; non con il token elevato dell'installer (da un servizio senza desktop ripartira' al prossimo accesso).
    ${If} $RestartConfigTool == "1"
        Exec '"$WINDIR\explorer.exe" "$INSTDIR\OnlyWorkspace Tray.lnk"'
    ${EndIf}
SectionEnd

Section "Nascondi nuovo Outlook" SecBlockNewOutlook
    ; /BLOCKNEWOUTLOOK=0 lo esclude anche nel setup silenzioso.
    ${If} $BlockNewOutlook == "1"
        DetailPrint "Applicazione policy di blocco Prova il nuovo Outlook..."
        !insertmacro WritePolicy "Options\General" "HideNewOutlookToggle" 1
        !insertmacro WritePolicy "Preferences" "UseNewOutlook" 0
    ${EndIf}
SectionEnd

Section "-Ritiro disinstallatore precedente"
    !insertmacro RunProgramFiles RetireLegacy
    ${If} $0 != 0
        MessageBox MB_OK|MB_ICONEXCLAMATION "La nuova installazione e' registrata. Il disinstallatore precedente e' ancora da verificare o rinominare. Usare la voce corrente in App e Funzionalita' e consultare il log del setup." /SD IDOK
        SetErrorLevel 3
    ${EndIf}
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
    !insertmacro MUI_DESCRIPTION_TEXT ${SecOnlyWorkspace} "Add-in per Outlook classico, strumento di configurazione, certificato OnlyApps e configurazione condivisa in ProgramData."
    !insertmacro MUI_DESCRIPTION_TEXT ${SecBlockNewOutlook} "Imposta le policy di Office che nascondono l'interruttore 'Prova il nuovo Outlook': l'add-in funziona solo nell'Outlook classico. La disinstallazione toglie solo i valori creati dal setup."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function LaunchConfigTool
    ; Explorer apre il collegamento nella sessione dell'utente, senza il token elevato del setup.
    Exec '"$WINDIR\explorer.exe" "$SMPROGRAMS\OnlyWorkspace\Configurazione OnlyWorkspace.lnk"'
FunctionEnd

Function SetupConfiguration
    ; Configurazione e segreti: scrittura solo ad Administrators e SYSTEM, lettura agli utenti. Senza questo
    ; la cartella eredita da ProgramData il permesso di creare file per ogni utente. Il secondo comando
    ; riporta ai soli permessi ereditati i file gia' presenti (aggiornamento).
    ; Un errore ferma il setup prima di scrivere la configurazione, che resta com'era. Niente /C: con /C icacls
    ; esce con 0 anche se un file non e' stato elaborato.
    CreateDirectory "$ProgramDataDir\OnlyWorkspace"
    nsExec::ExecToLog 'icacls.exe "$ProgramDataDir\OnlyWorkspace" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX'
    !insertmacro CheckExecResult "Protezione della cartella ProgramData\OnlyWorkspace (icacls)" "La configurazione esistente non e' stata modificata." ""
    nsExec::ExecToLog 'icacls.exe "$ProgramDataDir\OnlyWorkspace\*" /reset /T /Q'
    !insertmacro CheckExecResult "Ripristino dei permessi dei file in ProgramData\OnlyWorkspace (icacls)" "La configurazione esistente non e' stata modificata." ""

    ${If} $ConfigParam != ""
        DetailPrint "Importazione configurazione iniziale da: $ConfigParam ..."
        nsExec::ExecToLog '"$PLUGINSDIR\payload\OnlyWorkspace.ConfigTool.exe" import "$ConfigParam" "$ProgramDataDir\OnlyWorkspace" --skip-test'
        Pop $0
        ${If} $0 != 0
            MessageBox MB_OK|MB_ICONSTOP "Errore nell'importazione della configurazione iniziale (codice di uscita $0). Verificare sintassi del file config.json." /SD IDOK
            SetErrorLevel 2
            Abort "Importazione configurazione iniziale fallita."
        ${EndIf}
    ${Else}
        ; Se non passata da riga di comando, controlla se gia' presente configurazione in %ProgramData%
        ${If} ${FileExists} "$ProgramDataDir\OnlyWorkspace\config.json"
            DetailPrint "Configurazione esistente rilevata in ProgramData. Verifica integrita' in corso..."
            nsExec::ExecToLog '"$PLUGINSDIR\payload\OnlyWorkspace.ConfigTool.exe" validate "$ProgramDataDir\OnlyWorkspace\config.json"'
            Pop $0
            ${If} $0 != 0
                MessageBox MB_OK|MB_ICONSTOP "La configurazione esistente in ProgramData non e' valida: il setup si ferma senza registrare l'add-in. Correggerla o ripristinare un backup con la Configurazione, poi ripetere il setup." /SD IDOK
                SetErrorLevel 2
                Abort "Configurazione esistente in ProgramData non valida."
            ${EndIf}
        ${Else}
            DetailPrint "Nessuna configurazione pregressa rilevata. Creazione configurazione predefinita in ProgramData..."
            CreateDirectory "$ProgramDataDir\OnlyWorkspace"
            CopyFiles /SILENT "$PLUGINSDIR\payload\config.template.json" "$ProgramDataDir\OnlyWorkspace\config.json"
            nsExec::ExecToLog '"$PLUGINSDIR\payload\OnlyWorkspace.ConfigTool.exe" validate "$ProgramDataDir\OnlyWorkspace\config.json"'
            Pop $0
            ${If} $0 != 0
                MessageBox MB_OK|MB_ICONSTOP "Errore nella validazione della configurazione iniziale predefinita." /SD IDOK
                SetErrorLevel 2
                Abort "Configurazione predefinita non valida."
            ${EndIf}
            DetailPrint "Configurazione predefinita creata con successo in $ProgramDataDir\OnlyWorkspace\config.json."
        ${EndIf}
    ${EndIf}
FunctionEnd

; =========================================================================
; DISINSTALLAZIONE
; =========================================================================

Function un.onInit
    !insertmacro LockSetup
    ReadEnvStr $ProgramDataDir "ProgramData"
    ${If} $ProgramDataDir == ""
        StrCpy $ProgramDataDir "C:\ProgramData"
    ${EndIf}

    ${If} ${RunningX64}
        SetRegView 64
    ${Else}
        SetRegView 32
    ${EndIf}

    !insertmacro EnsureProcessClosed "OUTLOOK.EXE" "Microsoft Outlook e' attualmente in esecuzione. Chiudere Outlook prima di disinstallare OnlyWorkspace."
    !insertmacro CloseConfigToolIfSilent
    !insertmacro EnsureProcessClosed "OnlyWorkspace.ConfigTool.exe" "Lo strumento di configurazione di OnlyWorkspace e' in esecuzione (finestra o icona nell'area di notifica). Chiuderlo con Esci prima di disinstallare."
FunctionEnd

Section "Uninstall"
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File "ProgramFiles.ps1"
    !insertmacro RequireProgramFiles CheckUninstall
    ; De-registrazione COM tramite RegAsm
    DetailPrint "Deregistrazione componenti COM in corso..."
    ${If} ${RunningX64}
        nsExec::ExecToLog '"$WINDIR\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" /u "$INSTDIR\OnlyWorkspace.Outlook.dll"'
        !insertmacro LogExecResult "Deregistrazione COM a 64 bit"
    ${EndIf}
    nsExec::ExecToLog '"$WINDIR\Microsoft.NET\Framework\v4.0.30319\regasm.exe" /u "$INSTDIR\OnlyWorkspace.Outlook.dll"'
    !insertmacro LogExecResult "Deregistrazione COM a 32 bit"

    ; Rimozione chiavi Add-in da HKLM (sia 64 che 32 bit)
    SetRegView 64
    DeleteRegKey HKLM "Software\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin"
    SetRegView 32
    DeleteRegKey HKLM "Software\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin"
    ${If} ${RunningX64}
        SetRegView 64
    ${EndIf}

    ; Stato dell'add-in per l'utente che disinstalla (gli altri profili conservano una chiave inerte)
    DeleteRegKey HKCU "Software\Microsoft\Office\Outlook\Addins\OnlyWorkspace.Addin"
    !insertmacro RemoveAddinResiliency "Software\Microsoft\Office\16.0\Outlook\Resiliency"

    ; Policy del nuovo Outlook scritte dal setup (prima di cancellare UNINST_KEY, che ne tiene traccia)
    !insertmacro un.RemovePolicy "Options\General" "HideNewOutlookToggle"
    !insertmacro un.RemovePolicy "Preferences" "UseNewOutlook"

    ; Rimozione certificato OnlyApps dagli archivi della macchina
    DetailPrint "Rimozione certificato OnlyApps da Root e TrustedPublisher..."
    nsExec::ExecToLog 'certutil.exe -delstore "Root" "OnlyApps Code Signing"'
    !insertmacro LogExecResult "Rimozione del certificato da Root"
    nsExec::ExecToLog 'certutil.exe -delstore "TrustedPublisher" "OnlyApps Code Signing"'
    !insertmacro LogExecResult "Rimozione del certificato da TrustedPublisher"

    ; Rimozione eventuale avvio automatico tray in HKCU
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "OnlyWorkspaceTray"

    ; Rimozione file programma e collegamenti (per tutti gli utenti e, dalle versioni precedenti, dell'utente corrente)
    StrCpy $PurgeFailure 0
    SetShellVarContext all
    !insertmacro RemoveProgramShortcut "$SMPROGRAMS\OnlyWorkspace"
    SetShellVarContext current
    !insertmacro RemoveProgramShortcut "$SMPROGRAMS\OnlyWorkspace"
    ; Keep the registered uninstaller and inventory available if a program file is locked.
    ${If} $PurgeFailure == 0
        !insertmacro RunProgramFiles Uninstall
        ${If} $0 == 0
            DeleteRegKey HKLM "${UNINST_KEY}"
        ${Else}
            StrCpy $PurgeFailure 1
        ${EndIf}
    ${EndIf}
    StrCpy $ProgramCleanupFailure $PurgeFailure
    !insertmacro ReportProgramCleanup

    ; Dati in ProgramData e archivio dell'utente: /PURGE senza valore o conferma interattiva (SetupChecks.nsh)
    ${GetParameters} $R0
    !insertmacro PurgeRequested $R0 $PurgeParam
    !insertmacro PurgeUserData "un." $PurgeParam "$ProgramDataDir" "$LOCALAPPDATA"

    ${If} $PurgeFailure == 0
    ${AndIf} $ProgramCleanupFailure == 0
        DetailPrint "Disinstallazione completata."
    ${EndIf}
SectionEnd
