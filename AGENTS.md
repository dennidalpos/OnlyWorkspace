<!-- secondbrain:repository:begin -->
Common directives applied: revision **1.3** — 2026-11-13. <!-- secondbrain:source global=6B4E10F48D98505DB4ABF6C08AD8BB01229A0D33FF844A2C91781D63F291B480 workstation=BD2E210B49B3B77000F570E6F6191DC694798722B50900E6C3864E3DBDD78FF7 -->
## SecondBrain

Repository knowledge lives in the SecondBrain dossier; code, guides, and publishable assets stay here.

- Load the slim startup with the SecondBrain checkout's scripts/knowledge.ps1 -Startup or MCP get_startup_context, and follow its references for detail. If the root is unreachable, report the dossier as unavailable.
- Write only to this repository's dossier `01-projects/<repo>/`, only through SecondBrain scripts or MCP write tools. Work folders come from scripts/work.ps1 -Init; sessions, retention, and cleanup rules load from `sb prompt`.
- Commits, pushes, deletions, and publication require specific authorization.
<!-- secondbrain:repository:end -->

## Git workflow (push policy)

- The repository owner is an admin and changes are verified locally before any push. When a push is authorized, push directly to `main` and do not open a pull request. This applies only to pushes the user has authorized; it does not authorize committing or pushing on its own. If anything is unclear (failed checks, unexpected remote state, protected-branch rejection), stop and ask.
