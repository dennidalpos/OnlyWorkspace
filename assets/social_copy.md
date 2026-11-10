# OnlyWorkspace · Kit visivo e copy social

Revisione editoriale 23/10/2026. Il kit descrive le funzioni disponibili e la qualifica interna in corso; non costituisce una nuova prova della build installata.

## Inventario e provenienza

Set unico di **sette PNG 1080 × 1350 px (4:5)** in `assets/social/`, generato da `python scripts/generate-social-assets.py` con Pillow e font Windows. Il generatore social non produce PDF. Deck e scheda prodotto, con i rispettivi PDF, appartengono al generatore completo separato.

| File | Contenuto | Provenienza |
|---|---|---|
| [01-copertina.png](social/01-copertina.png) | Panoramica Outlook e assistente | Derivata dalla cattura installata del 21/10/2026 |
| [02-sintesi-email.png](social/02-sintesi-email.png) | Sintesi email | Derivata dalla cattura installata del 21/10/2026 |
| [03-verifica-sicurezza.png](social/03-verifica-sicurezza.png) | Evidenze tecniche e rischi | Derivata dalla cattura installata del 21/10/2026 |
| [04-estrai-dati.png](social/04-estrai-dati.png) | Tabella da ordine o preventivo | Derivata dalla cattura installata del 21/10/2026 |
| [05-bozze-scrittura.png](social/05-bozze-scrittura.png) | Scrittura assistita | Illustrazione, non schermata dell'app |
| [06-ricostruisci-flusso.png](social/06-ricostruisci-flusso.png) | Cronologia della corrispondenza | Illustrazione, non schermata dell'app |
| [07-privacy-sovranita.png](social/07-privacy-sovranita.png) | Archivio, IA e destinazioni | Illustrazione, non schermata dell'app |

Le tre catture originali in `screenshots/installed/` mostrano Outlook classico su WS-DP01, PST sintetico con indirizzi `.example` e QwenCloud. La copertina usa la cattura Sintesi. Date ed evidenze sono in [Verifica e qualifica](../docs/tecnico/verifica.md). Le catture documentano quella build, non ogni modifica successiva; non sono prove su cassette clienti.

## Messaggio e condizioni sui dati

**La tua posta. Un contesto chiaro.** OnlyWorkspace affianca Outlook classico con sintesi, verifica sicurezza, tabelle, bozze e ricerca nell'archivio acquisito. I risultati IA vanno rivisti: la verifica sicurezza mostra indizi tecnici, senza garantire che un messaggio sia sicuro.

- L'archivio resta sul PC in un database cifrato; la chiave è protetta da DPAPI Windows. I file allegati originali non sono conservati nell'archivio, mentre il testo acquisito può esserlo.
- L'inferenza con Ollama su loopback può lavorare offline **senza ricerca web**. La ricerca web usa servizi esterni anche con un modello locale; un endpoint su un'altra macchina non equivale a elaborazione sul proprio PC.
- Con un profilo cloud escono fatti locali a Minimo oppure anche originali ripuliti a Standard, con segnaposto e senza testo degli allegati. L'uscita dei dati Outlook richiede l'informativa accettata dal responsabile per le destinazioni. Le catture 01-04 sono storiche (2026-10-21) e possono mostrare comportamenti precedenti.
- Uso per addestramento, conservazione e condizioni del servizio dipendono dal provider e dal contratto/piano effettivo. Il prodotto non certifica i provider e non promette privacy assoluta. Vedere [Sicurezza e dati](../docs/admin/sicurezza.md), [Provider IA](../docs/admin/provider-ia.md) e, come esempio di condizioni differenti tra piani, [Gemini API](https://ai.google.dev/gemini-api/docs/pricing).
- Nessuna email viene inviata automaticamente: l'utente rivede le bozze e sceglie le azioni su Outlook.

## Post LinkedIn

Una decisione in una risposta, una scadenza in un allegato, un ordine da trasformare in tabella: OnlyWorkspace porta l'assistente IA in Microsoft Outlook classico.

Sintetizza le email, raccogli indizi su link e allegati sospetti, copia le tabelle o esportale in CSV e prepara bozze da rivedere prima dell'invio. L'archivio cifrato è sul PC; scegli il servizio IA e controlla le destinazioni autorizzate. Per lavorare offline usa un modello locale su loopback senza ricerca web. Con cloud o web, verifica le condizioni del servizio scelto.

Le immagini 01–04 derivano da catture installate del 21/10/2026 su dati sintetici; 05–07 sono illustrazioni. Qualifica interna in corso.

OnlyWorkspace per Microsoft Outlook classico. Ti interessa una demo sul tuo flusso di lavoro? Scrivici in privato.

## Post breve Instagram / Facebook

La tua posta. Un contesto chiaro. Sintesi, tabelle e bozze dentro Outlook classico, con archivio cifrato sul PC e servizio IA configurabile. Cloud e ricerca web comportano invii esterni autorizzati; le condizioni sui dati dipendono dal servizio e dal piano scelto. Nessun invio automatico di email.

Catture storiche su dati sintetici e illustrazioni sono indicate in ogni immagine. Qualifica interna in corso. Per una demo, scrivici in privato.

## Prima della diffusione

Usare i sette PNG con le indicazioni di provenienza visibili. Non presentarli come collaudo corrente, non promettere certificazioni o garanzie sui provider e non aggiungere un PDF social all'inventario. Il [repository del codice](https://github.com/dennidalpos/OnlyWorkspace) è pubblico; la diffusione dei materiali promozionali richiede una richiesta specifica. Generazione e revisione locale non autorizzano la pubblicazione dei materiali.
