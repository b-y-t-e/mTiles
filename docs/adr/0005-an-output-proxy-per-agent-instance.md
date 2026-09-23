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

**Installing it is Windows-only, and only where winget can be found.** winget names it `rtk-ai.rtk`. On Linux the published route
is a piped shell installer, and this application does not put `curl … | sh` behind a button — the
confirmation could not say what the user is approving. Those rows get the link instead, which is less
help and is the honest amount. `cargo install rtk` is never offered on any platform: crates.io carries
a *different* program under that exact name (Rust Type Kit), which then answers `rtk --version` and
fails every hook.

## Amendment, 2026-09-23 — the install runs in the background, and winget had to be found first

Two things came out of the first machine this was used on, and both were reported as one symptom:
`winget: command not found` in a terminal tile.

**winget was not on the PATH of any process.** `%LOCALAPPDATA%\Microsoft\WindowsApps` — where Windows
keeps its app-execution aliases — was absent from the PATH of the GUI, of PowerShell and of Git Bash
alike, while the alias itself sat there pointing at a perfectly good binary. `ExecutableFinder` did not
look there either, so the plan fell back to the bare name. Two corrections: that directory joins the
five developer-tool directories `ExecutableFinder.InHomeDirectories` already walks — it is exactly the
category that list exists for, a place installs put binaries that PATH may not carry — and
`OutputProxy.Plan` became a **per-call question** that answers `null` where winget cannot be found, so
the row shows the link rather than a button whose command is certain to fail.

**And the tile was the wrong shape for an install.** Even found, the binary would not have been used:
`IShellTerminal.Program(name, path)` hands the resolved path to PowerShell alone and every other shell
keeps the *name*, which is right for per-directory shims (mise, nvm, volta) and wrong for an installer.
Rather than weaken that rule, installs left the shell entirely: `BackgroundInstaller` starts the plan's
argv as a process of this application's own, with `ArgumentList` and no shell, so nothing is quoted,
nothing is parsed twice, and a profile directory with a space in it is not a special case.

**What that costs, and what was done about each part.** The tile was carrying three things for free.
*Visibility of what writes outside our directories* — the confirmation stays, and is now the only place
the command is read, which is a reason for it to be more careful rather than less. *A place for
interactive questions* — gone, so the plan answers them up front (`--accept-source-agreements`,
`--accept-package-agreements`, `--disable-interactivity`) and `BackgroundInstaller.Timeout` kills what
still hangs, naming the likely cause. *The installer's own output as the only diagnostic* — captured
from both streams, logged whole, and the last few lines carried into the failure dialog.

**Sign-ins did not move and could not.** They go through the same `RunInstallPlan` Func and are plans
only in shape: a login *starts* at the command and then prints a URL and waits for the user.
`InstallPlan.NeedsATerminal` is what says so — stated on the plan rather than inferred from `Arguments`
being empty, which is what the two sign-in plans happen to look like today and is a coincidence of how
they are built.

**One install at a time for the whole page**, because two package managers writing to one machine is a
lock file and a failure nobody asked for; the four buttons go down together and a line above the lists
says what is running, which is the whole of what the user can now see of it.

## Amendment, 2026-09-23 (second) — what the hook emits decides which `PATH` question to ask

Measured against rtk 0.46.0 by feeding it the payload Claude Code sends:

```
{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"git status"}}
→ {"hookSpecificOutput":{"permissionDecisionReason":"RTK auto-rewrite",
                         "updatedInput":{"command":"rtk git status"}}}
```

Two things follow, and the first one settles a question that had been answered by reasoning alone.

**`rtk init -g` is not a prerequisite.** The hook rewrote a command on a machine whose `rtk gain`
says `No hook installed — run rtk init -g`, i.e. where `init` had never run. There is no state for
`init` to create; what it does is put the same `PreToolUse` block into the user's own
`settings.json` that this application puts into its generated one. The difference is the file, the
scope, and that `init` writes the hook's command as a bare name while this writes the full path.

**But the rewrite it produces is a bare name, and that is a different `PATH` question.** However the
hook's own command is spelled, what finally runs is `rtk …` in the tile's shell. On a machine where
rtk sits somewhere no shell searches — exactly the winget case the first amendment dealt with — every
Bash call the agent makes becomes `rtk: command not found`. The command *fails* rather than missing
its saving, which is worse than having no proxy at all.

So the hook is now gated on `OutputProxy.OnTheShellsPath()` — our `PATH`, plus the login shell's on
Unix — and explicitly not on `ExecutableFinder.Anywhere`, which looks in places (`~/.local/bin`,
`WindowsApps`) that are right for a binary we start by its full path and wrong for a name somebody
else's rewrite is about to emit. Installed-but-unreachable is a fourth state on the Settings row,
naming where rtk is and what to do.

**`rtk gain` is the wrong instrument in both directions, and the row now says so.** It reads the
global hook, so it warns on a machine hooked from here whatever our hook is doing — and its advice,
`rtk init -g`, adds a second hook beside ours. It also counts only commands rtk *rewrote*, so a flat
counter is not evidence the hook did not fire. Both cost a real debugging session before being
written down.
