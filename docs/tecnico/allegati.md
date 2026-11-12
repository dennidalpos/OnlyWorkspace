# Allegati

Un documento entra solo come allegato di un'email Outlook. Outlook ne salva una copia temporanea (`%TEMP%\OnlyWorkspace`), [AttachmentExtractor](../../src/OnlyWorkspace.AttachmentExtractor) la legge in un processo separato e la copia viene eliminata. L'isolamento non è una certificazione di sicurezza del documento; Defender si usa solo in Verifica sicurezza.

## Invio con un profilo esterno

Per i profili esterni il testo degli allegati, estratto in locale e mascherato, è ammesso con **Esteso**, permesso distinto e informativa 8 valida; a Minimo solo i fatti. Nomi dei file e file binari restano sul PC. Estrazione e OCR sono locali. Gli esiti documentali sotto la risposta distinguono lettura locale e inclusione nella richiesta e sono conservati nello storico cifrato.

Restano i limiti esistenti: copia massima 30 MiB e 4.000.000 di caratteri per documento intero, con i limiti di espansione e profondità dei contenitori. Un documento incompleto, protetto, bloccato o con minaccia nota non contribuisce testo; il riquadro ne dichiara il motivo. Non si inviano estratti troncati per adattarli al contesto. Queste esclusioni non attestano l’assenza di minacce nei documenti non sottoposti a controllo antivirus.

## Formati

| Tipo | Testo estratto | Limiti |
| --- | --- | --- |
| PDF | Testo con ricostruzione delle righe; pagine scansionate riconosciute con OCR | PDF protetti esclusi; il documento è letto solo se ogni pagina scansionata viene riconosciuta |
| Word, Excel, PowerPoint OOXML (anche con macro e modelli) | Paragrafi, celle per foglio, testo delle diapositive; presenza di VBA | Nessun ricalcolo delle formule; un foglio oltre il limite di caratteri è marcato troncato |
| OpenDocument | Testo dell'XML | Senza impaginazione |
| RTF, HTML, testo | Testo leggibile; HTML senza marcatura e contenuti nascosti | Nessuno script |
| `.msg`, `.eml` | Intestazioni, corpo e allegati annidati supportati | Budget comune: espansione 30 MiB, profondità 2, nessun limite numerico agli allegati |
| `.p7m` | Documento incorporato, se supportato | **La firma non è verificata** |
| `.doc`, `.xls` | Testo Word 97–2003 e celle Excel binarie; presenza di macro | Strutture non supportate e password dichiarate |
| `.ppt` | Solo presenza di macro (Verifica) | Nessun testo |
| ZIP anche annidati | Testo degli elementi supportati nelle azioni dirette; solo riferimento nell'archivio | Lettura statica della Verifica sicurezza conservata |
| `.ics` | Testo iCalendar completo; righe ripiegate conservate nella sorgente | Struttura VCALENDAR con VEVENT/VTODO; nessuna apertura URL o creazione di appuntamenti |
| PNG, JPEG, GIF, TIFF (anche multipagina), BMP | Testo riconosciuto con OCR, una sezione per pagina | Fino a 200 pagine; ogni pagina deve essere riconosciuta |
| HEIC, WebP e altri | Nessun testo | Formato senza lettore |

## Lettura statica per la Verifica sicurezza

`--security <file>` ([ContentReader.Security](../../src/OnlyWorkspace.AttachmentExtractor/ContentReader.Security.cs)) percorre lo stesso albero della lettura del testo, con gli stessi limiti di dimensione, profondità e tempo e OCR spento, e restituisce `AttachmentInspection`: osservazioni con regola, file interno, riga e frammento, link e limiti. Gli script PowerShell, JScript/JavaScript, VBScript e batch e le pagine HTML/HTA/SVG/WSF sono letti con [ScriptStaticRules](../../src/OnlyWorkspace.Core/Security/ScriptStaticRules.cs) e [HtmlActiveContent](../../src/OnlyWorkspace.Core/Security/HtmlActiveContent.cs): commenti esclusi e stringhe mascherate, download ed esecuzione collegati solo da istruzione, variabile o percorso riconoscibili; comandi nascosti, comandi codificati, modifiche alle protezioni, moduli con password e reindirizzamenti; offuscamento e file codificati restano limiti dichiarati.

Dei PDF si leggono i link e i nomi completi `/JavaScript`, `/JS` e `/Launch`, decodificando una sola volta gli escape esadecimali `#xx` e rispettando maiuscole e delimitatori ([ISO 32000-1, §§ 7.2.2 e 7.3.5](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/PDF32000_2008.pdf)). Un nome più lungo non coincide con quello cercato; `OpenAction` da solo può indicare una destinazione interna e non produce una segnalazione di contenuto attivo. La scansione dei nomi è sui byte non compressi, senza analisi strutturale delle azioni: non dimostra l’assenza di contenuti attivi né copre gli object stream compressi. Dei documenti Office si leggono gli hyperlink esterni e i modelli remoti, dei `.msg`/`.eml` i link del corpo HTML.

I file trovati dentro archivi ed email allegate (fino a 200 nomi) sono elencati con il tipo riconosciuto, e l'analizzatore applica loro gli stessi controlli di nome e tipo degli allegati: estensione eseguibile, doppia estensione, programma con un'altra estensione. Gli allegati che Outlook blocca (`.exe`, `.ps1` e simili) non compaiono nel modello a oggetti: quando l'email dichiara allegati (`PR_HASATTACH`) ma nessuno è visibile, il rapporto segnala «Da verificare» e il limite; se convivono con allegati visibili non sono riconoscibili. Nulla è eseguito o scaricato e l'helper non referenzia API di rete (verificato dai test); l'isolamento di sistema resta quello del Job Object, senza token ristretto. Il risultato non tocca testi, cache, `ReaderRevision` dell'archivio.

Le immagini inline (loghi, firme) restano escluse. Nell'archivio `daticert.xml` viene letto soltanto per i fatti locali, anche se inline: stato `facts-only`, nessun termine FTS e nessun testo nei prompt. Le azioni dirette mantengono l'esclusione. Della PEC si legge anche il messaggio originale allegato; nessuna firma è verificata. Il recupero dei fatti rispetta il permesso dell'indice allegati. L'aggiunta di `.ics` e dei dati PEC riaccoda soltanto le sorgenti interessate o i contenitori incompleti, senza aumentare globalmente `ReaderRevision`.

La copertura PDF si verifica per pagina: una pagina non vuota con meno di 25 caratteri alfanumerici rende il documento incompleto (`scanned`), anche se altre pagine hanno testo. Una pagina priva di testo, immagini e tracciati è vuota; immagini decorative su una pagina con testo sufficiente non la rendono una scansione. La soglia è prudente anche per pagine intenzionalmente brevi. Il testo di un PDF misto incompleto non viene presentato come integrale. API: [PdfPig 0.1.16](https://github.com/UglyToad/PdfPig/blob/v0.1.16/src/UglyToad.PdfPig/Content/Page.cs).

## Indicizzazione locale degli allegati

L'archivio usa la modalità `--archive` dell'estrattore: documenti leggibili, MSG, EML e P7M conservano il testo completo entro i limiti dichiarati. Compressi generici (ZIP, RAR, 7z e simili), eseguibili e file senza lettore restano in stato `reference`: nome, estensione, dimensione originale in byte e collegamento all'email, senza testo estratto. Il contenuto non viene contato come documento letto nelle analisi. I pacchetti Office/OpenDocument restano documenti leggibili; la firma binaria impedisce che uno ZIP o un eseguibile rinominato `.txt` entri nell'indice.

La regola vale anche dentro MSG, EML e P7M. I contenitori leggibili riportano i riferimenti dei figli esclusi e conservano il proprio testo; per un riferimento MIME la dimensione dichiarata nel testo è quella codificata, esplicitamente indicata, senza attribuirle il significato di dimensione decodificata. Password, errori, OCR non disponibile e limiti restano motivi di lettura incompleta.

La revisione del lettore **2** rende inutilizzabili i vecchi testi allegati in cache e i loro termini, riaccodando la lettura senza cancellare i corpi. Le nuove colonne dei riferimenti sono aggiunte nel formato 13 senza ricostruire il database ([SQLite ADD COLUMN](https://www.sqlite.org/lang_altertable.html#alter_table_add_column)). La pipe corrente **v16** richiede l'aggiornamento congiunto di tray e add-in. La lettura generica delle azioni dirette e `--security` mantengono il supporto ai compressi: l'esclusione descritta qui riguarda l'archivio.

## OCR (E8)

Le pagine sotto soglia e le immagini allegate passano all'OCR di Windows ([Windows.Media.Ocr](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine)) nello stesso processo isolato, senza servizi esterni: le pagine PDF sono rese con [Windows.Data.Pdf](https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf) a 300 dpi, le immagini decodificate con l'orientamento EXIF e ridotte solo oltre `OcrEngine.MaxImageDimension`. Lingua: quelle del profilo utente di Windows per cui è installato un riconoscitore (`TryCreateFromUserProfileLanguages`); `OnlyWorkspace.AttachmentExtractor.exe --ocr-languages` elenca le lingue installate e quella usata. Ogni pagina riconosciuta deve raggiungere la soglia di 25 caratteri alfanumerici, altrimenti l'intero documento resta non letto (`scanned`): nessun testo parziale arriva a fatti o analisi. Senza riconoscitore per le lingue del profilo lo stato è `ocr-unavailable` («OCR non disponibile su questo PC»). L'amministratore può spegnerlo (ConfigTool → Generale, `attachmentOcr` in `config.json`, acceso per default): l'estrattore riceve `--no-ocr` sia dall'archivio sia dal riquadro e scansioni e immagini restano non lette con stato `ocr-off`; un testo già riconosciuto resta, anche dalla cache per SHA-256. Il controllo delle macro non usa l'OCR. Il testo riconosciuto è preceduto da `[testo riconosciuto con OCR]`.

Il riconoscimento non è esatto: su un PDF sintetico di 10 pagine A4 a 150 dpi con corpo 11 punti, ordini e scadenze erano 400/400 ma i centesimi erano spesso letti come lettere («1.250,OO»); due O dopo una cifra e un separatore, non seguite da lettere o cifre, diventano «00». I valori citati da una risposta vengono comunque verificati sul testo riconosciuto, non sull'immagine. Tempi misurati su WS-DP01 (25/10/2026): 10 pagine in circa 14,5 s nel processo a 64 bit e 19 s in un processo a 32 bit, entro i 60 s per allegato. Il pacchetto [Microsoft.Windows.SDK.Contracts 10.0.28000.2957](https://www.nuget.org/packages/Microsoft.Windows.SDK.Contracts/10.0.28000.2957) serve solo alla compilazione: le API fanno parte di Windows 10 e successivi e non si aggiungono file all'installazione.

Con l'OCR acceso il motore della tray rimette in coda una sola volta (`meta.ocr_reader` = 1, in `ConfigureStorage`) le email delle cassette elencate con scansioni, immagini o contenitori rimasti non letti (`scanned`, `ocr-off`, immagini `unsupported`): accade alla prima apertura dopo l'aggiornamento e a ogni riaccensione. L'indicizzatore dell'add-in rilegge l'impostazione prima di ogni email e non salva una lettura fatta mentre l'impostazione cambiava: l'email resta in coda e viene riletta con quella nuova (A130). A ogni avvio la tray chiede all'estrattore (`--ocr-languages`) la lingua OCR dell'utente: quando compare o cambia (per esempio dopo l'installazione di una lingua OCR di Windows) rimette in coda una volta le email con allegati `ocr-unavailable` (`meta.ocr_language`). Spegnendolo scrive 0 e non cancella nulla; le cassette chiuse conservano ciò che hanno. Il nuovo testo cambia la versione del corpo e invalida i fatti e gli indici derivati interessati.

## Limiti

| Livello | Valore |
| --- | --- |
| Dimensione di ogni allegato | 30 MiB |
| Testo per estrazione | 4.000.000 di caratteri; anche come somma di corpo e allegati destinabili alle analisi nell'archivio. I dati PEC locali hanno il proprio limite di estrazione e non consumano quel budget |
| Tempo di estrazione | 60 s per allegato, 20 s per il solo controllo macro; lettura completa di un'email per l'archivio entro 2 minuti, recupero dell'originale dalla tray entro 3 |
| Memoria dell'estrattore | 1 GiB |
| Scansione Defender | 60 s per file |

Un allegato oltre i limiti, protetto, senza lettore o in errore è elencato con il motivo e non viene trattato come letto per intero: le analisi lo dichiarano e l'archivio lascia l'email senza scheda. Il resoconto distingue limiti dimensionali, profondità, contenuti interni non supportati e lettura incompleta; un contenitore parziale non è necessariamente troppo grande. Per DOC/XLS/ZIP supportati segnala la struttura o il contenuto non leggibile, senza dichiarare escluso l’intero formato.

## Cosa resta e cosa va al modello

| Dato | Archivio | Al modello |
| --- | --- | --- |
| Oggetto, mittente, destinatari, date, cartella | Sì (50 destinatari) | I campi pertinenti alla funzione |
| Corpo | Testo integrale cifrato, indice completo e anteprima di 255 | Testo acquisito; Outlook per originali ancora mancanti |
| Nomi degli allegati | Sì (30 per email) | Anche quando il contenuto è escluso |
| Testo degli allegati | Testo cifrato deduplicato per SHA-256, stato, motivo e versione, se l’indice degli allegati è attivo | Solo nelle funzioni che lo usano e, per il cloud, con `allowAttachmentUpload` |
| File originali | No | Mai |

Spegnere **Indicizza il testo degli allegati** (Generale) elimina testi locali e termini degli allegati, rimuove anche i fatti derivati da quei testi; riaccenderla rimette in coda gli originali disponibili. Non cambia l'invio del testo al cloud.

La cache distingue il SHA-256 del file dalla revisione del lettore. Dopo una correzione della completezza, il testo precedente richiede rilettura anche a hash invariato; i fatti e i termini derivati non sono utilizzabili fino al recupero. I corpi acquisiti restano conservati.

Codice: [AttachmentTextExtractor](../../src/OnlyWorkspace.Infrastructure/Security/AttachmentTextExtractor.cs), [ContentReader](../../src/OnlyWorkspace.AttachmentExtractor/ContentReader.cs), [ArchiveAttachmentReader](../../src/OnlyWorkspace.Infrastructure/Archive/ArchiveAttachmentReader.cs).

La lettura statica della Verifica sicurezza (`--security`) controlla anche gli indirizzi scritti nel testo. Li deduplica rispetto ai link strutturati solo quando coincidono con la destinazione o con l'intero indirizzo visibile, confrontato senza prefisso HTTP/HTTPS; un testo breve come «.com» o «login» non esclude altri indirizzi. Se la lettura fallisce, conserva formato, macro e rilievi già rilevati e dichiara l'errore, senza contare il documento come letto. Le prove sono in `AttachmentInspectionTests`; questa modalità non modifica la revisione del lettore né la cache dell'archivio.

Formati di testo semplice letti come `.txt` (#151): `vcf`, `sql`, `srt`, `cfg`, `conf`, `properties`, `tex`, `rst`, `kml`, `gpx`, oltre a `txt`, `csv`, `tsv`, `md`, `log`, `json`, `xml`, `yaml`, `yml`, `ini`, `ics`. Gli script (`js`, `ps1`, `bat`) restano fuori: la verifica di sicurezza li esamina come script. Gli ZIP restano riferimenti nell'archivio, per scelta documentata nei test `ArchiveAttachmentPolicyTests`. Un file già archiviato come «formato non supportato» con una di queste estensioni resta tale finché non viene riletto.
