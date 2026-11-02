# Configurazione

## File

| File | Contenuto | Protezione |
| --- | --- | --- |
| `%ProgramData%\OnlyWorkspace\config.json` | Profili IA, limiti, storico, archivio, logging (schema 1) | Scrittura solo amministratori e SYSTEM |
| `%ProgramData%\OnlyWorkspace\secrets.bin` | Chiavi API dei profili | DPAPI LocalMachine |
| `%ProgramData%\OnlyWorkspace\admin-password.json` | Verificatore della password amministrativa facoltativa | Non è nel backup |
| `%APPDATA%\OnlyWorkspace\preferences.json` | Procedura guidata vista, `archiveOff` | Utente |

Archivio, storico, log e copie temporanee sono descritti in [Sicurezza e privacy](sicurezza.md). Il riquadro controlla le modifiche a `config.json` e `secrets.bin` quando riceve un messaggio dalla pagina; l’indicizzatore rilegge le impostazioni periodicamente, normalmente entro 5 secondi.

Riferimento dei campi: [WorkspaceConfig.cs](../../src/OnlyWorkspace.Core/Models/WorkspaceConfig.cs), [LlmProfileConfig.cs](../../src/OnlyWorkspace.Core/Models/LlmProfileConfig.cs); file per il primo import: [config.template.json](../../deployment/config.template.json); validazione: [ConfigurationValidator.cs](../../src/OnlyWorkspace.Core/Validation/ConfigurationValidator.cs) o `ConfigTool validate`.

## Impostazioni e default

| Campo | Default | Note |
| --- | --- | --- |
| `defaultProfile` | `ollama-lan` | Profilo usato da tutti gli utenti nel riquadro; il riquadro non ha una scelta del profilo |
| `attachmentOcr` | `true` | OCR di Windows su PDF scansionati e immagini allegate, nell'archivio e nel riquadro; spento, restano non letti (`ocr-off`) e i testi già riconosciuti restano; dopo l'installazione di una lingua OCR di Windows, al successivo avvio della tray si rileggono gli allegati rimasti «OCR non disponibile»; riacceso, la tray rimette in coda una volta le email rimaste non lette |
| `limits.maxSearchItems` | 50 | Risultati di agenda e contatti |
| `limits.followUpDays` | 5 | Email senza risposta, 1-30 |
| `limits.maxAnalysisEmails` | 300 | Email più recenti analizzate da Cerca e analizza e dalla chat; la ricerca copre tutto l'ambito e il risultato dichiara quante altre ne ha trovate |
| `history` | attivo, 90 giorni, 200 conversazioni | |
| `archive.enabled` | `false` nel codice, `true` nel modello installato | Permesso amministrativo; ogni utente restringe o spegne in Le mie cassette |
| `archive.indexAttachmentText` | `true` | Indice locale del testo degli allegati, per tutti i profili |
| `dataEgress` | nessuna accettazione | Informativa sull'uscita dei dati: `consent` (responsabile, data, ora, utente Windows, PC, versione e hash del testo, server coperti) e `history` di accettazioni e revoche; senza, profili cloud e ricerca web esterna sono bloccati |
| `logging.level` | `Information` | |

L'archivio non ha un tetto di dimensione: la tray avvisa sotto 5 GiB liberi e sospende le acquisizioni sotto 1 GiB.

## Profili IA

`type` è il connettore. Locali: `ollama`, `lmstudio`, `llamacpp`, `vllm`, `localai`, `jan`. Cloud: `openai`, `anthropic`, `gemini`, `azure-openai`, `mistral`, `xai`, `deepseek`, `openrouter`, `qwen`, `groq`, `together`, `fireworks`, `perplexity`, `ollama-cloud`. Ognuno usa una delle quattro famiglie di protocollo (Ollama, OpenAI chat completions, Anthropic Messages, Gemini) con URL e autenticazione proposti dal [catalogo](../../src/OnlyWorkspace.Core/Models/ProviderCatalog.cs).

| Campo | Significato |
| --- | --- |
| `baseUrl`, `authType`, `secretRef`, `model` | Endpoint (HTTPS, HTTP solo su loopback), autenticazione (Azure: header `api-key`), riferimento al vault, modello |
| `external`, `allowAttachmentUpload` | Il flag può rendere più prudente la classificazione: provider cloud ed endpoint non loopback sono sempre esterni, anche con `external: false`. Il testo degli allegati richiede un permesso distinto; i profili esterni non inviano testo degli allegati ai livelli 0–2; Esteso lo permette solo con `allowAttachmentUpload: true` |
| `webSearchEnabled`, `webSearchActions`, `webSearchSecretRef` | Web Search per azione; chiave ollama.com separata per Ollama |
| `modelCapabilities` | Capacità lette dall'endpoint dei modelli (strumenti, ragionamento, Web Search, visione) dove il servizio le pubblica |
| `firstTokenTimeoutSeconds`, `idleTimeoutSeconds` | 120 s fino alla prima risposta, inclusa la lettura di un corpo HTTP d'errore; poi 60 s di inattività del flusso. Interrompi resta attivo anche dopo gli header |
| `useSystemProxy` | Proxy di sistema |
| Solo Ollama: `numCtx`, `contextBudgetTokens`, `maxOutputTokens`, `keepAlive` | Contesto fisso e budget; preset 8K, 16K, 32K, 64K, 128K, 256K (dai 32K 4096 token di risposta), mostrati entro il contesto dichiarato dal modello |
| `disableThinking` (Ollama, Ollama Cloud, Qwen) | Predefinito `true`: invia `think: false` a Ollama e `enable_thinking: false` al connettore Qwen. L'effetto dipende dal modello; il riscontro sul QwenCloud configurato è datato in Verifica e qualifica |

Per i cloud il client non applica budget di contesto o output; Anthropic richiede `max_tokens` e il client lo adatta dopo un rifiuto riconoscibile; un parametro rifiutato con HTTP 400 (per esempio la temperatura) viene ritentato senza. Il profilo attivo non viene mai sostituito in silenzio.

I redirect HTTP sono bloccati, anche verso lo stesso server: impostare direttamente l'URL finale del servizio. Un connettore locale su HTTPS remoto richiede le stesse autorizzazioni di invio degli altri servizi esterni. Nel ConfigTool l'indicatore esterno è obbligatorio per questi endpoint, senza riscrivere il flag esplicito nel JSON.

Per Ollama il budget viene controllato sull'intera richiesta: istruzioni, messaggi e storico scelti, schema JSON, strumenti e riscontri web. La stima resta quella del progetto (3,5 caratteri per token con i margini del profilo), non una tokenizzazione del modello. Un superamento interrompe quella richiesta prima del trasporto; la verifica precede anche la ricerca web e si ripete dopo i riscontri.

Il budget Ollama deve essere positivo e non superare `numCtx − maxOutputTokens − max(512, numCtx / 10)`. Salvataggio, import e caricamento rifiutano valori incoerenti; anche il dispatch diretto li blocca. Il programma non aumenta il contesto né taglia i dati per farli entrare.

## Segreti e password

- Il file sorgente del provisioning può contenere `auth: {"type":"bearer","secret":"…"}`: `ConfigTool import` salva il segreto in `secrets.bin` e lascia in `config.json` solo `authType` e `secretRef`. Il file sorgente va protetto e rimosso a parte.
- Il vault DPAPI LocalMachine non si porta su un altro PC: lì le chiavi si reinseriscono.
- Se `secrets.bin` scompare, il reload revoca le chiavi caricate e i client che le avevano catturate. Un file presente ma illeggibile fa invece fallire il caricamento, conservando la copia in memoria senza autorizzarne il riuso tramite la configurazione fallita; un vault valido ripristinato può essere ricaricato.
- La password amministrativa blocca le modifiche nella GUI e chiede un prompt per `import` e `restore`; un amministratore Windows la reimposta via UAC. Non protegge i file da chi ne controlla le ACL.

## Aggiornamento esposizione e archivio

Sono disponibili i livelli 0–2 e **3 · Esteso**. Il nuovo valore JSON è `"Extended"` (valore enum 4); il numero 3 resta riservato al livello ritirato e non abilita Esteso. Il precedente valore `Full`/3 viene convertito in `Standard`/2 durante il caricamento, senza scritture implicite né modifiche a endpoint, modello o riferimenti alle chiavi. I campi `archive.cardsEnabled`, `cardsProfile`, `allowExternalCards`, `cardsDailyTokens` e `cardsBudgetResetUtc` non sono più impostazioni attive e non vengono riscritti al successivo salvataggio esplicito. L'informativa corrente è la versione 4 e richiede nuova accettazione per gli invii esterni. La policy dello storico resta alla revisione 6 e include livello e permesso allegati: il testo precedente resta consultabile e non viene riutilizzato come contesto di nuove richieste.

La migrazione dell'archivio personale 11→12 crea e verifica una copia cifrata e la chiave DPAPI prima di eliminare i dati IA pregressi. Backup conservati fuori dall'archivio attivo in `%LOCALAPPDATA%\OnlyWorkspace\archive-migration-backups`; configurazione e vault dei provider non vengono riscritti dalla migrazione. Dettagli e recupero: [Motore dell'archivio](../tecnico/archivio-motore.md#formato-conservazione-e-ricostruzione).
