# Vocabolario dell'interfaccia: stati e termini

Riferimento per riquadro, ConfigTool e tray. Fonte degli stati: `StatusLight` (`src/OnlyWorkspace.Core/Archive/StatusLight.cs`). Verificato sul codice il 4 novembre 2026; il test `tests/scripts/UiVocabulary.Tests.cjs` ne controlla i punti meccanici.

## Stati

| Stato | Parola (`StatusLights.Label`) | Riquadro (badge e stato) | ConfigTool: `StatusDot` (tray e menu) | ConfigTool: `OperationalStatusLabel` |
|---|---|---|---|---|
| Ok | sincronizzato | ✓ su verde | cerchio verde con segno di spunta | ✓ verde |
| Working | in lavorazione | anello giallo che ruota | arco che ruota (colore avviso) | arco che ruota |
| Error | errore | ! su rosso | quadrato rosso con ! | ! rosso |
| Paused | in pausa | ‖ | due barre grigie | ▲ (avviso: vedi sotto) |
| Waiting | in attesa | … | anello con punto grigio | … |
| Off | non attivo | – | anello vuoto | – |

- Il colore giallo «in corso» del riquadro è definito una volta (`--work-fill`, `--work-edge`) e vale per l'indicatore, il badge di stato e la nuvola cloud.
- Il badge del riquadro è disegnato da tre selettori CSS (`.ind-badge` negli indicatori in alto, `.state-badge` nelle schede e nel risultato, `.status-text` nella fase e nelle cassette dell'archivio) con gli stessi glifi; `stateBadge()` è l'unico punto JavaScript che li assegna.
- `OperationalStatusLabel` usa ▲ per un avviso (`Tone.Warning`), che `MainConfigForm.StateForTone` mappa su `Paused`: è l'unico stato che il ConfigTool mostra con una forma diversa dalle barre. Il rapporto di sicurezza ha un proprio vocabolario (✓ ! ✕ ?, `SecurityOutcome`) perché descrive l'esito di un controllo, non lo stato di un'operazione.
- Contrasto elevato di Windows è escluso dal perimetro (scelta del manutentore).

## Termini

- **Interrompi** (riquadro, ribbon, Esc): l'utente ferma un'operazione in corso; l'esito si dice «interrotta» («Operazione interrotta.», «Analisi interrotta», «Verifica interrotta»).
- **Annulla** (ConfigTool, finestre di conferma, autorizzazione di amministratore): l'utente rinuncia a un'azione o a una conferma; l'esito si dice «annullata/o» («Verifica annullata.», «Salvataggio annullato», «Conferma annullata.»).
- **Verifica sicurezza** è il nome dell'azione sulla singola email nel riquadro e nel menu contestuale; **Verifica** da solo, nel ribbon, indica la stessa azione sotto l'icona dello scudo.
- **Rispondi** nel riquadro apre le bozze; **Bozza risposta…** è la voce equivalente del ribbon.
- Azioni sulla singola email (Sintesi, Rispondi, Verifica sicurezza, Attività e appuntamenti, Estrai dati) compaiono solo con un'email selezionata o aperta e non in scrittura; Chat, Cerca e analizza e Briefing non richiedono selezione.
- Informazioni su privacy e uscita dei dati restano sempre per esteso; le descrizioni più lunghe vanno nella Guida o nell'aiuto contestuale («?»).
