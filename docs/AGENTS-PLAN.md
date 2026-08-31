# Agents, providers and shells — the plan

**Status:** stages 1–8 done, less a first-run wizard of its own (see the stage 8 note). Stage 1: `IShellTerminal`,
`ShellTerminalCatalog`, `cmd` removed. Stage 2: `Terminal.Avalonia` 0.3.0, a `null` in
`PtyOptions.Environment` unsets, proved against a real child in `ShellEnvironmentTests`. Stage 3:
`Services/Agents/` — `IAiAgent` (the extended `IAiToolRunner`, which is gone under that name), five agent
classes with their measured effort/behaviour/session tables, `AiAgentCatalog`, `SessionCapture`,
`AiAgentInstance`, `AiUsage`, `SessionStrategy`, `ApiFlavor`, `InstallPlan`;
`AiPermissionMode`/`AiPermissionModes` renamed to `AiBehaviour`/`AiBehaviours` and reduced to the
canonical vocabulary with the two rounding rules; `RejectedFlag` taught the `-c key=value` shape;
`AiToolDetector`'s scan salvaged into `ExecutableFinder.Anywhere`.

Stage 4: `TileKindIds.Agent`, `AgentTileKind`, `AgentTileViewModel`, its setup step, the instances
persisted in `settings.json` and seeded per agent (`SettingsService.SeedAgentInstances`), the two launch
moments a session needs (`TileLauncher` → `PrepareForLaunchAsync` / `OnLaunched`, and
`IAiAgent.CapturesWhileRunning` to say which of them an agent uses), and the rollback rule — an agent
leaf reads as a `terminal` on the shell it was running rather than as an empty tile.

One deviation from §4.5, deliberate: **`AgentTileViewModel` derives from `TerminalTileViewModel` rather
than composing it.** Everything a shell tile does an agent tile does identically — the PTY, the activity
light, the clipboard registration, the launch chain, the header's actions — so composition would have
meant a delegating implementation of six interfaces plus a seam in `TileLauncher` and `TerminalTileView`
for a type that behaves the same everywhere. What actually differs is two answers, and they are two
overrides.

Stage 5: `Services/Providers/` — `IAiProvider` and the six provider classes, `AiProviderInstance` with
its key encrypted the way the database passwords are, `AiProviderCatalog` (the registry, the flavor
compatibility rule and the effort-narrowing rule), `ProviderEndpoint` (pure, table-tested),
`AiModelChoice` with the `__first_loaded__` sentinel, `ILocalAiProvider` and `LocalProviderDiscovery`,
and the environment wired end to end — `IAiAgent.EnvFor` takes an `AgentRuntime`,
`TerminalTileViewModel` exposes a `LaunchEnvironment` that both launch paths pass to
`PtyOptions.Environment`, and `ClaudeAgent` **unsets** `ANTHROPIC_API_KEY` when an instance names a
provider. That last one is what stage 2 was for and it is asserted rather than assumed.

Stage 6: the Goal tile runs on instances. `SelectedToolName` became `ExecutionAgentInstanceId` and
`ReviewAgentInstanceId` (empty = the agent doing the work), `GoalAgents`/`GoalAgentChoice` decide what may
be offered and what a stored id means, `AgentFor(phase)` sends the review — and only the review — to the
second agent, `AiProcessRunner.RunPlainAsync` gained the instance's own environment so a goal can run
through a provider, and a failure names *which* of the two agents reported it. The execution agent is in
the strip; the reviewer is in the panel underneath, because it is a once-per-goal setting and five
controls do not fit a narrow tile. **A goal whose agent has gone is no longer moved onto another one**:
the choice stands and the tile says so. `SelectedToolName` is still read — for ever, not migrated once,
because a goal file travels with a branch — and matched against the agents' own names.

Stage 7: profiles and the AI Tools table are gone. `SettingsService` seeds, migrates and removes no
profile; `AppSettings` lost `CustomAiToolPaths`, `CustomAiTools` and `GoalDefaultModels`;
`AiToolDetector`, `AiToolInfo`, `UserAiTool` and `AiToolViewModel` are deleted; Settings has three tabs;
the terminal tile's setup step lists the detected shells. `AgentTileMigration` turns the terminal leaves
that were one of the four seeded AI profiles into agent tiles, and `PersistenceService` keeps
`{id}.pre-agents.json` before the first save in the new shape. **`AppSettings.ShellProfiles` is still
read**, and that is the one deviation from §6: the plan says the key is dropped, but it is the only
evidence of which tiles were an AI CLI in a shell, so dropping it would take somebody's agent tiles with
it. It goes when the migration does, a release from now.

Stage 5's last piece landed with stage 8: the **AI page** in Settings. Agent instances and provider
instances are added, edited, deleted, tested (`IAiProvider.TestAsync`), asked for their models and — for
a local server — discovered, from the dialog rather than by editing `settings.json` by hand. Until it
existed, `TestAsync`, `ModelsAsync`, `NarrowEfforts`, `CompatibleWith`, `LocalProviderDiscovery` and every
agent's `InstallPlan` were shipped code with no route to it.

**Stage 8.** Settings **export and import** (`SettingsPortability` + `SettingsService.Replace`): secrets
are stripped on the way out — a DPAPI blob is bound to this machine and would not work anywhere else —
and the keys already configured here are kept on the way in, matched by instance id, so a file meant to
add configuration cannot empty the one thing nobody can retype. And the **install** half: an agent row
that is not installed offers `InstallPlan`, shows the command in the question, and runs it in a terminal
tile in the current workspace (`TerminalTileKind.StartupScriptKey` — set at creation, never saved, so
reopening a layout does not install anything twice).

**What is deliberately not there: a first-run wizard of its own.** The AI page is where an agent is
configured anyway, and the install action is on the row that says `NOT INSTALLED` — a separate first-run
flow would be a second place to do the same thing, shown once, on the run where the user has least idea
what it is asking about. If it comes back it should be the dictation wizard's shape, and it is the one
item of §5 stage 8 left open.

**Also still open, from §6:** `AppSettings.ShellProfiles` is read (never written) because it is the only
evidence of which tiles were an AI CLI in a shell; it goes with `AgentTileMigration`, a release from now.

**Also measured and fixed here:** the Goal tile resolved no model at all. `AgentModelResolver` is now the
one rule both the agent tile and a goal run ask, so `__first_loaded__` and a model named on an agent that
cannot carry one refuse the run and say which agent it was, rather than launching on whatever the CLI
would have picked.

This document is the decision record and the running order; it is meant to be deleted (or reduced to a
section of `CLAUDE.md`) once the work lands.

What changes: mTiles stops having *shell profiles* and *AI tools* and starts having **shells**,
**agents** and **providers**, each a class with its own behaviour rather than a row of user-editable
strings. This is not a refactor of the existing model — the model is removed. Pieces of the old code are
salvaged where the mechanism is still right; nothing is preserved for compatibility's sake except what
opens a file already on somebody's disk.

---

## 1. Measured facts

Everything below was measured on 2026-08-29 against the CLIs installed on the development machine:
**Claude Code 2.1.251**, **codex-cli 0.141.0**, **opencode 1.18.18**, **pi 0.84.3**, **agy 1.1.22**.
Every one of these is somebody else's CLI contract and every one has moved before — the tables are the
contract *as it is today*, not an assumption to build on. `RejectedFlag` stays for exactly that reason.

### 1.1 Sessions — the hard part

| agent | can we choose the id? | resume | how |
|---|---|---|---|
| **claude** | yes | `--resume <id>` | `--session-id <uuid>`; `${tileId}` is already a hyphenated GUID |
| **pi** | yes | same command | `--session-id <id>` — *"creating it if missing"*, idempotent |
| **opencode** | no | `-s/--session <id>` | `opencode import` writes a session with a chosen id (`ses_<tileId>`) — already implemented |
| **codex** | no | `codex resume <uuid\|name>` | id is learned after the fact: `~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<uuid>.jsonl` |
| **agy** | **no — verified** | `--conversation <id>` | id is learned after the fact; see below |

**Antigravity, measured:**

```
agy --conversation 83af41f4-… --print "say OK" --output-format json
→ warning: conversation "83af41f4-…" not found
→ {"conversation_id":"50c369cc-…","status":"SUCCESS",…}   exit 0
```

An unknown id is **not an error**: agy warns, silently starts a new conversation, and exits 0. A launch
chain judging on the exit code therefore cannot tell a resumed tile from a lost one. There is no local
session store (`%LOCALAPPDATA%\agy` holds only `bin/`) — conversations live server-side.

But `--output-format json` returns `conversation_id`, and resuming a *known* id works (verified: a second
`--print` recalled the first message). So the way in is **pre-create**, not output scraping:

> On creating a new agy tile, run one cheap `agy --print … --output-format json`, take
> `conversation_id`, store it on the tile, and launch the TUI as `agy --conversation <id>`.

Scraping the id out of a TUI's output (ANSI, alt-buffer, a layout that changes with the next release) is
the fallback of last resort and should not be written unless the above stops working.

**Consequence for the tile model:** `TileId` is currently *also* the session id and is immutable. For agy
and codex the id exists only after the agent starts, so a tile needs a **writable `sessionId`** in its
saved state, defaulting to `TileId`, and the layout must be saved at the moment the id is captured.

Three strategies, named rather than branched on:

- `Fixed` — we choose it (claude, pi)
- `ImportedFixed` — we create the session first, then choose it (opencode)
- `CapturedAfterStart` — the agent chooses, we learn it (agy via pre-create, codex via the session file)

### 1.2 Effort and behaviour

| agent | effort | behaviour / permissions |
|---|---|---|
| **claude** | `--effort low\|medium\|high\|xhigh\|max` | `--permission-mode acceptEdits\|auto\|bypassPermissions\|manual\|dontAsk\|plan` (**six** — mTiles knows three) |
| **pi** | `--thinking off\|minimal\|low\|medium\|high\|xhigh\|max` | **none.** `--approve` is about trusting project-local files, not tool calls — a pi run is always unrestricted |
| **opencode** | `--variant <string>` — *provider-specific*, open list, and **only on `opencode run`**, not on the TUI | `--auto` — a boolean |
| **codex** | `-c model_reasoning_effort=minimal\|low\|medium\|high` — a **config key, not a flag** | two orthogonal axes: `--sandbox read-only\|workspace-write\|danger-full-access` × `-a untrusted\|on-failure\|on-request\|never`, plus `--dangerously-bypass-approvals-and-sandbox` |
| **agy** | `--effort low\|medium\|high` | `--mode accept-edits\|plan` + `--dangerously-skip-permissions` |

Three things follow immediately, and all three break the current code:

1. **A subset of a closed enum is not enough.** codex has no flag, opencode's list is open and belongs to
   the provider, and its permission control is a boolean. The interface must return a list of *options*.
2. **The existing runners are already wrong.** `AntigravityToolRunner` says agy has no effort flag (it
   has) and that only bypass maps (`--mode accept-edits` maps 1:1 to `acceptEdits`).
   `CodexToolRunner` passes no permission flags at all — every Goal run on codex uses whatever the
   user's `config.toml` says. `AiPermissionModes` knows three of claude's six modes.
3. **Capabilities differ between interactive and headless use of the same agent.** `opencode --variant`
   exists on `run` and not on the TUI. So the question is not "what does this agent support" but "what
   does this agent support *here*".

### 1.3 Two more measured details worth keeping

- `agy --print-timeout` defaults to **5 minutes**. The Goal tile's "a run has no wall-clock timeout"
  guarantee does not hold on agy: a long implementation is cut off by the agent itself.
- `codex resume <unknown-id>` opens an interactive **picker**. In a launch chain that is a hang.

---

## 2. Decisions already taken

Recorded so they are not reopened.

| # | decision |
|---|---|
| 1 | An old tile that ran an AI profile opens as an **agent tile** — one-time mapping by profile name and binary, deleted after one release |
| 2 | Agent and provider instances are **global** (`settings.json`), not per-workspace |
| 3 | On Linux, API keys are stored **in plain text with a visible warning** (no DPAPI equivalent); the file gets `0600`, and so do the `settings.bad-*.json` copies |
| 4 | An agent × provider pair that cannot work is **hidden**, not shown disabled |
| 5 | agy's session id is obtained by a **real API call** at tile creation (pre-create), with a test |
| 6 | **`cmd` is ignored entirely** — as if it did not exist |
| 7 | The agent chooser **hides** unavailable instances. Nothing installed ⇒ an empty list plus a line saying what to install |
| 8 | Effort uses **one canonical scale** mapped per agent; behaviour uses one canonical vocabulary mapped per agent |
| 9 | Plan and review phases get their permission mode **from the agent, by phase** — the user chooses only for the execution phase |

---

## 3. What is deleted

Not migrated — removed, together with the idea behind it.

**Shell profiles.** `UserShellProfile`, the Profiles tab and its CRUD, `SeedDefaultProfiles`,
`MigrateOpenCodeProfile`, `RemoveSeededOpenClaudeProfile`, `RequiredAiToolBinaryName` and the visibility
filter built on it, `WorkspaceViewModel.GetAvailableProfiles` with its 30-second cache,
`TileContext.AvailableProfiles`, the profile chooser on an empty tile. With them goes **the whole
user-facing scripting language**: `StartupScript`/`FallbackScript` as editable fields, `TileScript` and
its `${tileId}` / `${opencodeSessionFile}` placeholders. A startup script stops being data and becomes
code inside an agent class, so there is nothing left for a user to write and nothing to preserve.

**AI tools.** `AiToolInfo`, `UserAiTool`, `AiToolDetector` and its list of seventeen, the AI Tools tab,
`AiToolViewModel`, `CustomAiToolPaths`, `CustomAiTools`, custom-tool CRUD, the `CUSTOM` / `NOT FOUND`
chips. Five agents is not a filtered seventeen — it is a different thing: a class with behaviour, not a
row holding a binary name and `--version`.

**Shells.** `ShellProfile`, the `ShellType` enum, `ShellDetector`, `ShellCommandLine`,
`ShellDetector.ResolveForCommands` and the whole `cmd`→PowerShell substitution (decision 6),
`AppSettings.CustomShellPath` / `CustomShellArgs` / `CustomShellType`, and `DefaultShellName` in its
present meaning.

**Dead already, going with the rest.** `AppSettings.GoalDefaultModels` and `GoalTileState.SelectedModel`
are written and never read by anything — verified. `GoalTileState.SelectedToolName` becomes
`ExecutionAgentInstanceId` + `ReviewAgentInstanceId`.

**Documentation.** `CLAUDE.md`'s *Shell Profiles*, *AI Tools* and *Session resume* sections describe a
product that will not exist. They are rewritten, not amended. So is the *Known limitations* entry about
launch chains never running in `cmd`.

### 3.1 What is salvaged

The mechanism, not the model.

- `DirectLaunchSession` + `ChainPolicy` + `RelaunchBudget` — the launch/relaunch state machine stays,
  fed by `IAiAgent` instead of by a profile. The most valuable survivor.
- `AiToolDetector`'s PATH + home-directory scan (`FindTool`, `FindInHomeDirs`) — the technique. Not the
  `AiToolInfo` model around it.
- `ShellCommandLine`'s per-shell flag mapping — folded into `IShellTerminal`.
- `OpenCodeSession` — moves inside `OpenCodeAgent`.
- The `IAiToolRunner` implementations — become the headless half of `IAiAgent`.
- `SubnetScanner` / `DiscoveryService` — reused for LM Studio and Ollama discovery.
- UI patterns: the tile-kind registry, `ConfirmAction`, the modal-overlay form for editing a list entry.

---

## 4. Target architecture

### 4.1 `IShellTerminal`

One class per shell — PowerShell, Git Bash, bash, zsh, fish. Keyed by a **string id** the way
`TileKindIds` is, not by an enum: adding a shell must not be an enum change plus three switches.
`TolerantEnumConverter` stays only to read what old settings files say.

```csharp
string Id { get; }                    // "powershell", "gitbash", …
string DisplayName { get; }
string IconId { get; }
IReadOnlyList<string> DetectPaths();
IReadOnlyList<string> InteractiveArgs { get; }
IReadOnlyList<string> CommandArgs { get; }      // -c / -Command
IReadOnlyList<string> NoProfileArgs { get; }    // -NoProfile / --norc --noprofile
string Quote(string value);
string SetEnv(string name, string value);
string UnsetEnv(string name);
string WithEnv(IReadOnlyDictionary<string,string> vars, string command);
```

**Why the env members are here and not only in `PtyOptions`.** `PtyOptions.Environment` already exists
(`Terminal.Pty`, merged over the parent's environment) and `ShellStarter` simply never fills it in — that
is the right route for anything secret, because a startup script is *typed into a live PTY* and therefore
lands in the scrollback **and** in the shell's history file. An API key must not go that way.

But `PtyEnvironment.Build` starts from the parent's full environment and can only *add*: **it cannot
unset an inherited variable**. A machine with a global `ANTHROPIC_API_KEY` cannot have an agent instance
that authenticates through `ANTHROPIC_AUTH_TOKEN` instead — the very misconfiguration that must be
avoided. A shell can (`Remove-Item Env:X`, `unset X`), and `NoProfileArgs` covers the other half of the
same trap: a user's own profile overwriting what we set.

> **Preferred fix:** teach `Terminal.Pty` that a null value in the overrides dictionary means *unset*.
> One line in `PtyEnvironment.Build`, our own library, and it removes the whole class of problem along
> with the history-file exposure. `IShellTerminal` still needs `Quote` and `NoProfileArgs` regardless.

Rules: secrets and provider configuration go through `PtyOptions.Environment`; unsetting an inherited
variable and anything that must happen inside an already-running shell goes through the shell's own
syntax, as the first lines of the startup script — after the shell starts, before the agent runs.

### 4.2 `IAiAgent` and `AiAgentInstance`

Two things, deliberately separate.

**`IAiAgent`** — the behaviour of one CLI. This is the extended `IAiToolRunner`; do not add a third
abstraction beside it.

```csharp
string Id { get; }                                  // "claude", "opencode", …
string DisplayName { get; }
string BinaryName { get; }
string? InstallUrl { get; }
InstallPlan? InstallPlan { get; }                   // what a button would run, shown before it runs
SessionStrategy SessionStrategy { get; }            // Fixed | ImportedFixed | CapturedAfterStart
IReadOnlyList<ApiFlavor> ConsumesApiFlavors { get; }

IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance i, AiUsage usage);
IReadOnlyList<AiEffort>    SupportedEfforts(AiAgentInstance i, AiUsage usage);

IReadOnlyList<string> EffortArgs(AiEffort e, AiUsage usage);
IReadOnlyList<string> BehaviourArgs(AiBehaviour b, AiUsage usage);
IReadOnlyDictionary<string, string?> EnvFor(AiAgentInstance i);   // null value = unset

LaunchPlan Interactive(AiAgentInstance i, string sessionId);      // startup + fallback, in code
void ConfigureHeadless(ProcessStartInfo psi, …);
IReadOnlyList<AiOutputChunk> ParseLine(string line);
```

`AiUsage` is `Interactive` or `Headless(GoalPhase)`. It is a parameter and not a property because the
answer depends on it — `opencode --variant` exists on `run` and not on the TUI, and the plan and review
phases run under a different permission mode from the execution phase (decision 9).

**`EffortArgs` returns a whole argv fragment, not a flag name.** codex's effort is
`-c model_reasoning_effort=high`, which no "flag name plus value" shape can express. This also weakens
`RejectedFlag`: a refused `-c` key does not read like `unknown option '--effort'`, so the rejection
matcher needs a second shape for config-style arguments.

**`AiAgentInstance`** — persisted configuration, and the thing the user actually picks:

```
Id, AgentId, Name, ApiAccountId?, SignInId?, Model, FastModel,
DefaultEffort, DefaultBehaviour, ExtraEnv, ExtraArgs
```

Every agent has **at least one** instance, seeded on first run ("Claude Code", "OpenCode", …). Further
instances are how "Claude Code on GLM 5.3 via OpenRouter" exists at all. Its `DefaultEffort` and
`DefaultBehaviour` apply **wherever the instance is used** — the agent tile included, not only the Goal
tile.

Two different "defaults" have to be named apart in the UI, or they become a trap:

- in a Goal tile's combo: **"from the agent"** — take the instance's setting
- in the instance editor: **"tool default"** — pass no flag at all

**Availability** is not a bool. An instance is selectable when its binary is installed *and*, if it names
a provider, that provider is configured. It carries a reason when it is not, but the chooser **hides**
it (decision 7); the reason is for the settings page and for the one case below.

**When a tile's instance disappears** (uninstalled agent, deleted key), fall back to the first available
instance and **say so in the tile, once**. Silently changing which agent is working in somebody's
repository is not acceptable.

### 4.3 `IAiProvider` and `AiProviderInstance`

```csharp
IReadOnlyList<ApiFlavor> ApiFlavors { get; }
Task<ProviderCheck> TestAsync(AiProviderInstance i);      // reachable? key valid?
Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance i);
Task<decimal?> BalanceAsync(AiProviderInstance i);        // null = this service does not say
```

**`ApiFlavor` has four members, not two**, and the split inside OpenAI is the one that matters:

| flavor | endpoint | served by | consumed by |
|---|---|---|---|
| `OpenAiChatCompletions` | `/v1/chat/completions` | OpenAI, OpenRouter, z.ai, LM Studio, Ollama | opencode, pi |
| `OpenAiResponses` | `/v1/responses` | OpenAI, OpenRouter (some models) | **codex** |
| `Anthropic` | `/v1/messages` | Anthropic, OpenRouter, z.ai (`/api/anthropic`) | claude code |
| `OllamaNative` | `/api/tags`, `/api/ps` | Ollama | discovery and model listing |

Compatible = the intersection of the provider's flavors and the agent's is non-empty. Without splitting
the first two, codex + Ollama would be reported compatible and would not work. (codex reaches local
models through its own `--oss --local-provider lmstudio|ollama` instead — an exception inside
`CodexAgent`, not a provider configuration.) When a local server grows an Anthropic-compatible endpoint,
this becomes one added enum member and nothing else.

**Balance is tri-state and mostly absent.** OpenRouter answers (`/api/v1/key`, `/api/v1/credits`);
OpenAI and Anthropic have no per-key balance endpoint; local servers have no concept of one. `null` means
*this service does not say*, never *zero*. Same rule as `HasRepository` in the workspace panel.

**Effort per model is advisory.** Reasoning effort is a property of the **model**, and whether it can be
passed at all is a property of the **agent**:

| path | who really decides |
|---|---|
| claude + Anthropic, or claude + OpenRouter | `--effort` is Claude Code's own abstraction over a thinking budget — the provider's list is irrelevant |
| opencode `--variant` | the provider's list **is** the real list |
| codex `model_reasoning_effort` | goes to `reasoning.effort`; a non-reasoning model rejects it |
| agy | Google only, own `--effort` |

So `AiModelInfo.SupportedEfforts` is `IReadOnlyList<AiEffort>?` where **`null` means "unknown"**, and the
combining rule is: the provider's list *narrows* the agent's list only when it is known. "The provider
did not say" must never become "no effort available". In practice only OpenRouter answers honestly, via
`supported_parameters` per model.

**`AiProviderInstance`**: `Id, ProviderId, Name, BaseUrl?, ApiKey?, Timeout`. At least one instance per
configured provider; several instances of the same provider (different keys) are the point. The list of
providers shows, per instance, **which agent instances use it** — derived by scanning agent instances,
never stored as a back-reference.

### 4.4 Local providers — LM Studio and Ollama

Default ports **1234** (LM Studio) and **11434** (Ollama). The port is optional: what the user types is
parsed by one pure function accepting `192.168.1.10`, `host:port`, a full URL and IPv6, filling in the
default — testable in a table, in the style of `PhoneEndpointRanker`.

Discovery reuses `SubnetScanner`, with four differences from the database version:

1. **On demand, not on a timer.** The database scan runs every 30 minutes by default; sweeping a
   corporate network on a schedule looks like reconnaissance. A "Search the network" button is enough.
2. **Verify by protocol, not by port.** `GET /v1/models` for LM Studio, `GET /api/tags` for Ollama. An
   open 11434 is not proof of Ollama.
3. **Say that it will usually find nothing.** Ollama binds `127.0.0.1` unless `OLLAMA_HOST=0.0.0.0`;
   LM Studio needs its server started and "Serve on Local Network" enabled. Without one sentence about
   this, the feature reads as broken.
4. **Neither has authentication.** A discovered instance is open to everyone on that network. One
   sentence, no more.

**Model selection** offers a `__first_loaded__` sentinel **beside** the real list, so that changing the
model in LM Studio does not mean changing it in mTiles too. Resolved at the start of every session and
never persisted as a concrete name: `/api/ps` (Ollama) or `/api/v0/models` with `state: loaded`
(LM Studio) → first available → **fail the launch with a readable message** if the provider cannot be
reached. Note the list can be long (`opencode models` returned 374 entries here), so model selection is a
searchable field, not a combo box.

### 4.5 The tile split

`TileKindIds.Agent` joins `Terminal`. `AgentTileViewModel` is built **by composition** with
`TerminalTileViewModel` — the same PTY, a different header, setup step and saved state — not by
inheritance.

- **Shell tile** — setup step lists the detected shells (PowerShell / Git Bash / bash / …).
- **Agent tile** — setup step lists the *available agent instances*, preselecting the last one used, or
  the first configured one if there is no last. An empty list is an empty state with an install
  prompt, not a list of unavailable rows.

**Rollback matters here.** A tile saved as `Kind = "agent"` has no legacy `TileContentType`, so a build
rolled back by Velopack would open it as an *empty* tile and could then save that emptiness over the
layout. Following `TileNode`'s existing dual-write rule, an agent tile also writes
`ContentType: terminal` plus a `shellName`, so a rollback degrades it to a plain terminal rather than to
nothing.

### 4.6 The Goal tile

- `SelectedToolName` → `ExecutionAgentInstanceId` and `ReviewAgentInstanceId` (empty = same as
  execution).
- **Only non-asking behaviours are offered**, and only for the execution phase. In a headless run there
  is nobody to ask, so any mode that asks becomes a **silent denial** — which is exactly why
  `AiChunkKind.Denied` exists. That rules out `acceptEdits` too (it still asks for non-edit tools,
  which is worse than either alternative because it looks like it is working), and `manual` and
  `dontAsk`. What is left is `auto` and `bypass`, and even `auto` can refuse — the difference is one of
  degree, which is what the denial counter is for.
- **pi has no gate at all**, so a pi goal run is always effectively bypass. `PiAgent` declares
  `[Bypass]` only; offering "auto" there would be a lie about what is going to happen to a repository.
- **Beware the word "auto".** opencode's `--auto` *is* our bypass ("auto-approve permissions that are
  not explicitly denied (dangerous!)"). Map by meaning, never by spelling.

| canonical | claude | pi | opencode | codex | agy |
|---|---|---|---|---|---|
| auto | `--permission-mode auto` | *(unavailable)* | no flag | `--sandbox workspace-write -a never` | `--mode accept-edits` |
| bypass | `--permission-mode bypassPermissions` | *(default — no gate)* | `--auto` | `--dangerously-bypass-approvals-and-sandbox` | `--dangerously-skip-permissions` |

`-a on-request` is **not** auto: it is the asking mode, and codex's own help says of `never` that
"execution failures are immediately returned to the model" — which is what a run without a human needs.

**Effort** keeps the canonical five plus "tool default":

| canonical | claude | pi | agy (3) | codex (`-c model_reasoning_effort`) |
|---|---|---|---|---|
| low | low | low | low | low |
| medium | medium | medium | medium | medium |
| high | high | high | high | high |
| xhigh | xhigh | xhigh | high | high |
| max | max | max | high | high |

**Rounding is asymmetric, deliberately.** Effort rounds to nearest, ties upward — being wrong costs money
and the tile is meant to be left alone, where a shallow attempt spends as much of the attempt budget as a
careful one. Behaviour rounds **down, never up**: an unsupported mode falls to the weaker option or to
"tool default", never to bypass.

**Phases get different permission from the agent, not from the user.** Planning and reviewing need no
write access: `--permission-mode plan`, `--sandbox read-only`, `--mode plan`. This also matters now that
review can run as a *second* agent — otherwise two agents write into one worktree and `GoalBaseline`
only protects against one of them. The review agent starts read-only by default.

**The strip does not fit.** Today it is `[Claude Code] [auto] [medium] •` in a narrow tile; execution
agent, review agent, effort and behaviour make five controls. Put the execution agent in the strip and
the rest in a row that expands underneath — these are once-per-goal settings, not once-per-minute ones.
`LeafTileView.ApplyHeaderWidth`'s rules apply: something must stand down as the tile narrows, and a
control whose state must be visible cannot go into a menu.

**Two agents mean two ways to fail.** An auth or rate-limit failure must name *which* of the two it was.

---

## 5. Running order

Each stage ends in a state where the application builds, runs and is not half-migrated.

**Stage 1 — `IShellTerminal`, and `cmd` removed. — done.**
The least entangled part. One class per shell, string ids, `ShellCommandLine` folded in, the
`cmd`→PowerShell substitution and `ShellType` deleted. `ShellStarter` starts passing
`PtyOptions.Environment` (empty for now). *Checkpoint:* shell tiles launch on every detected shell; the
`ResolveForCommands` tests are replaced by per-shell quoting tests.

**Stage 2 — `PtyOptions` unset support (upstream). — done.**
One line in `Terminal.Pty`'s `PtyEnvironment.Build`: a null value means remove. Ship a
`Terminal.Avalonia` release, bump the `PackageReference`. Blocks stage 5, nothing else. *Checkpoint:* a
test that a variable present in the parent environment is absent in the child.

**Stage 3 — `IAiAgent` + instances, no providers yet. — done.**
Five agent classes carrying their own startup and fallback commands, session strategies, effort and
behaviour tables. Instances seeded one per agent, with default effort and behaviour. Session capture for
agy (pre-create) and codex (session file). `AiProcessRunner`'s runners fold in. `AiToolDetector` reduced
to its scanning technique. *Checkpoint:* a Goal run works on each of the five with effort and behaviour
correctly mapped; `agy` resumes across a restart.

**Stage 4 — the tile split. — done**, less the shell tile's own setup step (see the status note).
`TileKindIds.Agent`, `AgentTileViewModel`, both setup steps, the dual-write rollback rule.
*Checkpoint:* a new agent tile resumes its session across a restart of mTiles, on all five agents.

**Stage 5 — providers. — done**, less the settings page (see the status note).
`IAiProvider`, instances, keys (DPAPI on Windows, plain text plus `0600` and a warning elsewhere), model
lists, the flavor compatibility check, `EnvFor` wiring through `PtyOptions.Environment` and unset.
LM Studio and Ollama with discovery and `__first_loaded__`. *Checkpoint:* Claude Code driven through
OpenRouter and through z.ai, with the inherited `ANTHROPIC_API_KEY` proven absent from the child.

**Stage 6 — the Goal tile. — done.**
Execution and review agents, the reduced behaviour list, per-phase permission, the strip's layout.
*Checkpoint:* a goal executed by one agent and reviewed by another, with review read-only.

**Stage 7 — deletion and migration. — done**, less dropping `ShellProfiles` itself (see the status note).
Profiles and AI Tools tabs removed, settings sections dropped, layout mapping written, `CLAUDE.md`
rewritten, README's `cmd` limitation removed. *Checkpoint:* a settings file and a workspace layout from
the previous release open, and open correctly.

**Stage 8 — the roadmap items — done**, less a first-run wizard of its own: the install action lives on
the AI page's agent row (plan shown, then run in a terminal tile) and settings export/import is on
General. *Checkpoint:* a settings file exported on one machine and imported on another brings the agent
and provider rows across and leaves that machine's own keys alone.

---

## 6. Migration

**`settings.json`** — `ShellProfiles`, `CustomAiTools`, `CustomAiToolPaths`, `CustomShellPath/Args/Type`,
`GoalDefaultModels` are dropped, not converted. Agent instances are seeded fresh. Follows
`MigrateLegacySettings`: read once, then stop writing the old key.

**`workspaces/{id}.json`** — a leaf with `userProfileId` naming a seeded AI profile becomes
`Kind = "agent"` with the matching agent instance; anything else becomes a shell tile keeping its
`shellName`. Matched by profile name and required binary. A `{id}.pre-agents.json` copy is written before
the first save in the new format, exactly as `.pre-kind.json` was. **This code has an expiry date** —
delete it one release later.

**Goal files** (`.mtiles/goals/`) travel with a branch, so `SelectedToolName` must be read **tolerantly
for ever**, not rewritten once. A goal file written on another machine, on an older branch, will still
carry it.

**`sessions/opencode/*.json`** are untouched.

---

## 7. Tests

- `IShellTerminal`: quoting per shell for values containing `'`, `"`, `$`, `%`, spaces, newlines.
- Session strategies: agy pre-create returns a usable id and a second run resumes it (decision 5); codex
  captures the id from the newest session file; unknown-id behaviour for both is asserted, since agy's
  is a warning and an exit 0.
- Effort and behaviour mapping tables, per agent, per `AiUsage` — a table test, as `ChainPolicy` has.
- Rounding: behaviour never rounds up to bypass; effort rounds to nearest, ties up.
- Flavor compatibility: codex + Ollama is **not** compatible through provider configuration.
- Provider `null` semantics: unknown balance and unknown per-model effort do not empty the effort list.
- Settings migration: an old file loses the dropped sections and gains seeded instances.
- Layout migration: a golden file from the previous release, and the rollback dual-write.
- The endpoint parser: bare host, `host:port`, URL, IPv6, default ports.

---

## 8. Open risks

- **Every table here is somebody else's CLI, measured once.** claude's permission modes went from three
  to six without anything on this side changing. `RejectedFlag` and `AiEfforts.LooksLikeRejectedEffort`
  stay, and gain a shape for config-style arguments (`-c key=value`).
- **`agy --print-timeout` defaults to five minutes** — the Goal tile's "no wall-clock timeout" promise
  does not hold there. Either raise it explicitly or say so in the tile.
- **`codex resume <unknown>` opens a picker**, which in a chain is a hang. The capture-from-file path
  must never hand it an id it has not seen.
- **Provider keys reach the agent's process environment**, and the agent has a shell tool. This is not a
  secret from the machine's owner, but it is worth one sentence in the UI.
- **OpenRouter's Anthropic-compatible endpoint is not a drop-in** for everything Claude Code does
  (cache control, token counting, parts of tool streaming). Some combinations will not work, and the
  tile must say which rather than reporting "the AI tool reported a failure". z.ai's `/api/anthropic` is
  the better-trodden path.
- **The install button writes outside the application's directories**, sometimes with elevation, and
  differs per OS. It shows its `InstallPlan` first and runs in a visible terminal tile — never silently.
