# Motore dell'archivio

La tray possiede i database e li espone su pipe; Outlook raccoglie e invia i lotti. Uso lato utente: [Archivio](../utente/archivio.md).

## Formato, conservazione e ricostruzione

Il formato corrente è **13**, la pipe **v14**: tray e add-in devono essere aggiornati insieme. I nuovi archivi nascono senza generazione IA automatica. I formati diversi da 11, 12 e 13 sono rifiutati senza eliminare dati.

I formati 11 e 12 vengono migrati sotto ownership esclusiva, prima di servire richieste. Il motore crea una copia cifrata coerente con la [SQLite Backup API](https://www.sqlite.org/backup.html), include `archive.key` già protetta con DPAPI e verifica riapertura, tutte le righe di `integrity_check`, versione e impronta dei dati conservati. Backup e manifest SHA-256 rimangono in `%LOCALAPPDATA%\OnlyWorkspace\archive-migration-backups\<identificativo>`; non vengono eliminati automaticamente. Le copie appartengono allo stesso utente Windows: non contengono una chiave in chiaro e non sono un ripristino portabile su un altro account.

Solo dopo la verifica, una transazione rimuove le chiusure delle pratiche e, dal formato 11, elimina i dati IA pregressi, i consensi e la contabilità dedicati, i marcatori cloud e le strutture di ricerca relative. Email, identità, testi, allegati, fatti, indici degli originali, scelte e cancellazioni pendenti sono confrontati prima del commit. Errore, spazio insufficiente o annullamento impediscono il commit. Per ripristinare: fermare Outlook e tray, conservare lo stato corrente e ripristinare insieme DB e chiave dalla copia verificata; una build corrente migra nuovamente i formati 11 e 12.

La ricostruzione riguarda soltanto i derivati: gli indici mancanti sono recuperati dai testi acquisiti, senza eliminare gli originali o richiedere Outlook. Le identità rimaste fuori da un indice precedente sono conservate in `archive_source_checks`: Outlook deve ancora provarne l'assenza prima di invalidare lo Storico. `archive_deletions` resta durevole.

## Raccolta e sincronizzazione

| Fase | Quando |
| --- | --- |
| Avvio della raccolta | 60 s dopo l'avvio di Outlook, poi solo dopo 10 s senza tastiera né mouse |
| Posta recente ed elenco cartelle | Ogni 2 minuti; nel log solo quando le cartelle cambiano |
| Controllo completo | 4 minuti dopo la chiusura del precedente, appena scade; nel log solo quando toglie email |
| Impostazioni | Rilette ogni 5 secondi |

- Raccolta solo dagli store aperti in Outlook, sul thread UI, nelle cassette scelte (`ArchiveScope`; elenco vuoto = tutte, anche future). Il riquadro traduce questa scelta nel contratto delle query: `null` = tutte, elenco esplicito = selezionate, elenco vuoto = nessuna autorizzata. Ricerca, espansione e flusso applicano lo stesso perimetro durante la visita; gli originali già acquisiti degli store chiusi restano consultabili. Una preferenza utente non leggibile sospende la raccolta. Posta eliminata e sottocartelle contano come presenza. Feed RSS, Indesiderata, Bozze, Posta in uscita e problemi di sincronizzazione restano esclusi.
- Un'email assente esce dall'archivio solo con una prova: in un PST basta l'inventario completo; in una cache (OST, Exchange, IMAP come Gmail) a fine controllo completo Outlook viene interrogato con [`GetItemFromID`](https://learn.microsoft.com/en-us/office/vba/api/outlook.namespace.getitemfromid) e solo `MAPI_E_NOT_FOUND` (`0x8004010F`) la toglie. Errori, pause e cache incomplete conservano l'ultima verifica e dichiarano la copertura parziale.
- Il controllo completo conserva i corpi già letti se le intestazioni non cambiano. Le identità sono per cartella ed `EntryID`: due copie con lo stesso Message-ID restano distinte.
- Testi e allegati da riprovare: attesa da 5 minuti che raddoppia; dopo 8 tentativi (circa 21 ore) la sorgente è non disponibile finché l'email non cambia.
- Cassette tolte dall'archivio: email e cartelle eliminate a fette. Cassette chiuse in Outlook: restano, cercabili, finché l'utente non le toglie (`RemoveClosedStore`, a fette, rifiutato se Outlook ne elenca ancora una cartella), con indirizzi propri ed esclusi di Contatti della cassetta.
- Spazio: avviso sotto 5 GiB liberi, nuove acquisizioni sospese sotto 1 GiB; nessun tetto alla dimensione del database.

## Testi integrali e cancellazione

Un file storico non decifrabile non ferma la bonifica delle altre conversazioni leggibili: viene conservato e identificato nei log per nome e tipo d'errore, senza testo decifrato. La coda resta in «Pulizie in attesa» finché tutti i file sono verificabili e il checkpoint WAL riesce. Ripristinare il file da un backup verificato permette il retry; non viene cancellato o messo in quarantena automaticamente. Il ledger continua a bloccare caricamenti e salvataggi tardivi delle sorgenti eliminate, anche dopo un riavvio.

Il corpo è conservato nella base cifrata; `attachment_contents` conserva una sola versione del testo per SHA-256 del file, collegata alle copie email. La ricerca indicizza tutto il testo acquisito. La tray recupera il testo già acquisito anche a Outlook chiuso; un originale ancora da leggere richiede Outlook. Le analisi riusano il testo persistito, con gli stessi permessi sugli allegati; gli input parziali non diventano originali completi.

La revisione del lettore è distinta dal SHA-256: `attachments` e `attachment_contents` conservano `reader_revision`. Il motore sospende i vecchi testi e i loro termini, accodando la rilettura senza cancellare i corpi. Le correzioni della cache condivisa invalidano ricerca e fatti delle copie interessate; lavoro estratto da una versione superata non può essere confermato.

Il controllo periodico confronta `LastModificationTime` quando disponibile e conserva gli originali invariati. Le identità pregresse rimaste fuori dall’indice restano verificabili in `archive_source_checks`; la loro assenza richiede il controllo Outlook. `email_sequence` impedisce il riuso dell’ID di una versione eliminata da parte di un’acquisizione successiva e protegge dai lavori ancora in volo.

MSG, EML e P7M condividono profondità massima 2, espansione complessiva 30 MiB e limite testuale di 4 milioni di caratteri. In modalità archivio compressi generici, eseguibili e file senza lettore restano riferimenti senza estrazione, anche dentro questi contenitori; un riferimento dichiarato non rende incompleto il testo leggibile del contenitore. Un documento supportato in errore o oltre i limiti rimane invece incompleto. Office/OpenDocument restano leggibili. La lettura ZIP delle azioni dirette e della Verifica sicurezza conserva i limiti e i nomi temporanei generati, senza seguire i percorsi interni. DOC Word 97–2003 viene letto con OpenMcdf e la [piece table MS-DOC](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-doc/01d5d8c4-cf9c-4ef9-80fd-439e763cfe01); XLS con [ExcelDataReader 3.9.0](https://github.com/ExcelDataReader/ExcelDataReader/tree/v3.9.0). Nessuna macro o formula viene eseguita. Scansioni e immagini senza testo riconoscibile o senza OCR, password, strutture non supportate e limiti raggiunti hanno stato e motivo espliciti; l'OCR locale (E8) è descritto in [Allegati](allegati.md#ocr-e8).

Una copia assente esce dai risultati; il suo contenuto rimane finché esiste una copia dello stesso Message-ID. Senza Message-ID vale StoreID/EntryID. La cancellazione dell'ultima copia rimuove testi, indici e collegamenti; i testi degli allegati restano solo se ancora riferiti. Una cartella cache scomparsa richiede comunque la verifica Outlook. Le cassette chiuse rimangono disponibili fino alla rimozione esplicita.

La transazione registra `archive_deletions`, conservata anche durante una ricostruzione. La tray elimina l'intera conversazione che ha usato una sorgente eliminata, comprese le dipendenze non citate; un registro DPAPI di impronte e tempi impedisce salvataggi tardivi. Il riquadro invalida conversazione attiva e replay. Il registro distingue acquisizioni successive; non conserva estratti. Errori o riavvii lasciano la pulizia riprendibile e visibile come «Pulizie in attesa». SQLite e FTS usano [secure-delete](https://www.sqlite.org/fts5.html#the_secure_delete_configuration_option), seguito da checkpoint WAL: un checkpoint occupato non viene dichiarato concluso. Backup ed esportazioni precedenti non vengono modificati.

## Limiti

| Voce | Limite |
| --- | --- |
| Cartelle per cassetta | 3.000, profondità 12 (oltre: copertura incompleta dichiarata) |
| Testo indicizzato | Tutto il testo acquisito, senza taglio a 60.000 caratteri |
| Anteprima | 255 caratteri |
| Metadati | 50 destinatari e 30 nomi di allegati per email |
| Allegati | Tutti, copiati 10 per chiamata a Outlook, fino a 30 MiB ciascuno; testo complessivo fino a 4 milioni di caratteri |
| Ricerca | Pagine da 50 |
| Flusso | 20.000 email visibili, 20 possibili collegamenti (oggetto o mittente simili entro 30 giorni), i 20 messaggi più recenti della conversazione Outlook, con il totale dichiarato quando sono di più |

Fonti: [ArchiveCapacity](../../src/OnlyWorkspace.Core/Archive/ArchiveCapacity.cs), [ArchiveDb.Mail](../../src/OnlyWorkspace.Infrastructure/Archive/ArchiveDb.Mail.cs), [OutlookDataService.Archive](../../src/OnlyWorkspace.Outlook/Services/OutlookDataService.Archive.cs).

## Fatti locali

`ArchiveFactExtractor` (Core, revisione 3) legge deterministicamente testi integrali italiani/inglesi; non usa modelli. Ogni occorrenza conserva tipo, valore originale/normalizzato, eventuale valuta e giorno, fonte, versione del corpo, posizione UTF-16 e frase di origine nel testo acquisito. Date senza anno o ambigue restano irrisolte; gli inviti conservano giorni interi, UTC, orari senza fuso, TZID e regole di ricorrenza senza espanderle o inventare conversioni. La ricezione dell'email non diventa una scadenza. IBAN: normalizzazione e checksum modulo 97; i formati nazionali non riconosciuti sono indicati separatamente, senza attestare esistenza o titolarità del conto. P.IVA e CF italiani verificano forma e cifra di controllo, non l'anagrafe.

Le tabelle ricostruibili `archive_facts` e `archive_fact_jobs` si aggiungono al **formato 13**, senza ricostruzione dei corpi. La revisione dei fatti è distinta da quella del lettore. Le fonti mancanti nella coda sono recuperate dai testi conservati; il worker unico della tray estrae fuori dal lock del motore e ricontrolla identità, versione, testo e policy prima del commit atomico. Pausa e manutenzione impediscono nuovo lavoro/commit. Errori di estrazione sono contati separatamente, senza marcare completa una lettura parziale o ritentare continuamente la stessa versione fallita.

L'estrattore alla **revisione 3** conserva la valuta anche negli importi etichettati, per esempio `Importo 1000,01 EUR` e `Amount 1000.01 USD`. Riconosce anche «Scadenza pagamento:», «Scadenza fattura:» e «Data di scadenza:»; le date generiche non diventano scadenze. La coda recupera i fatti delle revisioni precedenti dai testi già acquisiti; fino al completamento quei derivati non vengono serviti come correnti. Nessuna valuta è dedotta da `$`, nessun separatore ambiguo viene interpretato. La revisione dei fatti non cambia gli originali o le scelte, né il formato 13. La pipe v14 aggiunge il filtro `ArchiveQuery.EmailRefs` per copie fisiche esatte (store/EntryID), combinato con le cassette consentite; elenco vuoto significa nessuna copia, massimo 500 per richiesta. Briefing e Appuntamenti vi leggono fatti Date/Deadline senza ricercare per oggetto/data.

Modifiche del corpo, degli allegati o della cache condivisa invalidano i fatti; cancellazioni e ricostruzioni li eliminano. Disabilitare il testo allegati rimuove anche i derivati e impedisce il commit di lavoro precedente. I dati PEC `facts-only` restano esclusi da FTS e prompt; il loro contenuto resta locale. Il recupero conserva originali, preferenze e Storico.

`ArchiveQuery.FactFilters` combina filtri per tipo/valore, importo/valuta e giorno prima di `LIMIT/OFFSET`; filtri distinti sono in AND e possono trovare occorrenze differenti della stessa email. `IncludeAttachmentText=false` esclude anche i fatti degli allegati; `IncludeFacts` aggiunge la provenienza ai risultati solo quando richiesta. `GetFactProgress` è esposto nella pipe corrente **v13** e i fatti arrivano con la ricerca (`IncludeFacts`): aggiornare tray e add-in insieme. Importi salvati come decimali canonici in TEXT, confrontati da una funzione SQLite deterministica con `decimal`, senza conversione a REAL: [tipi](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/types), [funzioni definite dall'applicazione](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/user-defined-functions).

Riferimenti: [iCalendar RFC 5545](https://www.rfc-editor.org/rfc/rfc5545.html), [regole tecniche PEC AgID](https://www.agid.gov.it/sites/default/files/repository_files/documentazione_trasparenza/dm_2-nov-2005.pdf), [registro IBAN SWIFT](https://www.swift.com/standards/data-standards/iban-international-bank-account-number) e [regex .NET con timeout](https://learn.microsoft.com/en-us/dotnet/standard/base-types/best-practices). Il download corrente del registro IBAN non era accessibile durante il lotto: nessuna certificazione del registro nazionale corrente.

## Ricerca e correlazioni locali

E5 mantiene parole alternative, prefissi e riduzione delle desinenze italiane (`fattura` e `fatture` cercano `fattur*`). I codici alfanumerici e numerici hanno due indici FTS5 contentless separati a trigrammi, per corpo/oggetto e allegati. Le parti di almeno tre caratteri, come `2026/118` in `ORD-2026/118`, sono letterali: separatori conservati, sintassi FTS non eseguibile. Più termini restano indizi alternativi; i filtri sui fatti sono invece in AND. Per codici di uno o due caratteri si confronta l'intero riferimento normalizzato. Oltre 100 termini di codice la ricerca chiede di restringere la richiesta.

`archive_search_jobs` riprende il recupero dai testi acquisiti, senza richieste IA. Finché una riga è in coda, la ricerca dei codici controlla anche i suoi originali conservati. Quando la coda è vuota non legge tutti i corpi: il sottoinsieme in attesa è isolato prima di valutare il ripiego. Il worker estrae fuori dal lock del motore e ricontrolla testi e policy prima del commit; aggiornamenti, cancellazioni, cache condivisa e revoca degli allegati invalidano i derivati. I trigger accodano soltanto identità distinte e assenti dalla coda, anche durante l'UPSERT di un testo condiviso: la [politica di conflitto esterna](https://www.sqlite.org/lang_createtrigger.html) può prevalere su `OR IGNORE` nel trigger. Le definizioni precedenti vengono sostituite nella transazione di apertura (`search_trigger_revision=2`), conservando testi, indici e lavori pendenti. Gli indici partecipano a ricostruzione, secure-delete e ottimizzazione; il formato resta 13 e la pipe v14. Riferimento: [FTS5 trigram](https://www.sqlite.org/fts5.html#the_trigram_tokenizer).

Le intestazioni aggiungono `References` e invio UTC; la riconciliazione aggiorna anche le righe esistenti senza invalidare corpo per soli cambi di questi metadati. Le colonne DASL forniscono UTC, mentre quelle Outlook con nome esplicito forniscono ora locale: [valori delle colonne](https://learn.microsoft.com/en-us/office/vba/api/outlook.row.getvalues), [References](https://learn.microsoft.com/en-us/office/client-developer/outlook/mapi/pidtaginternetreferences-canonical-property), [invio MAPI](https://learn.microsoft.com/en-us/office/client-developer/outlook/mapi/pidtagclientsubmittime-canonical-property). Proprietà non disponibili e store chiusi lasciano la copertura incompleta; non si ricavano date dal testo degli inoltri.

La catena percorre transitivamente Message-ID, In-Reply-To, References e ConversationId, oltre a riferimenti E4 risolti di uguale tipo e valore (ordine, fattura, protocollo). Conserva motivo e occorrenza di origine, senza trasformare la correlazione in una risposta. Importi, date e somiglianze non confermano collegamenti; i dati `facts-only` PEC non alimentano la catena o le sue evidenze per i prompt. La visita passa per identificativi condivisi, evitando di enumerare ogni coppia di messaggi di una conversazione ampia.

`ExpandRelated` sulla pipe v14 espande i risultati iniziali oltre periodo/mittente/fatti, segnala `OutsideFilters` e applica le cassette consentite durante la visita: una cassetta esclusa non può fare da ponte. Ordina per invio, altrimenti ricezione, in UTC; una voce per Message-ID conserva StoreId, EntryId e cartella di ogni copia. Senza Message-ID restano distinte le identità fisiche. Anche l'unione dei risultati archivio/Outlook nella chat evita copie doppie. Riquadro e finestra archivio espongono le copie, i motivi e la copertura incompleta. La risposta IA sulle email trovate è l'unica richiesta citata della E7 ([architettura](architettura.md)).

## Ricostruzione locale della pratica

Il preset `reconstruct` di Cerca e analizza usa l’ambito interno `flow` e la ricostruzione esistente, prima della factory IA. Il risultato `analysis_reconstruction` conserva identità dell’email di partenza, email, copie, possibili collegamenti, copertura e limiti; l’identificativo della richiesta impedisce di sostituire un risultato più recente. La sintesi parte soltanto dal comando esplicito sulla stessa fonte e applica la policy corrente del modello.

Non esistono stati o chiusure delle pratiche nel contratto attivo. La migrazione rimuove `archive_case_closures` e i lotti RPC `SetCaseClosed` dopo aver verificato il backup completo; i dati rimossi restano nella copia di recupero. I campi `Case`, `CaseFacts`, `Cases` e `CaseKey` sono ritirati dalla pipe v14.

## Manutenzione

**Ottimizza indice** usa [PRAGMA optimize](https://www.sqlite.org/pragma.html#pragma_optimize); **Compatta database** usa [VACUUM](https://www.sqlite.org/lang_vacuum.html) e richiede circa due volte il database più 1 GiB liberi. Durante la manutenzione l'archivio è in pausa e ne risponde lo stato memorizzato; gli altri profili restano disponibili, il timer del motore lo salta e le sue cancellazioni in sospeso si completano al termine.

## Evoluzione approvata

La ricerca locale, le correlazioni, la ricostruzione locale della pratica e l'OCR restano supportati. Le qualifiche del generatore ritirato sono soltanto storiche e restano nelle ricevute del dossier; il banco attivo misura esclusivamente i fatti locali, senza provider. Requisiti e qualifiche aperte sono nel tracker SecondBrain. Analisi e chat usano originali oppure fatti locali, secondo il livello di esposizione.

Il formato corrente mantiene copie distinte per cartella/EntryID e conserva i testi integrali. L'inattività di 10 secondi rimane nel raccoglitore; il 20/10 sono state tolte le opzioni configurabili di inattività e fascia oraria, non questa protezione del thread UI.

## Qualifica

Prove effettive e lacune, incluse Exchange/OST, grandi cassette e guasti fisici, sono mantenute solo in [Verifica e qualifica](verifica.md#non-qualificato).
