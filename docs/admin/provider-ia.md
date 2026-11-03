# Provider IA: cosa acquistare

Riferimenti principali ricontrollati il **21 ottobre 2026**; tariffe, crediti e condizioni dipendono dal contratto effettivo. OnlyWorkspace usa **API key** inserite nel ConfigTool. La posta viene letta da Outlook: non servono piani IA né client OAuth per raccoglierla. Ollama locale richiede un PC adeguato, senza un servizio IA cloud obbligatorio.

## Provider principali

| Provider | Da attivare | Distinzione da verificare | Riferimenti |
| --- | --- | --- | --- |
| QwenCloud | Workspace **Pay-As-You-Go**, relativo endpoint e API key per l'integrazione applicativa | Token Plan Individual è limitato agli strumenti interattivi ammessi; esclude backend personalizzati, automazioni e batch non interattivi | [Piani](https://docs.qwencloud.com/resources/faq-billing), [chiavi](https://docs.qwencloud.com/api-reference/preparation/api-key), [condizioni Token Plan](https://docs.qwencloud.com/token-plan/personal/token-plan-personal-overview#terms-of-use), [FAQ](https://docs.qwencloud.com/token-plan/personal/token-plan-personal-faq#what-does-no-automated-api-calls-mean) |
| OpenAI | API Platform con fatturazione API e API key | Questo add-in non integra l'autorizzazione per usare un piano ChatGPT; il solo abbonamento non configura la chiave API | [Prezzi API](https://developers.openai.com/api/docs/pricing), [distinzione dalle integrazioni ChatGPT](https://developers.openai.com/siwc/quickstart) |
| Anthropic | Accesso alla Console e API key; controllare credito e fatturazione API | I piani chat e l'API sono distinti; alcuni piani includono crediti API, ma serve comunque l'accesso Console | [Abbonamento e API](https://support.claude.com/en/articles/9876003-i-have-a-paid-claude-subscription-pro-max-team-or-enterprise-plans-why-is-claude-api-usage-billed-separately-from-my-paid-claude-plan) |
| Google Gemini | Gemini API in un progetto AI Studio con fatturazione adeguata; per posta reale valutare il livello a pagamento | Google AI Pro/Ultra | [Fatturazione](https://ai.google.dev/gemini-api/docs/billing/), [prezzi e dati](https://ai.google.dev/gemini-api/docs/pricing) |

Nel listino Gemini verificato, il grounding con Google Search per i modelli Gemini 3 e successivi consultati non è disponibile nel livello gratuito. Le condizioni sull'uso dei dati differiscono tra livelli: controllare il piano esatto prima di inviare posta reale, senza dedurre la disponibilità dalla sola risposta a Test connessione.

## Preset disponibili

Il ConfigTool e la procedura guidata propongono sei preset, sulle quattro famiglie di protocollo Ollama, compatibile OpenAI, Anthropic e Gemini:

| Preset | Tipo | Collegamento |
| --- | --- | --- |
| Ollama | `ollama` | Server Ollama locale su loopback |
| Gemini | `gemini` | Gemini API |
| Anthropic | `anthropic` | Anthropic Messages |
| OpenAI | `openai` | OpenAI, con ricerca web specifica quando autorizzata |
| QwenCloud | `qwen` | Endpoint compatibile OpenAI e ricerca web specifica quando autorizzata |
| Compatibile OpenAI | `openai-compatible` | Indirizzo e modello da compilare; nessuna autenticazione oppure token bearer |

Il generico usa chat completions e non offre lettura delle capacità proprietarie né ricerca web specifica. «Scegli modello…» può comunque interrogare l'elenco standard dei modelli. Il servizio può essere locale su loopback oppure esterno: vale sempre l'indirizzo effettivo, oltre al flag esterno del profilo. Un [template dedicato](../../deployment/config.compatible.template.json) mostra i campi da adattare, senza una chiave reale.

I profili precedenti LM Studio, llama.cpp, vLLM, LocalAI, Jan, Mistral, xAI, DeepSeek, OpenRouter, Groq, Together e Fireworks vengono convertiti in memoria al generico. Indirizzo, modello, autenticazione, riferimenti alle chiavi e impostazioni restano conservati; la lettura non riscrive la configurazione. La conversione viene persistita al successivo salvataggio, importazione o ripristino. I profili prima classificati cloud restano esterni. Le capacità specifiche memorizzate vengono invalidate; eventuali scelte web conservate non abilitano una funzione assente nel generico. Il cambio di tipo richiede una nuova autorizzazione per gli invii e impedisce il riuso automatico della conversazione precedente.

Azure OpenAI, Perplexity Sonar e Ollama Cloud restano disponibili soltanto nei rispettivi profili già esistenti. Conservano header, percorsi e protocollo precedenti; la voce di compatibilità appare solo mentre si modifica quel profilo. Non sono proposti per crearne altri. Questa eccezione evita di trasformare automaticamente richieste incompatibili in chat completions generiche.

Il [catalogo applicativo](../../src/OnlyWorkspace.Core/Models/ProviderCatalog.cs) definisce i comportamenti del candidato; la [qualifica](../tecnico/verifica.md#non-qualificato) distingue le fixture dai servizi realmente provati. Per Qwen il default tecnico Token Plan non autorizza usi esclusi dal contratto: acquisire dal portale endpoint e chiave del piano consentito. Le prove precedenti non attestano l'idoneità contrattuale del piano.

### Perplexity: contratto verificato e limiti

La [documentazione ufficiale di migrazione](https://docs.perplexity.ai/docs/agent-api/migrate-from-sonar/overview), consultata il 23/10/2026, dichiara concluso il supporto Sonar Chat Completions dal 27/09/2026, mantenendo operative le richieste sincrone e streaming mediante riformulazione progressiva in Agent API. Il connettore continua a usare il contratto Sonar documentato: bearer, `/v1/sonar`, `messages`, `choices` e `delta.content`. Le [istruzioni di migrazione](https://docs.perplexity.ai/docs/agent-api/migrate-from-sonar/how-to) distinguono il nuovo `/v1/agent`, con `input` e output/eventi tipizzati: non basta cambiare URL per migrare.

La riqualifica A67 esercita il client applicativo con fixture sintetiche di risposta JSON e SSE, schema richiesto, consumo osservato, conclusione/troncamento e metadati delle fonti. È una verifica locale del contratto descritto, non una prova autenticata del servizio: su WS-DP01 non è configurato un profilo Perplexity. Restano non verificati disponibilità per account/modello e accettazione effettiva dei parametri/schema. Il client conserva gli eventuali marcatori `[n]` nel testo ma non trasforma `citations`/`search_results` in un elenco di URL o in riferimenti `[Wn]`; questi ultimi appartengono al distinto flusso Web Search dell'app. Non presentare quei marcatori come fonti già verificate da OnlyWorkspace. Nessuna migrazione automatica o attestazione di funzionamento reale deriva dalle fixture.

## Prima dell'attivazione

1. Il cliente sceglie provider, modello, titolare dell'account e chi paga; chiavi separate per cliente.
2. Controllare nel listino input, output, ragionamento e strumenti. Stima: `(token input / unità del listino) × tariffa input + (token output / unità) × tariffa output + strumenti`, con eventuali costi di cache. Usare un campione reale: QwenCloud il 20/10/2026 ha usato circa 9.400 token per scheda in media e fino a circa 22.000 per una sola scheda; non è una tariffa né una previsione per ogni modello.
3. Creare la chiave nell'account del cliente e inserirla solo nel ConfigTool (vault cifrato); mai per email o nel repository.
4. Provare la connessione con un prompt sintetico, poi autorizzare i dati reali ([Modelli IA](../utente/modelli-web.md)).
5. Controllare consumi e limiti nel portale del provider: l'app non impone un tetto monetario alle azioni IA avviate dall'utente.

Una chiave funzionante non prova che il piano consenta l'uso previsto: verificare le condizioni del servizio e del piano effettivamente sottoscritto.
