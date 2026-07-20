# 2026-07-20 — Agent sandbox: tool tiers, path jail, destructive confirmation, audit

## What shipped

Sandbox for the agent's MCP tool calls, per the proposal approved in chat (4 layers, enforcement
server-side where possible):

- **`Contracts.Sandbox` (netstandard2.0, shared):** `SandboxPolicy` (built-in tiers for all 20 current
  tools — 14 read / 4 write / 2 destructive; overrides merge on top; unclassified → deny),
  `PathJail` (canonicalizes path args, rejects `..` escapes and UNC, allowlist roots),
  `SandboxConfig` (`%APPDATA%\PlcAiAssistant\sandbox.json`; missing/invalid → defaults; allowedRoots
  EXTEND the defaults `%LOCALAPPDATA%\PlcAiAssistant` + `Documents\Automation`),
  `SandboxAudit` (append-only JSONL per process, best-effort writes), `SandboxException`.
  `Contracts` gained a System.Text.Json 8.0.5 reference for this.
- **Server-side (Mcp.Engineering):** `EngineeringGuard` classifies every call and jails
  `outputDir`/`xmlFilePath`/`projectPath` BEFORE the adapter runs — holds for any MCP client, not
  just the chat agent. Denials return structured `SANDBOX_TOOL_UNKNOWN` / `SANDBOX_TOOL_DENIED` /
  `SANDBOX_PATH_DENIED` errors through the existing `ToolJson.Fail` envelope. Startup logs the
  effective policy to stderr.
- **Agent-side (Agent.Chat):** `AgentSandbox` gates each model-requested call in `AgentLoop` —
  unknown/denied blocked, destructive tools need user confirmation (`Allow once` / `Allow for
  session` / `Deny`) within a per-session budget (default 20, `maxDestructiveCallsPerSession`).
  Blocked calls go back to the model as structured error payloads (`SANDBOX_USER_DENIED`,
  `SANDBOX_BUDGET_EXCEEDED`, `SANDBOX_NO_CONFIRMATION`) so it can recover.
- **App:** modal `ConfirmToolDialog` (owner = main window, Deny is the default/Esc answer);
  chat logs every approval decision and shows the active sandbox config on startup.
- **Audit:** `%LOCALAPPDATA%\PlcAiAssistant\audit\agent.jsonl` + `engineering.jsonl` — every
  decision with tier, decision code, and truncated args.
- **Seeded** `%APPDATA%\PlcAiAssistant\sandbox.json` with the repo `exported\` dir in
  `allowedRoots` so the `scripts/e2e-*.json` Inspector scripts keep passing (they export outside
  the default roots).

## Proof

- **173/173 tests green** (36 Contracts + 43 Agent + 8 App + 86 Knowledge), run in Release because
  the previous app instance (pid 30548) and its MCP children held the Debug outputs locked.
  New coverage: tier classification incl. fail-closed unknowns and typo-tiers, path jail (inside/
  outside/root-exact/sibling-prefix/UNC/traversal), config load (missing/invalid/overrides/budget
  clamp), audit JSONL shape + failure tolerance, and full loop-level round trips: deny-by-user,
  allow-once, allow-session (asked once), budget stop, no-confirmation host, unknown tool,
  deny tier, audit trail.
- **Server smoke test:** `Mcp.Engineering.exe` starts, logs
  `sandbox: 20 tools classified from ...sandbox.json; roots: ...PlcAiAssistant\; ...Automation\; ...exported\`.

## Notes for next session

- The running app instance still has the pre-sandbox binaries — close it and rebuild Debug to pick
  the sandbox up (Debug build was blocked by the lock; Release is green).
- Remaining live E2E: re-run one `scripts/e2e-*.json` Inspector script (path jail active) and one
  real chat prompt that triggers `save_project` (dialog flow).
- Knowledge server paths (dbPath, exportRoot) are NOT jailed yet — engineering only, per the
  approved scope. `query` executes arbitrary SQL against the local knowledge db by design;
  consider a read-only enforcement there when the knowledge MCP grows write tools.
- OS-level isolation (service account / job objects) deliberately deferred: TIA Openness needs the
  engineer's own session and group membership.
