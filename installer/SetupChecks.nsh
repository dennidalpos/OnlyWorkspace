; OnlyWorkspace - controlli del setup condivisi con i test di packaging (tests\scripts\Packaging.Tests.ps1).
; Richiede LogicLib.nsh, x64.nsh e FileFunc.nsh. Solo testo ASCII: lo script NSIS non ha BOM.

!define WEBVIEW2_GUID "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

; Stack: versione pv -> 1 se e' una versione maggiore di 0.0.0.0, altrimenti 0. Microsoft considera assente
; il runtime con pv mancante, vuoto o 0.0.0.0:
; https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
Function WebView2VersionUsable
    Exch $0
    Push $1
    Push $2
    Push $3
    StrCpy $3 0
    StrCpy $1 0
    StrCpy $2 $0 1
    ${If} $2 == ""
    ${OrIf} $2 == "."
        StrCpy $0 0
    ${Else}
        ${Do}
            StrCpy $2 $0 1 $1
            ${If} $2 == ""
                ${ExitDo}
            ${EndIf}
            IntOp $1 $1 + 1
            ${If} $2 == "."
            ${OrIf} $2 == "0"
                ${Continue}
            ${EndIf}
            ; IntOp vale 0 per un carattere che non e' una cifra
            IntOp $2 $2 + 0
            ${If} $2 = 0
                StrCpy $3 -1
                ${ExitDo}
            ${EndIf}
            StrCpy $3 1
        ${Loop}
        ${If} $3 = 1
            StrCpy $0 1
        ${Else}
            StrCpy $0 0
        ${EndIf}
    ${EndIf}
    Pop $3
    Pop $2
    Pop $1
    Exch $0
FunctionEnd

; $0 = 1 se una delle chiavi pv documentate (per macchina, per utente) contiene una versione utilizzabile.
Function WebView2RuntimeInstalled
    ${If} ${RunningX64}
        ReadRegStr $1 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_GUID}" "pv"
    ${Else}
        ReadRegStr $1 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_GUID}" "pv"
    ${EndIf}
    Push $1
    Call WebView2VersionUsable
    Pop $0
    ${If} $0 != 1
        ReadRegStr $1 HKCU "Software\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_GUID}" "pv"
        Push $1
        Call WebView2VersionUsable
        Pop $0
    ${EndIf}
FunctionEnd

; Dopo nsExec::ExecToLog: l'esito sullo stack e' il codice di uscita, "error" (avvio non riuscito) o "timeout"
; (https://nsis.sourceforge.io/NsExec_plug-in). Qualsiasi esito diverso da 0 ferma il setup con codice 2,
; dopo l'eventuale funzione di pulizia CLEANUP ("" per nessuna).
!macro CheckExecResult LABEL HINT CLEANUP
    Pop $0
    ${If} $0 != 0
        DetailPrint "${LABEL} non riuscito (esito $0)."
        MessageBox MB_OK|MB_ICONSTOP "${LABEL} non riuscito (esito $0): il setup si ferma. ${HINT}" /SD IDOK
        SetErrorLevel 2
        !if "${CLEANUP}" != ""
            Call ${CLEANUP}
        !endif
        Abort "${LABEL} non riuscito."
    ${EndIf}
!macroend

; Disinstallazione: un comando fallito resta nel log e la rimozione prosegue; l'esito lascia comunque lo stack.
!macro LogExecResult LABEL
    Pop $0
    ${If} $0 != 0
        DetailPrint "Avviso: ${LABEL} non riuscito (esito $0)."
    ${EndIf}
!macroend

; OUT = 1 solo con /PURGE senza valore. GetOptions imposta l'errore se l'opzione manca e restituisce il testo
; che la segue (vuoto per /PURGE, "=1" per /PURGE=1): un valore non documentato conserva i dati.
; https://nsis.sourceforge.io/Docs/AppendixE.html#E.1.12
!macro PurgeRequested PARAMS OUT
    ClearErrors
    ${GetOptions} "${PARAMS}" "/PURGE" ${OUT}
    ${If} ${Errors}
        StrCpy ${OUT} 0
    ${ElseIf} ${OUT} == ""
        StrCpy ${OUT} 1
    ${Else}
        DetailPrint "Opzione /PURGE${OUT} non riconosciuta: dati conservati (usare /PURGE senza valore)."
        StrCpy ${OUT} 0
    ${EndIf}
!macroend

Var PurgeFailure
!define /math PURGE_CHILD_LIMIT ${NSIS_MAX_STRLEN} - 6
!define /math PURGE_ROOT_LIMIT ${NSIS_MAX_STRLEN} - 13

; Walk an extended path one directory at a time. Hold each parent open without write/delete
; sharing during enumeration. A reparse point is a leaf, including directory junctions.
!macro PurgeChild PREFIX MODE
    StrLen $R3 $R0
    StrLen $R4 $R2
    IntOp $R3 $R3 + $R4
    ${If} $R3 < ${PURGE_CHILD_LIMIT}
        Push "${MODE}"
        Push "$R0\$R2"
        Call ${PREFIX}PurgeTree
    ${Else}
        StrCpy $PurgeFailure 1
    ${EndIf}
!macroend

!macro DefinePurgeTree PREFIX
Function ${PREFIX}PurgeTree
    Exch $R0 ; extended path
    Exch 1
    Exch $R5 ; empty: any entry; dot: directory root; ?: validate only; otherwise: named child
    Push $R1
    Push $R2
    Push $R3
    Push $R4

    ; FILE_READ_ATTRIBUTES, FILE_SHARE_READ, OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT
    System::Call 'kernel32::CreateFileW(w R0, i 0x80, i 1, p 0, i 3, i 0x02200000, p 0) p.R1 ?e'
    Pop $R2
    ${If} $R1 == -1
        ${If} $R2 != 2 ; ERROR_FILE_NOT_FOUND
        ${AndIf} $R2 != 3 ; ERROR_PATH_NOT_FOUND
            StrCpy $PurgeFailure 1
        ${EndIf}
        Goto purgeDone
    ${EndIf}
    ; FileAttributeTagInfo gives attributes of the opened object, including the link itself.
    System::Call 'kernel32::GetFileInformationByHandleEx(p R1, i 9, *l .R2, i 8) i.R3'
    ${If} $R3 == 0
        StrCpy $PurgeFailure 1
        System::Call 'kernel32::CloseHandle(p R1)'
        Goto purgeDone
    ${EndIf}
    IntOp $R3 $R2 & 0x10 ; FILE_ATTRIBUTE_DIRECTORY
    IntOp $R2 $R2 & 0x400 ; FILE_ATTRIBUTE_REPARSE_POINT
    ${If} $R5 == "?"
        ${If} $R3 == 0
        ${OrIf} $R2 != 0
            StrCpy $PurgeFailure 1
        ${EndIf}
        System::Call 'kernel32::CloseHandle(p R1)'
        Goto purgeDone
    ${EndIf}
    ${If} $R5 != ""
    ${AndIf} $R5 != "."
        ${If} $R3 == 0
        ${OrIf} $R2 != 0
            StrCpy $PurgeFailure 1
        ${Else}
            StrCpy $R2 $R5
            !insertmacro PurgeChild "${PREFIX}" "."
        ${EndIf}
        System::Call 'kernel32::CloseHandle(p R1)'
        Goto purgeDone
    ${EndIf}

    ${If} $R3 == 0
        System::Call 'kernel32::CloseHandle(p R1)'
        ${If} $R5 == "."
            StrCpy $PurgeFailure 1
        ${Else}
            ClearErrors
            Delete "$R0"
            ${If} ${Errors}
                StrCpy $PurgeFailure 1
            ${EndIf}
        ${EndIf}
        Goto purgeDone
    ${EndIf}
    ${If} $R2 == 0
        ClearErrors
        FindFirst $R5 $R2 "$R0\*"
        ${Unless} ${Errors}
            ${Do}
                ${If} $R2 != "."
                ${AndIf} $R2 != ".."
                    !insertmacro PurgeChild "${PREFIX}" ""
                ${EndIf}
                ClearErrors
                FindNext $R5 $R2
            ${LoopUntil} ${Errors}
            FindClose $R5
        ${EndUnless}
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p R1)'
    ; Never remove recursively here. An enumeration error leaves a nonempty directory and fails.
    System::Call 'kernel32::RemoveDirectoryW(w R0) i.R3'
    ${If} $R3 == 0
        StrCpy $PurgeFailure 1
    ${EndIf}

    purgeDone:
    Pop $R4
    Pop $R3
    Pop $R2
    Pop $R1
    Pop $R5
    Pop $R0
FunctionEnd
!macroend

!macro PurgeFolder PREFIX ROOT CHILD
    Push $R0
    Push $R1
    StrCpy $R0 "${ROOT}"
    StrLen $R1 $R0
    ${If} $R1 >= ${PURGE_ROOT_LIMIT}
        StrCpy $PurgeFailure 1
    ${Else}
        StrCpy $R1 $R0 2
        ${If} $R1 == "\\"
            StrCpy $R0 $R0 "" 2
            StrCpy $R0 "\\?\UNC\$R0"
        ${Else}
            StrCpy $R1 $R0 2 1
            ${If} $R1 == ":\"
                StrCpy $R0 "\\?\$R0"
            ${Else}
                StrCpy $R0 ""
            ${EndIf}
        ${EndIf}
        ${If} $R0 != ""
            Push "${CHILD}"
            Push $R0
            Call ${PREFIX}PurgeTree
        ${Else}
            StrCpy $PurgeFailure 1
        ${EndIf}
    ${EndIf}
    Pop $R1
    Pop $R0
!macroend

; Report before any later cleanup can reset the shared tree status.
!macro ReportProgramCleanup
    ${If} $PurgeFailure == 1
        DetailPrint "Pulizia incompleta dei file programma o dei collegamenti di OnlyWorkspace."
        MessageBox MB_OK|MB_ICONEXCLAMATION "Alcuni file programma o collegamenti di OnlyWorkspace non sono stati rimossi. Controllare la cartella di installazione e il menu Start." /SD IDOK
        SetErrorLevel 3
    ${EndIf}
!macroend

!macro RemoveProgramShortcut ROOT
    !insertmacro PurgeFolder "un." "${ROOT}" "?"
    ${If} $PurgeFailure == 0
        ClearErrors
        Delete "${ROOT}\Configurazione OnlyWorkspace.lnk"
        ${If} ${Errors}
            StrCpy $PurgeFailure 1
        ${Else}
            ; Other shortcuts in this folder belong to their users.
            RMDir "${ROOT}"
        ${EndIf}
    ${EndIf}
!macroend

; Dati del prodotto alla disinstallazione: configurazione e segreti in PROGRAMDATA\OnlyWorkspace (con le copie non
; leggibili salvate dal ConfigTool in LOCALAPPDATA\OnlyWorkspace\config-non-leggibile), archivio delle email in
; LOCALAPPDATA\OnlyWorkspace\archive. Con PURGE = 1 li elimina senza chiedere; altrimenti chiede, e in modalita'
; silenziosa conserva entrambi. Il resto di LOCALAPPDATA\OnlyWorkspace (storico, log) resta.
!macro PurgeUserData PREFIX PURGE PROGRAMDATA LOCALAPPDATA
    StrCpy $PurgeFailure 0
    ${If} ${PURGE} == 1
    ${OrIf} ${Cmd} `MessageBox MB_YESNO|MB_ICONQUESTION "Mantenere i file di configurazione e i segreti crittografati in ProgramData per eventuali reinstallazioni successive?" /SD IDYES IDNO`
        DetailPrint "Rimozione configurazione e segreti..."
        !insertmacro PurgeFolder "${PREFIX}" "${PROGRAMDATA}\OnlyWorkspace" "."
        !insertmacro PurgeFolder "${PREFIX}" "${LOCALAPPDATA}\OnlyWorkspace" "config-non-leggibile"
    ${EndIf}

    ; Archivio delle email dell'utente che disinstalla (gli altri profili Windows conservano il proprio)
    ${If} ${PURGE} == 1
        !insertmacro PurgeFolder "${PREFIX}" "${LOCALAPPDATA}\OnlyWorkspace" "archive"
    ${ElseIf} ${FileExists} "${LOCALAPPDATA}\OnlyWorkspace\archive\*.*"
    ${AndIf} ${Cmd} `MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Eliminare anche l'archivio delle email di questo utente Windows? Le email in Outlook non vengono toccate; l'archivio si puo' ricreare dal riquadro." /SD IDNO IDYES`
        !insertmacro PurgeFolder "${PREFIX}" "${LOCALAPPDATA}\OnlyWorkspace" "archive"
    ${EndIf}
    ${If} $PurgeFailure == 1
        DetailPrint "Rimozione incompleta dei dati di OnlyWorkspace."
        MessageBox MB_OK|MB_ICONEXCLAMATION "Alcuni dati di OnlyWorkspace non sono stati rimossi. Controllare le cartelle dei dati." /SD IDOK
        SetErrorLevel 3
    ${EndIf}
!macroend

; Voce scritta da «Ripara add-in» del ConfigTool (Resiliency\DoNotDisableAddinList, HKCU): rimossa solo la nostra.
!macro RemoveAddinResiliency RESILIENCY
    DeleteRegValue HKCU "${RESILIENCY}\DoNotDisableAddinList" "OnlyWorkspace.Addin"
!macroend
