# Sicurezza e privacy

## Dati sul PC

L’archivio è uno specchio di Outlook: include testo integrale cifrato, con maggiore occupazione e sensibilità dei dati locali. Posta eliminata conta come presente; cassette chiuse e cache senza prova di assenza vengono conservate. La cancellazione confermata rimuove contenuti e conversazioni coinvolte, con pulizia SQLite/FTS e WAL riprendibile. Il registro DPAPI delle invalidazioni contiene impronte e tempi, non testo. Backup ed esportazioni precedenti richiedono gestione separata. La diagnostica include solo metadati e misure, senza database, chiavi o testi.

| Dato | Percorso | Protezione |
| --- | --- | --- |
| Configurazione | `%ProgramData%\OnlyWorkspace\config.json` | Leggibile dagli utenti, scrivibile da amministratori e SYSTEM |
| Chiavi IA | `secrets.bin` nella stessa cartella | DPAPI LocalMachine; proteggerlo anche con ACL |
| Password amministrativa | `admin-password.json` nella stessa cartella | Verificatore derivato |
| Preferenze | `%APPDATA%\OnlyWorkspace\preferences.json` | Utente |
| Archivio | `%LOCALAPPDATA%\OnlyWorkspace\archive\` | SQLite cifrato, chiave casuale protetta DPAPI CurrentUser; corpi integrali, testi degli allegati deduplicati, metadati, indici e fatti locali |
| Storico | `%LOCALAPPDATA%\OnlyWorkspace\history\` | DPAPI CurrentUser; 90 giorni o 200 conversazioni; conversazioni coinvolte rimosse quando l’ultima copia dell’email viene eliminata |
| Log e dati del riquadro | `%LOCALAPPDATA%\OnlyWorkspace\logs` e `WebView2` | Utente |
| Configurazione non leggibile | `%LOCALAPPDATA%\OnlyWorkspace\config-non-leggibile` | Copie fatte da **Nuova configurazione** |
| Copie degli allegati | `%TEMP%\OnlyWorkspace\scan` | Eliminate dopo l'uso; i residui oltre un'ora all'avvio di Outlook |
| Esportazioni | CSV in Download, diagnostica sul Desktop, backup nella cartella scelta | Su richiesta |

Un archivio con chiave assente o illeggibile non viene sovrascritto. La migrazione 11→12 crea prima un backup cifrato verificato con chiave DPAPI e manifest SHA-256, poi rimuove i dati IA pregressi in transazione preservando originali e scelte. Le copie rimangono in `%LOCALAPPDATA%\OnlyWorkspace\archive-migration-backups`; sono utilizzabili dallo stesso utente Windows. Il ripristino di una build precedente richiede la copia verificata, separata dai dati correnti. Dettagli e procedura: [Motore dell'archivio](../tecnico/archivio-motore.md#formato-conservazione-e-ricostruzione).

La rimozione dell’ultima copia verificata elimina anche le conversazioni dipendenti. La cancellazione manuale dell’intero archivio resta distinta dalla riconciliazione delle email e non cancella automaticamente ogni conversazione indipendente. Dettagli: [motore archivio](../tecnico/archivio-motore.md#testi-integrali-e-cancellazione).

## Invio ai modelli

- Gli invii di dati Outlook verso profili esterni (anche server LAN) e la ricerca web esterna richiedono l’autorizzazione esplicita in **Privacy e dati**. Il consenso v4 conserva responsabile e dichiarazioni dell’azienda/fornitore e registra separatamente ogni profilo, legandolo a endpoint, livello, permessi allegati/web, impostazioni di pseudonimizzazione e chiavi effettive. Le impronte delle chiavi usano SHA-256 con salt casuale per autorizzazione; le chiavi restano nel vault DPAPI e le impronte non compaiono nei report. I consensi precedenti per host richiedono rinnovo; originali, accettazioni e revoche restano in `config.json` e nei backup. ConfigTool permette di salvare un predefinito cloud senza consenso o di revocarlo: gli invii rimangono bloccati. Il riquadro ricontrolla autorizzazioni, credenziali e modifiche pendenti della configurazione prima del trasporto. Letture locali, risposte senza trasporto e IA su loopback non richiedono questo passaggio; la ricerca esterna di un profilo locale lo richiede. Le sonde del ConfigTool usano richieste sintetiche senza dati Outlook. L’informativa v4 e le dichiarazioni obbligatorie restano invariate; [Conformità UE](conformita-ue.md).
- Il profilo attivo decide il destinatario, sempre indicato nel riquadro. Le autorizzazioni (cloud, livello di esposizione, testo degli allegati, Web Search) si decidono nel ConfigTool; il riquadro non chiede conferme. Ai livelli 1 e 2 le strategie locali sostituiscono i valori riconosciuti con segnaposto: nomi accanto agli indirizzi e identità raccolte da contatti/archivio, email, telefoni, IBAN, partite IVA, codici fiscali e riferimenti etichettati. Importi e date hanno opzioni separate, inizialmente disattivate. La risposta ripristina i valori sul PC; errori di preparazione o di registrazione prima del trasporto bloccano l'invio. È pseudonimizzazione reversibile con copertura limitata: [strategie e registro](../tecnico/pseudonimizzazione.md), [Modelli IA e Web Search](../utente/modelli-web.md).
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
