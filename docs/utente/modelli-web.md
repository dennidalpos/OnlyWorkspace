# Modelli IA e Ricerca web

Se una domanda sulla posta chiede esplicitamente anche il web, per esempio «cerca online», la risposta non termina con i soli fatti locali: prosegue nel percorso del modello e della ricerca web, secondo le autorizzazioni del profilo. Senza questa richiesta esplicita, una domanda risolvibile dai fatti locali continua a ricevere una risposta locale.

## Chi riceve i dati

Il riquadro usa il profilo predefinito scelto dall'amministratore nel ConfigTool. Il suggerimento del pallino di stato nella barra mostra profilo, modello, server, destinazione (cloud o PC) e area del servizio, e il globo indica le azioni con Ricerca web. Un profilo cloud usa le autorizzazioni già configurate per le azioni avviate: il riquadro non chiede conferme. Prima serve che un responsabile accetti l'informativa sull'uscita dei dati nel ConfigTool (**Privacy e dati**): finché manca, o se il profilo non è autorizzato con destinazione, permessi e chiavi correnti, il riquadro mostra «Profilo cloud bloccato» e non invia nulla; la ricerca web verso quei server viene saltata con una nota. Un cambio di destinazione, livello, permessi o chiavi richiede un rinnovo esplicito, anche se la chiave mantiene lo stesso nome nel vault. I precedenti consensi per solo server vanno rinnovati con la nuova informativa 8. Si può salvare un profilo cloud predefinito senza consenso: gli invii rimangono bloccati. Nessuna informativa per IA su loopback o risultati locali senza invio; la ricerca web esterna richiede invece autorizzazione. Un errore di Ollama non passa mai da solo a un profilo cloud.

Con Minimo il cloud riceve soltanto fatti strutturati protetti; sintesi, riscritture e Briefing richiedono Standard o un profilo locale. Con Standard escono anche email e testo libero, già mascherati (nomi, indirizzi, recapiti, IBAN, codici, importi e date sostituiti da segnaposto, ripristinati sul PC); Esteso, con il permesso distinto, aggiunge il testo degli allegati. Nessun file binario o nome di file viene inviato. I dati sensibili che la maschera non riconosce non sono anonimizzati. Le domande locali su importi e scadenze continuano a rispondere senza modello quando i fatti sono disponibili. Nessun cambio automatico di provider.

```mermaid
flowchart TD
    A(["Email delle cassette aperte in<br/>Outlook"]) --> B{"Cosa succede?"}
    B -->|"Archivio attivo"| C["PC · intestazioni e testo<br/>disponibile"]
    C --> D[("DB cifrato · metadati, anteprima,<br/>indice di ricerca")]
    B -->|"Azione IA avviata"| E["Outlook · dati della funzione"]
    E --> F["Autorizzazioni del profilo"]
    F --> G["IA · locale o cloud"]
    G --> H(["Risultato; storico se attivo"])
    D --> L(["Ricerca e fatti locali senza IA"])
```

| Impostazione del profilo (ConfigTool → Profili IA → Autorizzazioni) | Effetto |
| --- | --- |
| Destinazione cloud (servizio o indirizzo esterno) | Invii soggetti al livello di esposizione e all’informativa accettata |
| Allegati | Testo estratto con profili locali; nel cloud, mascherato, con Esteso, permesso distinto e autorizzazione |
| Ricerca web | Le query con contenuti non verificabili sono bloccate; restano le sonde sintetiche del ConfigTool |

Archiviare una cassetta non autorizza invii IA.

Conta anche l'indirizzo del servizio: un connettore locale configurato su un server fuori dal PC viene trattato come esterno. Se il server richiede un reindirizzamento, l'azione si ferma: l'amministratore deve configurare l'indirizzo finale.

Se cambi destinazione o permessi dei dati, il prossimo messaggio apre automaticamente una nuova chat. Puoi ancora leggere le conversazioni precedenti nello storico; un avviso spiega perché i loro contenuti non saranno riutilizzati. Vale anche per lo storico creato prima di questa protezione.

## Contesto

- **Ollama**: contesto fisso per profilo. La verifica include istruzioni, schema, storico e riscontri web, oltre a email e documenti interi. Una richiesta che supera il budget si ferma senza tagli o cambio automatico del modello; nelle analisi su più email, le esclusioni previste sono dichiarate.
- **Cloud**: il client non impone budget di contesto o di output; un rifiuto del provider viene mostrato.

## Ricerca web

L'amministratore la abilita per profilo e sceglie le azioni: Chat, Sintesi, Risposte, Follow-up, Verifica sicurezza, Attività e scadenze, Estrai dati, Briefing, Analisi della posta (predefinite: Chat, Follow-up, Verifica sicurezza, Briefing).

Le ricerche web con dati non verificabili sono bloccate, inclusi domini ricavati dalla posta e query generate da Ollama. Le verifiche di capacità del ConfigTool usano solo input sintetici fissi; la presenza di una capacità non autorizza l’invio di contenuti.

- **Cosa esce oggi**: a Minimo e Standard nessuna ricerca web durante le azioni invia dati (la Verifica sicurezza a Standard non cerca sul web, solo sui dati Outlook); a Esteso, con un provider che cerca da sé, la ricerca invia l'intera richiesta (anche il testo di email e allegati che contiene, ultimi 6000 caratteri) già pseudonimizzata. Con Ollama le query generate dal modello restano bloccate e non parte nulla verso ollama.com (serve una chiave dedicata solo per la verifica). Il ConfigTool verifica la capacità con testo sintetico fisso. Il suggerimento «Ricerca web» del ConfigTool lo ripete.
- **Verifica sicurezza** e **Appuntamenti e scadenze**: nessuna ricerca per soli domini; a Esteso cercano come le altre azioni, con il testo pseudonimizzato.
- Se il modello giudica la ricerca inutile, l'azione usa i soli dati Outlook.
- Se la ricerca fallisce o scade (almeno 180 s), l'azione prosegue: la risposta dichiara all'inizio che mancano i riscontri web; Verifica sicurezza e Appuntamenti e scadenze mostrano «Ricerca web non riuscita» con il motivo.
- Sotto la risposta e nello storico compaiono solo le fonti HTTPS citate nel testo, con identificatori [Wn] coerenti. Nelle risposte sulle email ogni punto cita le email [Mn] o il web [Wn]: i valori di un punto citato dal web non vengono cercati nelle email. Una citazione non prova da sola la pertinenza: verificare il collegamento tra fonte e affermazione.
- L'input web resta limitato a 6.000 caratteri (query Ollama: 240), distinto dal testo integrale dato al modello dell'azione: oltre il limite la ricerca usa solo la parte finale della richiesta e sotto la risposta compare l'avviso «La ricerca web ha usato solo gli ultimi 6.000 caratteri…». Ampliare il limite richiederebbe una nuova informativa e una nuova accettazione.
- In **Chat** la ricerca parte solo per le domande generali o quando la domanda chiede il web («sul web», «online», «in rete», «notizie»); le domande su posta, agenda e contatti rispondono dai dati Outlook.
- La raccolta dell’archivio e la ricostruzione locale del flusso non usano Ricerca web. La ricerca può essere addebitata dal provider.

### QwenCloud

Prova reale dell'11/11/2026 sull'endpoint del profilo, `https://token-plan.maas.qwencloudapi.com/compatible-mode/v1`. La documentazione del provider è stata usata solo come riferimento: le pagine non concordano sui modelli supportati.

- **Endpoint**: `POST {BaseUrl}/responses` (Responses API compatibile OpenAI). `GET {BaseUrl}/models` elenca i modelli della chiave.
- **Richiesta inviata da OnlyWorkspace**: `model`, `input`, `tools: [{"type":"web_search"}]`, `tool_choice: {"type":"web_search"}` (strumento forzato, come nella verifica del ConfigTool), `store: false`, senza `max_tool_calls` e `include`. Il provider la accetta, e con un modello che cerca risponde con elementi `web_search_call` (stato `completed`, `action.query`, `action.sources[].url`) seguiti dal `message` finale. Gli URL sono in `action.sources` anche senza `include`. `annotations` era vuoto in tutte le prove: le fonti citate compaiono nel testo.
- **Parametri facoltativi**: `tool_choice` accetta `"auto"` (valore predefinito restituito), `"required"` e `{"type":"web_search"}`. Con `qwen3.7-plus` la ricerca parte solo con `"required"` o con l'oggetto. `search_context_size` è accettato, ma il suo effetto non è stato misurato. `max_tool_calls: 1` è accettato, ma con `qwen3.8-flash` non è partita nessuna ricerca: non va usato.
- **Modelli provati** (testo, stessa domanda sintetica):

| Modello | Ricerca senza `tool_choice` | Con `tool_choice` forzato (richiesta dell'app) |
|---|---|---|
| qwen3.8-flash, qwen3.8-max, qwen3.7-max, qwen4-max-preview, glm-5.3, glm-5.2, deepseek-v4-pro, deepseek-v4.1-flash, auto | sì | sì (provato su qwen3.8-flash) |
| qwen3.7-plus | no | sì |
| qwen3.6-flash | no | no: risponde senza cercare e può indicare una fonte non consultata |

L'elenco dipende dalla chiave e dal piano. Per il profilo configurato vale la verifica del ConfigTool: con un modello come qwen3.6-flash risulta non conclusa e dice che il modello non cerca nemmeno con lo strumento forzato, quindi conviene sceglierne un altro. Senza `web_search_call` l'app considera la ricerca non eseguita: l'azione usa i soli dati Outlook, anche se il testo del modello nomina una fonte, e sotto la risposta compare la nota «Il modello non ha eseguito la ricerca web: la risposta non contiene fonti web.» (anche con Anthropic, che non può forzare lo strumento).

Abilitazione e verifica: [ConfigTool](../admin/configtool.md#profili-ia-e-ricerca-web). Codice: [WebSearchService](../../src/OnlyWorkspace.Infrastructure/Llm/WebSearchService.cs), [WebSearchAction](../../src/OnlyWorkspace.Core/Models/WebSearchAction.cs).
