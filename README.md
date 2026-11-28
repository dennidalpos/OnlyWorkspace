# OnlyWorkspace

AI assistance for your correspondence inside **classic Outlook**.

## Purpose

A managed COM add-in with a WebView2 task pane: summaries, reply drafts, search, case reconstruction and data extraction over your mail, backed by an encrypted local archive and by local AI models or external services chosen by the administrator. Nothing is sent automatically: you review and decide. It does not use Graph, IMAP or OAuth connectors, and the interface is in Italian.

## Requirements

- Windows x64, classic Outlook 2016 or later (not the new Outlook), .NET Framework 4.8 and WebView2 Evergreen (the setup can install it).
- A signed setup from your administrator: see [installation and update](docs/admin-guide.md#installation-and-update).

## First setup

1. Install the setup and open Outlook.
2. Open **Configurazione**: choose the AI service, model and default profile.
3. Reopen Outlook and use **OnlyWorkspace → Sintesi** on a selected email.

Archive collection starts after 60 seconds and runs when idle, so search is not complete immediately.

## Usage

Summarize an email or thread, prepare a reply to review, run a local security check, search by text, person and period, rebuild a case, or extract tables and tasks. Local Ollama stays on loopback. External services receive data only as the privacy level allows, after masking; pseudonymization can miss unrecognized data and does not guarantee anonymity.

## Build and test

.NET SDK 10 and PowerShell on Windows; the add-in targets `net48`.

```powershell
dotnet build OnlyWorkspace.sln -c Release --nologo
dotnet test tests/OnlyWorkspace.Tests -c Release --no-build -f net48 --nologo
dotnet test tests/OnlyWorkspace.Tests -c Release --no-build -f net10.0-windows --nologo
```

Run the two test runtimes in sequence. `pwsh scripts/build-installer.ps1 -Unsigned` (NSIS 3.13) builds an unsigned setup; see the [developer guide](docs/developer-guide.md#build-and-isolated-outputs) for signing and isolated outputs. The CI workflow has not completed a run yet: build and test locally.

## Documentation and security

[Guides index](docs/README.md) · [User](docs/user-guide.md) · [Administrator](docs/admin-guide.md) · [Developer](docs/developer-guide.md) · [Verification](docs/tecnico/verifica.md) · [Security policy](SECURITY.md) · [License](LICENSE) (Apache 2.0, see [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)).
