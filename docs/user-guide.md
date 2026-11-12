# User guide

OnlyWorkspace is a COM add-in for classic Outlook on Windows. The interface is Italian; the labels below match the application. Mail comes from stores already open in Outlook. Acquired mail can also be searched from the tray with Outlook closed. No email is sent automatically.

## First use and navigation

Open **OnlyWorkspace → Configurazione** and complete the first-run wizard. Configure **Servizio → Indirizzo → Chiave → Modello** in order. Model listing is not a connection test. In the profile, **Diagnosi** checks the connection and the model's functions with synthetic input and may incur provider charges. Save the default profile; authorizing external data transfer is a separate action. Open an email and choose **Sintesi**.

The pane belongs to its Outlook window. **Chat**, **Posta** and **Storico** preserve their own filters/results. Posta contains search/analysis and briefing. The status dot opens **Stato**; endpoint, versions, measurements and **Diagnostica** are under **Avanzate**. Green requires a verified state, yellow actual work, red an error, grey off/paused/waiting/unverified. Selecting a model does not verify it. The cloud symbol means an external dispatch attempt, not a server receipt.

The Home/OnlyWorkspace ribbons expose pane actions. The right-click menu uses the clicked message. Received-message actions require a selected/open reading message; a draft, including an inline reply, takes precedence over the original still selected by Outlook. Keyboard: **Alt+S** summary, **Alt+R** reply, **Alt+T** other actions, **Alt+V** security, **Alt+C** chat, **Esc** close/interrupt. The minimum page is 340 CSS pixels; narrow Outlook panes scroll horizontally, and navigation wraps below 380 pixels.

## Message actions and writing

| Action | What to expect |
| --- | --- |
| **Sintesi** | Summary of the message, available thread and permitted attachment text. Outlook conversations use at most their latest 20 messages, declaring exclusions. Copy/regenerate without changing the original. |
| **Rispondi / Bozza risposta…** | Accept, decline with alternative or give an instruction. Review the draft before inserting at the cursor or opening reply/reply-all. |
| **Follow-up** | Conversation plus up to 12 matching earlier originals. Missing originals are counted, never replaced with previews. Earlier messages' attachments are excluded. |
| **Attività e appuntamenti** | Local facts at Minimal or permitted acquired text at other levels. Review proposals before saving or choosing **Aggiungi a Outlook**. |
| **Estrai dati** | Copy table/fields or **Salva CSV in Download**. Invalid entries are reported as a partial result; no fixed local 40-field/200-row/10-task cutoff. |
| **Verifica sicurezza** | Local sender/header/link/attachment-format/macro/static-script checks and separately attributed Defender results, with cited AI observations when permitted. |

Security distinguishes no anomaly detected, review needed, suspicious and unverifiable. It does not execute scripts, cryptographically validate header signatures, certify authenticity or replace antivirus. Failed/missing scans are not clean results. Local findings remain available without AI or if final history persistence fails.

Each action binds its source at launch: **Riprova/Rigenera** keeps that source after selection changes. If local context cannot contain all chosen input, the action stops before transport rather than trimming messages silently.

During composition, **Scrivi** uses request, subject, To/Cc names, draft and quoted message; attachment text only when requested and permitted. **Migliora** and **Traduci** use selected text, otherwise text above the quotation; **Oggetto** proposes a subject. Review **Inserisci**, **Sostituisci selezione**, **Copia** or **Usa come oggetto**. Replacement is refused if the selection changed while generating. These actions do not search mail/web. Compose chat writes unless mail search is explicitly requested. Outside composition, a writing request can offer **Nuova email**, with recipients left for the user.

## Search, briefing and chat

Generic chat does not automatically include a selected message: refer to it explicitly. Local intent rules recognize mail, calendar, contacts, writing and general questions. Recognized sender/period/document reference/amount/deadline filters narrow archive search; ambiguous values stay unresolved. PEC mentions do not imply a reliable mailbox filter; select the mailbox explicitly in **Cerca e analizza**.

In **Posta → Cerca e analizza**, choose summary, open points, custom analysis, table, appointments/deadlines, case reconstruction or unanswered mail. Scope can be accounts/mailboxes, chosen folders, whole archive, selected-message folder or conversation. Conversation/folder requires a reading selection; whole-mailbox scope excludes Deleted Items, Junk, Drafts and Outbox. Choose sender/recipient role explicitly. Outside archive search, comma/semicolon values are alternatives and * / ? are wildcards.

Search traverses scope; analysis reads the newest configured maximum (default 300) and reports additional matches. The local result list appears before AI output. Unread attachments and coverage remain visible. **Email senza risposta** searches Sent Items locally with followUpDays (default 5, range 1–30), then offers a reminder draft.

**Briefing** uses today's agenda and up to the latest 25 unread Inbox messages of the default account. Applying categories requires a click. At Minimal, agenda remains in local display/history and is excluded from model follow-up context. Search/analysis stopped after some mail was read preserves the partial result and avoids a new model call, or interrupts one already started.

## Archive and reconstruction

The administrator enables the archive; users choose **Configurazione → Archivio email → Le mie cassette**. By default all mailboxes, including future ones, are eligible. Per-user scope changes apply immediately without Save/UAC. Excluding a mailbox deletes its archived content/derivatives. Closed PSTs remain searchable until **Togli dall'archivio** is confirmed; reopening allows acquisition again. **Indirizzi miei** supplies editable own addresses for PSTs without an account.

Acquisition starts 60 seconds after Outlook, then waits for 10 idle seconds. Recent mail/folders are checked every two minutes, full checks four minutes after the previous check completes and settings about every five seconds. Deleted Items counts as presence. Absence needs a complete PST inventory or a cache-specific not-found check; errors/incomplete inventories retain existing data.

Search covers full acquired text, not only the 255-character preview. Local facts retain source text for amounts, deadlines, references, identifiers, invitations and PEC. Unresolved values do not satisfy numeric filters; fact/OCR output does not prove authenticity. Local fact recovery is resumable, respects Pause and does not consume AI tokens. Repeat searches after pending recovery.

**Ricostruisci la pratica** builds a local chain from Message-ID, reply/reference headers, Outlook conversation and matching order/invoice/protocol references, showing the reason. Equal dates/amounts are insufficient. Similar subject/sender within 30 days is tentative (maximum 20 links). One Message-ID entry retains all physical copies; absent IDs do not cause resemblance-based merging. **Copie → Apri copia** chooses a location. The view pages by 50, up to 20,000 messages. **Genera sintesi** separately requests AI using acquired originals, counts missing text and does not search the web.

## Maintenance and recovery

Tray search/status/maintenance works with Outlook closed. **Pausa/Riprendi**, **Aggiorna da Outlook**, **Ottimizza indice** and **Compatta database** are distinct actions. Compaction needs about twice database size plus 1 GiB free. Warning below 5 GiB; acquisition pauses below 1 GiB.

Removing the last verified copy invalidates dependent text/facts/unshared attachments and conversations that used it; existing exports/backups need separate handling. Confirmed **Elimina archivio** removes the user's archive, not Outlook mail, independent history or external backups. Unreadable archives/missing DPAPI keys are never replaced with an empty archive. Known old formats migrate only after verified backups; unknown formats are refused.

Limits: 3,000 folders/mailbox, depth 12, metadata for 50 recipients and 30 attachment names, 30 MiB/attachment and four million extracted characters. Partial documents remain explicitly unread/partial.

**Interrompi** stops work; starting another action replaces it. After 15 seconds loading history/flow, **Riprova** is offered; earlier requests may still finish. Browser failure triggers one automatic pane recreation, then **Riprova a caricare il pannello**. Export diagnostics from **Stato → Avanzate → Diagnostica**. Report version/action/synthetic reproduction without mail, credentials or raw logs.

## Data controls

Local AI uses loopback. External endpoints require authorization per service (it covers every profile and model of that service with the same destination, region, permissions and credentials). Level 0 sends nothing; Minimal sends protected structured facts; Standard permits mechanically masked free text; Extended adds masked attachment text only with separate permission. Original files/file names are not uploaded. Pseudonymization is reversible, can miss sensitive text and is not anonymity. Amounts written with the word **euro** (**1234,56 euro**) are recognized; company names in free text are not.

Web search runs only for the actions the administrator enables, listed next to the globe in the status-dot tooltip. With a cloud profile at Minimal or Standard no action searches the web; at Extended, a provider that searches by itself receives the whole request, already pseudonymized. Queries generated by Ollama stay blocked. If the model does not search, the answer says it has no web sources. ConfigTool capability checks send fixed synthetic requests. Changed credentials/policy interrupt prior work and start a fresh model conversation; old history stays readable.

See [administrator controls](admin-guide.md), [technical limits](developer-guide.md) and [the Italian references](README.md#italian-reference-map).