## Git workflow (push policy)

- The repository owner is an admin and changes are verified locally before any push. When a push is authorized, push directly to `main` and do not open a pull request. This applies only to pushes the user has authorized; it does not authorize committing or pushing on its own. If anything is unclear (failed checks, unexpected remote state, protected-branch rejection), stop and ask.

<!-- secondbrain:begin -->
<!-- secondbrain:directive-version: v1-00304689082027f9 -->
## SecondBrain

The private context, decisions and tasks for linked repositories live in SecondBrain, outside their checkouts. Apply the repository workflow below when working in a linked checkout.

- At the start of a session run `sb context` (MCP tool `get_context`) and work from it.
- Run `sb start` to check recorded checkouts, refresh managed agent files and see which SecondBrain build is running.
- When asked to run `sb start` in chat, include the full result in the first reply after execution, even in a new session. Use bullet lists for the executable and PATH status, repository, context and tasks, instruction alignment, MCP configuration, health findings, all suggested next steps and every command shown. Put a brief description and an example beside each command and parameter. State when no issues are reported and distinguish MCP configuration from runtime verification.
- When asked to onboard a repository with missing or empty context, read `sb guide onboarding` or call `get_guide` with topic `onboarding`. Inspect the checkout and save the verified starting context in the private dossier.
- When asked to work through tasks in the current chat, read `sb guide chat` or call `get_guide` with topic `chat`.
- Track work with `sb task add`, `sb task list` and `sb task done` (MCP task tools).
- When more than one reasonable scenario exists, interview the user before acting, using the client's structured question tool (function calling or tool use, such as AskUserQuestion). Offer two or three concrete options with their trade-offs and a recommendation. Never choose silently.
- Do not drift from the stated intent on your own initiative. Before adding scope, changing approach or touching files or systems the request did not name, stop, ask and propose alternatives.
- Collect authorizations before a task starts, not during it, so unattended work is not interrupted: push or commit, network or installs, deleting or overwriting data, spending money, secrets or accounts, external services. Ask once, up front, and record the answer in the task handoff (`sb task prepare`) or in the chat.
- Without a structured question tool, ask in plain text with numbered options and wait. In an unattended run with nobody to answer, do not guess: mark the task `--needs-user` and record what is missing.
- Record durable decisions with `sb decide "..."` and keep the current state with `sb context --set`.
- Never copy dossier content into the repository.
<!-- secondbrain:end -->
