# Archivio

L'archivio è uno specchio personale e cifrato della posta acquisita da Outlook classico. Conserva corpi integrali e testi degli allegati, condivisi quando il file è identico, e serve ricerca offline, Controparti, Follow-up e Ricostruisci la pratica. I testi non ancora acquisiti richiedono Outlook. Dettagli tecnici: [Motore dell'archivio](../tecnico/archivio-motore.md).

I documenti leggibili sono indicizzati per intero entro i limiti di lettura. Compressi, eseguibili e formati senza lettore restano come riferimenti all'email, con nome, tipo e dimensione: il loro contenuto non viene estratto né indicato come letto. MSG, EML e P7M restano leggibili; gli eventuali compressi al loro interno sono soltanto nominati. Dopo l'aggiornamento i vecchi testi allegati richiedono una rilettura dalle cassette aperte in Outlook; il corpo conservato resta disponibile.

In **Stato**, la riga **Cartelle** separa completate, residue e non leggibili: **Completati**, **Restano**, **Non leggibili** nel riquadro; **Fatti**, **Restano**, **Non letti** nella finestra della tray. La loro somma dà il totale. La percentuale considera solo le cartelle leggibili: 27 completate e una non leggibile su 28 danno 100%, zero residue e una non leggibile; l'indicatore rimane in errore. Se tutte sono non leggibili, la percentuale non è disponibile. Una cartella già completata che fallisce al controllo successivo passa tra le non leggibili, conservando il punto di ripresa e i dati acquisiti.

Per una cartella non disponibile controlla in Outlook se esiste ancora, anche nel Cestino; ripristinala solo se necessario. Una cartella eliminata non va ricreata per far scomparire l'avviso. Con la cassetta aperta il controllo completo può aggiornare l'elenco; un errore di accesso non autorizza a dedurre l'assenza delle email né a eliminare gli originali acquisiti.

## Attivare e scegliere le cassette

- L'amministratore consente l'archivio (attivo nel modello di configurazione installato); allora si crea da sé per ogni utente su **tutte le cassette, anche future**.
- Ogni utente lo restringe o lo spegne in **Configurazione → Archivio email → Le mie cassette** (il pulsante **Cassette** di Stato la apre direttamente): la scelta vale subito, senza Salva né UAC. Togliere una cassetta ne cancella email e dati derivati dall'archivio.
- Una cassetta chiusa in Outlook (per esempio un PST tolto dal profilo) resta cercabile e compare in Le mie cassette come «(chiusa)». **Togli dall'archivio**, accanto al nome, ne cancella email, dati derivati e scelte dopo una conferma, anche con «Tutte le cassette, anche future» attivo; se la riapri in Outlook viene archiviata di nuovo.
- **Indirizzi miei** (PST senza account): indirizzi dell'utente ricavati dalla Posta inviata del PST, correggibili; non diventano mai controparti.

La raccolta parte 60 secondi dopo l'avvio di Outlook e lavora dopo 10 secondi senza input, cedendo alle operazioni interattive. Le variazioni (email nuove, spostate, eliminate) arrivano di norma entro 5 minuti, un obiettivo e non un tempo massimo garantito: inattività insufficiente e controlli lunghi possono ritardare l'aggiornamento. Nella prova su WS-DP01 con circa 1.400 email: email nuova in 80 s, spostamento in 261 s.

## Stato

**Archivio** nella barra del riquadro (dettagli in Stato) e la finestra archivio della tray mostrano le stesse tabelle: metriche (testi, allegati, cartelle, controparti, database, disco) e, per cassetta, stato, email, testi %, cartelle.

| Stato cassetta | Significato |
| --- | --- |
| verificata | PST: l'inventario prova anche le assenze |
| cache verificata / da controllare | Gmail IMAP, OST: l'ultimo controllo completo ha chiesto (o non ancora) a Outlook le email non viste |
| inventario, in attesa | Lettura in corso o non ancora iniziata |
| errore | Il motivo è sotto il nome della cassetta |
| chiusa, esclusa | Non aperta in Outlook, oppure tolta in Le mie cassette |

Sotto 5 GiB liberi sul disco la tray avvisa; sotto 1 GiB le nuove acquisizioni si fermano e riprendono da sole.

## Ricerca offline

Clic sull'icona della tray → **Ricerca e stato archivio**: ricerca paginata (50 risultati), anteprima con oggetto, mittente, allegati e primi 255 caratteri. **Recupera testo originale** legge dall’archivio cifrato anche a Outlook chiuso, se il testo è già stato acquisito; altrimenti richiede Outlook. Include gli allegati letti per intero e indica quelli non leggibili con il motivo. La ricerca usa tutto il testo acquisito. `fattura` e `fatture` trovano le stesse varianti; frammenti come `2026/118` trovano `ORD-2026/118`. Nel campo di ricerca puoi anche scrivere `ordine ORD-2026/118`, `importo >=100 EUR` o `scadenza entro 2026-11-30`. Per un'analisi IA usare [Cerca e analizza → Tutto l'archivio](cerca-analizza.md).

## Fatti e filtri locali

La finestra della tray cerca anche **importi** (minimo, massimo e valuta facoltativa) e **scadenze** (dal/al, estremi inclusi). Attiva le caselle delle date per applicarle: sono scadenze scritte nei documenti, non date di ricezione. Per cercare esattamente 1.000 euro imposta entrambi gli importi a `1000,00` e la valuta a `EUR`. Non vengono convertite valute.

L'anteprima mostra i fatti riconosciuti, il corpo o l'allegato di origine e la frase del testo acquisito: date, riferimenti di ordine/fattura/protocollo, importi, IBAN, P.IVA/codice fiscale, parti dichiarate, dati PEC e inviti. I valori ambigui restano «non risolti» e non soddisfano i filtri numerici o sulle scadenze: per esempio `03/04/2026` e `1.000` senza altri separatori. La lettura non attesta autenticità, titolarità o validità fiscale.

Il recupero dai testi già acquisiti parte automaticamente nella tray, senza IA, e riprende dopo una chiusura. Rispetta **Pausa** e manutenzione; non richiede Outlook per i testi già conservati. Le email ancora in attesa o non elaborate sono contate: ripeti la ricerca dopo il recupero. I testi parziali degli allegati non producono fatti. Gli inviti `.ics` sono leggibili anche negli allegati annidati; gli elementi calendario Outlook non sono raccolti. `daticert.xml` serve soltanto ai fatti locali, senza entrare nei prompt o nella ricerca per parole.

Alla dimensione minima la pagina scorre verticalmente per mantenere raggiungibili filtri, risultati e anteprima.

## Controparti

Raggruppamento locale delle persone: per dominio aziendale, per singolo indirizzo sui provider di posta noti. Si possono unire e separare (**Ripristina…**) senza toccare Outlook e senza IA. Da **Persone → Controparti**, la scheda offre email, documenti e **Cerca e analizza la corrispondenza** sugli indirizzi del gruppo. Oggetti completi a capo e azioni **Apri originale** e **Ricostruisci la pratica** su una riga dedicata.

## Ricostruisci la pratica e sintesi

**Ricostruisci la pratica** è un preset di **Posta → Cerca e analizza**: mostra l’email di partenza e ricostruisce la catena sul PC, anche senza un profilo IA. La finestra archivio della tray offre la stessa ricostruzione locale anche a Outlook chiuso. La ricerca collega transitivamente Message-ID, In-Reply-To, References e conversazione Outlook. Collega anche thread con lo stesso riferimento di ordine, fattura o protocollo: mostra il motivo e la frase di origine, distinguendoli da una risposta. Importi e date uguali non bastano; oggetto o mittente simili entro 30 giorni restano possibili collegamenti (massimo 20).

La cronologia usa l'ora d'invio, se disponibile, altrimenti la ricezione; ignora le vecchie date citate negli inoltri. Mostra una voce per Message-ID e conserva le posizioni delle copie: **Copie → Apri copia**, con cassetta e cartella nel riquadro; tasto destro nell'anteprima della finestra archivio per leggerne il testo acquisito. Senza Message-ID non unisce email distinte per somiglianza. Pagine da 50, fino a 20.000 messaggi nella vista; il riquadro aggiunge i messaggi non ancora indicizzati tra i 20 più recenti della conversazione Outlook. Intestazioni o fatti incompleti delle email del flusso sono dichiarati. Le schede email distinguono data, mittente e acquisizione; **Dettagli del messaggio** raccoglie cassetta, collegamenti e risposte. La copertura incompleta e gli errori restano visibili; inventario e diagnostica completa sono in **Dettagli dell’archivio**, per cassetta. Espansioni e focus si conservano negli aggiornamenti della stessa vista. La ricostruzione non usa IA. Da un'email di una cassetta esclusa, o con l'archivio spento, il risultato svuota la catena precedente e ne indica il motivo.

**Genera sintesi** usa i testi integrali acquisiti, leggendo da Outlook quelli ancora mancanti, in una sola richiesta e restituisce tappe, decisioni, punti aperti e riferimenti [Mn] apribili. Allegati facoltativi; non usa Ricerca web. Gli originali mancanti sono contati.

## Analisi e aggiornamento

Le analisi usano originali acquisiti o fatti locali secondo il profilo. Al primo avvio sul formato precedente, il motore crea e verifica un backup cifrato prima di rimuovere i dati IA pregressi e le chiusure delle pratiche ritirate. Email e testi restano nell'archivio. Le copie sono conservate in `%LOCALAPPDATA%\OnlyWorkspace\archive-migration-backups`, con un manifest SHA-256 e la chiave protetta per lo stesso utente Windows. Se il backup o la migrazione falliscono, lo Stato mostra l'errore e i dati precedenti restano disponibili per il recupero. La configurazione e il vault dei provider restano separati.

## Manutenzione ed eliminazione

L’archivio segue Outlook: Posta eliminata conta come presenza. Quando scompare l’ultima copia del Message-ID vengono rimossi anche testi, fatti, allegati non più condivisi e intere conversazioni che avevano usato quell’email. Le cassette chiuse restano finché le togli. Errori o controlli incompleti conservano i dati; «Pulizie in attesa» indica una bonifica da completare. I backup e le esportazioni già creati restano separati.

Nella finestra archivio: **Pausa/Riprendi**, **Aggiorna da Outlook** (chiede una nuova passata), **Ottimizza indice**, **Compatta database** (serve spazio libero pari a circa due volte il database più 1 GiB). **Elimina archivio** (schermata Stato del riquadro, conferma in due clic) cancella indice e testi dell'utente; non tocca Outlook, lo storico o i backup operativi esterni. Le copie automatiche di rollback sono state rimosse.

Un archivio non leggibile (altro utente Windows o chiave DPAPI mancante) non viene sovrascritto. I formati sconosciuti sono rifiutati; la migrazione dai formati 11 e 12 avviene soltanto dopo la verifica del backup. Gli indici mancanti sono ricostruiti dai testi conservati.

Testi integrali, cancellazione sull’ultima copia, fatti e correlazioni locali, pratiche e OCR restano supportati. Le qualifiche correnti e i limiti sono in [Verifica e qualifica](../tecnico/verifica.md); il banco attivo misura i fatti locali.
