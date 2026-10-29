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
        !if "${CLEANUP}" != ""
            Call ${CLEANUP}
        !endif
        SetErrorLevel 2
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

; Dati del prodotto alla disinstallazione: configurazione e segreti in PROGRAMDATA\OnlyWorkspace (con le copie non
; leggibili salvate dal ConfigTool in LOCALAPPDATA\OnlyWorkspace\config-non-leggibile), archivio delle email in
; LOCALAPPDATA\OnlyWorkspace\archive. Con PURGE = 1 li elimina senza chiedere; altrimenti chiede, e in modalita'
; silenziosa conserva entrambi. Il resto di LOCALAPPDATA\OnlyWorkspace (storico, log) resta.
!macro PurgeUserData PURGE PROGRAMDATA LOCALAPPDATA
    ${If} ${PURGE} == 1
        DetailPrint "Rimozione completa configurazione e segreti in ProgramData (/PURGE)..."
        RMDir /r "${PROGRAMDATA}\OnlyWorkspace"
        RMDir /r "${LOCALAPPDATA}\OnlyWorkspace\config-non-leggibile"
    ${ElseIf} ${Cmd} `MessageBox MB_YESNO|MB_ICONQUESTION "Mantenere i file di configurazione e i segreti crittografati in ProgramData per eventuali reinstallazioni successive?" /SD IDYES IDNO`
        RMDir /r "${PROGRAMDATA}\OnlyWorkspace"
        RMDir /r "${LOCALAPPDATA}\OnlyWorkspace\config-non-leggibile"
    ${EndIf}

    ; Archivio delle email dell'utente che disinstalla (gli altri profili Windows conservano il proprio)
    ${If} ${FileExists} "${LOCALAPPDATA}\OnlyWorkspace\archive\*.*"
        ${If} ${PURGE} == 1
        ${OrIf} ${Cmd} `MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Eliminare anche l'archivio delle email di questo utente Windows? Le email in Outlook non vengono toccate; l'archivio si puo' ricreare dal riquadro." /SD IDNO IDYES`
            DetailPrint "Rimozione archivio delle email dell'utente..."
            RMDir /r "${LOCALAPPDATA}\OnlyWorkspace\archive"
        ${EndIf}
    ${EndIf}
!macroend

; Voce scritta da «Ripara add-in» del ConfigTool (Resiliency\DoNotDisableAddinList, HKCU): rimossa solo la nostra.
!macro RemoveAddinResiliency RESILIENCY
    DeleteRegValue HKCU "${RESILIENCY}\DoNotDisableAddinList" "OnlyWorkspace.Addin"
!macroend
