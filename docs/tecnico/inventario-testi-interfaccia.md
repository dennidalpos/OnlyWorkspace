# Inventario dei testi dell'interfaccia

Esito per schermata del passaggio di riduzione del 4 novembre 2026 (vedi [vocabolario](interfaccia-vocabolario.md) per termini e stati). Misura: stringhe di codice sopra 140 caratteri nelle schermate del ConfigTool (57 prima, 55 dopo, perché molte erano pezzi di testi su più righe), voci della Guida del riquadro sopra 165 caratteri (oltre una decina prima, 0 dopo, controllo in `tests/scripts/UiVocabulary.Tests.cjs`) e tooltip oltre 130 caratteri (3 prima, 0 dopo).

## Riquadro

| Area | Esito |
|---|---|
| Guida: primi passi, Posta, Scrittura, Chat, Cerca, Storico | Accorciata a una riga per voce; rimosso il riferimento alla rubrica (nessuna scrittura contatti dopo A207/A209) |
| Guida: dati inviati al profilo IA, Cloud/PC/globo | Mantenuta e asciugata: sono avvisi su uscita dei dati |
| Guida: legenda del pallino di stato | Accorciata; i glifi vengono dal CSS (`--glyph`); un solo pallino per Archivio e IA, senza marchio nella barra |
| Tooltip Follow-up, Scrivi, filtro persona | Accorciati; «Elimina archivio» mantenuto (dice che le email non vengono toccate) |
| Tooltip «Invio del testo degli allegati» | Invariato; i messaggi di rifiuto per livello (`TaskPaneCoordinator`) citavano «2 · Standard» e «3 · Esteso» allo stesso modo (al 12/11 resta solo il rifiuto a Minimo, che cita «2 · Standard») |

## ConfigTool

| Schermata | Esito |
|---|---|
| Procedura guidata (passi 1-5) | Accorciati introduzione, archivio, avvio con Windows, allegati; mantenuti i testi sui profili cloud e sul loopback (righe di uscita dati) |
| Configurazione: Archivio, Allegati, OCR, Log, Tray, Salva, Esporta informativa | Accorciati i tooltip; invariati stato dell'informativa, revoca e «Accetta e salva» |
| Profilo IA | Accorciati Servizio IA e Budget token; livelli di esposizione, ricerca web e servizio esterno invariati; gli altri tooltip di campo restano come aiuto contestuale |
| Capacità e verifiche | Accorciati introduzione e legenda; invariata l'avvertenza su dati inventati, token a pagamento e ricerca web |
| Ricerca archivio, Cassette | Accorciati compattazione, Message-ID e scelta delle cassette |
| `DataEgressNotice` | Versione 6 al 4/11 (oggi 8, inventario storico): testo, titolo e hash fanno parte delle autorizzazioni salvate; ogni modifica del testo richiede una nuova versione |

## Non fatto

- Tray: nessun testo lungo.
- Prova dal vivo a 100% e 200%: #012 è chiuso (7/11) e la prova a schermo è passata a #021, ancora aperto. Inventario dei campi completato nel passaggio seguente.


## Campi del profilo IA — 5 novembre 2026

Inventario per campo del lotto #087/A202. «Accorciato» riguarda il tooltip, non le etichette o i comportamenti. Le descrizioni lunghe su uscita dati e costi restano nell’aiuto esistente; l’informativa versione 6 era invariata in quel lotto (oggi 8). La scheda dei dati si chiama «Autorizzazioni». Nessuna nuova prova a schermo sulla build installata.

| Scheda | Campo o comando | Esito e contenuto conservato |
|---|---|---|
| Connessione | ID profilo | Accorciato: unicità, Invio/uscita dal campo e Salva |
| Connessione | Nome visualizzato | Accorciato: Stato e tooltip del pallino; rimosso il nome della barra ritirata; Salva |
| Connessione | Servizio IA | Accorciato l’aiuto statico: sei preset e limiti del compatibile; tooltip dinamico mantenuto con documentazione e indicazione Salva |
| Connessione | Indirizzo del servizio | Accorciato: HTTP solo loopback, HTTPS esterno, Salva |
| Connessione | Modello | Accorciato: nome esatto, chiave prima della scelta cloud, Salva |
| Connessione | Scegli modello | Accorciato: chiave, nessun dato Outlook, capacità in Avanzate, Salva per la scelta |
| Connessione | Limiti del modello | Mantenuto: dichiarazioni del servizio, non sempre disponibili |
| Connessione | Autenticazione | Accorciato: nessuna/Bearer/API key, valore proposto e Salva |
| Connessione | Chiave di accesso | Accorciato: cifratura sul PC e rimozione con campo vuoto e Salva |
| Connessione | Mostra chiave | Mantenuto: mostra in chiaro senza salvare |
| Connessione | Timeout primo token | Accorciato: caricamento incluso, secondi, Salva |
| Connessione | Timeout inattività | Accorciato: silenzio dopo l’avvio, 0 senza limite, Salva |
| Connessione | Proxy | Accorciato: proxy Windows, spento sul PC locale, Salva |
| Contesto | Preset | Accorciato: solo locale, memoria, cloud gestito dal servizio, Salva |
| Contesto | NumCtx | Accorciato: domanda e risposta, possibile ricaricamento, Salva |
| Contesto | Max output token | Accorciato: limite della risposta locale, Salva |
| Contesto | Budget token | Accorciato: testi interi, arresto se non entrano, Salva; tooltip dinamico conserva il valore consigliato |
| Contesto | Avviso budget | Mantenuto: consiglio e superamento di NumCtx |
| Contesto | Temperatura | Accorciato: variazione delle risposte, Salva; rimossa la promessa di risposte sempre uguali a zero |
| Contesto | Mantieni in memoria | Accorciato: durata, esempi e 0 per liberare subito, Salva |
| Contesto | Disattiva ragionamento | Accorciato: modelli supportati, tempi/token, ragionamento non mostrato, Salva |
| Autorizzazioni | Servizio esterno | Mantenuto: mascheramento, consenso e classificazione obbligatoria per destinazioni esterne |
| Autorizzazioni | Esposizione dati | Mantenuto: blocco, Minimo, Standard, Esteso e permesso allegati distinto, Salva |
| Autorizzazioni | Descrizione del livello | Mantenuta: dettaglio dei dati in uscita nell’aiuto esistente |
| Autorizzazioni | Area del servizio | Mantenuto: dichiarazione contrattuale, conferma se non specificata, Salva |
| Autorizzazioni | Includi allegati verso il cloud (già «Testo degli allegati») | Rinominato; invariato: Esteso, permesso, testo estratto mascherato; nomi e file locali, Salva |
| Autorizzazioni | Sostituzione importi/date | Mantenuti e nascosti come prima; da #084 «Sostituisci gli importi/le date con segnaposto» compaiono solo per i profili locali, nel cloud restano nascoste |
| Autorizzazioni | Supporto Ricerca web | Mantenuto: supporto dichiarato o provato |
| Autorizzazioni | Ricerca web attiva | Mantenuto: ricerche delle azioni bloccate, nessun testo/query inviato, informativa necessaria |
| Autorizzazioni | Azioni con ricerca | Mantenuti aiuti delle singole azioni e riepilogo |
| Autorizzazioni | Chiave Ollama Web | Accorciato: distinta dalla chiave modello, cifratura locale con Salva |
| Avanzate | Introduzione e tabella capacità | Mantenute: differenza tra comandi, esito/provenienza e limiti delle funzioni non usate |
| Profilo | Diagnosi | Unico pulsante di prova: connessione, capacità dichiarate, richieste sintetiche, durata, nessun dato Outlook, token e possibile costo web; annulla con lo stesso pulsante |
| Avanzate | Annulla | Superato: durante la prova il pulsante Diagnosi diventa «Annulla diagnosi»; gli esiti già ottenuti restano |
| Avanzate | Prova anche Ricerca web | Superato: la casella non esiste più; Diagnosi prova la ricerca web con una richiesta sintetica quando il servizio la offre, con l'avviso sui possibili addebiti |
| Avanzate | Esito delle prove | Mantenuto: avanzamento, esito e Salva |
| Procedura guidata | Verifica connessione | Accorciato: servizio/chiave/modello, richiesta sintetica senza dati Outlook, nessun salvataggio (le prove di capacità sono solo nel profilo) |

`ConfigToolTooltipTests` controlla presenza degli aiuti e significati essenziali (Salva, rimozione della chiave, HTTPS, arresto per budget, dati Outlook, costi e blocchi), senza fissare intere frasi. Il passaggio non qualifica layout, zoom o lettore schermo.
