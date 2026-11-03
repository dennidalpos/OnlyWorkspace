# Marcatura dei frammenti HTML generati

Primo incremento A175: nelle prove su Word e sulla build installata in Outlook i commenti v1 non persistono integri. Il testo visibile dei casi controllati resta invariato. Il formato descritto è una dichiarazione applicativa di OnlyWorkspace, senza firma: il rilevatore riconosce la dichiarazione, non autentica l'origine del testo e non classifica testi arbitrari come umani o generati. A175 resta parziale.

## Formato e perimetro

Le nuove email e risposte preparate da `PrependDraft` in formato HTML (compreso il ramo RTF convertito in HTML) usano `DraftFormatter.ToGeneratedHtml`. Il convertitore HTML ordinario resta disponibile senza marcatura. Il solo frammento della bozza viene delimitato prima di combinarlo con firma e originale:

```html
<!--OnlyWorkspace:generated-text:v1:begin-->
<p class=MsoNormal>Testo della bozza</p>
<!--OnlyWorkspace:generated-text:v1:end-->
```

I due commenti sono costanti: non contengono identità, indirizzi, prompt, modello, data, hash del contenuto o identificatori personali. Non introducono caratteri invisibili nella stringa di testo semplice né elementi di layout. Il frammento HTML tra i commenti è identico a quello prodotto dal convertitore precedente; firma e testo citato rimangono fuori dai delimitatori. Una bozza vuota non riceve una dichiarazione.

I commenti sono aggiunti al contenuto sintetico dopo la generazione, senza cambiare prompt o provider. Non si riscrivono email, schede o storico esistenti. Le azioni di inserimento/sostituzione Word, i corpi in testo semplice, l'oggetto, la copia dal riquadro, le tabelle/CSV e gli altri output non sono coperti da questo incremento.

## Rilevatore locale

```powershell
. .\scripts\build-paths.ps1
$configToolDir = Get-OnlyWorkspaceOutputDirectory -RepositoryRoot (Get-Location).Path -Project OnlyWorkspace.ConfigTool
& (Join-Path $configToolDir 'OnlyWorkspace.ConfigTool.exe') inspect-ai-html .\prova.html | Out-Host
```

Eseguire dalla radice dopo la build Release, con la [radice degli output](output-locali.md) configurata e valida. Il comando usa l'output del grafo corrente, senza ripiegare su un precedente `bin` o `publish`.

Il comando legge un solo file `.html`/`.htm` locale, con limite di 4 MiB di file e di caratteri, senza troncamenti. Rifiuta i percorsi UNC e di dispositivo. Non carica configurazioni, segreti, immagini o collegamenti, non esegue HTML e non invia il testo a un servizio. L'output JSON contiene soltanto stato, conteggi, `authenticated: false` e un avviso; non riporta testo o percorso del file.

Il limite è applicato anche ai byte effettivamente letti. Senza BOM l'input deve essere UTF-8 valido; con BOM sono accettati UTF-8, UTF-16 LE/BE e UTF-32 LE/BE. Le sequenze malformate sono rifiutate, senza sostituzione silenziosa dei caratteri. Altre codifiche HTML non vengono desunte dal tag `meta`.

`GeneratedTextMarkup.InspectHtml` usa il tokenizer MimeKitLite 4.18.1 già incluso nel progetto per riconoscere commenti, ignorando esempi codificati nel testo, attributi e stringhe di script. Le coppie devono essere sequenziali, senza annidamento. Gli stati sono:

| Stato | Significato |
| --- | --- |
| `declared` | Trovata almeno una coppia v1, senza anomalie rilevate |
| `not-found` | Nessuna dichiarazione riconosciuta; non esclude origine IA |
| `malformed` | Delimitatori sbilanciati, annidati o commenti non validi |
| `unsupported` | Presente una variante/versione non riconosciuta; non viene promossa a v1 |

Il tokenizer HTML può recuperare un commento anche quando il file termina prima della chiusura. Il rilevatore verifica quindi anche la chiusura letterale `-->` nel sorgente consumato: un token riparato non completa una dichiarazione. Posizioni del sorgente e recupero sono confrontati con il [sorgente MimeKit della versione 4.18.1](https://github.com/jstedfast/MimeKit/blob/4.18.1/MimeKit/Text/HtmlTokenizer.cs).

Il codice d'uscita è 0 se l'ispezione termina, anche con dichiarazione assente o non valida; 1 per sintassi/contenitore non supportato; 2 per input illeggibile, percorso non ammesso o limite superato. `declaredFragments` conta le coppie v1 lette, ma non deve essere interpretato separatamente dallo stato e da `authenticated: false`.

## Verifiche e limiti

I test mirati coprono conservazione esatta del frammento HTML, Unicode e formattazione, confini con firma/originale, frammenti multipli, bozze vuote, esempi in testo/attributi/script, delimitatori non validi/versioni future, commenti troncati, fine riga CR/LF/CRLF, codifiche Unicode rigorose e limite di lettura su stream senza lunghezza disponibile. Questi test restano distinti dalla persistenza in Office.

Il 28/10/2026, con Office 16.0.19127.20860 e il candidato A175 installato, sono stati osservati questi risultati su contenuti sintetici:

| Percorso osservato | Esito dei commenti v1 |
| --- | --- |
| Word: apertura HTML e salvataggio come Pagina Web | Inizio perso, fine presente: `malformed` |
| Word: apertura dell'originale e salvataggio come Pagina Web filtrata | Inizio perso, fine presente: `malformed` |
| Add-in: Nuova email, sostituzione della sola firma copiata con una firma sintetica, export HTML | Inizio perso, fine presente: `malformed` |
| Chiusura, riapertura del MSG Unicode e nuovo export HTML | Entrambi persi: `not-found` |
| MIME locale UTF-8, quoted-printable/base64/8bit, corpo singolo e multipart/alternative | Stato dei commenti conservato nei 42 casi; tre corpi 8bit acquisiscono solo un fine riga finale |

Testo visibile confrontato dopo normalizzazione degli spazi: conservati i quattro output acquisiti, inclusi accenti, Ω, emoji e firma sintetica; Word conserva anche l'originale citato. L'export HTML di Outlook aggiunge l'intestazione dell'oggetto. Le schermate verificano i casi di formattazione osservati, senza qualificare ogni possibile documento.

Il marcatore non soddisfa quindi la persistenza nei percorsi provati. La prova di nuova email comprende la modifica della firma e non individua la fase esatta della prima perdita. Il passaggio DOCX non ha artefatti verificati. La risposta da EML sintetico ha incontrato un timeout QwenCloud nella fase web, prima della produzione della bozza: non qualifica `OpenReply`. MIME locale e MSG non qualificano invio/ricezione o persistenza nelle cassette. Ricevuta: `history/2026-10-28-a175-persistence/report.md` del dossier SecondBrain.

Una persona può aggiungere o rimuovere questi commenti: non c'è una catena di fiducia o una firma. Non viene dichiarata conformità C2PA o all'art. 50(2). Prima di considerare soddisfatto A175 servono una valutazione di autenticità/robustezza e interoperabilità, una soluzione che superi le perdite osservate, ulteriori prove sui percorsi non completati e sul trasporto reale, oltre alla soluzione per testo semplice e appunti. La scelta di mantenere invariato il testo visibile resta vincolante anche per quei percorsi.

[HTMLBody di Microsoft](https://learn.microsoft.com/en-us/office/vba/api/outlook.mailitem.htmlbody) documenta l'assegnazione del corpo HTML e l'aggiornamento del corpo testuale; non garantisce la persistenza di questi metadati. Valutazione e fonti normative restano in [Conformità UE](../admin/conformita-ue.md) e nelle ricevute A175 del dossier SecondBrain.
