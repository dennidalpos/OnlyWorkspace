# Istruzioni del repository: OnlyWorkspace

<!-- secondbrain:repository:begin -->
Direttive comuni applicate: revisione **1.0** — 2026-11-05. <!-- secondbrain:source global=A673995DCF6B94765D80ABE7C4216E8357899F1E08A81FF549E350901F45768A workstation=877122D70AC9C38F743C7D502CA4572FC59BE3C295D7AA722C7E7B5BDE8B4777 -->
## SecondBrain

Direttive comuni e conoscenza del repository risiedono nel dossier SecondBrain; qui restano codice, guide e asset pubblicabili.

- Dossier: risolvilo per identità Git, indice PARA e repository.json. SECONDBRAIN_HOME viene dall’ambiente utente; se la radice non è raggiungibile, segnala l’indisponibilità del dossier. Per i progetti condivisi usa la radice UNC della squadra.
- Contesto: carica il profilo snello con scripts/knowledge.ps1 -Startup (o MCP get_startup_context) e approfondisci con i riferimenti restituiti.
- Chiusura: verifica i criteri di accettazione e aggiorna il tracker centrale; i task conclusi vanno nello storico, contesto e memoria contengono le sole novità verificate.
- Autorizzazioni: commit, push, cancellazioni e pubblicazione richiedono autorizzazione specifica.

### Cartelle SecondBrain e sessioni
- Le cartelle di lavoro stanno solo in SecondBrain/.runtime, mai nel repository prodotto. Creale dal clone SecondBrain con scripts/work.ps1 -Repository <identità> -Category <work|evidence|report|fixture|tmp> -Slug <nome> -Init -Task <descrizione> -SessionId <id>: producono .runtime/hosts/<host>/repos/<repo>/<categoria>/<yyyyMMdd>-<slug>/ con MANIFEST.json (proprietario, sessione, task, stato, scadenza). Non creare cartelle piatte o con altri nomi.
- Una cartella appartiene a una postazione, un utente e una sessione. Non scrivere nelle cartelle di altri utenti o postazioni; sulla radice condivisa di squadra si condividono solo dossier e access/.
- Sessione: per scritture prolungate apri scripts/access.ps1 -Start e chiudi con -End nello stesso script (try/finally); passa lo stesso SessionId a work.ps1 e alle CLI. Se la sessione è occupata, attendi o scegli un’altra attività: non forzare lock né cancellare marker.
- A fine sessione porta le evidenze o i rapporti utili nel dossier (history/), chiudi la cartella con lo stesso comando e -Close (aggiorna MANIFEST.json) e lascia fixture e tmp alla retention (7 giorni, ultime 5 conservate; work 30 giorni dalla chiusura; evidence e report mai automatici). Ricostruisci l’indice con scripts/cleanup.ps1 -RuntimeReport.
- Pulizia e migrazione (scripts/cleanup.ps1 -Migrate, -Regenerable) partono in anteprima e si applicano solo su richiesta esplicita, sulla propria postazione, con perimetro concreto e inattività delle sessioni.
- Backup, journal e artefatti ufficiali non si eliminano; i dati sensibili grezzi restano nell’area locale esclusa da Git.
<!-- secondbrain:repository:end -->
