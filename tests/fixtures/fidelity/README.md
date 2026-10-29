# Banco dei fatti locali

Il banco attivo misura gli estrattori deterministici sul corpus sintetico, senza configurazione, credenziali o richieste ai provider. I contratti e le catture storiche del generatore IA ritirato rimangono consultabili nelle ricevute del dossier e nella storia Git; non qualificano il prodotto corrente.

Dopo il build Release:

```powershell
pwsh -NoProfile -File scripts/measure-fidelity.ps1 -OutputDirectory "$env:TEMP\ow-local-facts-evidence"
```

`-CorpusPath` sceglie un corpus alternativo. Il rapporto `offline.json` contiene precisione, richiamo, duplicati e attribuzioni errate rispetto alle attese indipendenti per origine e posizione. `manifest-offline.json` registra hash del corpus e degli assembly. Una cartella di output già associata ad altri byte del corpus viene rifiutata. Il runtime copiato e il corpus sono evidenze intenzionali da conservare con il rapporto.

Le metriche numeriche non attestano fedeltà semantica universale, conformità normativa o funzionamento di provider reali. Il recupero dell'archivio è verificato separatamente dai test di migrazione e storage.
