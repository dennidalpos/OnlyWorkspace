# OnlyWorkspace

Add-in COM gestito per Outlook classico 2016 e successivi su Windows, a 32 e 64 bit. Il riquadro WebView2 porta in Outlook sintesi, bozze, verifica di sicurezza, estrazione di dati, ricerca e analisi della corrispondenza e chat, con modelli Ollama locali o servizi cloud configurati dall'amministratore. Un archivio locale cifrato, ospitato dalla tray, rende la posta cercabile anche a Outlook chiuso. La posta viene letta solo dagli store aperti in Outlook: nessun connettore Graph, IMAP o OAuth.

## Documentazione

[Indice delle guide](docs/README.md): [utente](docs/README.md#utente), [amministratore](docs/README.md#amministratore), [tecnico](docs/README.md#tecnico). Per iniziare: [Riquadro](docs/utente/riquadro.md) e [Installazione](docs/admin/installazione.md).

Le guide distinguono il comportamento disponibile dalle evoluzioni approvate e ancora aperte nel tracker (SecondBrain: `02-areas/products/OnlyWorkspace/tracker.json`). Prove, limiti e discrepanze: [Verifica e qualifica](docs/tecnico/verifica.md). Il [kit illustrativo](assets/social_copy.md) include quattro immagini derivate da catture installate del 21 ottobre 2026 su dati sintetici e tre illustrazioni, identificate nei PNG; le catture non qualificano le modifiche successive.

## Sviluppo

Da PowerShell nella radice: `dotnet build OnlyWorkspace.sln -c Release`. Comandi di test, gate e smoke: [Verifica e qualifica](docs/tecnico/verifica.md) e [AGENTS.md](AGENTS.md). Il setup firmato richiede il PFX e la sua password: [Installazione](docs/admin/installazione.md).

OnlyWorkspace è software proprietario OnlyApps, autore Danny Perondi ([GitHub](https://github.com/dennidalpos)). Licenze delle dipendenze: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt); i crediti sono nella pagina **Informazioni** della Configurazione.
