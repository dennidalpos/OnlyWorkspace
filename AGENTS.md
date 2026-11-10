<!-- secondbrain:repository:begin -->
Direttive comuni applicate: revisione **1.0** — 2026-11-05. <!-- secondbrain:source global=1B867747278BC2981BC6AFADAC58B8394CCC30B2D829EA634F080FA6F03AB980 workstation=0D7C7667F4003AA5041CC43071CAED6B4745B351F4B1D296237DD3A6025C6F9E -->
## SecondBrain

Common directives and repository knowledge live in the SecondBrain dossier. Keep code, guides, and publishable assets here.

- Resolve the dossier by Git identity, the PARA index, and repository.json. Read SECONDBRAIN_HOME from the user environment. If the root is unreachable, report that the dossier is unavailable. Use the team's UNC root for shared projects.
- Load the slim profile with scripts/knowledge.ps1 -Startup, run from the SecondBrain checkout (it is not in this repository), or the MCP get_startup_context tool. Follow the returned references for more detail.
- At closure, verify acceptance criteria and update the central tracker. Move completed tasks to history and record only verified changes in context and memory.
- Commits, pushes, deletions, and publication require specific authorization.

### SecondBrain folders and sessions

- Keep product builds, caches, temporary files, fixtures, and evidence in the product's Git-ignored local folders. For SecondBrain activities, create work folders from the SecondBrain checkout with `scripts/work.ps1 -Repository <identity> -Category <work|evidence|report|fixture|tmp> -Slug <name> -Init -Task <description> -SessionId <id>`. This creates `.runtime/hosts/<host>/repos/<repo>/<category>/<yyyyMMdd>-<slug>/` with a MANIFEST.json recording owner, session, task, status, and expiration. Use this structure for every work folder.
- Each folder belongs to one workstation, user, and session. Write only to folders you own. On the team's shared root, share only dossiers and access/.
- For extended writes, call the SecondBrain checkout's scripts/access.ps1 -Start and -End in the same script using try/finally. Pass the same SessionId to SecondBrain CLIs, including work.ps1. If a session is busy, wait or switch activities. Preserve locks and incomplete transaction markers.
- At session end, move useful evidence or reports into dossier history/. Close the folder with the same command and -Close to update MANIFEST.json. Leave fixture and tmp folders to retention. They expire after seven days and the latest five are retained. Work folders expire 30 days after closure. Evidence and report folders are never removed automatically. Rebuild the index with the SecondBrain checkout's scripts/cleanup.ps1 -RuntimeReport.
- Cleanup and migration with the SecondBrain checkout's scripts/cleanup.ps1 -Migrate or -Regenerable start in preview. Apply them only on explicit request, on your own workstation, within a concrete scope, and while sessions are inactive.
- Preserve backups, journals, and official artifacts. Keep raw sensitive data in the local Git-ignored area.
<!-- secondbrain:repository:end -->
