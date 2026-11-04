# Inventario dei testi dell'interfaccia

Esito per schermata del passaggio di riduzione del 4 novembre 2026 (vedi [vocabolario](interfaccia-vocabolario.md) per termini e stati). Misura: stringhe di codice sopra 140 caratteri nelle schermate del ConfigTool (57 prima, 55 dopo, perché molte erano pezzi di testi su più righe), voci della Guida del riquadro sopra 165 caratteri (oltre una decina prima, 0 dopo, controllo in `tests/scripts/UiVocabulary.Tests.cjs`) e tooltip oltre 130 caratteri (3 prima, 0 dopo).

## Riquadro

| Area | Esito |
|---|---|
| Guida: primi passi, Posta, Scrittura, Chat, Cerca, Storico | Accorciata a una riga per voce; rimosso il riferimento alla rubrica (nessuna scrittura contatti dopo A207/A209) |
| Guida: dati inviati al profilo IA, Cloud/PC/globo | Mantenuta e asciugata: sono avvisi su uscita dei dati |
| Guida: legenda del pallino di stato | Accorciata; i glifi vengono dal CSS (`--glyph`); un solo pallino per Archivio e IA, senza marchio nella barra |
| Tooltip Follow-up, Scrivi, filtro persona | Accorciati; «Elimina archivio» mantenuto (dice che le email non vengono toccate) |
| Tooltip «Invio del testo degli allegati» | Invariato; i messaggi di rifiuto per livello (`TaskPaneCoordinator`) ora citano «2 · Standard» e «3 · Esteso» allo stesso modo |

## ConfigTool

| Schermata | Esito |
|---|---|
| Procedura guidata (passi 1-5) | Accorciati introduzione, archivio, avvio con Windows, allegati; mantenuti i testi sui profili cloud e sul loopback (righe di uscita dati) |
| Configurazione: Archivio, Allegati, OCR, Log, Tray, Salva, Esporta informativa | Accorciati i tooltip; invariati stato dell'informativa, revoca e «Accetta e salva» |
| Profilo IA | Accorciati Servizio IA e Budget token; livelli di esposizione, ricerca web e servizio esterno invariati; gli altri tooltip di campo restano come aiuto contestuale |
| Capacità e verifiche | Accorciati introduzione e legenda; invariata l'avvertenza su dati inventati, token a pagamento e ricerca web |
| Ricerca archivio, Cassette | Accorciati compattazione, Message-ID e scelta delle cassette |
| `DataEgressNotice` | Versione 6 (4/11): testo, titolo e hash fanno parte delle autorizzazioni salvate; ogni modifica del testo richiede una nuova versione |

## Non fatto

- Tray: nessun testo lungo.
- Prova dal vivo a 100% e 200% e inventario riga per riga dei tooltip dei campi del profilo: aperti in #012.
