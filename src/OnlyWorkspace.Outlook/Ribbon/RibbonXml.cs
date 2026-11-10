namespace OnlyWorkspace.Outlook.Ribbon;

/// <summary>
/// Ribbon markup per Outlook window type. Every onAction names a public method of OnlyWorkspaceAddin.
/// Layout rule: large buttons, or full columns of three normal ones (a partial column misaligns the group).
/// </summary>
internal static class RibbonXml
{
    public static string Build(string ribbonId) => ribbonId switch
    {
        "Microsoft.Outlook.Explorer" => Document(QuickGroup("TabMail", "GroupMailRespond"), MainTab(EmailGroup), ContextMenus),
        "Microsoft.Outlook.Mail.Read" => Document(QuickGroup("TabReadMessage", "GroupRespond"), MainTab(EmailGroup), string.Empty),
        // The item being written is not an email to summarise or verify: the writing actions in its own tab.
        "Microsoft.Outlook.Mail.Compose" => Document(ComposeGroup, MainTab(string.Empty), string.Empty),
        // Appointments, contacts, tasks...: the email actions do not apply there.
        _ => string.Empty
    };

    private static string Document(string hostTab, string mainTab, string contextMenus) => $"""
        <customUI xmlns="http://schemas.microsoft.com/office/2009/07/customui" loadImage="LoadImage" onLoad="OnRibbonLoad">
          <ribbon>
            <tabs>
        {hostTab}
        {mainTab}
            </tabs>
          </ribbon>
        {contextMenus}
        </customUI>
        """;

    // Ids are unique per document: each placement of the reply menu gets its own prefix.
    private static string ReplyItems(string prefix) => $"""
                      <button id="{prefix}ReplyAccept" label="Bozza: accetta" imageMso="AcceptInvitation" onAction="OnReplyAcceptClicked"
                              screentip="Bozza che accetta"
                              supertip="Prepara una risposta cortese che accetta la proposta o la richiesta dell'email. Non accetta inviti di riunione e non invia nulla." />
                      <button id="{prefix}ReplyDecline" label="Bozza: rifiuta con alternativa" imageMso="DeclineInvitation" onAction="OnReplyDeclineClicked"
                              screentip="Bozza che rifiuta con alternativa"
                              supertip="Prepara una risposta cordiale che declina la richiesta e propone una data o una modalità alternativa. Non invia nulla." />
        """;

    private const string ReplyTip = "Il testo del thread selezionato va al modello del profilo attivo; la bozza viene inserita al cursore o in una finestra di risposta Outlook e nulla viene inviato automaticamente.";

    private static string ReplySplit(string prefix, string size) => $"""
                  <splitButton id="{prefix}SplitReply" size="{size}" getVisible="GetCanActOnEmail">
                    <button id="{prefix}Reply" label="Bozza risposta..." imageMso="Reply" onAction="OnReplyCustomClicked"
                            screentip="Bozza risposta" supertip="Apre il riquadro per indicare come rispondere; dalla freccia scegli una bozza che accetta o rifiuta. {ReplyTip}" />
                    <menu id="{prefix}MenuReply">
        {ReplyItems(prefix)}
                    </menu>
                  </splitButton>
        """;

    // Group inside Outlook's own Home / Message tab: one column of three.
    private static string QuickGroup(string tabMso, string afterGroupMso) => $"""
              <tab idMso="{tabMso}">
                <group id="grpQuick" label="OnlyWorkspace" image="ow16" insertAfterMso="{afterGroupMso}">
                  <labelControl id="quickEmailHint" label="Seleziona o apri un'email" getVisible="GetNeedsEmailSelection" />
                  <button id="quickSummary" label="Sintesi" imageMso="AlignLeft" onAction="OnSummaryClicked" getVisible="GetCanActOnEmail"
                          screentip="Sintesi email" supertip="Sintesi del thread dell'email selezionata nel riquadro OnlyWorkspace, preparata dal modello del profilo attivo." />
        {ReplySplit("quick", "normal")}
                  <button id="quickSecurity" label="Verifica" imageMso="MacroSecurity" onAction="OnSecurityClicked" getVisible="GetCanActOnEmail"
                          screentip="Verifica sicurezza" supertip="Controlla autenticazione, mittente, link e allegati dell'email selezionata." />
                </group>
              </tab>
        """;

    // The email being written: the writing actions work on its text, selection first (Danny, 20/10/2026).
    private const string ComposeTip = "Il testo selezionato, o quello che hai scritto sopra la citazione, va al modello del profilo attivo; il risultato compare nel riquadro e entra nell'email solo con un tuo clic.";

    private static readonly string ComposeGroup = $"""
              <tab idMso="TabNewMailMessage">
                <group id="grpWrite" label="OnlyWorkspace" image="ow16">
                  <button id="writeAsk" label="Scrivi con IA" size="large" image="ow32" onAction="OnWriteClicked"
                          screentip="Scrivi con IA" supertip="Apre il riquadro per dire cosa scrivere: il testo tiene conto di oggetto, destinatari, di quanto hai già scritto e dell'email a cui rispondi. Non cerca nella posta se non lo chiedi." />
                  <menu id="writeImprove" label="Migliora" size="large" imageMso="SpellingAndGrammar" screentip="Migliora il testo" supertip="{ComposeTip}">
                    <button id="improveCorrect" label="Correggi" tag="correct" onAction="OnImproveClicked" />
                    <button id="improveFormal" label="Più formale" tag="formal" onAction="OnImproveClicked" />
                    <button id="improveFriendly" label="Più cordiale" tag="friendly" onAction="OnImproveClicked" />
                    <button id="improveShorter" label="Più breve" tag="shorter" onAction="OnImproveClicked" />
                    <button id="improveClearer" label="Più chiaro" tag="clearer" onAction="OnImproveClicked" />
                  </menu>
                  <menu id="writeTranslate" label="Traduci" size="large" imageMso="TranslateMenu" screentip="Traduci il testo" supertip="{ComposeTip}">
                    <button id="translateEn" label="Inglese" tag="en" onAction="OnTranslateClicked" />
                    <button id="translateIt" label="Italiano" tag="it" onAction="OnTranslateClicked" />
                    <button id="translateFr" label="Francese" tag="fr" onAction="OnTranslateClicked" />
                    <button id="translateDe" label="Tedesco" tag="de" onAction="OnTranslateClicked" />
                    <button id="translateEs" label="Spagnolo" tag="es" onAction="OnTranslateClicked" />
                  </menu>
                  <button id="writeSubject" label="Oggetto" size="large" imageMso="TextBoxInsertMenu" onAction="OnSubjectClicked"
                          screentip="Suggerisci oggetto" supertip="Propone un oggetto dal testo scritto; lo imposti con «Usa come oggetto» nel riquadro." />
                </group>
              </tab>
        """;

    private static readonly string EmailGroup = $"""
                <group id="grpEmail" label="Email" imageMso="AlignLeft">
                  <labelControl id="emailSelectionHint" label="Seleziona o apri un'email" getVisible="GetNeedsEmailSelection" />
                  <button id="btnSummary" label="Sintesi" size="large" imageMso="AlignLeft" onAction="OnSummaryClicked" keytip="S" getVisible="GetCanActOnEmail"
                          screentip="Sintesi email"
                          supertip="Sintesi del thread dell'email selezionata e degli allegati leggibili, preparata dal modello del profilo attivo. Il testo viene usato intero: se non entra nel contesto di un modello locale l'operazione si ferma senza inviare nulla." />
                  <splitButton id="btnSplitReply" size="large" keytip="B" getVisible="GetCanActOnEmail">
                    <button id="btnReply" label="Bozza risposta..." imageMso="Reply" onAction="OnReplyCustomClicked"
                            screentip="Bozza risposta" supertip="Apre il riquadro per indicare come rispondere; dalla freccia scegli una bozza che accetta o rifiuta. {ReplyTip}" />
                    <menu id="btnMenuReply">
        {ReplyItems("btn")}
                    </menu>
                  </splitButton>
                  <button id="btnSecurity" label="Verifica" size="large" imageMso="MacroSecurity" onAction="OnSecurityClicked" keytip="V" getVisible="GetCanActOnEmail"
                          screentip="Verifica sicurezza"
                          supertip="Mostra i risultati di autenticazione riportati e controlla localmente mittente, link e allegati. L'analisi IA segue il profilo attivo, anche cloud, e le sue autorizzazioni." />
                  <menu id="emailMore" label="Altre azioni" size="large" imageMso="NewTask" getVisible="GetCanActOnEmail">
                    <button id="btnActionItems" label="Attività e appuntamenti" imageMso="NewTask" onAction="OnActionItemsClicked" keytip="T"
                            screentip="Attività e appuntamenti" supertip="Propone bozze Outlook da verificare e salvare; nulla viene salvato automaticamente." />
                    <button id="btnExtractData" label="Estrai dati" imageMso="ConvertTextToTable" onAction="OnExtractDataClicked" keytip="E"
                            screentip="Estrai dati" supertip="Estrae campi e righe dagli allegati consentiti in tabella, con Copia e Salva CSV." />
                  </menu>
                </group>
        """;

    private static string MainTab(string emailGroup) => $"""
              <tab id="tabOnlyWorkspace" label="OnlyWorkspace" keytip="OW">
                <group id="grpChat" label="Assistente" imageMso="ReplyWithInstantMessage">
                  <splitButton id="chatSplit" size="large" keytip="C">
                    <button id="btnChat" label="Chat" image="ow32" onAction="OnChatClicked" screentip="Chat assistente"
                            supertip="Domande su posta, agenda e contatti, o testi da scrivere. Cerca nella posta solo quando lo chiedi." />
                    <menu id="chatMenu">
                      <button id="btnNewChat" label="Nuova chat" imageMso="Delete" onAction="OnNewChatClicked" getEnabled="GetCanClearChat" keytip="U"
                              screentip="Nuova chat" supertip="Apre una chat vuota; la conversazione resta nello Storico. Disponibile quando nessuna operazione è in corso." />
                      <button id="btnHistory" label="Storico" imageMso="RecentSearches" onAction="OnHistoryClicked" keytip="H"
                              screentip="Storico" supertip="Conversazioni e risultati salvati cifrati su questo PC: riapri o elimina quelli che scegli." />
                    </menu>
                  </splitButton>
                  <button id="btnCancel" label="Interrompi" size="large" imageMso="CancelAll" onAction="OnCancelClicked" keytip="X"
                          screentip="Interrompi" supertip="Ferma l'operazione in corso nel riquadro." />
                </group>
        {emailGroup}
                <group id="grpMailbox" label="Posta e persone" imageMso="GotoCalendar">
                  <splitButton id="analysisSplit" size="large" keytip="A">
                    <button id="btnAnalysis" label="Cerca e analizza" imageMso="ChartInsert" onAction="OnAnalysisClicked"
                            screentip="Cerca e analizza" supertip="Scegli cassette, filtri e operazioni sul risultato." />
                    <menu id="analysisMenu">
                      <button id="searchOpen" label="Punti aperti" tag="open" onAction="OnAnalysisPresetClicked" />
                      <button id="searchTable" label="Tabella dati" tag="table" onAction="OnAnalysisPresetClicked" />
                      <button id="searchCalendar" label="Appuntamenti e scadenze" tag="calendar" onAction="OnAnalysisPresetClicked" />
                      <button id="btnFollowUps" label="Email senza risposta" tag="followups" onAction="OnAnalysisPresetClicked" keytip="F" />
                      <button id="searchCustom" label="Personalizzata" tag="custom" onAction="OnAnalysisPresetClicked" />
                    </menu>
                  </splitButton>
                  <button id="btnBriefing" label="Briefing" size="large" imageMso="GotoCalendar" onAction="OnBriefingClicked" keytip="G"
                          screentip="Briefing di oggi" supertip="Agenda di oggi e fino a 25 email non lette della Posta in arrivo predefinita, nel rispetto del profilo e degli allegati consentiti." />
                </group>
                <group id="grpSystem" label="Sistema" imageMso="CurrentViewSettings">
                  <button id="btnTogglePane" label="Riquadro" image="ow16" onAction="OnTogglePaneClicked" keytip="R"
                          screentip="Mostra o nascondi il riquadro" supertip="Apre o chiude il riquadro OnlyWorkspace in questa finestra." />
                  <button id="btnConfig" label="Configurazione" imageMso="CurrentViewSettings" onAction="OnConfigClicked" keytip="K"
                          screentip="Configurazione OnlyWorkspace"
                          supertip="Apre la configurazione: profili e chiavi IA, autorizzazione dell'archivio, backup e dati salvati sul PC. Le modifiche richiedono un amministratore." />
                  <menu id="stateMenu" label="Stato" imageMso="Info" keytip="N">
                    <button id="btnArchiveInfo" label="Archivio" imageMso="Folder" onAction="OnArchiveInfoClicked"
                            screentip="Stato archivio" supertip="Cassette, raccolta e diagnostica dell'archivio." />
                    <button id="btnAiInfo" label="IA" imageMso="Info" onAction="OnInfoClicked"
                            screentip="Stato IA" supertip="Profilo, modello, endpoint e verifica della connessione." />
                  </menu>
                </group>
              </tab>
        """;

    // tag = action for OnContextActionClicked, which works on the right-clicked email.
    private const string ContextMenus = """
          <contextMenus>
            <contextMenu idMso="ContextMenuMailItem">
              <menu id="ctxOnlyWorkspace" label="OnlyWorkspace" image="ow16">
                <button id="ctxSummary" label="Sintesi" imageMso="AlignLeft" tag="summary" onAction="OnContextActionClicked" />
                <button id="ctxReplyAccept" label="Bozza: accetta" imageMso="AcceptInvitation" tag="accept" onAction="OnContextActionClicked" />
                <button id="ctxReplyDecline" label="Bozza: rifiuta con alternativa" imageMso="DeclineInvitation" tag="decline" onAction="OnContextActionClicked" />
                <menuSeparator id="ctxSeparator" />
                <button id="ctxSecurity" label="Verifica sicurezza" imageMso="MacroSecurity" tag="security" onAction="OnContextActionClicked" />
                <button id="ctxActionItems" label="Attività e appuntamenti" imageMso="NewTask" tag="action_items" onAction="OnContextActionClicked" />
              </menu>
            </contextMenu>
          </contextMenus>
        """;
}
