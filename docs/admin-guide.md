# Administrator guide

## Installation and update

The managed COM add-in supports classic Outlook 2016+ x86/x64 on Windows x64 with .NET Framework 4.8 and WebView2 Evergreen. New Outlook is unsupported. Close Outlook before setup. Setup elevates, validates configuration, installs the public certificate in Root/TrustedPublisher, registers both RegAsm variants and sets Addins/OnlyWorkspace.Addin LoadBehavior=3. Root certificate installation is current behavior, not permission to trust arbitrary certificates.

Missing WebView2 is downloaded only after verifying its Microsoft signature and its registration is checked again. icacls/certutil/RegAsm failures stop setup with code 2; a stopped installation after file copying remains uninstallable.

| Setup option | Effect |
| --- | --- |
| /S | Silent; 0 success, 2 expected stop such as Outlook open. |
| /CONFIG=<file> | Imports provisioning which may contain plaintext secrets: protect/manage the source separately. |
| /BLOCKNEWOUTLOOK=0 | Skips the default New Outlook-switch policy component. |

Silent setup closes tray/ConfigTool and requests restart through Explorer; without an interactive desktop restart waits for logon. Interactive setup asks for closure and offers Configuration afterward.

Update tray/add-in together (pipe v15). Current code still migrates archive 11/12 to 13 only after verified encrypted database/key/SHA-256 backups. Unknown formats are refused. Rollback needs the matching old build and verified archive copy. A lifecycle flag does not remove compatibility code.

Interactive uninstall defaults to keeping ProgramData config/secrets and the current user's archive. Silent `/S /PURGE` requests removal of those files, unreadable configuration copies and the invoking user's archive. `/PURGE=1` preserves those data. Back up first and verify the affected folders afterward. In a synthetic NSIS test, files on long paths remained even though the process returned exit code 0. Other users' archives and Outlook mail are outside the purge scope. Uninstall removes only New Outlook policies created by setup and logs RegAsm/certificate removal failures. [Verification and limits](developer-guide.md#verification)

## ConfigTool, first run and recovery

Launch OnlyWorkspace.ConfigTool.exe or --gui for GUI/tray; --tray for tray only; a CLI command exits after its operation. The wizard covers data locations, AI model, archive/attachment index, Windows startup/add-in state and summary. Configure **Servizio → Indirizzo → Chiave → Modello**; model listing/connection tests use synthetic input, not mail. **Applica e salva** validates and elevates.

| Page | Controls |
| --- | --- |
| Profili IA | Endpoint/model/key/timeouts, context, exposure/attachment/web permissions, capability results/provenance. |
| Generale | Default profile, limits, OCR/indexing, history/password/startup and add-in repair. |
| Archivio email | Administrative permission and immediate user mailbox scope. |
| Manutenzione e backup | Config backup/restore, data folder/activity log. |
| Privacy e dati | Technical authorization, PDF, storage/registry/recipient overview. |
| Informazioni | Version, author, license and credits. |

**Ripara add-in** changes only HKCU LoadBehavior, disabled entries and DoNotDisableAddinList, then requires restart. It does not qualify all Office combinations.

Unreadable config/vault blocks save and offers restore, **Nuova configurazione** (first preserves files under the user's config-non-leggibile directory) or **Sola lettura**. Missing configuration is different from unreadable files and may offer local Ollama. Zero-byte vault is corrupt; an encrypted empty dictionary is valid. A missing vault revokes captured keys; unreadable vault loading fails without authorizing reuse through the failed configuration.

## Configuration and defaults

| File | Contents / protection |
| --- | --- |
| %ProgramData%/OnlyWorkspace/config.json | Schema 1 config; users read, administrators/SYSTEM write. |
| secrets.bin alongside config | DPAPI LocalMachine API-key vault; ACL protection remains necessary. |
| admin-password.json alongside config | Optional admin-password verifier, excluded from backup. |
| %APPDATA%/OnlyWorkspace/preferences.json | User wizard/archiveOff preferences. |

Pane reload follows messages; indexing rereads settings periodically. [WorkspaceConfig](../src/OnlyWorkspace.Core/Models/WorkspaceConfig.cs), [profile model](../src/OnlyWorkspace.Core/Models/LlmProfileConfig.cs), [validator](../src/OnlyWorkspace.Core/Validation/ConfigurationValidator.cs) and [template](../deployment/config.template.json) define accepted values.

| Field | Default / meaning |
| --- | --- |
| defaultProfile | ollama-lan; pane has no profile selector. |
| attachmentOcr | true; Windows OCR. Existing text remains when disabled. |
| limits.maxSearchItems | 50 calendar/contact results. |
| limits.followUpDays | 5, range 1–30. |
| limits.maxAnalysisEmails | 300 newest messages analyzed; extra matches declared. |
| history | Enabled; 90 days, 200 conversations. |
| archive.enabled | false in code, true in installed template; users may restrict/disable. |
| archive.indexAttachmentText | true; disabling removes indexed attachment text/derived facts, not originals. |
| dataEgress | No default authorization; technical grants/revocations. |
| logging.level | Information. |

## AI profiles and capability checks

Six new-profile presets: Ollama, Gemini, Anthropic, OpenAI, QwenCloud and OpenAI-compatible. Generic requires explicit endpoint/model and none/bearer authentication. Existing Azure/Perplexity/Ollama Cloud paths and preset conversion remain current code, separate from the open greenfield alignment.

baseUrl/authType/secretRef/model specify endpoint/authentication/vault/model. Remote requires HTTPS; HTTP only loopback. Non-loopback is external even with external:false. Redirects are refused: configure the final URL. allowAttachmentUpload additionally gates attachment text at Extended. Web settings and the separate Ollama web key do not grant arbitrary mail-derived searches.

First response timeout is 120 seconds, including HTTP error bodies; streaming idle timeout is 60 seconds. Cancellation stays active after headers. useSystemProxy selects the system proxy. Ollama numCtx/contextBudgetTokens/maxOutputTokens/keepAlive use explicit contexts (presets 8K–256K where model supports them). Budget must be positive and <= numCtx − maxOutputTokens − max(512,numCtx/10). The 3.5-characters/token estimate is not model tokenization. Overflow stops the request instead of truncating or switching providers. Cloud clients handle protocol-required output parameters/rejections without applying this local budget.

disableThinking defaults true for applicable Ollama/Qwen connectors; support depends on model. **Scegli modello…** lists models only. **Verifica connessione** makes a charged-or-free synthetic test. **Aggiorna capacità** reads published metadata; **Prova funzioni** sequentially tests text/JSON/reasoning/web with synthetic input, up to 60 seconds each and possible charges. Timeouts/rejected credentials are inconclusive. Vision/tools are metadata, not app workflows. Save persists capability results; endpoint/model/auth/key changes invalidate them.

Clearing a key removes it on Save; profile ID rename moves references/default; confirmed removal cleans unreferenced keys. Provider region/retention/contract and authenticated compatibility must be checked for the chosen service. The dated Italian provider survey is research, not a purchase recommendation.

## Authorization and data protection

Level 0 sends no external data; Minimal permits typed protected facts/opaque references; Standard masked free text; Extended additionally masked attachment text with distinct permission. Original binary files, names, StoreId/EntryId are not sent. Local requests mask recognized identifiers; optional local amount/date settings differ from mandatory cloud masking.

Notice 6, authorization key 3 and conversation policy 8 bind grants to profile, destination, declared EU/non-EU/unspecified region, permissions and credentials. The UI requests an extra unspecified-region confirmation; core enforcement is a tracked issue, so UI behavior alone is not a policy guarantee. Changed policy/credentials interrupt old work and start a fresh conversation; old history stays readable.

Catalog 2 recognizes currency symbols/codes EUR/USD/GBP, not **1234,56 euro**. Unrecognized sensitive information may remain in permitted free text. Failed masking/audit preparation blocks sending; failure after dispatch cannot retract receipt. Free-form action web queries remain blocked; capability checks use fixed synthetic requests.

Technical authorization is not legal basis, contract, impact assessment or a compliance certificate. Legal applicability/current primary sources must be assessed separately. The dated Italian regulatory discussion is preserved as source work; this guide does not extend it to another jurisdiction/date.

## Storage, CLI and backups

Archive is encrypted SQLite under %LOCALAPPDATA%/OnlyWorkspace/archive, key protected with CurrentUser DPAPI. History uses CurrentUser DPAPI; logs/WebView2 use user product folders. Scan copies use %TEMP%/OnlyWorkspace/scan and application cleanup. Diagnostics export system metadata/space measures, excluding configuration, logs, text, database and keys. Review exports before sharing.

LocalMachine vault is machine-bound, not by itself a barrier against another local user; protect ACLs. Archive/history are user-bound. Do not transplant a vault/key expecting access under another machine/user.

| CLI form after OnlyWorkspace.ConfigTool.exe | Effect |
| --- | --- |
| validate <config.json> | Validate without contacting providers. |
| show [config_path] | Masked configuration from named directory/file or common default. |
| test [config_path] | Contact configured providers. |
| import <source.json> [target_dir] [--skip-test] | Import/validate pair; tests connections unless skipped. |
| backup [dest.owbak] [config_dir] | Config and vault only. |
| restore <source.owbak> [config_dir] | Validated same-machine restore. |

Import moves inline auth.secret to the vault, rejects duplicate case-insensitive auth/profiles/secret properties and retains references when no inline secret is given. Import/restore recover ordinary second-file replacement failures; crash/power-loss atomicity is not guaranteed. Preserve any recovery copy named by an error. Backup excludes archive/history/preferences/admin verifier. Active admin password prompts for import/restore; it does not replace filesystem ACLs.

The documentation audit does not run live provider probes, signing, installation, repair or destructive maintenance. See [developer verification](developer-guide.md#verification).