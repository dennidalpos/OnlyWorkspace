# Developer guide

## Architecture and source boundaries

Core contains models/privacy/facts; Infrastructure contains archive/config/security/transports; Outlook supplies COM/ribbon/WebView2 coordination; ConfigTool hosts configuration/tray/archive engine; AttachmentExtractor isolates document reading. Installed add-in targets net48; tests also target net10.0-windows. [Solution](../OnlyWorkspace.sln) and manifests define dependencies/versions.

Outlook Object Model stays on Outlook UI STA. Tray owns the encrypted archive, exposing SID-restricted local pipe v15; add-in/tray update together. WebView messages and mail/attachments are untrusted input. No Graph/IMAP/OAuth mail connectors exist.

The dispatcher mechanically masks input, checks exposure and seals a nonserializable request proof. HTTP clients revalidate immediately before transport. Minimal accepts embedded typed facts/opaque references; Standard/Extended masked free text. Altered messages/schemas or unmasked text are refused. Unknown/duplicate/missing/truncated fact references refuse responses. Restore values locally; no automatic provider fallback.

## Build and isolated outputs

Requirements: Windows, .NET SDK 10, PowerShell 7; Node/Python 3 for syntax gates. Packaging additionally needs NSIS 3.13, signtool and existing signing materials.

Set ONLYWORKSPACE_RUNTIME_ROOT to an existing absolute managed runtime directory. Directory.Build.props derives per-checkout ArtifactsPath using a 12-character SHA-256 key; explicit ArtifactsPath wins. No variable means ordinary SDK paths; invalid configured root fails. Old bin/obj remain excluded from source inputs.

    dotnet build OnlyWorkspace.sln -c Release --nologo
    dotnet test tests/OnlyWorkspace.Tests -c Release --no-build -f net48 --nologo
    dotnet test tests/OnlyWorkspace.Tests -c Release --no-build -f net10.0-windows --nologo
    pwsh -NoProfile -File scripts/test-scripts.ps1

Run runtimes/scripts sequentially on one output graph. NuGet caches are separate (NUGET_PACKAGES, NUGET_HTTP_CACHE_PATH, NUGET_PLUGINS_CACHE_PATH, NUGET_SCRATCH); inspect with dotnet nuget locals all --list. Do not change shared user settings implicitly.

scripts/build-paths.ps1 evaluates TargetPath/TargetDir/ProjectAssetsFile/ArtifactsPath through MSBuild. Harnesses resolve that graph, not stale publish folders. External candidate folders need their own extractor/ConfigTool. Managed test TEMP/TMP and artifact settings apply to verification processes; fixture evidence is retained. Environment variables alone do not redirect every third-party tool.

## Verification

    pwsh -NoProfile -File scripts/check-code.ps1

The gate builds Release with warnings as errors, SDK analyzer level 10 including net48, analyzer verification, selected PSScriptAnalyzer 1.25.0 correctness rules, JavaScript/embedded TaskPane syntax, Python in-memory compilation, JSON and XML checks. It does not lint every YAML/NSIS/SVG/CSS or qualify Outlook/security. Missing tools require explicit provisioning, not silent installation.

Run `test-scripts.ps1` after the build. `Packaging.Tests.ps1` compiles a synthetic fixture against the production NSIS macros. It uses short case directories under a unique temporary root, exercises quoted paths with spaces and checks the contents of files that should remain. The long-path fixture checks the original failure, preservation of a junction target and the direct uninstaller exit code when a file is locked. It uses the NSIS `_?=` mechanism to observe the uninstaller process. [Dated verification](tecnico/verifica.md)

Windows PowerShell 5.1 is needed for the net48 and WebView smoke tests. Loopback Ollama, external providers, signing, installation, registry repair and real Outlook tests have separate prerequisites and side effects. The maintainer currently disables GitHub Actions and uses local gates despite retaining CI source.

[Dated verification](tecnico/verifica.md) preserves past counts and installed-build receipts; they do not certify this checkout. Outlook bitness, Exchange/OST/shared stores, huge mailboxes, clean machines, DPI/screen reader, disk-full/power-loss and authenticated providers remain separate qualifications. High contrast is excluded by maintainer decision. UI inventories reference actual Italian labels rather than requesting app translation.

## Archive and local facts

The archive initializes directly in format 13. Older formats and unreadable DPAPI keys fail without overwriting originals. Missing derived indexes rebuild from retained text within format 13.

Per-folder EntryID identities preserve physical copies. Deleted Items proves presence; RSS/Junk/Drafts/Outbox/sync-problem folders are excluded. Complete PST inventory proves absence; cache GetItemFromID must return MAPI_E_NOT_FOUND, not generic error. Closed stores remain searchable. Retry starts at five minutes, doubles and stops after eight attempts until source changes.

Full encrypted body/search input is retained with 255-character preview; attachment text is SHA-256 deduplicated and reader-versioned. Facts retain provenance. On last-copy loss, dependent facts/text/unshared attachments/history are revoked; DPAPI ledger precedes deletion and cleanup resumes. Exports/backups remain separate. Query scope null/all, explicit/chosen and empty/none must stay distinct through expansion.

Reconstruction uses reply references/conversation/document references, never equal amounts/dates alone. Similar subject/sender remains tentative. Facts recover locally/resumably without a model; invitation and PEC extraction does not imply calendar ingestion/authenticity.

Limits: 3,000 folders/store, depth 12, 50-recipient/30-attachment-name metadata, pages of 50, 20,000 visible chain messages, 20 tentative links and latest 20 Outlook conversation messages with exclusions. Warn below 5 GiB; pause acquisition below 1 GiB. Compact requires about twice database size plus 1 GiB.

## Attachments

Extraction is a cancellable separate process, maximum 1 GiB memory. PDF/OOXML/binary Office/OpenDocument/RTF/MSG/EML/MIME/P7M support is defined by [reader code](../src/OnlyWorkspace.AttachmentExtractor/ContentReader.cs). Binary signatures matter: renaming an archive/executable to txt does not make it text.

Archive mode stores generic archives/executables only as references (name/extension/size/source), including excluded nested children. Direct/security extraction has a different generic-archive boundary. Original files are not archived/uploaded. Disabling text indexing removes extracted text/derived facts, not source mail and not upload-policy settings.

Windows OCR uses installed profile-language recognizers. OnlyWorkspace.AttachmentExtractor.exe --ocr-languages lists capability. PDF renders at 300 dpi; images honor EXIF/orientation/dimension limits. Each page needs 25 alphanumeric characters, otherwise the document stays unread. Disabled/unavailable OCR are distinct; enabling OCR/new recognizer requeues eligible items once. OCR can misread values: verification compares acquired text, not the image.

Limits: 30 MiB/file, four million extracted characters, 60 seconds extraction/file, 20 seconds macro-only, two minutes archived message, three minutes original retrieval, 60 seconds Defender/file. Password/error/unsupported/partial documents retain reasons rather than becoming fully read. Security mode statically reads scripts/pages/links/macros without execution/network. Failed Defender is not clean.

## Pseudonymization and policy

Catalog 2 uses reversible prioritized patterns, bounded regexes, cancellation and ten-second preparation budget. Identities use a literal index per request. Cloud always masks recognized amounts/dates; local options differ. Currency symbols/codes work; the word form **1234,56 euro** remains unsupported.

CloudFactsRequest keeps values/provenance local and sends embedded categories/opaque values. CloudRequestProof cannot be reconstructed from JSON/history. Audit metadata stores random ID/time/outcome/revisions/category counts with CurrentUser DPAPI, never originals/map. Preparation failure blocks sending; dispatching is an attempt, not a receipt. Policy changes prevent old-context reuse while keeping history readable.

## Packaging, dependencies and UI vocabulary

scripts/build-installer.ps1 [-SkipTests] [-OutputDirectory <new-directory>] builds, verifies unless skipped, stages/signs binaries/uninstaller, compiles/signs NSIS. Existing output directories are refused; failed/old packages stay. Use existing PFX/certificate and ONLYAPPS_PFX_PASSWORD environment; never command-line passwords or implicit certificate regeneration. Timestamp failure stops signing; AllowNoTimestamp is for explicit local tests only.

[Notices](../THIRD-PARTY-NOTICES.txt), [credits](../CREDITS.md) and manifests describe shipped libraries. Office interop types are embedded build references, not redistribution rights or proof of installed Office version. Windows SDK OCR/PDF metadata is build-only. Providers use HTTP rather than provider SDKs. Match actual restored/shipped assemblies to notices.

UI states derive from StatusLight: Ok/green check, Working/yellow animation, Error/red !, Paused/grey pause, Waiting/grey dots, Off/grey dash. ConfigTool warning maps to Paused; security outcomes use a separate vocabulary. Interrompi means stopping an operation; Annulla abandons a dialog/confirmation. Privacy/help detail must remain explicit. Exact Italian label inventories and dated screenshots remain original references, mapped in the [index](README.md).