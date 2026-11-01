# Pseudonimizzazione delle richieste IA

Le richieste dei profili esterni passano dalla pipeline locale prima del trasporto. La mappa fra valori e segnaposto vive in memoria per una sola richiesta; la risposta viene ripristinata sul PC, anche in JSON e quando un segnaposto è diviso fra frammenti dello streaming. I profili locali non applicano questa pipeline.

## Strategie e dati locali

`Privacy/strategies.json`, incorporato nell'assembly Core, contiene revisione, pattern e parole introduttive dei nomi. Il catalogo iniziale riconosce email, telefoni italiani, IBAN, codici fiscali italiani, partite IVA e riferimenti etichettati a pratiche/protocolli/fascicoli. Non scarica regole né esegue estensioni ricevute dalla rete. Una modifica alle regole richiede una nuova revisione e la distribuzione dell'app.

I nomi riconosciuti accanto a un indirizzo email vengono cercati in tutti i messaggi della richiesta. Si aggiungono nomi, aziende, email e telefoni dei contatti negli store aperti, comprese le sottocartelle dei contatti, e nomi visualizzati nell'archivio delle sole cassette consentite. La raccolta dei contatti non usa il limite di 200 risultati della ricerca; opera sul thread UI STA, con pause fra le letture. L'archivio espone pagine ordinate per ID attraverso la pipe v15; il formato del database resta 13. La mappa derivata non è persistita.

In caso di sovrapposizioni prevalgono gli identificativi specifici, quindi le corrispondenze più lunghe a parità di priorità. Token già presenti, come `[M1]` e `[PERSONA_1]`, sono preservati; i nuovi segnaposto non ne riutilizzano il nome. Le stesse occorrenze letterali condividono un segnaposto nella richiesta.

Nel ConfigTool, **Profili IA → Autorizzazioni**, le opzioni per importi e date sono indipendenti e inizialmente disattivate, anche quando mancano nelle configurazioni precedenti. Le date riconosciute sono numeriche con anno; gli importi richiedono un simbolo o codice di valuta. Un cambiamento delle opzioni o del catalogo cambia la policy conversazionale (revisione 5): lo storico resta consultabile e il messaggio successivo apre una nuova conversazione.

Ogni espressione regolare ha un timeout di due secondi; la pipeline ha un budget complessivo di dieci secondi e rispetta l'annullamento. I controlli di timeout sono cooperativi e possono terminare dopo il controllo successivo della regex in corso. Errori di lettura delle fonti, catalogo non valido, timeout e fallimenti delle strategie impediscono il trasporto: non esiste un invio di ripiego senza sostituzioni.

Il budget di contesto viene ricontrollato anche dopo le sostituzioni. Lo schema delle risposte strutturate conserva esattamente il proprio contratto: se contiene dati personali riconosciuti, la richiesta viene rifiutata invece di modificarne chiavi, tipi o valori enumerati o di inviarli senza sostituzioni.

## Registro locale

`%LOCALAPPDATA%/OnlyWorkspace/privacy-audit` contiene un file cifrato con DPAPI per ogni richiesta: identificativo casuale, data UTC, esito, revisioni e numero di sostituzioni per categoria. Non contiene testi originali, valori riconosciuti, mappa, chiavi, soggetti o indirizzi del servizio. Un mutex fra processi protegge gli aggiornamenti atomici.

I metadati scadono dopo 30 giorni, alla lettura o alla scrittura del registro. Questa scadenza riguarda esclusivamente i file del nuovo registro: non elimina storico, backup, log tecnici o altri file. Un file non valido blocca l'operazione senza azzerare il registro. Un errore di registrazione prima del trasporto impedisce l'invio; se avviene dopo l'avvio del trasporto, non può ritirare dati già ricevuti dal server.

Il PDF esportato da **Privacy e dati** include il registro disponibile. `dispatching` documenta un tentativo, non una conferma di ricezione del server; `completed`, `failed`, `blocked` e `cancelled` ne distinguono l'esito. Anche i tentativi non riusciti possono avere conteggi di sostituzione. La voce dedicata in Privacy e dati misura lo spazio del registro.

## Limiti

È pseudonimizzazione reversibile, non anonimizzazione completa. Un nome sconosciuto scritto da solo, un identificativo fuori dai pattern, una lingua o un formato non coperto possono restare nel testo. Intestazioni e firme conservano la struttura e sostituiscono solo i valori riconosciuti. Le identità dell'archivio sono nomi visualizzati e non distinguono sempre persona e azienda. Le opzioni importi/date possono ridurre la capacità del modello di calcolare o confrontare i valori.

Le prove sintetiche e i test del trasporto sono distinti dalla raccolta reale dei contatti dentro Outlook e dalla qualità delle risposte di un provider. Qualifica e limiti correnti sono in [Verifica](verifica.md).
