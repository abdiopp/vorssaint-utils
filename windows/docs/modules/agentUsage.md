# AI agent usage (`agentUsage` module)

Spec: `docs/specs/07-shelf-media-input-agents-island.md` §3.8, §4.8, §5.8, §6.8, §7.8, §8.
Feature: `agentUsage` (no enable key in the catalog: installed = running).

> Namespaces are `Rivet.Core.Agents`, `Rivet.App.Features.Agents`,
> `Rivet.Platform.Windows.Agents`, `Rivet.Platform.Fake.Agents` (folders are
> still `AgentUsage/`). A namespace named `AgentUsage` under `Rivet.Core` or
> `Rivet.App.Features` would shadow the `FeatureIds.AgentUsage` constant that
> `FeatureCatalog.cs` (and any `using static FeatureIds` code) refers to.

## Where things live

| Path | Contents |
|---|---|
| `src/Rivet.Core/AgentUsage/` | Models, settings, the shared number/time rules (`AgentJson`), price list parsing + validation + lookup + cost math (`AgentPricing`), the bundled price table (`BundledAgentPrices.cs`, generated once from the macOS `Resources/agent-prices.json`; regenerate by replacing the raw string with that file's contents), the parsers (`ClaudeTranscriptParser`, `CodexRolloutParser`, `CopilotEventParser`), the incremental JSONL reader (`LogCursorReader`), a minimal SQLite binding and `OpenCodeReader`, the store (records, limits, plans, turns), `LimitAlerts`, `ClaudeLimits` (desktop-app history + 5-hour estimate), summaries, the archive, the price downloader and `AgentUsageEngine` |
| `src/Rivet.App/Features/AgentUsage/` | `AgentUsageModule`, `AgentUsageService` (feature controller, notifications), the "AI Agents" panel tab (`AgentUsageSection` / `AgentUsageCards`) and the Settings page |
| `src/Rivet.Platform.Windows/AgentUsage/` | `WindowsAgentUsagePlatform` (NTFS file ids, process liveness with pid-reuse guard, `QueryUnbiasedInterruptTime`) |
| `src/Rivet.Platform.Fake/AgentUsage/` | `FakeAgentUsagePlatform` (a sandboxed "home" with synthetic sample logs — the dev build never reads the real home folder) |
| `tests/Rivet.Core.Tests/AgentUsage/`, `tests/Rivet.App.Tests/AgentUsage/` | Synthetic fixtures for every agent, pricing anchors, archive bit-flip test, resume-equals-fresh-read, engine end-to-end, snapshot tests |

## Implemented

**Lifecycle.** While installed, `AgentUsageService` runs the engine on a
background worker: discovery → resume from the archive (same build and agent
set only) → read every log (cancellable) → transitions enabled (history never
notifies) → file watchers, a 2 s poll of recent logs (agents keep logs open)
and a 30 s housekeeping tick → snapshots debounced by 1 s. Uninstalling stops
it and deletes the archive; changing the agent set or a log folder restarts it
with a fresh read. Only counters, model names, times, session ids and folder
names are kept; prompts, replies and tool output are never stored.

**Log locations** (each overridable in Settings; environment variables honored):

| Agent | Default on Windows | Override variable |
|---|---|---|
| Claude Code | `%USERPROFILE%\.claude\projects\**\*.jsonl` and `%USERPROFILE%\.config\claude\projects` (subagents in `<session>\subagents\`) | `CLAUDE_CONFIG_DIR` |
| Claude Code account / sessions | `%USERPROFILE%\.claude.json`; `%USERPROFILE%\.claude\sessions\<pid>.json` | `CLAUDE_CONFIG_DIR` |
| Claude desktop limits | `%APPDATA%\Claude\plan-usage-history.json`, then `%LOCALAPPDATA%\Packages\*Claude*\LocalCache\Roaming\Claude\…` | — |
| Codex | `%USERPROFILE%\.codex\sessions\**\*.jsonl`, `…\archived_sessions\**` | `CODEX_HOME` |
| OpenCode | `%XDG_DATA_HOME%\opencode\opencode.db`, `%USERPROFILE%\.local\share\opencode\opencode.db`, `%LOCALAPPDATA%\opencode\opencode.db` (first found) | `XDG_DATA_HOME` |
| GitHub Copilot CLI | `%USERPROFILE%\.copilot\session-state\<id>\events.jsonl` (exactly one level) | `COPILOT_HOME` |

Logs modified within 91 days are read; main logs first, then Claude subagent
logs, each by modification time. Files are opened with full sharing; paths
compare case-insensitively and project names split on both separators.

**Reading.** 4 MiB chunks split on `\n`; a trailing partial line waits for its
newline; lines over 32 MiB are skipped; a file whose identity changed, shrank,
or was rewritten in place (FNV-1a-64 fingerprint over the offset and the
first/last 4 KiB before it) is read again from the start; duplicate keys merge
by per-category maximum, so rereads are idempotent.

**Parsers** (spec §3.8.5): Claude (byte pre-filter + verified `type`, usage
incl. 1-hour cache writes, fast, US-only, web searches, dedup key, turn
open/close/interrupt rules, Bash running commands, sidechain and subagent
rules, worktree project names); Codex (64-byte type sniff so quoted records
are ignored, `token_usage_record` mapping input/cached/written, legacy
`total_token_usage` fallback, `turn_context` model/tier, `thread_settings`,
rate limits → windows by length, main bucket only, individual limit, plan,
task start/complete/abort with Codex's own duration, side threads); OpenCode
(SQLite read-only, 2 s busy timeout, JSON extracted in SQL when JSON1 is
available, rowid-incremental with a binary search for the 91-day start, open
replies re-queried until complete, 64-row tail revert detection, quiet-prompt
turn rules, settle → silent end after 30 s, subagent sessions roll into the
parent, reported cost kept for unknown models); Copilot (strict envelope:
unique top-level keys ≤ 256 bytes, depth ≤ 128, one complete object; activity
records per API call; final-response + matching turnId ends a turn; abort;
shutdown metrics count growth only and reconcile requests with counted activity).

**Turns and the "finished" notice** (§3.8.6): replay guard 1 s, usage counted
from turn start − 1 s, idle 10 min → waiting, waiting expires after 1 h
(Claude) / 6 h (others), a waiting turn that ends still notifies with its
whole duration; notifications only for completed turns ending within 5 min,
for enabled agents, when "When a task finishes" is on and the task is at
least the chosen length (default 60 s). Claude session registry: records for
another `pidDomain` skipped, dead process (pid-reuse guarded by the record's
time) or vanished record → silent end; at launch a turn without a record ends
when the folder exists. Offline: 20 s of awake time without a network ends
Claude turns unless the model replied since the drop or a Bash command runs.
After a silent end the file's parser forgets its open turn, so the next prompt
opens a new one.

**Plans and limits** (§3.8.7): Claude plan from `.claude.json` `oauthAccount`
(substring match on tier + type, first wins; unknown types prettified); Claude
limits from the desktop app's history (org filter, ≤ 5 min future, < 7 days,
session window dropped after 5 h, session renewal from the run of non-zero
readings dated by Claude Code's first request, weekly renewal on the next UTC
hour after the last drop, windows omitted when renewed or undated and a day
old); otherwise the 5-hour block estimate from Claude Code activity; Codex
limits newest-wins and plan from `plan_type`. Warnings at the threshold
(default 80 %) never on the first reading, on crossing or on renewal above;
"Limit renewed" for warned windows whose renewal passed or that renewed
below the threshold; daily budget once per local day (already over at start
→ silently marked).

**Prices** (§3.8.9, §6.8): bundled list (schema 1, 2026-10-02, 24 Claude +
73 Codex models) validated by the same code as downloads; the newer list by
`updated` wins, a download wins a tie; daily download when "Keep prices up to
date" is on (cache ≥ 24 h old, last attempt ≥ 24 h ago or ≥ 6 h after a
failure), no cookies/cache, 15 s per request, 256 KiB cap, redirects only to
https on the same host; a new list reprices every record. Lookup: normalization
(cut before `claude-`, `.`→`-` for Claude, last `/`, cut at `@`/`[`), word-
boundary families (`-`/`_`/`:` then no digits or a 4+ digit date), longest
wins, sibling words unpriced, unknown versions unpriced. Cost math and every
spec anchor are tested.

**Summaries** (§3.8.10): Today / 7 days / 30 days in local calendar days, 24
hourly and 91 daily buckets (future records excluded), per-agent totals,
models (`<agent>:<display name>`), projects, sorting by cost when everything is
priced else by tokens, heatmap quartile levels, active days, busiest day,
streak; display names and number formats per spec. Recomputed from scratch on
every publish, so it always equals a full recompute.

**Archive** (§3.8.11): `%LOCALAPPDATA%\<app>\cache\AgentUsage\agent-usage.bin`;
resume rules ported exactly (cursor kept only if discovered, same identity
and fingerprint up to its offset; records touching a gone file are dropped and
their other files reread from the start; everything repriced) — the
resume-equals-fresh-read test covers growth, a deleted log, a log rewritten in
place and a record shared between files.

**UI.** Panel tab "AI Agents" (Order 100, feature `agentUsage`): Limits (per
agent: session + most-used window, countdown, left/used, pace tick, orange ≥
80 %, red ≥ 95 %, dimmed stale Claude-app readings, Claude estimate, OpenCode
day line, Codex "Limits appear after the next reply"), Spending (period
chips, "≥" when partly unpriced, plan multiples for 30 days, agent split bar,
tokens/cache rate, savings tooltip), Now (live turns with a 1 s stopwatch,
"+N", or last activity), Trend, Models, Projects, Activity heatmap. Card order
(`notchAgentsCardOrder`) and visibility (`notchAgentsHiddenCards`) are
honored. Settings › Tools › AI Agents: per-agent switches (the last one stays
on) with Found/Not found and an overridable logs folder, alerts (finish +
minimum, near-limit + threshold, daily budget), display (left/used, card
switches), Claude plan-limit status (fresh / stale / no app with "Get the
Claude app"), price updates with the list date. Notifications use
`INotificationService` (Windows toasts; clicking opens the tab via
`agentUsage.show`).

## Not implemented / deviations
- **Codex banked resets ("Resets" card)**: not implemented. It talks to the private `account/rateLimits/read` and `…/consume` methods of `codex app-server`, which call OpenAI's servers with the user's sign-in and can spend a paid credit — not local-only, so left out per the brief.
- Island-only surfaces (closed-island strip, resting "AI limits" ring, mascot reactions, live strip preview) — no island on Windows; the data shows in the panel tab and toasts instead.
- Pause/resume on display sleep or lock is not implemented (the reader keeps its cheap timers).
- Card reordering has no drag UI (the order setting is honored; Settings offers show/hide only).
- Archive format: a JSON payload (ISO-8601 dates) in a checksummed binary envelope (`RAUA`, format byte, length, FNV-1a-64) instead of the macOS string-table format. It is not shared across platforms, which the spec allows.
- File identity off Windows (dev build) is the creation time; on Windows the NTFS file id.
- OpenCode: the `tool_calls`/`auto_compaction` flags are an approximation of the macOS SQL (tool part not provider-executed and not errored; compaction part with `auto`), and are unavailable without SQLite's JSON functions. The older JSON `storage/` folder is not read (same as macOS).
- Copilot premium requests are not counted (same as macOS); VS Code / JetBrains / Visual Studio Copilot are not read.
- WSL installs (`\\wsl.localhost\<distro>\home\…`) are not scanned (a logs-folder override can point at one).
- The burn rate (computed but never shown on macOS) is not computed.

## Unverified Windows assumptions (risks)
- Whether Claude Code writes `~/.claude/sessions/<pid>.json` on Windows and its `pidDomain` value (`win32` assumed; records with another domain are skipped). If the folder does not exist, the registry rules are skipped entirely.
- Whether the Claude desktop app writes `plan-usage-history.json` on Windows at all, and its MSIX path; without it the 5-hour estimate is shown (the spec's recommended default).
- OpenCode's Windows data folder (`%USERPROFILE%\.local\share\opencode` assumed, `%LOCALAPPDATA%\opencode` tried too) and its schema details.
- Copilot CLI's Windows folder (`%USERPROFILE%\.copilot\session-state`).
- `winsqlite3.dll` (Windows 10/11 System32) is used for OpenCode when no bundled SQLite exists; whether it includes the JSON functions depends on the Windows build (the reader detects and falls back).
- Log formats are undocumented and drift: readers skip what they do not understand.

## Windows manual test checklist
Use test machines or test accounts; never upload anyone's real logs.

1. Install the feature; open the tray panel → "AI Agents" tab shows "Reading usage…" then cards (or the empty-state text when no agent has logs).
2. With Claude Code installed: run a short task. The Claude card shows the 5-hour estimate (or limits when the Claude app is running), "Now" shows the project with a running stopwatch; when the reply finishes (task ≥ 60 s) a toast "Claude finished · <project> · 4m 12s · $x" appears; shorter tasks don't toast unless "For tasks longer than" is "Any length".
3. Codex: run a prompt → Codex card shows Session/Week windows with countdowns and the plan chip; usage appears in Spending/Models/Projects.
4. OpenCode (if installed): its card shows today's cost/tokens/cache rate; Settings shows "Found on this PC" (or the SQLite note).
5. Copilot CLI: run a session → counted in Spending/Models (activity and shutdown totals), no limits card.
6. Settings › AI Agents: switch an agent off → its data disappears after the restart of the reader; the last remaining switch cannot be turned off. Set a custom logs folder (e.g. a copy of synthetic logs) → it is read; clear it → back to the default.
7. Alerts: set "Warn at" to 50 % and use Codex past 50 % → toast "Codex · Session" "x% left" once; set a $5 daily budget and exceed it → one "Daily budget" toast per day. Turn notifications off for the app in Windows Settings → the warning note appears in the page.
8. Period chips (Today / 7 days / 30 days) change Spending, Trend, Models and Projects; "Show limits as" Used/Left flips the percentages; hiding cards in Settings removes them from the tab.
9. Restart the app: data reappears quickly (archive) and matches what was shown before; delete a Claude log between runs → its usage disappears.
10. Disconnect the network while Claude Code works → after ~20 s the Claude task leaves "Now" without a toast.
11. Uninstall the feature in the Features hub → reader stops, `cache\AgentUsage\agent-usage.bin` is deleted.
12. Price list: with "Keep prices up to date" on, after a day `agent-prices.json` appears in the app's local data folder and "Price list of …" shows its date (requires the list to exist at `https://raw.githubusercontent.com/<update repository>/main/Resources/agent-prices.json`).

## Requests for shared code
1. **Price list source**: confirm the URL. The downloader uses `https://raw.githubusercontent.com/{AppIdentity.UpdateRepository}/main/Resources/agent-prices.json` (falling back to `vorssaint/vorssaint-utils`); the fork must keep `Resources/agent-prices.json` from the macOS tree or this needs a constant.
2. **SQLite**: optionally reference `Microsoft.Data.Sqlite`/`SQLitePCLRaw.bundle_e_sqlite3` (already in `Directory.Packages.props`) from `Rivet.Core` so a SQLite with JSON1 is guaranteed on every Windows build; the binding here already prefers `e_sqlite3` when present and otherwise uses `winsqlite3.dll`.
3. **Session/power events** (same request as the input module): pause the reader while the session is locked or the display is off, resume afterwards.
4. **Panel tab deep link**: `IAppShell.ShowPanel(sectionId)` is used by the toast click action `agentUsage.show`; please make sure section ids are honored (or tell me the expected id).
