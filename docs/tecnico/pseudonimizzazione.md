# Pseudonimizzazione delle richieste IA

## Confine per livello

Ogni richiesta al modello passa dal dispatcher (`LlmRequestDispatcher`) e da una pseudonimizzazione meccanica prima del trasporto, anche per i profili locali. In locale sono sostituiti solo gli identificativi riconosciuti (nomi, indirizzi, recapiti, IBAN, codici); importi e date solo con le opzioni del profilo. Nel cloud la sostituzione di importi e date riconosciuti è sempre attiva. Le risposte vengono ripristinate sul PC (`Restore`, `RestoreJson`, `StreamRestorer`).

| Livello del profilo cloud | Cosa esce |
| --- | --- |
| 0 · Solo locale | Nulla |
| 1 · Minimo | Solo fatti strutturati: categorie fisse e riferimenti opachi (`CloudFactsRequest`) |
| 2 · Standard | Email e testo libero, già mascherati; ricerca web solo per la verifica di sicurezza |
| 3 · Esteso | Come Standard; con il permesso del profilo anche il testo degli allegati estratto in locale, mascherato |

Nessun file binario, nome di file, StoreId o EntryId esce. Se il masker, il dizionario o il registro di audit non sono disponibili, nulla viene inviato (`DataEgressBlockedException`).

## Prova interna dei messaggi

Il contratto non è serializzabile come autorizzazione: la prova (`CloudRequestProof`) non viene ricostruita da storico o JSON. Il dispatcher accetta testo libero prima della maschera solo a Standard ed Esteso (`CloudRequestGuard.Validate(..., beforeMasking: true)`), lo maschera e lo sigilla (`SealMasked`). Ogni client HTTP rivalida senza quel permesso: testo non mascherato, messaggi aggiunti o schemi diversi sono rifiutati a ogni livello, e una prova di maschera non vale sotto Standard. A Minimo restano valide solo le richieste di fatti, il cui testo la maschera non può modificare. Le sonde usano input sintetici fissi.

## Fatti (Minimo)

`CloudFactsRequest` mantiene una copia locale dei valori e della provenienza; i messaggi contengono solo il template `cloud.facts` incorporato e righe con numero della fonte, categoria e segnaposto. Le risposte elencano ogni riferimento una sola volta: riferimenti sconosciuti, duplicati, mancanti o output troncato rifiutano la risposta. Le richieste «elenca i fatti», «mostra le scadenze» e «elenca gli importi» hanno una rappresentazione predefinita; le domande risolvibili in locale non contattano alcun provider. Sintesi, riscritture e Briefing richiedono Standard o un profilo locale. Non esiste cambio automatico di provider o invio di ripiego.

## Strategie e audit locali

Il catalogo incorporato è alla revisione 4. La pipeline mantiene sostituzioni reversibili, priorità fra pattern, budget di dieci secondi, timeout delle regex e annullamento. Le identità vengono raccolte dagli store autorizzati e cercate con un indice letterale compilato una volta per richiesta. Query web libere, domini ricavati dalla posta e query generate da Ollama restano bloccati.

Il registro DPAPI conserva identificativo casuale, data, esito, revisioni e conteggi per categoria; non contiene originali o mappa. Il mutex protegge gli aggiornamenti. Scadono dopo trenta giorni solo i metadati di questo registro. Un errore precedente al trasporto impedisce l'invio; un errore successivo non ritira richieste già ricevute. `dispatching` indica un tentativo, non una ricevuta. Il PDF di Privacy e dati include i metadati disponibili.

## Autorizzazione e limiti

L'informativa 6 registra una conferma tecnica con utente Windows, postazione, data e testo accettato, distinta per profilo, destinazione, area del servizio, permessi e credenziali effettive. L'area (UE, non UE, non specificata) è dichiarata dall'amministratore per profilo; se non specificata serve una conferma separata. Non registra dichiarazioni aziendali o contrattuali. I record precedenti restano conservati e richiedono rinnovo. La policy conversazionale 8 separa i nuovi contesti dallo storico precedente.

Il catalogo 3 riconosce anche il nome «euro» come parola intera, senza distinzione fra maiuscole e minuscole, prima o dopo un numero separato da spazio orizzontale. Sono coperte forme come `1234,56 euro` ed `EURO -1.234,56`, oltre a EUR, USD, GBP e ai simboli €, $, £. Il nome «euro» richiede cifre, con eventuali separatori interni virgola e punto. Parole come «eurozona» ed «euros», numeri raggruppati con spazi e altri nomi di valuta esulano da questa regola. La revisione 3 richiede di rinnovare le autorizzazioni dei profili interessati e apre un nuovo contesto conversazionale.

Il catalogo 4 maschera gli indirizzi postali italiani (categoria `INDIRIZZO`): via, viale, piazza, piazzale, corso, largo, vicolo, strada, lungotevere, contrada o località seguiti da un nome con iniziale maiuscola e da un numero civico, con CAP, una parola di città e sigla di provincia se presenti. Una parola comune come «via» o «corso» senza nome proprio e civico resta com’è; una città di più parole può restare in parte visibile. Indirizzi esteri e scritti in altro modo, e **le ragioni sociali nel testo libero**, non sono mascherati: escono come scritti, salvo le aziende note agli store autorizzati. La revisione 4 richiede di rinnovare le autorizzazioni dei profili interessati (stessa regola della 3).

## Catture reali (7 novembre 2026, QwenCloud)

Una chat con testo sintetico, inviata con la chiave del profilo `qwencloud` attraverso `LlmClientFactory` e `LlmRequestDispatcher` e registrata da un gestore HTTP, ha dato: a **Minimo** zero richieste HTTP (`DataEgressBlockedException`: il testo libero non ha rappresentazione protetta); a **Standard** ed **Esteso** una richiesta (HTTP 200) con `[PERSONA_1]`, `[EMAIL_1]`, `[TEL_1]`, `[IBAN_1]`, `[CF_1]`, `[IMPORTO_1]`, `[DATA_1]` al posto dei valori e nessuno degli originali nel corpo; la risposta tornava con nome, email e IBAN ripristinati. Nello stesso corpo restavano in chiaro una ragione sociale e un indirizzo postale (via, civico, città): il catalogo 3 non ha un modello per questi (modelli: IBAN, CF, EMAIL, PIVA, TEL, PRATICA, IMPORTO, DATA e le identità degli store), quindi «indirizzi» in questa pagina indica gli indirizzi email. Non coperti da questa prova: le altre azioni (sintesi, bozza, Cerca e analizza, Briefing, controllo di sicurezza), gli allegati, la raccolta COM e i fornitori UE.

Seconda serie, con il catalogo 4 (stesso metodo, testo con indirizzo «via Dante Alighieri 5/B, 20121 Milano (MI)» e «Ditta Verdi Srl»; chat in streaming e risposta JSON strutturata): a **Minimo** zero richieste HTTP in entrambe le forme; a **Standard** ed **Esteso** una richiesta (HTTP 200) per forma, con segnaposto `PERSONA`, `EMAIL`, `TEL`, `IBAN`, `CF`, `IMPORTO`, `DATA` e `INDIRIZZO` e nessun originale nel corpo tranne «Ditta Verdi Srl», che resta in chiaro (limite dichiarato sopra); nome, email e indirizzo tornano ripristinati. Senza concessione valida (copia della configurazione senza autorizzazione) zero richieste HTTP e `DataEgressBlockedException`. Rimangono le altre azioni (che richiedono Outlook), gli allegati e i fornitori UE.

È pseudonimizzazione reversibile, non anonimizzazione né attestazione di conformità. I dati sensibili che la procedura non riconosce, comprese le categorie particolari, restano nel testo e non sono anonimizzati. Prove sintetiche, runtime isolato e Outlook installato restano qualifiche distinte: [Verifica](verifica.md).
