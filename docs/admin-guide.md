# Administrator guide

## Installation and update

The managed COM add-in supports classic Outlook 2016+ x86/x64 on Windows x64 with .NET Framework 4.8 and WebView2 Evergreen. New Outlook is unsupported. Close Outlook before setup. Setup elevates, validates configuration, installs the public certificate in TrustedPublisher only (an earlier Root entry is removed), registers both RegAsm variants and sets Addins/OnlyWorkspace.Addin LoadBehavior=3. Trusting the public certificate does not extend to any other certificate.

Missing WebView2 is downloaded only after verifying its Microsoft signature and its registration is checked again. Configuration is validated before the program files are deployed. Certificate or RegAsm failures stop setup with code 2 and leave the registered uninstaller available.

| Setup option | Effect |
| --- | --- |
| /S | Silent; 0 success, 2 expected stop such as Outlook open. |
| /CONFIG=<file> | Imports provisioning which may contain plaintext secrets: protect/manage the source separately. |
| /BLOCKNEWOUTLOOK=0 | Skips the default New Outlook-switch policy component. |

Silent setup closes tray/ConfigTool and requests restart through Explorer; without an interactive desktop restart waits for logon. Interactive setup asks for closure and offers Configuration afterward.

Choose a new or empty folder on first installation or when moving from an older setup that has no file inventory. The earlier folder remains in place. For silent setup, specify the new folder with `/D=C:\Path\OnlyWorkspace` as the final argument, without enclosing it in quotes. The new setup stores its file inventory in the machine uninstall registry key. Later updates use the registered directory, remove retired package files and stop if a new package file would overwrite an unlisted file. A failed deployment restores the previous files if the rollback succeeds.

After registering the new installation, setup checks that the old registry entry points to `Uninstall.exe` in the previous directory and verifies its OnlyWorkspace metadata and OnlyApps signature. It renames that file to `Uninstall.exe.disabled` and preserves the other files in the old folder. If the file is locked, verification fails, or the new name is already taken, setup reports exit code 3 and keeps the current Apps & Features entry available. Rerun setup after resolving the reported issue. Use that current entry to uninstall.

Uninstall removes the inventoried program files and the OnlyWorkspace Start menu shortcut. It preserves other files and refuses redirected program paths. Incomplete program or shortcut cleanup is reported with exit code 3 by the running uninstaller. The uninstall entry is kept if program file removal fails.

Update tray/add-in together (pipe v16). New archives use format 13. Earlier formats, including 11 and 12, are refused with an explicit error and preserved for recovery. Use the matching build and a verified archive copy for recovery.

Interactive uninstall defaults to keeping ProgramData config/secrets and the current user's archive. Silent `/S /PURGE` requests removal of those files, unreadable configuration copies and the invoking user's archive. `/PURGE=1` preserves those data. Back up first and verify the affected folders afterward. Purge uses extended Windows paths and removes directory junctions as links, preserving their targets. If removal is incomplete, the running uninstaller returns exit code 3. The standard NSIS launcher returns its own launch status. Other users' archives and Outlook mail are outside the purge scope. Uninstall removes only New Outlook policies created by setup and logs RegAsm/certificate removal failures. [Verification and limits](developer-guide.md#verification)

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
| secrets.bin in each user's %LOCALAPPDATA%\OnlyWorkspace | Per-user DPAPI CurrentUser API-key vault, never written elevated; another standard user cannot decrypt it. A custom config folder keeps both files together. The LocalMachine secrets.bin of earlier builds in ProgramData is no longer read: re-enter the keys and delete the file. |
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

Six new-profile presets: Ollama, Gemini, Anthropic, OpenAI, QwenCloud and OpenAI-compatible. Generic requires explicit endpoint/model and none/bearer authentication. Azure OpenAI, Perplexity Sonar and Ollama Cloud stay available only to profiles that already use them; retired types (LM Studio, llama.cpp, vLLM, LocalAI, Jan, Mistral, xAI, DeepSeek, OpenRouter, Groq, Together, Fireworks) are converted to the generic preset in memory and persisted on the next save, import or restore. For the generic preset, **Preset cloud** fills the address of Mistral AI, IONOS, OVHcloud, Scaleway or STACKIT.

baseUrl/authType/secretRef/model specify endpoint/authentication/vault/model. Remote requires HTTPS; HTTP only loopback. Non-loopback is external even with external:false. Redirects are refused: configure the final URL. allowAttachmentUpload additionally gates attachment text at Extended. Web settings and the separate Ollama web key do not grant arbitrary mail-derived searches.

First response timeout is 120 seconds, including HTTP error bodies; streaming idle timeout is 60 seconds. Cancellation stays active after headers. useSystemProxy selects the system proxy. Ollama numCtx/contextBudgetTokens/maxOutputTokens/keepAlive use explicit contexts (presets 8K–256K where model supports them). Budget must be positive and <= numCtx − maxOutputTokens − max(512,numCtx/10). The 3.5-characters/token estimate is not model tokenization. Overflow stops the request instead of truncating or switching providers. Cloud clients handle protocol-required output parameters/rejections without applying this local budget.

disableThinking defaults true for applicable Ollama/Qwen connectors; support depends on model. **Scegli modello…** lists models only. **Diagnosi** (one button) checks the connection, reads published metadata, then sequentially tests text/JSON/reasoning/web with synthetic input, up to 60 seconds each and possible charges. Timeouts/rejected credentials are inconclusive. Vision/tools are metadata, not app workflows. Save persists capability results; endpoint/model/auth/key changes invalidate them. Such a change also clears **Ricerca web attiva**, and a note under the web actions gives the reason; tick it again before Save if the new model supports it.

Clearing a key removes it on Save; profile ID rename moves references/default; confirmed removal cleans unreferenced keys. Provider region/retention/contract and authenticated compatibility must be checked for the chosen service. The dated Italian provider survey is research, not a purchase recommendation.

## Authorization and data protection

Level 0 sends no external data; Minimal permits typed protected facts/opaque references; Standard masked free text; Extended additionally masked attachment text with distinct permission. Original binary files, names, StoreId/EntryId are not sent. Local requests mask recognized identifiers; optional local amount/date settings differ from mandatory cloud masking.

Notice 8, authorization key 4 and conversation policy 8 bind grants per service: a grant covers every profile and model of the same service with the same destination, declared EU/non-EU/unspecified region, permissions and credentials, until revoked or a new notice version. When issuing a new grant, the authorization engine requires explicit confirmation for each cloud profile whose service region is unspecified. The acceptance history and its report list the confirmed profile IDs. Changed policy or credentials interrupt old work and start a fresh conversation. Old history stays readable.

Masking catalog revision 4 recognizes currency symbols/codes EUR/USD/GBP and the whole word **euro** before or after a number (**1234,56 euro**, since revision 3), and adds Italian postal addresses (`INDIRIZZO`); company names in free text are not masked. Moving from revision 3 requires renewing the affected profile authorizations in **Privacy e dati**. Unrecognized sensitive information may remain in permitted free text. Failed masking/audit preparation blocks sending; failure after dispatch cannot retract receipt. [Recognized forms and limits](tecnico/pseudonimizzazione.md)

Web search during actions follows the level: at 0, Minimal and Standard no action searches the web. At Extended, a provider that searches by itself receives the whole request, already pseudonymized, as the search input (last 6,000 characters). Queries generated by Ollama stay blocked. ConfigTool capability checks use fixed synthetic requests. [Details](utente/modelli-web.md#ricerca-web)

Technical authorization is not legal basis, contract, impact assessment or a compliance certificate. Legal applicability/current primary sources must be assessed separately. The dated Italian regulatory discussion is preserved as source work; this guide does not extend it to another jurisdiction/date.

## Storage, CLI and backups

Archive is encrypted SQLite under %LOCALAPPDATA%/OnlyWorkspace/archive, key protected with CurrentUser DPAPI. History uses CurrentUser DPAPI; logs/WebView2 use user product folders. Scan copies use %TEMP%/OnlyWorkspace/scan and application cleanup. Diagnostics export system metadata/space measures, excluding configuration, logs, text, database and keys. Review exports before sharing.

The vault, archive and history are user-bound (DPAPI CurrentUser). Do not transplant a vault/key expecting access under another machine/user.

| CLI form after OnlyWorkspace.ConfigTool.exe | Effect |
| --- | --- |
| validate <config.json> | Validate without contacting providers. |
| show [config_path] | Masked configuration from named directory/file or common default. |
| test [config_path] | Contact configured providers. |
| import <source.json> [target_dir] [--skip-test] | Import/validate pair; tests connections unless skipped. |
| backup [dest.owbak] [config_dir] | config.json; secrets.bin only for a custom config folder (with the standard folder each user's keys stay out and are entered again after restore). |
| restore <source.owbak> [config_dir] | Validated same-machine restore. |

Import moves inline auth.secret to the vault, rejects duplicate case-insensitive auth/profiles/secret properties and retains references when no inline secret is given. Import/restore recover ordinary second-file replacement failures; crash/power-loss atomicity is not guaranteed. Preserve any recovery copy named by an error. Backup excludes archive/history/preferences/admin verifier. Active admin password prompts for import/restore; it does not replace filesystem ACLs.

The documentation audit does not run live provider probes, signing, installation, repair or destructive maintenance. See [developer verification](developer-guide.md#verification).
