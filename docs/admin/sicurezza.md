# Sicurezza e privacy

## Dati sul PC

L’archivio è uno specchio di Outlook: include testo integrale cifrato, con maggiore occupazione e sensibilità dei dati locali. Posta eliminata conta come presente; cassette chiuse e cache senza prova di assenza vengono conservate. La cancellazione confermata rimuove contenuti e conversazioni coinvolte, con pulizia SQLite/FTS e WAL riprendibile. Il registro DPAPI delle invalidazioni contiene impronte e tempi, non testo. Backup ed esportazioni precedenti richiedono gestione separata. La diagnostica include solo metadati e misure, senza database, chiavi o testi.

| Dato | Percorso | Protezione |
| --- | --- | --- |
| Configurazione | `%ProgramData%\OnlyWorkspace\config.json` | Leggibile dagli utenti, scrivibile da amministratori e SYSTEM |
| Chiavi IA | `%LOCALAPPDATA%\OnlyWorkspace\secrets.bin` di ogni utente | DPAPI CurrentUser, mai scritto con diritti di amministratore; un altro utente standard non lo decifra |
| Password amministrativa | `admin-password.json` nella stessa cartella | Verificatore derivato |
| Preferenze | `%APPDATA%\OnlyWorkspace\preferences.json` | Utente |
| Archivio | `%LOCALAPPDATA%\OnlyWorkspace\archive\` | SQLite cifrato, chiave casuale protetta DPAPI CurrentUser; corpi integrali, testi degli allegati deduplicati, metadati, indici e fatti locali |
| Storico | `%LOCALAPPDATA%\OnlyWorkspace\history\` | DPAPI CurrentUser; 90 giorni o 200 conversazioni; conversazioni coinvolte rimosse quando l’ultima copia dell’email viene eliminata |
| Log e dati del riquadro | `%LOCALAPPDATA%\OnlyWorkspace\logs` e `WebView2` | Utente |
| Configurazione non leggibile | `%LOCALAPPDATA%\OnlyWorkspace\config-non-leggibile` | Copie fatte da **Nuova configurazione** |
| Copie degli allegati | `%TEMP%\OnlyWorkspace\scan` | Eliminate dopo l'uso; i residui oltre un'ora all'avvio di Outlook |
| Esportazioni | CSV in Download, diagnostica sul Desktop, backup nella cartella scelta | Su richiesta |

Il motore rifiuta gli archivi con chiave assente o illeggibile e i formati precedenti al 13, preservando i dati. Le copie di migrazione esistenti sotto `%LOCALAPPDATA%\OnlyWorkspace\archive-migration-backups` includono la chiave DPAPI e restano utilizzabili dallo stesso utente Windows con una build compatibile. [Motore dell'archivio](../tecnico/archivio-motore.md#formato-conservazione-e-ricostruzione)

La rimozione dell’ultima copia verificata elimina anche le conversazioni dipendenti. La cancellazione manuale dell’intero archivio resta distinta dalla riconciliazione delle email e non cancella automaticamente ogni conversazione indipendente. Dettagli: [motore archivio](../tecnico/archivio-motore.md#testi-integrali-e-cancellazione).

## Invio ai modelli

- Gli invii cloud richiedono l’autorizzazione tecnica dell’informativa 7, distinta per profilo/destinazione/area/permessi/chiavi. Nessun campo manuale o nuova dichiarazione aziendale; precedenti record e revoche restano nello storico. Salvataggio e revoca sono effettivi solo dopo persistenza riuscita. Le sonde usano input sintetici fissi. [Privacy e dati](configtool.md#uscita-dei-dati).
- I client esterni ammettono, a Minimo, soltanto fatti strutturati con valori integralmente sostituiti; a Standard ed Esteso anche testo libero già mascherato dalla pseudonimizzazione meccanica (identificativi, importi e date), che i client rifiutano se privo della prova di maschera. Query web non verificabili sono bloccate; i dati sensibili non riconosciuti non sono anonimizzati. Valori e voci agenda sono ripristinati localmente. Errori di preparazione o audit precedenti al trasporto impediscono l’invio. [Contratto e limiti](../tecnico/pseudonimizzazione.md).
- La classificazione usa anche il destinatario: qualsiasi endpoint non loopback è esterno, compresi quelli dei connettori locali. Il flag JSON `external: false` non esenta da informativa o permesso allegati. I redirect sono disabilitati per modelli, Web Search e sonde: [Microsoft documenta](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=netframework-4.8.1) che gli header di autenticazione personalizzati non vengono rimossi automaticamente.
- Le nuove conversazioni registrano una chiave della policy di destinazione e dei permessi, senza valori delle credenziali. Un cambio di policy interrompe le operazioni precedenti; il prossimo messaggio apre una nuova conversazione. Lo storico precedente, compreso quello senza questa metainformazione, resta consultabile ma non viene riutilizzato come contesto del modello o della ricerca web.
- Al modello va testo estratto, mai il file originale. Contenuti di posta, allegati, agenda e contatti sono trattati nel prompt come dati non attendibili.
- Il modello non invia né salva nulla da solo: bozze, categorie e attività richiedono un clic. La Chat cerca nei contatti Outlook senza modificarli.
- Una richiesta già ricevuta dal provider non si può ritirare.

## Trasporto e processi

- Endpoint remoti solo HTTPS; HTTP solo su loopback.
- Nuova posta e modifiche si acquisiscono da Outlook aperto; i testi conservati si consultano anche offline. Nessun connettore Graph, IMAP o OAuth.
- Il motore dell'archivio comunica su named pipe locale limitata al SID dell'utente.
- Gli allegati si leggono in un processo separato con timeout e limite di memoria ([Allegati](../tecnico/allegati.md)); la Verifica sicurezza vi aggiunge una lettura statica di script, pagine e link (nessuna esecuzione, nessun accesso alla rete) e usa Microsoft Defender, i cui risultati restano attribuiti a Defender. Una scansione assente o fallita non è un esito pulito; una copia scomparsa non prova un'infezione.

## Log, diagnostica e firma

[OnlyWorkspaceLog](../../src/OnlyWorkspace.Infrastructure/Logging/OnlyWorkspaceLog.cs) registra eventi operativi senza chiavi. [DiagnosticsExporter](../../src/OnlyWorkspace.Infrastructure/Logging/DiagnosticsExporter.cs) esporta soltanto metadati di sistema e misure di spazio: esclude configurazione, log, testi, database e chiavi, senza aprire il database né inviare lo ZIP. Binari e setup sono firmati con il PFX privato; il setup installa il certificato pubblico in Root e TrustedPublisher ([Installazione](installazione.md)).
