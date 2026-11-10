# Vocabolario dell'interfaccia: stati e termini

Riferimento per riquadro, ConfigTool e tray. Fonte degli stati: `StatusLight` (`src/OnlyWorkspace.Core/Archive/StatusLight.cs`). Verificato sul codice il 4 novembre 2026; il test `tests/scripts/UiVocabulary.Tests.cjs` ne controlla i punti meccanici. Esito della riduzione dei testi: [inventario](inventario-testi-interfaccia.md).

## Stati

| Stato | Parola (`StatusLights.Label`) | Riquadro (badge e stato) | ConfigTool: `StatusDot` (tray e menu) | ConfigTool: `OperationalStatusLabel` |
|---|---|---|---|---|
| Ok | sincronizzato | ✓ su verde | cerchio verde con segno di spunta | ✓ verde |
| Working | in lavorazione | anello giallo che ruota | arco che ruota (colore avviso) | arco che ruota |
| Error | errore | ! su rosso | quadrato rosso con ! | ! rosso |
| Paused | in pausa | ‖ | due barre grigie | ‖ (anche per un avviso: vedi sotto) |
| Waiting | in attesa | … | anello con punto grigio | … |
| Off | spento | – | anello vuoto | – |

- Il colore giallo «in corso» del riquadro è definito una volta (`--work-fill`, `--work-edge`, con bordo più chiaro nel tema scuro) e vale per il pallino di stato, il badge di stato e la nuvola cloud.
- Il glifo di ogni stato è definito una sola volta nel CSS del riquadro (`--glyph` su `[data-light]`) ed è ereditato da `.ind-badge` (pallino in alto), `.state-symbol` (badge `.state-badge` nelle schede e nel risultato) e `.status-text` (fase e cassette dell'archivio); JavaScript imposta solo `data-light`.
- Un avviso (`Tone.Warning`) del ConfigTool è mappato da `MainConfigForm.StateForTone` su `Paused` e quindi mostra ‖ come in pausa (scelta A200 confermata il 4 novembre): le due situazioni condividono il simbolo, la parola esatta sta nel testo accanto. Il rapporto di sicurezza ha un proprio vocabolario (✓ ! ✕ ?, `SecurityOutcome`) perché descrive l'esito di un controllo, non lo stato di un'operazione.
- Contrasto elevato di Windows è escluso dal perimetro (scelta del manutentore).

## Termini

- **Interrompi** (riquadro, ribbon, Esc): l'utente ferma un'operazione in corso; l'esito si dice «interrotta» («Operazione interrotta.», «Analisi interrotta», «Verifica interrotta»).
- **Annulla** (ConfigTool, finestre di conferma, autorizzazione di amministratore): l'utente rinuncia a un'azione o a una conferma; l'esito si dice «annullata/o» («Verifica annullata.», «Salvataggio annullato», «Conferma annullata.»).
- **Verifica sicurezza** è il nome dell'azione sulla singola email nel riquadro e nel menu contestuale; **Verifica** da solo, nel ribbon, indica la stessa azione sotto l'icona dello scudo.
- **Rispondi** nel riquadro apre le bozze; **Bozza risposta…** è la voce equivalente del ribbon.
- Azioni sulla singola email (Sintesi, Rispondi, Verifica sicurezza, Attività e appuntamenti, Estrai dati) compaiono solo con un'email selezionata o aperta e non in scrittura; Chat, Cerca e analizza e Briefing non richiedono selezione.
- Informazioni su privacy e uscita dei dati restano sempre per esteso; le descrizioni più lunghe vanno nella Guida o nell'aiuto contestuale («?»). Un «?» non sta mai da solo: accanto resta visibile la prima frase (`DescriptionHelp`, `AddWide` in `MainConfigForm.cs`; come le note della procedura guidata); una descrizione di una sola frase breve non ha «?».
- Un errore dell'archivio nomina la cassetta (riga Errore di Stato, «Vedi cassette» apre la tabella Cassette); il rosso dei pulsanti è per le azioni che perdono dati («Ripristina backup», «Elimina archivio»), non per «Compatta database». Il pulsante di Cerca e analizza dice «Avvia analisi» e resta fisso in fondo al modulo.
- Decisione (6 nov 2026): i selettori «Scadenza dal» e «al» dell'archivio restano il `DateTimePicker` nativo. Solo il calendario segue il tema (`CalendarMonthBackground`, `CalendarForeColor`): la casella ignora `BackColor` e nel tema scuro resta chiara. Un controllo proprio sostituirebbe tastiera, lettore di schermo e formato della data del sistema per un difetto estetico; si riesamina se l'archivio avrà più filtri a data.
