# Guida visiva ai percorsi utente

Guida dei principali percorsi, riallineata staticamente al codice il 01/11/2026. Questa revisione non è una nuova prova installata; evidenze e limiti sono in [Verifica e qualifica](../tecnico/verifica.md). Il [kit social](../../assets/social_copy.md) distingue catture datate e illustrazioni.
Ciascuna sezione descrive un'area funzionale tramite un diagramma Mermaid orientato all'esperienza dell'utente finale, indicando:
- **Punti di ingresso**: ribbon, menu contestuali, riquadro WebView2, finestra di composizione o icona nella tray.
- **Azioni e scelte**: opzioni disponibili prima e durante l'esecuzione.
- **Bivi decisionali**: controlli locali, vincoli di contesto, verifiche e opzioni di salvataggio.
- **Risultati ed esiti operativi**: bozze e tabelle (sempre su conferma o espliciti: nessun invio automatico di email).
- **Percorsi di errore o recupero**: superamento contesto locale, assenza di riscontri web, interruzioni parziali e blocchi di sicurezza.

---

## 1. Azioni sull'email selezionata

Quando selezioni un'email nell'elenco di Outlook o la apri in una finestra di lettura, puoi avviare le azioni rapide dedicate dal Ribbon, dal menu contestuale o dal riquadro.
Il modello riceve i testi interi puliti necessari all’azione e gli allegati consentiti e leggibili per intero; quelli esclusi o incompleti sono dichiarati. La richiesta locale completa, inclusi prompt e schema, deve rientrare nel contesto. La ricerca web ha un invio distinto e limitato, descritto in [Sicurezza e dati](../admin/sicurezza.md).

Tutti i percorsi IA restano soggetti al profilo: i livelli esterni correnti escludono il testo degli allegati; Minimo consente i soli fatti locali nelle azioni previste, Standard anche gli originali ripuliti. Il web esterno è limitato alla Verifica sicurezza a Standard. [Autorizzazioni e limiti](modelli-web.md).

```mermaid
flowchart TD
    subgraph INGRESSO["Punti di ingresso"]
        direction TB
        E1["Scheda OnlyWorkspace nel Ribbon"]
        E2["Gruppo veloce in Home o Messaggio"]
        E3["Menu contestuale (tasto destro sull'email)"]
        E4["Scorciatoie nel riquadro (Alt+S, Alt+R, Alt+V, Alt+T)"]
        E5["Barra azioni rapide del riquadro"]
    end

    INGRESSO --> SCELTA{"Quale azione avvii?"}

    SCELTA -->|"Sintesi"| A_SINTESI["Lettura email, thread e allegati leggibili"]
    A_SINTESI --> C_CTX1{"Il testo entra nel contesto IA?"}
    C_CTX1 -->|"No (profilo locale Ollama)"| ERR_CTX1["Arresto protettivo con avviso:<br/>«Testo troppo lungo per il profilo»<br/>(nessun dato inviato o troncato)"]
    C_CTX1 -->|"Sì"| RES_SINTESI["Sintesi visualizzata nel riquadro<br/>con comandi Copia e Rigenera"]

    SCELTA -->|"Bozza risposta"| A_BOZZA{"Tipo di risposta?"}
    A_BOZZA -->|"Con istruzione..."| B_CUSTOM["Inserisci istruzioni nel riquadro"]
    A_BOZZA -->|"Bozza: accetta"| B_ACCEPT["Prepara accettazione cortese"]
    A_BOZZA -->|"Bozza: rifiuta"| B_DECLINE["Prepara rifiuto cordiale con alternativa"]
    B_CUSTOM --> GEN_BOZZA["Generazione testo da modello attivo"]
    B_ACCEPT --> GEN_BOZZA
    B_DECLINE --> GEN_BOZZA
    GEN_BOZZA --> RES_BOZZA["Bozza pronta nel riquadro:<br/>• Inserisci al cursore<br/>• Nuova risposta Outlook<br/>• Copia negli appunti<br/>(Nessun invio automatico)"]

    SCELTA -->|"Verifica sicurezza"| A_SEC["Esecuzione controlli tecnici locali:<br/>SPF/DKIM/DMARC, domini simili, allegati, Defender"]
    A_SEC --> C_WEB_SEC{"Ricerca web reputazione attiva?"}
    C_WEB_SEC -->|"Sì (cerca solo domini)"| W_SEC["Interroga domini del mittente e dei link"]
    C_WEB_SEC -->|"No o non disponibile"| NO_W_SEC["Prosegue con i soli controlli locali"]
    W_SEC -->|"Errore o timeout web"| ERR_W_SEC["Segnala «Ricerca web non riuscita»"]
    W_SEC -->|"Completata"| REP_SEC["Analisi IA dei riscontri"]
    NO_W_SEC --> REP_SEC
    ERR_W_SEC --> REP_SEC
    A_SEC -->|"Profilo cloud al livello 1"| RES_LOCAL_SEC["Rapporto con i soli controlli locali"]
    REP_SEC --> RES_SEC["Rapporto sicurezza con evidenze dettagliate<br/>(L'IA non può abbassare il rischio tecnico locale)"]

    SCELTA -->|"Attività e appuntamenti"| A_TASK["Individua impegni nel corpo e allegati"]
    A_TASK --> RES_TASK["Elenco proposte nel riquadro:<br/>• Rivedi dettagli<br/>• «Aggiungi a Outlook» (categoria OnlyWorkspace)"]

    SCELTA -->|"Estrai dati"| A_DATA["Estrae campi e righe documento commerciale"]
    A_DATA --> RES_DATA["Visualizzazione griglia dati:<br/>• «Copia tabella»<br/>• «Salva CSV in Download»"]

    SCELTA -->|"Follow-up"| A_FLW["Legge fino a 12 precedenti dalla conversazione<br/>e dall'archivio, quando disponibili"]
    A_FLW --> C_CTX2{"Contesto locale sufficiente?"}
    C_CTX2 -->|"No"| ERR_CTX2["Arresto senza invio: nessun precedente<br/>viene accorciato o escluso arbitrariamente"]
    C_CTX2 -->|"Sì"| RES_FLW["Punti da seguire con riferimenti [Mn]<br/>apribili e dichiarazione di copertura"]
```

---

## 2. Scrittura e composizione

Nelle finestre di composizione (Nuova email, Risposta o Inoltro), il riquadro offre strumenti di supporto alla stesura. Il testo in lavorazione non è trattato come "email selezionata": non cerca nella posta né sul web se non espressamente richiesto.
La funzione di sostituzione della selezione protegge l'utente: se il cursore o la selezione cambiano durante la generazione, la sostituzione viene bloccata per evitare sovrascritture accidentali.

```mermaid
flowchart TD
    subgraph INGRESSO_COMP["Punti di ingresso in composizione"]
        direction TB
        C1["Scheda Messaggio: gruppo OnlyWorkspace"]
        C2["Barra comandi del riquadro (in composizione)"]
        C3["Chat nel riquadro (chiede di redigere o modificare)"]
        C4["Chat generale fuori composizione («Scrivi una mail a...»)"]
    end

    INGRESSO_COMP --> CHECK_TIPO{"Quale comando scegli?"}

    CHECK_TIPO -->|"Scrivi con IA"| ACT_WRITE["Inserisci istruzioni nel riquadro"]
    ACT_WRITE --> PREP_WRITE["Rileva destinatari, oggetto, testo scritto<br/>e messaggio citato (se risposta/inoltro)"]
    PREP_WRITE --> CALL_WRITE["Generazione bozza con modello attivo<br/>(Nessuna ricerca nella posta, salvo richiesta esplicita)"]

    CHECK_TIPO -->|"Migliora"| ACT_IMP{"Scegli lo stile:"}
    ACT_IMP -->|"Correggi"| M_CORR["Grammatica e ortografia"]
    ACT_IMP -->|"Più formale"| M_FORM["Tono professionale"]
    ACT_IMP -->|"Più cordiale"| M_FRIEND["Tono caldo ed empatico"]
    ACT_IMP -->|"Più breve"| M_SHORT["Sintesi e concisione"]
    ACT_IMP -->|"Più chiaro"| M_CLEAR["Semplificazione espressiva"]
    M_CORR --> PREP_IMP["Rileva testo selezionato in Word<br/>(oppure tutto il testo sopra la citazione)"]
    M_FORM --> PREP_IMP
    M_FRIEND --> PREP_IMP
    M_SHORT --> PREP_IMP
    M_CLEAR --> PREP_IMP
    PREP_IMP --> CALL_IMP["Elaborazione del testo col modello"]

    CHECK_TIPO -->|"Traduci"| ACT_TRAD{"Scegli la lingua di destinazione:"}
    ACT_TRAD -->|"Inglese, Francese, Tedesco, Spagnolo, Italiano"| PREP_TRAD["Rileva testo selezionato o scritto"]
    PREP_TRAD --> CALL_TRAD["Traduzione accurata senza alterare il senso"]

    CHECK_TIPO -->|"Oggetto"| ACT_SUBJ["Analizza il testo scritto sopra la citazione"]
    ACT_SUBJ --> CALL_SUBJ["Genera oggetto efficace e pertinente"]

    CALL_WRITE --> POST_PROC["Verifica post-elaborazione:<br/>• Rimozione duplicazione firma esistente<br/>• Esclusione prefisso «Oggetto:» non richiesto"]
    CALL_IMP --> POST_PROC
    CALL_TRAD --> POST_PROC
    CALL_SUBJ --> RES_SUBJ["Testo proposto nel riquadro:<br/>• Clic su «Usa come oggetto»"]

    POST_PROC --> CHECK_SEL{"C'era testo selezionato in Outlook all'avvio?"}

    CHECK_SEL -->|"Sì"| VERIF_SEL{"La selezione nell'editor è rimasta identica?"}
    VERIF_SEL -->|"Sì"| BTN_REPLACE["Pulsante «Sostituisci selezione» attivo"]
    VERIF_SEL -->|"No (spostata o deselezionata)"| BLK_REPLACE["«Sostituisci selezione» rifiutato per sicurezza<br/>(evita sovrascritture accidentali)"]

    CHECK_SEL -->|"No"| BTN_INS["Pulsante «Inserisci» al cursore"]

    BTN_REPLACE --> OPZ_FINALI["Azioni disponibili:<br/>• Inserisci / Sostituisci selezione<br/>• Copia negli appunti<br/>• Rigenera o modifica istruzione<br/>(Nessuna email viene mai inviata da sola)"]
    BLK_REPLACE --> OPZ_FINALI
    BTN_INS --> OPZ_FINALI

    C4 --> CALL_EXT["Generazione autonoma del testo"]
    CALL_EXT --> RES_EXT["Riquadro: pulsante «Nuova email»<br/>(apre finestra Outlook con testo e oggetto pronti)"]
```

---

## 3. Chat nel riquadro e ricerca assistita

La chat assistente risponde a domande sulla posta, sull'agenda o generali.
L'intento viene classificato sul PC; la risposta può comunque richiedere il modello. Chat e Cerca e analizza mostrano subito l'elenco delle email trovate; un elenco o un conteggio finisce lì. Altrimenti un'unica richiesta legge fatti locali a Minimo oppure originali ripuliti a Standard e con profili locali, in ordine cronologico. Ogni punto cita le sue email [Mn]: importi, date e riferimenti non trovati nell'email citata sono marcati «non verificato». Le schede IA pregresse non alimentano la richiesta e non vengono generate automaticamente.

```mermaid
flowchart TD
    subgraph INGRESSO_CHAT["Punti di ingresso Chat"]
        direction TB
        K1["Icona Chat nella barra del riquadro"]
        K2["Scheda OnlyWorkspace: pulsante Chat"]
        K3["Campo testo «Scrivi un messaggio...»"]
    end

    INGRESSO_CHAT --> DOMANDA["Invio della domanda o richiesta"]

    DOMANDA --> VERIF_EMAIL{"Fa riferimento all'email selezionata?<br/>(es. «riassumi questa email»)"}
    VERIF_EMAIL -->|"Sì"| LETTURA_SEL["Legge corpo integrale e allegati<br/>consentiti dell'email selezionata"]
    LETTURA_SEL --> RISP_FINALE

    VERIF_EMAIL -->|"No"| VERIF_SCRIV{"Chiede di redigere un messaggio?<br/>(o siamo in composizione bozza)"}
    VERIF_SCRIV -->|"Sì"| FLUSSO_SCRIV["Elabora il testo richiesto senza cercare nella posta<br/>(Pulsante «Nuova email» o «Inserisci» se in bozza)"]
    FLUSSO_SCRIV --> SALVA_STORICO

    VERIF_SCRIV -->|"No"| FILTRI_LOC["Classificazione e filtri sul PC;<br/>i termini non riconosciuti restano indizi di ricerca"]
    FILTRI_LOC --> ROUTE_DATI{"Quali dati occorrono?"}

    ROUTE_DATI -->|"Posta elettronica"| CHECK_PEC{"Nomina PEC o cassette specifiche?"}
    CHECK_PEC -->|"Filtro cassetta non gestibile in chat"| AVVISO_PEC["Avvisa del limite e rimanda a<br/>«Cerca e analizza» con selettore cassette"]
    CHECK_PEC -->|"Cassette standard"| CERCA_POSTA["Cerca nell'archivio locale cifrato;<br/>se non può rispondere, usa la ricerca Outlook"]

    ROUTE_DATI -->|"Agenda o contatti"| CERCA_OUTLOOK["Interroga calendario o rubrica di Outlook"]
    CERCA_OUTLOOK --> RISP_FINALE

    ROUTE_DATI -->|"Domanda generale / Web"| CHECK_WEB{"Ricerca web attiva o richiesta<br/>(«online», «notizie»...)?"}
    CHECK_WEB -->|"Sì"| CALL_WEB["Ricerca web secondo profilo e azione"]
    CHECK_WEB -->|"No"| SOLO_MODELLO["Risponde con la conoscenza del modello"]
    CALL_WEB -->|"Timeout o errore web"| RECUPERO_WEB["Avviso «Riscontri web mancanti» e risposta da IA"]
    RECUPERO_WEB --> RISP_FINALE
    CALL_WEB -->|"Successo"| RISP_FINALE
    SOLO_MODELLO --> RISP_FINALE

    CERCA_POSTA --> ELENCO_IMMED["Mostra subito l'elenco dei risultati:<br/>data/ora, mittente, oggetto, sintesi"]
    ELENCO_IMMED --> CHIAMATA_UNICA["Fatti locali o originali ripuliti secondo il profilo,<br/>in ordine cronologico, in un'unica richiesta;<br/>elenco e conteggio finiscono senza IA"]
    CHIAMATA_UNICA --> RISP_FINALE

    AVVISO_PEC --> SALVA_STORICO
    RISP_FINALE["Risposta strutturata a schermo:<br/>• Riferimenti alle email [Mn], valori controllati nel codice<br/>• Fonti web citate [Wn] e riga Copertura<br/>• Comandi Copia, Riprova e Rigenera"]
    RISP_FINALE --> SALVA_STORICO["Salvataggio automatico nello Storico cifrato<br/>(collegato all'email; riapribile con «Riprendi»)"]
```

---

## 4. Cerca e analizza

Questa schermata consente di impostare ricerche mirate su uno specifico perimetro (conversazione, cartella, cassette o intero archivio) ed eseguire analisi avanzate o estrazioni di tabelle e scadenze. Include inoltre la modalità speciale **Email senza risposta**, dedicata a monitorare la posta inviata e preparare solleciti.

Il diagramma riassume le analisi della posta. Per **Appuntamenti e scadenze** la selezione è locale e la richiesta usa i soli fatti locali a Minimo oppure originali ripuliti e fatti a Standard o con un profilo locale; se viene interrotta, resta un riepilogo della lettura, non l'elenco descritto per le altre analisi. Dettagli: [Appuntamenti e scadenze](cerca-analizza.md#appuntamenti-e-scadenze).

```mermaid
flowchart TD
    subgraph INGRESSO_SEARCH["Punti di ingresso Cerca e analizza"]
        direction TB
        S1["Icona Cerca nella barra del riquadro"]
        S2["Scheda OnlyWorkspace: pulsante «Cerca e analizza»"]
        S3["Scheda OnlyWorkspace: pulsante «Email senza risposta»"]
    end

    INGRESSO_SEARCH --> RAMO_TIPO{"Quale modalità avvii?"}

    RAMO_TIPO -->|"Email senza risposta"| ESR_SETUP["Imposta soglia giorni di attesa (es. 5 gg)<br/>e periodo (fino a 365 gg)"]
    ESR_SETUP --> ESR_RUN["Scansione locale della Posta Inviata<br/>nelle cassette scelte (senza IA)"]
    ESR_RUN --> ESR_RES["Elenco messaggi inviati senza riscontro"]
    ESR_RES --> ESR_ACT{"Azione sul messaggio:"}
    ESR_ACT -->|"Esporta"| ESR_EXP["Copia o esporta l'elenco"]
    ESR_ACT -->|"Bozza sollecito"| ESR_DRAFT["Il modello prepara un sollecito cordiale<br/>e apre la finestra Outlook da rivedere"]

    RAMO_TIPO -->|"Cerca e analizza standard"| AMBITO{"Dove cercare?"}

    AMBITO -->|"Conversazione dell'email"| AMB_CONV["Conversazione: i 20 messaggi più recenti,<br/>totale dichiarato (altri filtri disattivati)"]
    AMBITO -->|"Cartella dell'email"| AMB_FOLD["Cartella corrente dell'email selezionata"]
    AMBITO -->|"Account e cassette"| AMB_BOX["Cassette aperte (Solo Posta in arrivo o Intera cassetta)"]
    AMBITO -->|"Cartelle scelte"| AMB_TREE["Cartelle selezionate manualmente con/senza sottocartelle"]
    AMBITO -->|"Tutto l'archivio"| AMB_ARCH["Tutte le cassette indicizzate nell'archivio locale"]

    AMB_CONV --> FILTRI
    AMB_FOLD --> FILTRI
    AMB_BOX --> FILTRI
    AMB_TREE --> FILTRI
    AMB_ARCH --> FILTRI

    FILTRI["Imposta filtri opzionali:<br/>• Persona e ruolo (mittente, destinatario, entrambi)<br/>• Parole chiave in oggetto o testo<br/>• Intervallo date · Allegati"]

    FILTRI --> OBIETTIVO{"Scegli l'obiettivo di analisi:"}

    OBIETTIVO -->|"Riepilogo o Punti aperti"| OB_SUM["Sintesi strutturata degli argomenti"]
    OBIETTIVO -->|"Personalizzata"| OB_CUST["Istruzione libera scritta dall'utente"]
    OBIETTIVO -->|"Tabella dati"| OB_TAB["Estrazione campi e righe documento"]
    OBIETTIVO -->|"Appuntamenti e scadenze"| OB_DATE["Ricerca impegni e termini temporali"]

    OB_SUM --> RUN_SEARCH["Avvio scansione ambito"]
    OB_CUST --> RUN_SEARCH
    OB_TAB --> RUN_SEARCH
    OB_DATE --> RUN_SEARCH

    RUN_SEARCH --> CHECK_CAND{"Trovate email candidate?"}
    CHECK_CAND -->|"No"| RES_ZERO["Messaggio «Nessuna candidata trovata»<br/>con dettaglio della copertura esaminata"]
    CHECK_CAND -->|"Sì"| READ_BATCH["Elenco immediato in ordine cronologico;<br/>fatti locali o originali secondo il profilo"]
    READ_BATCH --> PROCESS_IA["Un'unica richiesta al modello IA,<br/>citazioni [Mn] verificate dal PC"]

    PROCESS_IA --> CHECK_USER_STOP{"L'utente preme «Interrompi»?"}
    CHECK_USER_STOP -->|"Sì"| RES_PARTIAL["Resta l'elenco delle email trovate,<br/>registrato nello storico («Analisi interrotta»)"]
    CHECK_USER_STOP -->|"No (analisi completata)"| RES_FULL["Risultato finale strutturato:<br/>• Riepilogo o punti aperti con fonti [Mn]<br/>• Tabella dati (con comandi Copia / Salva CSV)<br/>• Scadenze (con «Aggiungi a Outlook» per quelle nuove)"]
```

---

## 5. Archivio locale, ricostruisci flusso e manutenzione

L'archivio è uno specchio cifrato locale della posta acquisita da Outlook.
Consente la ricerca offline e il recupero del testo integrale anche a Outlook chiuso, la ricostruzione della cronologia dei thread con le loro ramificazioni e la gestione autonoma per-utente delle cassette senza privilegi amministrativi.

```mermaid
flowchart TD
    subgraph INGRESSO_ARCH["Punti di ingresso Archivio e Flusso"]
        direction TB
        AR1["Indicatore «Archivio» nella barra del riquadro"]
        AR2["Scheda OnlyWorkspace: pulsante «Stato»"]
        AR3["Menu «⋯» dell'email: «Ricostruisci la pratica»"]
        AR4["Icona tray Windows: «Ricerca e stato archivio»"]
    end

    INGRESSO_ARCH --> SCELTA_ARCH{"Quale funzione utilizzi?"}

    SCELTA_ARCH -->|"Raccolta automatica"| BK_RACCOLTA["Acquisizione specchio cifrato in background:<br/>• Parte 60s dopo l'avvio di Outlook<br/>• Lavora dopo 10s di inattività dell'utente<br/>• Cede subito il passo alle azioni interattive"]
    BK_RACCOLTA --> CHECK_DISCO{"Spazio su disco disponibile?"}
    CHECK_DISCO -->|"< 5 GiB"| WARN_DISCO["Avviso di spazio in esaurimento nella tray"]
    CHECK_DISCO -->|"< 1 GiB"| SUSP_DISCO["Sospensione automatica protettiva delle acquisizioni<br/>(riprende da sola quando si libera spazio)"]
    CHECK_DISCO -->|"Normale"| SALVA_DB["Salvataggio locale cifrato formato 13"]

    SCELTA_ARCH -->|"Ricerca offline da tray"| TRAY_SEARCH["Ricerca paginata su testi e allegati<br/>(funzionante anche con Outlook chiuso!)"]
    TRAY_SEARCH --> RECUPERO_ORIG{"Clic su «Recupera testo originale»"}
    RECUPERO_ORIG -->|"Testo già acquisito"| VIEW_OFFLINE["Visualizzazione testo integrale offline"]
    RECUPERO_ORIG -->|"Non ancora acquisito"| NEED_OUTLOOK["Avvisa che occorre aprire Outlook"]

    SCELTA_ARCH -->|"Ricostruisci la pratica"| FLOW_TREE["Costruzione albero cronologico conversazione:<br/>collegamento per Message-ID e riferimenti,<br/>ramificazioni e messaggi non ancora indicizzati"]
    FLOW_TREE --> BTN_SINT_FLOW{"Clic su «Genera sintesi»?"}
    BTN_SINT_FLOW -->|"Sì"| RUN_SINT_FLOW["Una sola richiesta sulle email del flusso:<br/>tappe, decisioni e riferimenti [Mn] verificati"]
    BTN_SINT_FLOW -->|"No"| VIEW_FLOW["Consultazione visiva della cronologia"]

    SCELTA_ARCH -->|"Gestione «Le mie cassette»"| MIE_CASSETTE["Pannello di controllo personale (senza UAC):<br/>• Spunta per includere/escludere ogni cassetta<br/>• Opzione «Tutte le cassette, anche future»<br/>• Configurazione «Indirizzi miei» per archivi PST"]
    MIE_CASSETTE --> GEST_CHIUSE{"Cassetta chiusa o rimossa da Outlook?"}
    GEST_CHIUSE -->|"Sì"| BTN_DEL_BOX["Etichettata «(chiusa)»: pulsante «Togli dall'archivio»<br/>(rimuove email e dati derivati dopo conferma)"]

    SCELTA_ARCH -->|"Manutenzione archivio"| MANUTENZIONE["Comandi disponibili in Stato o Tray:<br/>• Pausa / Riprendi raccolta<br/>• «Aggiorna da Outlook» (nuova passata)<br/>• «Ottimizza indice» e «Compatta database»<br/>• «Elimina archivio» (conferma in due clic)"]
```

---

## 6. ConfigTool, primo avvio e governance privacy

La finestra ConfigTool gestisce la configurazione dell'add-in e la tutela dei dati.
Nessun dato di Outlook esce verso server esterni finché un responsabile non accetta l'informativa in **Privacy e dati** (*Data Egress Gate*). In caso di problemi di avvio con Outlook, il comando **Ripara add-in** ripristina la registrazione dell'utente senza reinstallare.

```mermaid
flowchart TD
    subgraph INGRESSO_CONF["Punti di ingresso Configurazione"]
        direction TB
        CF1["Primo avvio utente Windows (Wizard guidato)"]
        CF2["Scheda OnlyWorkspace: pulsante «Configurazione»"]
        CF3["Menu contestuale icona Tray Windows"]
        CF4["Avvio diretto «OnlyWorkspace.ConfigTool.exe»"]
    end

    INGRESSO_CONF --> CHECK_STATO{"Quale operazione esegui?"}

    CHECK_STATO -->|"Primo avvio guidato"| WIZARD["Procedura guidata in 5 passaggi:<br/>1. Spiegazione dell'app e dei dati salvati sul PC<br/>2. Scelta servizio IA: provider, endpoint, chiave, modello<br/>3. Abilitazione archivio email e indice allegati<br/>4. Avvio con Windows e integrità add-in<br/>5. Riepilogo e salvataggio"]
    WIZARD --> SAVE_WIZ["Salvataggio configurazione (richiede UAC per ProgramData)"]

    CHECK_STATO -->|"Profili IA e Modelli"| PROFILI["Pannello Profili IA:<br/>• Aggiunta/modifica provider (Ollama, OpenAI, Anthropic, Gemini, Qwen...)<br/>• Inserimento credenziali nel vault cifrato DPAPI<br/>• Contesto Ollama e autorizzazioni sui dati"]
    PROFILI --> TEST_CONN{"Verifiche del profilo:"}
    TEST_CONN -->|"Verifica connessione"| RUN_TEST["Verifica raggiungibilità dell'endpoint"]
    TEST_CONN -->|"Scegli modello…"| RUN_MODELS["Elenco modelli disponibili direttamente dal servizio"]
    TEST_CONN -->|"Verifica Ricerca web"| RUN_WEB_CHECK["Prova reale di ricerca web (singola, entro 60s)<br/>per abilitare la casella di ricerca web"]

    CHECK_STATO -->|"Governance Privacy e Uscita Dati"| PRIVACY["Pannello Privacy e dati:<br/>Mostra server destinatari calcolati e spazio occupato"]
    PRIVACY --> CHECK_EGRESS{"L'uscita dati verso i server è approvata?"}
    CHECK_EGRESS -->|"No (mancante o nuovi server)"| BLK_CLOUD["Stato: «(bloccato)» per i profili cloud e web search<br/>(Nessun dato Outlook lascia il PC)"]
    CHECK_EGRESS -->|"Sì"| OK_CLOUD["Uscita dati attiva verso i soli server autorizzati"]

    BLK_CLOUD --> COMPILA_INF["Clic su «Compila informativa...»:<br/>Registrazione dati responsabile (nome, ruolo, azienda, email, data)"]
    COMPILA_INF --> ACCETTA_INF["«Accetta e salva» (sblocca i server coperti)"]
    ACCETTA_INF --> OK_CLOUD
    OK_CLOUD --> REVOCA_INF["«Revoca»: prima scegliere un profilo predefinito locale;<br/>non richiama dati già inviati"]
    REVOCA_INF --> BLK_CLOUD

    CHECK_STATO -->|"Riparazione add-in Outlook"| REPAIR["Pannello Generale → Primo avvio:<br/>Diagnosi stato di caricamento e disattivazioni Office"]
    REPAIR --> BTN_REPAIR["Clic su «Ripara add-in»:<br/>• Ripristina LoadBehavior=3 in HKCU<br/>• Rimuove l'add-in da Resiliency\\DisabledItems<br/>• Registra DoNotDisableAddinList"]
    BTN_REPAIR --> RESTART_OUTLOOK["Riavvio di Outlook con add-in ripristinato"]

    CHECK_STATO -->|"Backup e Ripristino"| BACKUP_CFG["Pannello Manutenzione e backup:<br/>• Creazione copia cifrata configurazione e vault<br/>• «Ripristina backup...» con validazione preventiva<br/>• Gestione file di configurazione non leggibili"]
```

---

## Controlli e limiti sui dati

| Principio | Comportamento |
|---|---|
| **Nessun invio automatico** | Bozze, solleciti e risposte vengono sempre mostrati all'utente o aperti in Outlook per la revisione prima dell'invio. |
| **Modifiche esplicite** | Le proposte per calendario e attività vengono salvate con un’azione dell’utente. La Chat può cercare contatti Outlook; non esiste un flusso di scrittura della rubrica. |
| **Protezione contesto locale** | Se il testo supera il limite con Ollama, l'azione si ferma con un messaggio chiaro anziché inviare estratti mozzati. |
| **Governance uscita dati** | Gli invii di dati Outlook a servizi esterni richiedono l’informativa accettata per le destinazioni, anche per il web con Ollama locale. Le prove di connessione usano dati sintetici. |
| **Ricerca web non disponibile** | L'azione può proseguire senza riscontri web e ne segnala l'assenza; il modello configurato può comunque richiedere una connessione. |

L’archivio cifrato locale non rende locale un’elaborazione cloud. Solo l’inferenza su loopback senza web può lavorare offline; uso e conservazione dei dati esterni dipendono dal contratto/piano del provider. Vedere [Sicurezza e dati](../admin/sicurezza.md). Le keytip del ribbon sono distinte dalle scorciatoie del riquadro (per esempio B per Bozza risposta nel ribbon, Alt+R nel riquadro).
