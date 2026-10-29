# Dipendenze

Versioni dai manifest del repository; non attestano cosa è installato su un altro PC. Licenze: [THIRD-PARTY-NOTICES.txt](../../THIRD-PARTY-NOTICES.txt). Manifest: [Outlook](../../src/OnlyWorkspace.Outlook/OnlyWorkspace.Outlook.csproj), [AttachmentExtractor](../../src/OnlyWorkspace.AttachmentExtractor/OnlyWorkspace.AttachmentExtractor.csproj), [Infrastructure](../../src/OnlyWorkspace.Infrastructure/OnlyWorkspace.Infrastructure.csproj), [Core](../../src/OnlyWorkspace.Core/OnlyWorkspace.Core.csproj).

## Sul PC

| Componente | Uso | Se manca |
| --- | --- | --- |
| Outlook classico 2016+, x86 o x64 | Posta, agenda, contatti, bozze | Niente raccolta né originali; resta la ricerca nell'indice dalla tray |
| .NET Framework 4.8 | Add-in, ConfigTool, estrattore | Il setup si ferma |
| WebView2 Runtime (SDK 1.0.4258.31) | Riquadro; PDF dell'informativa nel ConfigTool | Il setup lo scarica; senza, il riquadro non si apre e l'export PDF fallisce con un messaggio |
| Microsoft Defender (`MpCmdRun.exe`) | Scansione degli allegati in Verifica sicurezza | Il rapporto dichiara la scansione assente |
| DPAPI | Chiave dell'archivio, storico, vault | Dati protetti non leggibili; mai sovrascritti |
| Ollama (facoltativo) | Profili locali | Le azioni di quel profilo falliscono con l'errore |

Le interop Office 15.0.4797.1004 servono alla compilazione per leggere Outlook e scrivere nell'editor Word; non indicano la versione di Office installata. I tipi sono incorporati applicando `EmbedInteropTypes` a `ReferencePath`, come documentato da [NuGet](https://learn.microsoft.com/en-us/nuget/create-packages/author-packages-with-com-interop-assemblies). Il packaging controlla i riferimenti runtime prima di escludere le due DLL. I pacchetti community non dichiarano una licenza: firma e strong name attestano l'autenticità, non i diritti di redistribuzione separata; le DLL non vengono distribuite.

## Librerie incluse

| Libreria | Uso |
| --- | --- |
| PdfPig 0.1.16 | Testo dei PDF |
| DocumentFormat.OpenXml 3.5.1 | Word, Excel, PowerPoint OOXML e parti VBA |
| OpenMcdf 3.3.0 | `.msg`, testo dei DOC e macro dei file Office binari |
| ExcelDataReader 3.9.0 | Celle dei fogli XLS binari |
| MimeKitLite 4.18.1 | `.eml` e MIME |
| Microsoft.Windows.SDK.Contracts 10.0.19041.1 | Solo compilazione: metadati di Windows.Media.Ocr e Windows.Data.Pdf per l'OCR (E8); nessun file distribuito. Licenza del Windows SDK con accettazione richiesta dal pacchetto ([termini](https://aka.ms/WinSDKLicenseURL)) |
| Microsoft.Data.Sqlite.Core 10.0.13, SQLite3MC.PCLRaw 2.6.1 | Database cifrato e FTS5 |
| Nager.PublicSuffix 3.8.0 | Domini nei controlli di sicurezza (lista locale) |
| Serilog 4.4.0, Serilog.Sinks.File 7.0.0 | Log |
| System.Text.Json e pacchetti .NET 10.0.13 | JSON, canali, DPAPI su .NET Framework |

OpenDocument, RTF e `.p7m` usano le API standard .NET/Windows. I provider cloud sono chiamati via HTTP, senza SDK.

## Servizi esterni (facoltativi)

| Servizio | Quando | Dati |
| --- | --- | --- |
| Provider IA del profilo | Azioni IA esplicite, test e rilevamento modelli | Quelli della funzione, secondo le autorizzazioni |
| Web Search del provider | Solo sulle azioni scelte | Fino a 6.000 caratteri della richiesta |
| ollama.com Web Search | Solo sulle azioni scelte, con Ollama | Query di massimo 240 caratteri |
| Browser | Clic su una fonte | Normale navigazione |
| Download WebView2 | Setup, se manca il runtime | Nessun dato dell'utente |
