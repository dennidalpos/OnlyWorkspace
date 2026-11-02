# Pseudonimizzazione delle richieste IA

## Confine cloud rigoroso

Il cloud riceve soltanto categorie fisse e riferimenti opachi a fatti strutturati risolti sul PC. `CloudFactsRequest` mantiene una copia locale dei valori e della provenienza; i messaggi contengono solo il template `cloud.facts` incorporato nell’assembly (indipendente da eventuali file di prompt modificati), e righe con numero della fonte, categoria e segnaposto. Non includono `Raw`, contesto, oggetto, nomi dei file, StoreId/EntryId, corpi, nomi, recapiti, importi o date originali.

Il contratto è immutabile e non serializzabile come autorizzazione: la prova interna dei messaggi non viene ricostruita da storico o JSON. Dispatcher e singoli client HTTP rifiutano messaggi privi della preparazione, messaggi aggiunti e schemi diversi. Le sonde usano esclusivamente input sintetici fissi. Prima del trasporto il riquadro ricontrolla autorizzazione, chiavi e configurazione.

Le risposte ammesse elencano tutti i riferimenti una sola volta. Riferimenti sconosciuti, duplicati, mancanti o output troncato rifiutano la risposta. Valori, date, importi, citazioni e voci agenda sono ricostruiti sul PC; il modello non calcola sui segnaposto. La classificazione generale, sintesi e riscrittura di testo libero richiedono un profilo locale. Non esiste cambio automatico di provider o invio di ripiego.

Le richieste esplicite «elenca i fatti», «mostra le scadenze» e «elenca gli importi», anche con «delle email» o «di questa email», hanno una rappresentazione predefinita. Le domande sui fatti già risolvibili localmente continuano a non contattare alcun provider. Le query web libere, i domini ricavati dalla posta e le query generate da Ollama non soddisfano il contratto e restano bloccati. Le verifiche sintetiche della capacità web restano disponibili.

## Strategie e audit locali

Il catalogo incorporato è alla revisione 2. La pipeline locale mantiene sostituzioni reversibili, priorità fra pattern, budget di dieci secondi, timeout delle regex e annullamento. Le identità vengono raccolte dagli store autorizzati e cercate con un indice letterale compilato una volta per richiesta, senza una regex per contatto. I valori dei fatti cloud vengono sempre sostituiti integralmente, anche se sconosciuti al dizionario; importi e date non hanno un interruttore di esclusione nel cloud.

Il registro DPAPI conserva identificativo casuale, data, esito, revisioni e conteggi per categoria; non contiene originali o mappa. Il mutex protegge gli aggiornamenti. Scadono dopo trenta giorni solo i metadati di questo registro. Un errore precedente al trasporto impedisce l'invio; un errore successivo non ritira richieste già ricevute. `dispatching` indica un tentativo, non una ricevuta. Il PDF di Privacy e dati include i metadati disponibili.

## Autorizzazione e limiti

L'informativa 5 registra una conferma tecnica con utente Windows, postazione, data e testo accettato, distinta per profilo, destinazione, permessi e credenziali effettive. Non registra nuove dichiarazioni aziendali o contrattuali. I record precedenti restano conservati e richiedono rinnovo. La policy conversazionale 7 separa i nuovi contesti dallo storico precedente.

È pseudonimizzazione reversibile, non anonimizzazione né attestazione di conformità. La copertura dipende dai fatti risolti localmente: contenuti non rappresentabili vengono rifiutati, anziché essere dichiarati privi di dati sensibili. I profili locali elaborano il testo completo sul PC. Prove sintetiche, runtime isolato e Outlook installato restano qualifiche distinte: [Verifica](verifica.md).
