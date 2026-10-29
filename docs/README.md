# Documentazione di OnlyWorkspace

Guide del comportamento disponibile, organizzate per destinatario. [Verifica e qualifica](tecnico/verifica.md) distingue prove e limiti; il tracker del dossier SecondBrain (`02-areas/products/OnlyWorkspace/tracker.json`) conserva il lavoro aperto, incluse le evoluzioni approvate non ancora implementate.

## Utente

| Voglio… | Guida |
| --- | --- |
| Usare il riquadro: sintesi, bozze, verifica, attività, dati, briefing, chat | [Riquadro e azioni](utente/riquadro.md) |
| Cercare e analizzare la corrispondenza, trovare scadenze o email senza risposta | [Cerca e analizza](utente/cerca-analizza.md) |
| Aggiungere o aggiornare in rubrica le persone della posta | [Contatti](utente/contatti.md) |
| Attivare l'archivio, cercare offline, vedere controparti e flussi | [Archivio](utente/archivio.md) |
| Sapere chi riceve i dati e quando si cerca sul web | [Modelli IA e Web Search](utente/modelli-web.md) |
| Avere una mappa visiva di tutti i percorsi, scelte e recuperi | [Guida visiva ai percorsi](utente/guida-visiva.md) |

## Amministratore

| Voglio… | Guida |
| --- | --- |
| Compilare, firmare, installare o disinstallare | [Installazione](admin/installazione.md) |
| Conoscere `config.json`, i profili IA e i segreti | [Configurazione](admin/configurazione.md) |
| Usare la finestra Configurazione, la CLI e la tray | [ConfigTool e tray](admin/configtool.md) |
| Scegliere e acquistare un servizio IA | [Provider IA](admin/provider-ia.md) |
| Sapere dove stanno i dati e come sono protetti | [Sicurezza e privacy](admin/sicurezza.md) |
| Configurare l'uscita dei dati secondo GDPR e AI Act, livelli di esposizione ed export dell'informativa | [Conformità UE](admin/conformita-ue.md) |

## Tecnico

| Argomento | Guida |
| --- | --- |
| Componenti, thread e confini | [Architettura](tecnico/architettura.md) |
| Formato, sincronizzazione e limiti dell'archivio | [Motore dell'archivio](tecnico/archivio-motore.md) |
| Lettura degli allegati, formati e limiti | [Allegati](tecnico/allegati.md) |
| Programmi, librerie e servizi usati | [Dipendenze](tecnico/dipendenze.md) |
| Comandi di verifica, CI e stato della qualifica | [Verifica e qualifica](tecnico/verifica.md) |
| Corpus sintetico, misure dei fatti e revisione della fedeltà | [Banco di fedeltà](../tests/fixtures/fidelity/README.md) |

## Fonti e manutenzione

- Ogni guida mantiene un ambito; qui si trova l'indice completo. I comandi obbligatori degli agenti restano in [AGENTS.md](../AGENTS.md), le attività aperte nel tracker e le prove datate in Verifica e qualifica.
- Il dossier SecondBrain indicato in AGENTS.md conserva decisioni tecniche e stato corrente. I suoi file `.snapshot.txt` sono copie generate di consultazione: si aggiornano con SecondBrain Sync, mai a mano.
- [Kit illustrativo e copy](../assets/social_copy.md): sorgenti editoriali e istruzioni di rigenerazione. Il set social contiene sette PNG (01–04 da catture installate del 21/10/2026 su dati sintetici con QwenCloud; 05–07 illustrazioni). I PDF sono del deck e della scheda prodotto, non del set social.
- [Avvisi e licenze](../THIRD-PARTY-NOTICES.txt): conservare gli avvisi originali; verificare versioni e testi nei pacchetti effettivamente ripristinati. I file di licenza delle dipendenze e la Public Suffix List incorporata non si sintetizzano come una guida.
