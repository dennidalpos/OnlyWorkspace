# Security Policy

## Supported versions

Only the latest commit on the `main` branch is supported.

## Reporting a vulnerability

Please do not open a public issue for security problems.

Use GitHub's private vulnerability reporting: open the **Security** tab of this repository and choose **Report a vulnerability**. Include the affected component (add-in, task pane, tray and archive, ConfigTool, AI provider integration or installer), reproduction steps and the impact you observed. Use synthetic data: do not attach real emails, credentials or unsanitized logs.

You can expect an acknowledgement within a few days. Confirmed issues are fixed on `main` and disclosed after a fix is available.

## Scope notes

- The archive is an encrypted local database whose key is protected with Windows DPAPI.
- Data leaves the PC only for external AI services configured by the administrator, according to the privacy level and the pseudonymization rules described in `docs/admin/sicurezza.md`.
- The signing certificate password and provider API keys must never be committed. `certs/OnlyApps.cer` is a public certificate.
- Dependencies are monitored with Dependabot (security updates, plus weekly version updates for NuGet and GitHub Actions configured in `.github/dependabot.yml`); secret scanning runs with push protection.
