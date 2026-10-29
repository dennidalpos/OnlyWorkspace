# Contatti

**Persone → Rubrica** (ribbon o riquadro) propone le persone a cui hai scritto almeno una volta e che mancano nella rubrica, o i contatti che un'email più recente potrebbe aggiornare. Richiede l'[archivio](archivio.md) attivo.

## Cercare

1. In **Dove cercare** spunta le cassette archiviate e le cartelle (sono archiviate anche quelle create da te o dalle regole). Una cassetta non archiviata è indicata con il motivo; durante l'indicizzazione i risultati sono parziali.
2. Facoltativo: **Escludi i colleghi dei miei domini**.
3. Scegli la **rubrica di destinazione** (cassetta e cartella); se manca, **Crea cartella «Contatti»**. Con una sola cassetta scelta la destinazione è la sua rubrica.
4. **Cerca contatti**. Le scelte vengono ricordate per la volta successiva.

Con la pagina aperta l'elenco delle cassette si aggiorna ogni 30 secondi: una cassetta aggiunta a Outlook diventa selezionabile appena l'archivio ne ha elencato le cartelle, senza perdere le scelte fatte.

| Stato | Significato |
| --- | --- |
| **Mancante** | Non è nella rubrica di destinazione (il riquadro dice se è in un'altra cassetta) |
| **Da verificare** | L'email più recente è successiva all'ultima modifica del contatto |
| **Conflitto** | Più contatti con lo stesso indirizzo |
| **Aggiornato** | Già in ordine; visibile con **Mostra anche i contatti aggiornati…** |

Gli indirizzi automatici o a cui non si è mai risposto sono esclusi e contati.

## Analizzare e scrivere in rubrica

- **Analizza selezionati** invia al modello del profilo attivo l'email più recente scritta da ciascun indirizzo; i valori che non compaiono nell'email vengono scartati. Un'email troppo lunga per il contesto locale viene saltata e si prova la successiva.
- Ogni scrittura richiede un clic: **Aggiungi**, **Aggiungi come indirizzo a «…»**, **Aggiorna** (diff campo per campo), **Applica gruppo**. Un contatto modificato nel frattempo non viene sovrascritto.
- **Rifiuta** esclude l'indirizzo per quella rubrica; **Esclusi per questa rubrica → Riproponi** lo riporta.
- L'analisi si ferma con **Interrompi** della schermata o del ribbon e con Esc (il primo Esc chiude la schermata, il secondo interrompe).

Codice: [TaskPaneCoordinator.Contacts](../../src/OnlyWorkspace.Outlook/UI/TaskPaneCoordinator.Contacts.cs), [OutlookDataService.Contacts](../../src/OnlyWorkspace.Outlook/Services/OutlookDataService.Contacts.cs).
