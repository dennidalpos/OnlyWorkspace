<!-- secondbrain:repository:begin -->
Common directives applied: revision **1.6** — 2026-11-20. <!-- secondbrain:source global=0A70848B8CBBE1939BDB240F37280AB3594914B41E4152F8543A554F04FEF0FF workstation=2B4198D3D9F66B2EC4AD159DC7889F6C21AD64C5E025B99005D26122D6E5AEDB -->
## SecondBrain

Repository knowledge lives in the SecondBrain dossier; code, guides, and publishable assets stay here. This block is generated: write project rules outside it, because a sync replaces everything inside.

- Load the slim startup with MCP get_startup_context or the startup mode of the SecondBrain knowledge tool, and follow its references for detail. If the checkout is unreachable, report the dossier as unavailable and continue from the repository files.
- Write only to this repository's dossier `repos/<repo>/`, only through SecondBrain scripts or MCP write tools. Work folders come from the work tool's init mode; sessions, retention, and cleanup rules load from `sb prompt`.
- Commits, pushes, deletions, and publication require specific authorization.
- Every task you create or update must run unattended. For an autonomous task, ask the user for what `needsInfo` or the readiness result reports missing (files in scope, checks to run, acceptance, inputs, decisions) with the client's structured question tool; skip it only when the user says otherwise, then set `execution` to `user_required`.
<!-- secondbrain:repository:end -->

## Git workflow (push policy)

- The repository owner is an admin and changes are verified locally before any push. When a push is authorized, push directly to `main` and do not open a pull request. This applies only to pushes the user has authorized; it does not authorize committing or pushing on its own. If anything is unclear (failed checks, unexpected remote state, protected-branch rejection), stop and ask.
