# 0005 — The token proxy is a tick on an agent instance, not a change to the machine

Date: 2026-09-22

## Context

[rtk](https://github.com/rtk-ai/rtk) ("Rust Token Killer") is a CLI proxy that filters the output of
common development commands — `git status`, `cargo test`, `npm run build` — before it reaches a model,
cutting 60–90% of the tokens they cost. It is the sort of thing this application exists to host: every
agent tile here is a CLI running shell commands and paying for their output.

Its own way in is `rtk init --global`, which **patches `~/.claude/settings.json`** to add a
`PreToolUse` hook. That is a file the user owns, and this application has a rule about those, paid for
several times over: the database skill stopped being a section in `CLAUDE.md` for exactly this reason,
`OpenCodeProviderConfig` writes a generated document rather than editing the user's own, and
`ClaudeSessionSettings` already hands Claude Code a `--settings` file instead of touching theirs.

Three routes were measured against rtk 0.46.0 on 2026-09-22, each probed with `rtk init` against a
sandboxed configuration directory:

| Agent | What rtk writes | Is there a per-run flag to carry it? |
|---|---|---|
| Claude Code | a `PreToolUse` hook in `settings.json` | **yes** — `--settings <file>`, which this application already passes |
| pi | `<PI_CODING_AGENT_DIR>/extensions/rtk.ts` | **yes** — `pi -e <path>`, printed by rtk's own output as the verification line |
| opencode | `~/.config/opencode/plugins/rtk.ts` | **no** |

rtk's `init --agent` also names cursor, windsurf, cline, kilocode, antigravity, kimi, hermes, droid and
vibe — so this is a vocabulary that will keep growing, and a flag-shaped answer would not have held it.

## Decision

**A `bool` on `AiAgentInstance`, and the agent says whether it has a route.**

`IAiAgent.OutputProxySupport` answers one of three things — `None`, `GeneratedFile`,
`WritesOutsideOurDirectories` — and `SessionDefaultArgs` grew an `AiAgentInstance` parameter so the
answer can depend on the row being launched. Claude Code answers `GeneratedFile` and is wired; pi
answers `None` *with the measurement written into its own class*, because its file would have to be
generated into a directory this application owns and that is a decision of its own; opencode answers
`WritesOutsideOurDirectories`, which is a refusal that names the route rather than pretending there
isn't one.

Three facts decide whether a ticked instance actually gets the proxy, and they are asked at the moment
the settings file is written rather than remembered:

1. the instance asked for it,
2. `rtk` is on this machine (`ExecutableFinder.Anywhere`, so a GUI process finds `~/.local/bin`),
3. Claude Code's own settings do **not** already carry an rtk hook.

## Consequences

**A tick changes this application's sessions and nothing else.** A Claude Code started from a shell is
untouched, which is the whole difference from `rtk init --global` — and it is also the cost: somebody
who wants it everywhere still runs rtk's own command, and then mTiles stands down (3) rather than
stacking a second hook on the same `Bash` call.

**Three enum members instead of a bool.** `None` and `WritesOutsideOurDirectories` both mean "no tick
on this row", so the distinction buys nothing at the call site. It buys the next reader the
measurement: without it, opencode reads as unmeasured and gets probed again.

**`SessionDefaultArgs` is no longer a question about the CLI alone.** Two call sites were changed —
`AiAgent.InteractiveArguments` and `ClaudeStreamSession` — so the terminal agent tile and the Agent
tile both carry it. **Goal runs do not**: `AiProcessRunner` never passed `--settings` at all, and
giving it one now would also hand every goal run the `Concise` output style, which its parser has never
seen. That is a separate change with its own risk, and it is deliberately not made here.

**Two settings files, not one rewritten per launch.** The path is what reaches the command line, and
two tiles on two instances are launched milliseconds apart from one process; one file rewritten per
launch is those two racing over a path they have both already been handed. `session-settings.json` and
`session-settings-rtk.json` cannot be raced, because the name says what is in it.

**The hook's shape is somebody else's contract** and is pinned in `OutputProxyTests` the way
`AiAgentTests` pins the flags, read as JSON rather than compared as a string. When rtk moves it, that
is a failing build rather than sessions that quietly stop being filtered.

**The check for an existing hook is a substring and not a parse.** A JSON walk looking for the exact
shape would answer *no* for a hook written by hand, spelled with an absolute path, or wrapped in a
shell — all of which are live. Being wrong towards "already there" costs one tick and says why on the
row; being wrong the other way is the double rewrite.

**Installing it is Windows-only for now.** winget names it `rtk-ai.rtk`. On Linux the published route
is a piped shell installer, and this application does not put `curl … | sh` behind a button — the
confirmation could not say what the user is approving. Those rows get the link instead, which is less
help and is the honest amount. `cargo install rtk` is never offered on any platform: crates.io carries
a *different* program under that exact name (Rust Type Kit), which then answers `rtk --version` and
fails every hook.
