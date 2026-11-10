# ConfigTool e tray

[Program.cs](../../src/OnlyWorkspace.ConfigTool/Program.cs) espone GUI, tray e CLI.

| Avvio | Risultato |
| --- | --- |
| `OnlyWorkspace.ConfigTool.exe` o `--gui` | Finestra di configurazione, con la tray |
| `OnlyWorkspace.ConfigTool.exe --tray` | Solo tray |
| `OnlyWorkspace.ConfigTool.exe <comando>` | CLI, poi esce |

Una sola istanza GUI/tray per utente; una seconda apertura porta avanti quella esistente. Si apre da **OnlyWorkspace → Configurazione** in Outlook o dal menu della tray. Chiudere o ridurre la finestra la nasconde nella tray.

## Pagine

| Pagina | Contenuto |
| --- | --- |
| **Profili IA** | **Connessione** (connettore, indirizzo, modello, chiave, tempi), **Contesto**, **Autorizzazioni** (cloud, livello di esposizione dei dati, testo degli allegati, Ricerca web e azioni), **Avanzate** (esito, provenienza e data di ogni capacità; Aggiorna capacità, Prova funzioni) |
| **Generale** | Profilo predefinito, limiti, indice del testo degli allegati, OCR di scansioni e immagini, storico, password, avvio con Windows, Primo avvio e Ripara add-in |
| **Archivio email** | Permesso dell'archivio e **Le mie cassette** |
| **Manutenzione e backup** | Backup e ripristino, Apri cartella dati, Log attività |
| **Privacy e dati** | Informativa sull'uscita dei dati ed export PDF, spazio occupato dai file dell'app, chiavi di registro e server destinatari calcolati dai profili |
| **Informazioni** | Autore, versione, licenza, contributi |

In basso: **Sblocca**, **Reimposta password (UAC)**, **Salva** (con UAC per un utente senza privilegi), **Chiudi**; dopo il salvataggio gli eventuali avvisi di validazione compaiono nella barra di stato (tutti nel log). Con la configurazione bloccata restano usabili Esporta PDF, avvio con Windows, Ripara add-in e la cartella dei dati. Le conferme di operazioni difficili da annullare (revoca, rimozione, ripristino, compattazione) propongono di default «Annulla». La revoca dell'informativa è consentita anche con un profilo cloud predefinito: dopo il salvataggio riuscito gli invii esterni sono bloccati. Ogni comando e campo ha un suggerimento che dice a cosa serve, chi ne è toccato e se serve Salva; compare anche sui campi disattivati (configurazione bloccata o opzione non disponibile); i testi informativi si copiano con Ctrl+C o **Copia tutto**. Tema e DPI seguono Windows e il monitor. Navigazione: Tab tra i gruppi, frecce dentro barre laterali e schede.

Il salvataggio diretto e quello elevato preparano configurazione e vault cifrato prima di sostituire la coppia attiva. Se fallisce la scrittura del JSON, recuperano il vault precedente; le modifiche restano nella finestra per riprovare. Se anche il recupero fallisce, il messaggio indica la copia conservata. Questa gestione degli errori non garantisce l'atomicità dei due file in caso di crash o perdita di alimentazione: conservare un backup verificato.

I messaggi d'errore mostrano istruzioni in italiano; il dettaglio tecnico resta nei log. Per i salvataggi elevati, l'esito italiano è separato dal dettaglio copiabile in **Manutenzione e backup → Log attività**. La procedura guidata distingue la connessione non riuscita dal servizio raggiungibile con il modello configurato assente: in questo secondo caso invita a scegliere un modello dall'elenco.

## Spazio occupato

**Privacy e dati → File sul PC e spazio occupato** misura, all'apertura della pagina e con **Aggiorna**, le cartelle che l'app scrive: archivio (diviso in database, giornali, copie di ripristino, chiave e blocchi), log, storico, dati del riquadro, preferenze, copie temporanee, configurazioni non leggibili e configurazione comune, con totale e spazio libero sul disco dell'archivio. Legge solo le dimensioni dei file, senza aprire l'archivio. Outlook e ConfigTool scrivono log separati, con rotazione giornaliera e al massimo 14 file da 10 MB per componente; quelli del ConfigTool hanno prefisso `onlyworkspace-configtool-`. Le cartelle sono quelle dell'utente Windows che apre il ConfigTool.

## Uscita dei dati

**Privacy e dati → Leggi e autorizza…** mostra l'informativa 7 e i profili/destinatari ricavati dalla configurazione, con l'area del servizio di ciascuno. Non richiede anagrafica o dichiarazioni da compilare: seleziona la conferma (e, per i profili con area non specificata, la conferma dedicata) e premi **Autorizza e salva** (UAC se necessario). Utente Windows, postazione, data e testo accettato sono registrati automaticamente. Le autorizzazioni restano distinte per profilo e legate a destinazione, livello, permessi e chiavi effettive. I record precedenti sono conservati e richiedono rinnovo.

Il motore richiede la conferma dedicata per tutti i profili cloud con area non specificata prima di registrare una nuova accettazione. Lo storico delle autorizzazioni e il rapporto esportabile riportano gli identificativi dei profili confermati.

Passando alla revisione 4 del catalogo di mascheramento, rinnova da questa schermata le autorizzazioni dei profili interessati. [Forme di importo riconosciute](../tecnico/pseudonimizzazione.md#autorizzazione-e-limiti)

**Revoca** blocca gli invii. Il normale **Salva** permette di mantenere un profilo cloud non autorizzato; non apre l'informativa. Annullamento, diniego UAC o errore di salvataggio preservano l'autorizzazione precedente. **Esporta PDF…** descrive configurazione salvata, autorizzazione tecnica, storico e testo accettato, senza inventare contratti o adempimenti.

Minimo consente esclusivamente fatti strutturati protetti. Standard invia anche email e testo libero, già mascherati; Esteso aggiunge il testo degli allegati, con il permesso distinto **Includi allegati verso il cloud**. Nomi dei file, file binari e query web non verificabili non escono. Importi e date sono sempre sostituiti nel cloud; in locale solo gli identificativi, salvo le opzioni del profilo. **Area del servizio** (UE, non UE, non specificata) è dichiarata per profilo e non è dedotta dall'indirizzo. Sintesi e riscrittura a Minimo richiedono Standard o un profilo locale, senza cambio automatico di provider. Dettagli in [Pseudonimizzazione](../tecnico/pseudonimizzazione.md).

Le spiegazioni delle impostazioni sono disponibili nei tooltip e nei pulsanti **?**, che aprono descrizioni dedicate. Stato, errori e motivi di blocco rimangono visibili. La conferma tecnica non certifica conformità normativa.

## Le mie cassette

Vale per l'utente Windows corrente e si applica subito, senza Salva né UAC, anche con la configurazione bloccata. **Archivio attivo per me** spegne o riaccende l'archivio dell'utente. La griglia elenca le cassette viste da Outlook con la colonna **Archivio** e la riga **Tutte le cassette, anche future**: senza «Tutte», una casella vuota è un rifiuto esplicito.

Durante il salvataggio le scelte sono bloccate. Se il salvataggio non è confermato, la griglia mostra lo stato riletto dall’archivio; se anche la rilettura fallisce, le caselle diventano indeterminate e occorre **Aggiorna** prima di cambiarle. Le caselle bloccate spiegano il motivo nel suggerimento.

Prima che esista l'archivio, il nome del profilo Outlook viene letto dal registro; non è ancora un elenco di cassette indicizzate. I messaggi distinguono:

| Messaggio | Cosa fare |
| --- | --- |
| **Archivio non ancora creato** | Aprire Outlook; le cassette compaiono dopo la prima raccolta |
| **Outlook non è ancora stato configurato** | Creare un profilo in Outlook classico |
| **L'archivio non è abilitato su questo PC** | L'amministratore attiva **Archivio locale delle email** e salva |

Finché mancano le cassette, la pagina visibile rilegge lo stato ogni 5 secondi. Dopo un caricamento completo conserva la griglia per un minuto e durante le operazioni, così messaggi e indirizzi in modifica restano; **Aggiorna** richiede una rilettura quando non è già in corso un'operazione.

Le cassette chiuse, anche quelle tolte dal profilo Outlook, sono elencate come «(chiusa)» con le caselle bloccate e il pulsante **Togli dall'archivio**, che ne cancella i dati dopo una conferma. **Indirizzi miei** compare per i PST senza account. Dettagli d'uso: [Archivio](../utente/archivio.md).

## Profili IA e Ricerca web

Per un nuovo profilo sono disponibili Ollama, Gemini, Anthropic, OpenAI, QwenCloud e Compatibile OpenAI. Nel generico compilare l'indirizzo; autenticazione e capacità dipendono dal collegamento. Le tre eccezioni per profili già esistenti e la conversione degli altri preset sono descritte in [Provider IA](provider-ia.md#preset-disponibili). Cambiare servizio resta una scelta esplicita e si applica con Salva.

- Quattro comandi distinti. **Scegli modello…** elenca i modelli del servizio e non prova nulla. **Verifica connessione** controlla indirizzo, chiave e modello con una richiesta sintetica (con eventuale consumo). **Aggiorna capacità** rilegge limiti e capacità che il servizio pubblica, senza generare testo e senza costi; QwenCloud e altri servizi compatibili non pubblicano nulla. **Prova funzioni** verifica risposta testuale, JSON strutturato (contro uno schema) e ragionamento osservabile, più la Ricerca web se selezionata: richieste brevi con dati inventati, una alla volta, al massimo 60 s ciascuna, senza ripetizioni e annullabili; può consumare token.
- **Avanzate** mostra per ogni funzione esito e provenienza: dichiarata dal servizio, verificata dall'app, non dichiarata o non verificabile (prova inconcludente), con la data. Timeout o chiave rifiutata lasciano la prova inconcludente, non dimostrano un'incapacità; il ragionamento è «sì» solo se osservato (testo di ragionamento o token dichiarati), altrimenti inconcludente. Visione e Strumenti restano solo dichiarati: l'app non li usa e non li prova. Gli esiti si salvano con **Salva**, valgono per lo stesso servizio, autenticazione, indirizzo e modello e si cancellano cambiando la chiave; nessuna prova abilita da sola capacità o permessi.
- Svuotare **Chiave di accesso** rimuove la chiave al salvataggio; rinominare l'ID sposta chiavi, predefinito; rimuovere un profilo (con conferma) pulisce le chiavi non più usate.
- **Ricerca web** è abilitabile subito se il provider la dichiara (Anthropic; Ollama con strumenti e chiave ollama.com), altrimenti dopo **Verifica Ricerca web** (o **Prova funzioni** con la Ricerca web selezionata): una richiesta reale senza dati Outlook, una sola volta, entro 60 s, eventualmente addebitata. Cambiare modello, endpoint o credenziali annulla la verifica e spegne la spunta. La verifica riuscita non salva da sola e compare anche in Avanzate.

## Primo avvio

Al primo avvio per ogni utente si apre la **Configurazione guidata** (riapribile da Generale o dalla tray): cosa fa l'app e quali file scrive, modello IA, permesso dell'archivio e dell'indice degli allegati, avvio con Windows e controllo dell'add-in, riepilogo.

Il passo **Archivio delle email** spiega che il database cifrato conserva intestazioni e corpo integrale acquisito, oltre al testo degli allegati leggibili se abilitato. La ricerca usa il testo conservato; l'anteprima di 255 caratteri è solo una modalità di visualizzazione. I file allegati originali non vengono archiviati. Un modello su loopback senza Ricerca web mantiene il testo sul PC; destinazioni esterne e Ricerca web seguono i rispettivi permessi e il controllo dell'informativa.

Nel passo **Modello IA**, compilare nell'ordine **Servizio → Indirizzo → Chiave → Modello**. **Scegli modello…** usa indirizzo e chiave per elencare i modelli del servizio; con un modello vuoto anche **Avanti** o **Verifica connessione** propongono l'elenco. Se il servizio non elenca modelli, scriverne il nome. Queste prove non inviano dati Outlook. Il passo **Avvio e Outlook** mostra il profilo letto dal registro anche prima della creazione dell'archivio. **Applica e salva** modifica il profilo predefinito e passa da validazione e UAC.

## Ripara add-in

**Generale → Primo avvio** mostra se Outlook carica l'add-in per l'utente (`LoadBehavior` in HKLM a 64/32 bit e HKCU, `Resiliency\DisabledItems` di Office 16.0). **Ripara add-in**, solo su HKCU: `LoadBehavior=3`, rimozione dagli elementi disattivati e `DoNotDisableAddinList\OnlyWorkspace.Addin=1`; poi riavviare Outlook.

## Configurazione non leggibile

Il `secrets.bin` è per utente in `%LOCALAPPDATA%\OnlyWorkspace` (cartella di configurazione scelta: accanto a `config.json`). Se `config.json` o `secrets.bin` esistono ma non si leggono, il salvataggio è bloccato e un dialogo propone **Ripristina backup…**, **Nuova configurazione** (copia prima i file in `%LOCALAPPDATA%\OnlyWorkspace\config-non-leggibile\<data>`; le chiavi vanno reinserite) o **Sola lettura**. Un `config.json` assente non è un errore: viene proposto un profilo Ollama locale.

Un `secrets.bin` presente di zero byte è danneggiato: il caricamento fallisce senza sostituire le chiavi già in memoria e backup/ripristino/apply-staged lo rifiutano prima di modificare la destinazione. Un vault correttamente cifrato con dizionario vuoto è valido; un file assente continua invece a revocare le chiavi caricate.

## CLI

| Comando | Azione |
| --- | --- |
| `validate <config.json>` | Valida schema e policy |
| `import <source.json> [target_dir] [--skip-test]` | Valida e importa la coppia configurazione/vault; senza `--skip-test` prova le connessioni |
| `show [config_path]` | Mostra la configurazione con segreti mascherati |
| `test [config_path]` | Prova gli endpoint dei profili |
| `backup [dest.owbak] [config_dir]` | Salva `config.json`; con la cartella standard le chiavi (per utente) restano fuori e si reinseriscono dopo un ripristino, con una cartella scelta salva anche `secrets.bin` |
| `restore <source.owbak> [config_dir]` | Valida e ripristina sullo stesso PC |

`validate` legge il file indicato. `show` e `test` usano la directory indicata oppure quella che contiene il percorso passato, e vi cercano `config.json`; senza percorso usano la configurazione comune del PC. `test` contatta i provider configurati.

`import` accetta `profiles`, `auth` e `secret` senza distinguere maiuscole e minuscole; proprietà duplicate per questi nomi e strutture incompatibili sono rifiutate prima del salvataggio. Un `auth.secret` non vuoto viene trasferito al vault e prevale sul `secretRef` del profilo; `auth` assente/null o `secret` assente/null/vuoto conservano il riferimento esistente. Il JSON salvato non contiene il segreto inline. Con `--skip-test` non viene contattato alcun provider.

Anche `import` prepara entrambi i file e usa il recupero della coppia dei salvataggi GUI: un errore sulla sostituzione del JSON ripristina il vault precedente, inclusa la sua assenza iniziale. Se il recupero fallisce, l’errore indica la copia cifrata conservata. È gestione degli errori ordinari, senza garanzia di atomicità in caso di crash o perdita di alimentazione.

`restore` accetta solo i due file (con la cartella standard ignora `secrets.bin`: le chiavi per utente non si scrivono con i diritti di amministratore), ne controlla dimensioni e vault prima di scrivere e, se la seconda sostituzione fallisce, riporta `secrets.bin` com'era. Un arresto improvviso tra le due sostituzioni non è coperto: ripetere il ripristino dal backup. Il backup non include archivio, storico, preferenze né la password amministrativa. Con la password attiva `import` e `restore` la chiedono nel terminale.

## Tray

Il clic sull'icona apre **Ricerca e stato archivio** (Ricerca, Stato e sincronizzazione, Manutenzione), anche a Outlook chiuso. Il menu ha tre gruppi: **Archivio** (stato e ricerca), **IA** (stato separato e verifica esplicita della connessione) e **Configurazione** (finestra, procedura guidata, cartelle, **Avvia con Windows**). **Esci** ferma il motore: Outlook lo riavvia alla raccolta successiva. Il marchio della tray mantiene un badge dell'archivio; lo stato IA diventa verificato solo dopo una verifica richiesta dall'utente e ritorna non verificato al cambio di configurazione. I badge gialli animano solo lavoro effettivo, rispettando la preferenza di animazione e l'alto contrasto di Windows. Le attese restano grigie. Sotto 5 GiB e 1 GiB liberi la tray avvisa dello spazio.

Codice: [MainConfigForm](../../src/OnlyWorkspace.ConfigTool/Forms/MainConfigForm.cs), [SetupWizardForm](../../src/OnlyWorkspace.ConfigTool/Forms/SetupWizardForm.cs), [ArchiveSearchForm](../../src/OnlyWorkspace.ConfigTool/Forms/ArchiveSearchForm.cs), [TrayApplicationContext](../../src/OnlyWorkspace.ConfigTool/Tray/TrayApplicationContext.cs).

Nella ricerca locale sono disponibili importo minimo/massimo, valuta e scadenze dal/al; l'anteprima mostra i fatti con l'origine. Il recupero automatico dai testi conservati è locale, rispetta Pausa e non consuma token. Le email in attesa o non elaborate restano dichiarate; a recupero concluso ripetere la ricerca. Alla dimensione minima la pagina scorre. Dettagli: [Fatti e filtri locali](../utente/archivio.md#fatti-e-filtri-locali).

Salvataggio elevato, modifica della password e ripristino mostrano lo stato di attesa lasciando reattiva la finestra. Durante l’applicazione le altre azioni e la chiusura sono bloccate; annullare UAC conserva la configurazione precedente. Le etichette italiane del livello di log salvano i valori tecnici originali. Il testo firmato dell’informativa v2 conserva «Test connessione»: il comando attuale è **Verifica connessione**, senza nuova accettazione per questo cambio di etichetta.
