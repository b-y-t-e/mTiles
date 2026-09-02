# mTiles

Cross-platform terminal manager — .NET 10 + Avalonia 12.

## Building and running

```bash
dotnet build
dotnet run --project src/mTiles
dotnet test                     # tests/mTiles.Tests
```

## Structure

- `src/mTiles/` — the application
- `tests/mTiles.Tests/` — the launch chain, driven through a fake `IPtyConnection` injected via `TerminalControl.PtyFactory` (no shell is spawned). `ChainPolicy` holds the thresholds so a test drives the chain in milliseconds instead of sleeping through the real ten-second and two-minute thresholds
- `Models/` — DTOs and data models, no behaviour (Workspace, WorkspaceState, TileNode, TileKindIds, TileContentType (closed — see Tiles below), AppSettings, AppDefaults, LaunchScripts, UserShellProfile, TerminalTheme, GitFileChange, CommitLogEntry, GoalTileState, GoalCommit, GoalFinding, GoalReviewResult, GoalClarifyResult, GoalCompletionCriteria, GoalStopReason, GoalImageAttachment, SolidPrinciples, AiBehaviour, AiEffort, AiUsage, AiAgentInstance, AiProviderInstance, AiSignIn, AiModelInfo, ProviderCheck, SessionStrategy, ApiFlavor, InstallPlan, DatabaseSettings, DatabaseInstance, ManualDatabaseConnection, WorkspaceDatabaseConfig, SpeechSettings, PhoneSettings)
- `ViewModels/` — MVVM with CommunityToolkit.Mvvm (source generators)
- `Views/` — Avalonia AXAML + code-behind
- `Styles/` — design tokens (`AppTheme.axaml`) and global control styles (`Controls.axaml`, including GridSplitter). UI colors exclusively via `DynamicResource`, terminal ANSI colors separately in `TerminalTheme`. `BgCanvas` is the odd one out: it is what the tiles are laid on and the only colour here not meant to be looked at (see Split tiles architecture)
- `Services/` — JSON persistence (PersistenceService, SettingsService, WorkspaceService), AgentTileMigration (the terminal tiles that were an AI CLI in a shell, turned into agent tiles once), GoalAgents/GoalAgentChoice (which agents a Goal tile may offer, and what a stored id means), InstallCommand (the line an install or a sign-in actually types into a tile — `InstallPlan.CommandLine` is for reading and never what runs), ExecutableFinder (a program on `PATH`, for the callers a GUI process cannot rely on its own resolution for), ThemeBridge, JsonDefaults, AppPaths, AppInfo, GitService/GitCommandRunner/GitDirectoryWatcher/GitIgnoreFile, DiffFormatter, ProcessTreeMemory/MemoryDisplay (what a workspace's tiles are holding, and how that reads on its row), FileHelper, ProtectedStringConverter, TolerantEnumConverter, TileTreeSerializer, TileNameGenerator, TileMinimumSize, SpecialDirectories, SafePathComponent (the one rule for turning an id into a directory or file name — an allow-list plus the Windows reserved names, because both the sign-in directories and the generated opencode files are named after ids that reach `settings.json` by hand), DefaultWorkspace, the Goal tile's engine (AiProcessRunner, AiBehaviours, AiEfforts, GoalWorkflowEngine, GoalPromptBuilder, GoalStatePersistence, GoalLoopPolicy, GoalTilePolicy, GoalCompletionPolicy, GoalBaseline, GoalCommitter, GoalCommitPlan, GoalDiffContext, CommandDisplay, CommandLineLength, ElapsedDisplay, GoalStageDisplay, RejectedFlag, UnrecognizedModel (Claude Code refusing to start a headless run on a model it cannot verify against the gateway — the one recognisable failure that names itself and the route that still works), GoalResponseParser, GoalScopeFilter (the composer typed beside Detect/Review as a scope: its words a narrowing block in the prompt, its `@` paths a hard filter on the working-tree block), GoalStateStore, GoalTranscript, GoalImageStore, GoalImageMarker, SolidPrincipleCatalog, WorktreeReader, and its `@` file mentions — IFileMentionSource/WorkspaceFileMentionSource, FileSuggestionIgnore, FileMentionToken, FileMentionMatcher, FileMentionCorpus), UpdateService (its Velopack manager is built lazily and fails soft — an installation it cannot ask about must not stop the main view model being built), CrashHandler, FileLogWriter, LogTraceListener
- `Services/Tiles/` — the tile registry (see *Tiles* below and [`docs/TILES.md`](docs/TILES.md)): ITileKind, TileKind<T>, TileCatalog/TileCatalogEntry, TileContext, TileState, and one class per kind (TerminalTileKind, AgentTileKind, NoteTileKind, TodoTileKind, GitTileKind, DatabaseTileKind, GoalTileKind, UsageTileKind)
- `Services/Database/` — DatabaseServiceManager, DbHttpServer, DiscoveryService, DbRegistry, DbLogger, QueryHandler, SqlGuard, SqlGuardProfile, SqlServerProvider, PostgreSqlProvider, SubnetScanner, IDbProvider, ClaudeLocalMdWriter
- `Services/ShellStarter.cs` — one call that replaces whatever session a `TerminalControl` holds and hands the shell its startup script (`${tileId}` substituted, one line per `\r`). The control owns the rest: killing the old session, waiting for it, and gating the script on `ShellReady` for *that* session
- `Services/TileLauncher.cs` — launching a terminal or agent tile: disposes the previous launch, asks the tile what it runs now (`ResolveCurrentScripts`), then either the direct-launch chain or a plain interactive shell. First launch and "restart shell" both go through it. It reads `TileId`, it never assigns it. **A launch that has to wait checks that it is still the tile's launch before it starts one**: preparation for an agent that creates its conversation first is a model call with a minute's timeout, and closing the tile cancels the capture — which ends the preparation *normally*, so without the check the launch carried on, started a session in a disposed terminal and left a chain owned by a tile whose `Dispose` had already run. `TerminalTileViewModel.BeginLaunch`/`IsCurrentLaunch` is that claim, and it answers no for a restart in the same window too — which is the two competing chains this one call exists to prevent
- `Services/DirectLaunchSession.cs` — one tile's command chain (see Shell Profiles below); disposable, and disposing it is what stops it relaunching
- `Services/TerminalClipboardCoordinator.cs` — window-level Ctrl+C across tiles (see Terminal key handling)
- `Services/AiBehaviours.cs` + `Services/AiEfforts.cs` — the **canonical vocabulary and scale** the whole application speaks: what each mode and level is called, how much each one lets an agent do, and what an agent whose own list is shorter is given instead. **Neither holds a flag.** They used to hold Claude Code's words under neutral names, which is precisely how a second agent came to be launched with the first agent's flags; the spellings are now on the agent classes (see *Agents* below). The rounding is asymmetric on purpose — **effort to nearest, ties upward** (being wrong costs money, and the tile is left alone where a shallow attempt spends as much of the budget as a careful one), **behaviour downward, never up** (being wrong the other way is somebody's repository under an unattended agent they never authorised) — downward *among the modes the agent actually has*: an agent with no weaker gate falls to `ToolDefault`, which passes no flag and therefore leaves the CLI's own configuration in charge, so what is promised is that nothing here ever asks for more than was wanted, not that the run comes out weaker. The chooser in Settings and the Goal tile's strip are both narrowed to the agent's `SupportedBehaviours`, so that floor is reached by a stored value and never by a mode somebody was offered. Both rules are applied in one place — `AiProcessRunner.Fit`, asked by the run and by the failure path that names a refused flag — so `SupportedBehaviours`/`SupportedEfforts` are enforcement rather than documentation; and the Goal tile's strip offers `AiBehaviours.Headless` **narrowed by the execution agent's own list** rather than the whole vocabulary, because `ask`, `accept edits` and `plan` each fail in their own way in a run with nobody to ask — and a mode the agent has no gate for is one the run would round away to `ToolDefault` while the strip promised otherwise. The instance's `ExtraArgs` reach a headless run too (`AiProcessRunner.AddExtraArgs`, at the position the agent itself names — `IAiAgent.ExtraArgsIndex`, in front of a positional prompt by default and in front of `--print` on agy, whose prompt is that flag's own value): they applied in the agent tile and nowhere else, so `--add-dir` set on an instance silently did nothing in a goal run on it. They also count against the prompt's own budget (`AiProcessRunner.PromptBudget` takes the instance): the 256 characters `CommandLineLength.Budget` keeps back are for the agent's own flags, while these are unbounded user-typed text, so a prompt fitted without them passed the guard and then overflowed on a `.cmd` shim — the opaque `Win32Exception` the guard exists to replace with a sentence. Both still recognise the tool *rejecting* the flag it was passed (`RejectedFlag`, which learned a second shape for codex's `-c key=value`, since a refused config key does not read like `unknown option`): those are somebody else's CLI contract, it has moved once already, and "the AI tool reported a failure" over a usage message about a flag the user never typed names no cause at all. The setting itself is read tolerantly (`TolerantAiBehaviourConverter`) because it lives in `settings.json`: a mode written by a newer build and read after a Velopack rollback would otherwise quarantine the agent instances, the provider keys and the DPAPI-encrypted database passwords along with it. **`bypass` asks once before it is stored** — it is the largest single grant here, it applies to every Goal tile, and a combo box is a thin control for a decision whose first symptom is an unattended run that already happened
- `Services/Agents/` — **one class per AI CLI**, keyed by a string id the way `TileKindIds` and `IShellTerminal` are: `IAiAgent` (what it is called, how its session gets an identity, which API flavors it speaks, which behaviours and efforts it supports *for a given `AiUsage`*, the argv fragments that ask for them, its environment, its interactive startup and fallback commands, whether it reads its prompt on standard input — `AcceptsPromptOnStdin`, opt-in per agent because it is a claim about somebody else's CLI: Claude Code and opencode, both measured; the rest take it as an argument, fitted to the command line's budget — and how to read a line of its output), the `AiAgent` base that derives the blameable flag from the fragment and composes `Interactive` out of the agent's own `Resume` plus the instance's default behaviour, default effort and `ExtraArgs` (the same rule as `EnvFor`/`Configure`, and for the same reason — six classes wrote `Interactive` and all six ignored the instance, so an agent tile ran on the CLI's factory settings whatever its row said) — **quoting those arguments is the shell's job, not the base class's**: `Interactive` is handed the tile's `IShellTerminal` and calls its `Quote`, because a `\"` escape means nothing to PowerShell and inside its double quotes a `$` interpolates, so an `ExtraArgs` entry carrying a quote, a `$` or a backtick used to be mangled or partly executed — and **the session id goes through the same quoting before `Resume` sees it**, because it is not ours to trust: a `TileId` read out of a hand-editable layout file, or the string a captured agent printed as its conversation id, is interpolated into a script handed whole to `powershell -Command`/`bash -c`, where a `;` in it is a second command running in somebody's repository (an id made only of quote-free characters — every real one — comes out unchanged, and an empty id stays empty, since that is what codex and agy branch on to start a plain session), `ClaudeAgent`/`OpenCodeAgent`/`CodexAgent`/`PiAgent`/`AntigravityAgent`, `GenericAgent` for a binary nothing is known about, `AiAgentCatalog` (the registry, availability, and one seeded `AiAgentInstance` per agent), `AgentAvailability` (why a configured instance cannot be run — one sentence, read by the chooser that hides it, the Settings row that explains it and the launch that refuses it), `AiSignInStore` (where a login's directory is and how it is made, owner-only), `SignInStatus` (what the CLI's own files say about that directory), `OpenCodeProviderConfig` (the generated config that is opencode's only route to an address) and `SessionCapture`.
  **Why a whole argv fragment rather than a flag and a value.** codex's effort is `-c model_reasoning_effort=high` — a config key, not an option — which no "flag plus value" shape can express; and codex's permission is two orthogonal axes (`--sandbox` × `-a`) while opencode's is a boolean. A subset of a closed enum could not describe these five, which is why `SupportedBehaviours`/`SupportedEfforts` return **lists**.
  **The model is the instance's, and it reaches the agent by the agent's own route** (`ModelArgs`, and `AcceptsModel` for the one that answers otherwise). Measured 2026-08-30: `--model` on opencode, codex, pi and agy; Claude Code takes `ANTHROPIC_MODEL` through `EnvFor` instead, which is the same route as its base URL and token. Four of the five used to read the field not at all, so an instance pointed at a provider ran on the CLI's default model against an address that usually does not serve it — a launch that succeeds and a run that fails. An agent that can carry no model **says so** rather than dropping one silently, and the tile refuses that launch: `AgentRuntime.RequestedModel` is the other half of it, because an unresolved `__first_loaded__` on a command line is a model name no provider has.
  **Why `AiUsage` is a parameter and not a property.** Measured: `opencode --variant` exists on `opencode run` and not on the TUI, so "what does this agent support" has no answer until you say *where*. It also carries the `GoalPhase`, which is what lets a phase that writes nothing run read-only whatever the tile's strip says — clarify, plan and summarise get their permission **from the agent, by phase**, and that is what stands between a second agent and the worktree `GoalBaseline` photographed only once. **Review is the documented exception, and on the default criteria it is the usual case**: `RequireBuild` and `RequireTestsPass` both default to on, and a build writes into `obj/` and `bin/`, so a review asked to establish them is given the execution phase's permission (`AiUsage.RunsProjectCommands`) — what keeps it from editing source is then the sentence in the review prompt and the baseline behind it, not the sandbox. Turning both criteria off is what makes the review read-only, and `AiUsage.MayOnlyRead` is the one question the agents ask so the two cannot drift apart.
  **Every table in there is somebody else's CLI, measured once** (2026-08-29, against Claude Code 2.1.251, codex-cli 0.141.0, opencode 1.18.18, pi 0.84.3, agy 1.1.22) and pinned by `AiAgentTests`. Three of them correct what this application used to believe: agy **does** have `--effort` and its `--mode accept-edits` is the canonical *auto*; codex was passing no permission flags at all, so every goal run used whatever the user's `config.toml` said; codex's second permission axis reaches only its interactive commands (`codex exec` answers `unexpected argument '-a' found`, so a headless run carries `--sandbox` alone — pinned as a whole argv rather than as a fragment, which is what let a fragment right about the TUI be wrong about the only place it was used); and opencode's `--auto` is the canonical **bypass** — "auto-approve permissions that are not explicitly denied (dangerous!)" — mapped by meaning and never by spelling.
  **A CLI can hold more than one login, and that is an account like any other** (`AiSignIn`,
  `AiSignInStore`, `IAiAgent.SupportsSignIns`/`SignInEnv`/`ReadSignIn`). A provider and a sign-in answer
  the same question — as whom does this agent run — so they are **one slot**, `AccountChoice`, and
  `AgentRuntime.For` is where "never both" is finally enforced: an instance carrying a sign-in *and* a
  provider would point the CLI at one subscription's directory while authenticating with somebody
  else's key, so the work is billed to the provider while every row on screen names the subscription.
  Measured 2026-08-30, each against an empty directory: `CLAUDE_CONFIG_DIR` (Claude Code 2.1.251 →
  `Not logged in`), `CODEX_HOME` (codex-cli 0.141.0 → `401 Unauthorized`), and for opencode 1.18.18
  somebody else's variable, `XDG_DATA_HOME`, pointed at `<dir>/data` rather than at `<dir>` itself
  (`opencode auth list` → `0 credentials`) — which is why `SignInEnv` answers a **block** rather than a
  name and a value, the same reason `EffortArgs` is a whole fragment. That one is **narrower than it
  first was**: it began as both XDG variables, which isolated the login and also took the user's own
  `~/.config/opencode/opencode.json` — their default model, MCP servers and instructions — away from
  every tile on that sign-in, arriving silently by the one path `OpenCodeProviderConfig` spends a
  paragraph defending against on the other. Measured 2026-08-31: `XDG_DATA_HOME` alone answers
  `0 credentials`, so the config variable was never buying the isolation it cost. pi has
  one too, `PI_CODING_AGENT_DIR` (2026-08-31: with `OPENROUTER_API_KEY` out of the environment,
  `pi auth check --provider openrouter` answers `not_ready` against a fresh directory and `ready`
  against the default one) — **first recorded as having none**, from a reading of `--help` rather than a
  run, which is the correction `PiAgent` now carries in its own words. Only **agy** has none and says
  so: its binary carries no `*_HOME`, `*_DIR` or `*_CONFIG` variable at all, it keeps its state in
  `~/.gemini`, and it switches Google accounts itself in `google_accounts.json`. Inventing one would be
  a row the user could name, log into and never actually run as. **The default account sets nothing, and must not be "the variable pointed at
  the CLI's own directory"**: with `CLAUDE_CONFIG_DIR` set, Claude Code keeps `.claude.json` *inside*
  that directory, while by default it keeps it at `~/.claude.json` and only the credentials in
  `~/.claude` — so pointing it at `~/.claude` yields a session that is logged in and has lost its
  projects, its MCP servers and its history. The variable is applied in `AiAgent.EnvFor` before
  `Configure` and before `ExtraEnv`, and for the reason that method is not virtual: an agent that forgot
  the line would run every one of its tiles on the default account whatever the row said. The
  directory is **derived** from the sign-in's id (`AppPaths.GetAgentAccountsDirectory()`), never stored
  as a path, so `settings.json` carries nothing that stops being true on the machine it is imported
  into; it is created owner-only, since the CLI is about to write a refresh token in it; and **nothing
  ever deletes it** — removing the row removes the row, and the confirmation says where the login stays.
  `AiSignIn.ConfigDirectory` is the **one exception and has no field on the page**: a path written into
  `settings.json` by hand, used verbatim, for pointing a row at a directory that already exists — another
  profile's, or one somebody keeps elsewhere. Nothing here rewrites it, which is the point; it travels
  with an export like every other non-secret, and on a machine that does not have that path the row
  simply reads as not signed in rather than quietly becoming the default account.
  A sign-in belongs to **one agent**, so the chooser narrows to it exactly as it narrows providers by
  flavor, and a deleted one makes the instance unavailable rather than silently the default account
  (`AiAgentCatalog.IsAvailable`, `AgentModelResolver`). It also relocates `sessions/`, which is why
  adding one is a *new* instance rather than an edit to an existing one: the tiles on it would come back
  without their conversations.
    **Sessions are three named strategies, not a branch per agent** (`SessionStrategy`): `Fixed` (claude, pi — the tile's own id is the whole of the bookkeeping; pi's `--session-id` both creates and resumes, while Claude Code splits them and is launched `--resume` first with `--session-id` as its fallback, because each of the two refuses what the other wants), `ImportedFixed` (opencode — `--session` only *continues* one, so `opencode import` brings the chosen id into being; see `OpenCodeSession`), and `CapturedAfterStart` (codex, agy — the agent names it and we find out afterwards, so the tile's session id is *writable* and its layout has to be saved at the moment the id is captured). **Neither captured agent is ever handed an id it has not seen**: `codex resume <unknown>` opens an interactive picker, which in a launch chain is a tile waiting for a keystroke nobody knows it wants, and `agy --conversation <unknown>` is worse in a quieter way — it warns, silently starts a *new* conversation and exits 0, so a chain judging on the exit code cannot tell a resumed tile from a lost one. An empty session id therefore starts a plain session. **How a tile id becomes a session id is the agent's answer** (`IAiAgent.SessionIdForTile`): four take it verbatim, opencode puts its `ses_` prefix on it — a tile that spelled the id itself handed opencode a bare GUID, which its own import rule refuses before the tile can launch. `SessionCapture` holds the two pieces of plumbing (run a CLI and read what it printed; find the newest `rollout-*.jsonl` **written since this tile started**, because resuming a stranger's session is worse than starting a fresh one), and each agent overrides `CaptureSessionAsync` for itself
- `Services/Providers/` — **one class per service an agent can be pointed at**, keyed by a string id: `IAiProvider` (its wire formats, its address, whether it needs a key, and how to ask it what it serves), the `AiProvider` base that makes every call answer rather than throw — a test button that throws is a dialog with a stack trace in it — `AnthropicProvider`/`CcsProvider`/`OpenAiProvider`/`OpenRouterProvider`/`ZaiProvider`/`LmStudioProvider`/`OllamaProvider`, `ILocalAiProvider` for the two questions only a server on this network can answer, `IManagedAiProvider` for the one question only a provider that *owns* a service on this machine can answer — is it running, and can it be brought up (`CcsProvider.EnsureRunningAsync`: probe by protocol, start `ccs cliproxy start` when down, poll for health, answer — asked from `AgentModelResolver.ResolveAsync` *before* any model question, which is how the agent tile's `LaunchProblem` and the Goal run's refusal are the same sentence; a `cmd /c` invocation takes its arguments separately, never one pre-quoted string, and its reads are bounded by a drain deadline of their own — a daemonized child inherits the pipes and would hang the launch past the timeout otherwise) — `CcsProvider` being the bridge that runs Claude Code on a Codex subscription through a local OAuth proxy, which is why its flavor list admits Claude Code alone and why it is deliberately **not** an `ILocalAiProvider` (a fixed published address has nothing to discover, and a subscription has no loaded model), `AiProviderCatalog`, `ProviderEndpoint` (pure), `AiModelChoice`, `AgentModelResolver`, `ModelContextWindow`, `LocalProviderDiscovery` and `AgentRuntime`.
  **The model's context window is a tri-state like every other answer a provider gives** (`AiModelInfo.ContextWindowTokens`, `IAiProvider.ContextWindowAsync`): OpenRouter says it in the listing (`context_length`), LM Studio in its own (`max_context_length`), Ollama only on a per-model POST to `api/show` — which is why the question is per model and not read off the list — and `null` is *did not say*, never zero, because a guessed window reaches an agent's environment as a fact. **What spends that answer is Claude Code's pair of windows** (`ModelContextWindow`, the `UsesModelContextWindow` question — the gate that keeps the provider call away from the five agents that read none of it): on a third-party provider the CLI does not recognise the model id and *assumes* a context window of 200 000 for it — assumed wrongly by half, and the assumption is two failures at once. The compact failure is the older one: `CLAUDE_CODE_AUTO_COMPACT_WINDOW` = **80% of the model's context, rounded down** (a margin this application chose — the CLI's documented default is the full limit — argued in a table test; below the variable's documented minimum of 100 000 nothing is set, because the CLI would clamp it up past the margin). The second is the stop the compact window cannot reach: the compact variable moves when compaction fires, not what the CLI *believes* the context is, so the hard `Context limit reached` fired at the assumed 199.8k on z-ai/glm-5.3-flash — advertised at 1 310 720 — with a million-token compact window that never came due (measured 2026-09-01). `CLAUDE_CODE_MAX_CONTEXT_TOKENS` is the documented correction — "override the context window size Claude Code assumes for the active model", applying directly to an id that neither starts with `claude-` nor carries `[1m]` — and it is handed the provider's context at **100%, unclamped**: the margin is an opinion about when to compact, and the assumption being corrected is a fact, which is also why a 32k model is told the truth there while getting no compact window at all. Each window typed on the instance (`AutoCompactWindow`, `MaxContextTokens` — Settings → AI, Claude Code only) is the whole answer for it when present, and one typed alone still triggers the resolution, because the other window is then derived from the model's context; the resolution is the fallback for the fields left empty, and it is cached for half an hour against provider, address and model, because the Goal tile resolves per AI call and OpenRouter's catalogue is a megabyte.
  **Compatibility is the intersection of two flavor lists and nothing else** (`AiProviderCatalog.IsCompatible`). That is what the four-member `ApiFlavor` buys: without splitting `/v1/chat/completions` from `/v1/responses`, codex and Ollama would be reported compatible — both "OpenAI" — and the launch would fail, and a pairing offered and then failed is worse than one never offered.
  **Two `null`s are load-bearing and neither is a zero.** A balance of `null` means *this service does not say* — only OpenRouter has an endpoint for it, and showing an absent figure as 0 tells a user whose key works that they have run out. A model's `SupportedEfforts` of `null` means *the provider did not say*, and `AiProviderCatalog.NarrowEfforts` leaves the agent's list untouched when it does: silence read as denial would empty the effort chooser for five providers out of six. An **empty** list is a different answer — a model that takes no reasoning parameter — and narrows to `ToolDefault` rather than to nothing, because a chooser with no options says nothing.
  **Not every agent is pointed at a service by its address.** Claude Code is — `ANTHROPIC_BASE_URL`
  redirects it — but opencode and pi keep their own **registry** of providers, identify one by *name*,
  and validate `provider/model` against a catalogue before opening a socket. Measured 2026-08-31: given
  `OPENAI_API_KEY` for an OpenRouter instance, `opencode auth list` reports it as the **OpenAI**
  provider, so the run went to api.openai.com while every row on screen said OpenRouter — and the bare
  model id was refused with `ProviderModelNotFoundError`. Three members carry the difference:
  `IAiProvider.KeyEnvironmentVariable` and `CatalogueId` (facts about the *service*, so they are stated
  once rather than per agent), and `IAiAgent.QualifiedModel` (the model spelled that CLI's way, asked
  once and used by the tile *and* the headless goal run so one instance cannot be spelled two ways).
  **`IAiAgent.SupportsCustomEndpoint` is the fourth and is not the same question as
  `IsCompatible`**: opencode and pi both speak `/v1/chat/completions`, and only opencode has anywhere to
  put an address — through `OpenCodeProviderConfig`, a generated file that is its only route to a local
  server, written per instance and rewritten every launch the way `OpenCodeSession`'s import document
  is. pi has none, says so, and `AgentModelResolver` refuses that pairing by name rather than letting it
  launch on pi's own default provider. agy is unmeasured and inherits the permissive default.
  **The key goes through the environment, and one of the variables is emptied rather than removed.** `IAiAgent.EnvFor` takes an `AgentRuntime` (the instance, the provider, and the model *resolved* for this session) and answers a dictionary whose `null` values unset — `TerminalTileViewModel.LaunchEnvironment` carries it to both launch paths and into `PtyOptions.Environment`. `ClaudeAgent` sets `ANTHROPIC_BASE_URL`/`ANTHROPIC_AUTH_TOKEN` and **empties `ANTHROPIC_API_KEY`** (`""`, not a removal — the gateway's own recipe, read 2026-09-01: the CLI's auth resolution treats a missing variable and a present-but-empty one differently, and the missing one lets a cached claude.ai login answer; an empty value overrides an inherited global key just as surely, and an empty key authenticates nothing). It also sets **`CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1`**: without it a headless (`-p`) run verifies the model id against Anthropic's own families and refuses anything else before asking the model a question — measured 2026-09-01 against 2.1.250–2.1.252 with `z-ai/glm-5.3-flash` on OpenRouter, every spelling, env and flag alike; with it the CLI asks the gateway's model list, and a gateway that fails to answer is handled by the CLI and the run carries on. Never the startup script — that is typed into a live prompt and lands in the scrollback and the shell's history file. `EnvFor` is **not virtual**; what an agent overrides is `Configure`, so the rule that the user's own `ExtraEnv` is merged last cannot be dropped by an agent that forgets it.
  **`AiModelChoice.FirstLoaded` is resolved at every launch and never written down.** Persisting the answer is the same as not having the sentinel: the point of it is that changing the model in LM Studio does not also mean changing it in mTiles. **A resolution that fails stops the launch and shows the sentence** (`TerminalTileViewModel.LaunchProblem`, refused by `TileLauncher`, drawn over the tile by `TerminalTileView`) rather than substituting one of ours: the rule is `AgentModelResolver`, asked by the agent tile *and* by the Goal tile's run — it lived in the tile alone, so a goal on an instance asking for the first loaded model launched with no model at all while the environment still pointed at the local server, and a model named on an agent that cannot carry one was dropped without a word; the tile that started anyway looked like it had worked, and the only account of the model it was really running on was a line in `%APPDATA%/mTiles/logs`. **It also refuses an instance whose provider is gone or is one this agent cannot speak to** — the same question `AiAgentCatalog.IsAvailable` asks as a filter, said out loud, because the chooser and the Goal tile's list hide such an instance while a tile restored from a layout is handed its stored one without anybody asking: the one path where nobody is choosing is the one where a silent fall back to the CLI's own account and model would never be noticed. Discovery is **on demand, never on a timer** (a scheduled sweep of a corporate network looks like reconnaissance) and verifies **by protocol, not by port** — an open 11434 is not proof of Ollama. It will usually find nothing, and whatever shows it has to say so: Ollama binds `127.0.0.1` unless `OLLAMA_HOST=0.0.0.0`, LM Studio needs "Serve on Local Network". **Neither has any authentication**, so a reachable instance is open to everyone on that network. Both are configured on the Settings dialog's **AI** page, which is also the only thing that calls `TestAsync`, `ModelsAsync`, `NarrowEfforts` and `LocalProviderDiscovery` (see *Settings UI*)
- `Services/AppPaths.cs` + `Services/WorkspacePaths.cs` — the two directories this application owns, and the one-time move each performs from the name it used before the rename. **Both fail soft**: a move that cannot be made leaves the old directory in use rather than presenting a first run, because the first run saves. `WorkspacePaths` is the one inside the user's repository, so its move shows up as a rename in their next `git status` — visible and reversible, which is the most it can be
- `Services/Phone/` — dictation from a phone (see `docs/DICTATION.md` → *Dictating from a phone*): PhoneEndpoint/IPhoneEndpointSource with NetworkEndpointSource, TailscaleEndpointSource and MulticastDnsEndpointSource, PhoneEndpointRanker (pure — the one part whose behaviour is an opinion, so it is argued in a table test), PhoneEndpointDirectory, SessionLocationProbe, PhonePairing, PhoneCertificates, PhoneFirewall, PhoneAudioCapture + RoutedAudioCapture, PhoneBridgeServer (Kestrel — the only server here that faces the network), PhoneBridgeManager, PhoneKeys (the keys the page can press, and where they land), QrCodeImage, UiDispatcher
- `Services/Speech/` — dictation (see `docs/DICTATION.md`): IAudioCapture/PortAudioCapture, AudioResampler, ISpeechToTextEngine with ParakeetSpeechEngine (+ParakeetVocabulary) and WhisperSpeechEngine, SpeechEngines (the one map from model kind to engine and to what it looks like on disk), SpeechModelCatalog, SpeechModelStore, TarGzExtractor, DictationService, TranscriptPostProcessor, DictationTextSink, HotkeyGesture, HotkeyCapture (what a keystroke means to something reading a new shortcut — shared by the Speech tab and the setup wizard, and pure, because it lived in view code where the "mark it handled only where it is taken" rule had no test), HotkeyAdvice, DictationHotkeyMachine, DictationHotkeys
- `Services/Shells/` — **one class per shell**, keyed by a string id the way `TileKindIds` is: `IShellTerminal` (id, display name, icon, where to look for it, interactive/command/no-profile flags, quoting, and the shell's own `export`/`unset` syntax), the `ShellTerminal` base that composes those into `WithEnv` and refuses a name that is not a variable name, `PosixShellTerminal` with `BashTerminal`/`ZshTerminal`/`GitBashTerminal` under it, `FishTerminal` (not a POSIX shell — it escapes inside single quotes), `PowerShellTerminal`, `ShellInstallation` (a shell **and** where it was found — the two are separate so quoting is testable without a filesystem, and `CommandLineFor` is the old `ShellCommandLine`), and `ShellTerminalCatalog` (the registry, detection, and the one tolerant lookup that reads both an id and the display name older settings and layouts store).
  **`cmd` is not in the catalog, and that is a decision.** It cannot run what this application asks a shell to run: it does not parse its command line by the `CommandLineToArgvW` rules the PTY backend quotes with, runs only the first line of a multi-line command, and does not treat `;` as a separator — all measured, and the last of those silently reduced OpenCode's own two-command chain to a bare shell. It used to be offered and then swapped for PowerShell behind the user's back, which meant a shell that was neither the one they picked nor the one running their commands. A stored `CMD` now finds nothing and falls back to the default — and so does a `$SHELL` the old Unix detection offered (`nu`, `ksh`, `dash`), which is why `SettingsService.ReportUnknownDefaultShell` logs the name once — remembering in `ReportedUnknownShellName` that it has, so the warning does not return every launch — and **leaves the name in the file**: a name this build cannot match is also what a shell added by a newer version looks like after a Velopack rollback, so clearing it would let the older build settle the question for the newer one for good. `DropCustomShell` is the one that does clear, because a path to an arbitrary binary is an answer nothing here could ever honour.
  **Why the environment members are on the shell and not only in `PtyOptions.Environment`.** Anything secret goes through the process environment — a startup script is *typed into a live PTY*, so it lands in the scrollback and in the shell's history file, which is why a key must never go that way. Since **Terminal.Avalonia 0.3.0** that block can also *remove*: a `null` value in `PtyOptions.Environment` unsets the variable, so a machine with a global `ANTHROPIC_API_KEY` **can** be given a child that authenticates through `ANTHROPIC_AUTH_TOKEN` instead. That was one line in our own `PtyEnvironment.Build`, and it is the right route — `ShellEnvironmentTests` proves it against a real child rather than a fake that would only report what it was handed. What the shell's own `SetEnv`/`UnsetEnv` are still for is everything that has to happen *inside a shell that is already running*, and `NoProfileArgs` covers the other half of the same trap — the user's own profile overwriting what we set
- `Services/ChainPolicy.cs` + `Services/RelaunchBudget.cs` — the launch chain's rules and its rate limit, pure and separate from the loop that carries them out
- `Services/TileScript.cs` — the one place that expands an agent script's placeholders (`${tileId}`, `${opencodeSessionFile}`), and the only thing that decides what an acceptable tile id is — a rule `OpenCodeSession` asks for rather than copies, because the same value also becomes a file name
- `Services/OpenCodeSession.cs` — how an OpenCode tile gets its conversation back (see Session resume below)
- `SettingsService` writes on a debounce **and** directly when the window closes, so both the write and the timer swap are locked, and the timer's write is wrapped: an unhandled exception on a thread-pool thread ends the process, and no settings save is worth the application

`ShellStarter`, `TileLauncher`, `DirectLaunchSession` and `TerminalClipboardCoordinator` drive a `TerminalControl` but draw nothing, so they live in `Services/` rather than `Views/` — which also keeps `ViewModels/` from reaching into `Views/`.

- `ViewModels/TileActivationScope.cs` — per-workspace tile activation scope with suppression mechanism

## Key libraries

- **Terminal.Avalonia** (`PackageReference`, NuGet) — our own control: VT engine, ConPTY/forkpty and rendering, no third-party terminal or PTY package. Source lives at `D:\work\sources\Terminal.Avalonia`; to try an unreleased change, swap the `PackageReference` for a `ProjectReference` locally and swap it back before committing.
  - Three packages: `Terminal.Avalonia` → `Terminal.Emulation` + `Terminal.Pty`. Only the first is referenced; the other two come with it. `release.yml` builds again — the absolute-path `ProjectReference` that blocked it is gone.
  - It **is** the terminal: no template, no inner view, no `PART_TerminalView`. Focus it directly.
  - Detaching from the visual tree does **not** end the session (only UI timers pause), so moving tiles between panes needs no bracketing.
  - It never launches on its own, and never twice at once: `RestartAsync(options, startupInput)` is what this app uses — it kills the live session, waits for it to be reported dead, starts the new one and types the startup script into it once *that* session is ready.
  - **A session has an identity.** `SessionId` and `SessionExitedEventArgs.SessionId` are how a relaunch-on-exit tells its own session from the next one. Never infer it from elapsed time.
  - API used here: `RestartAsync`/`Dispose`/`IsRunning`/`IsDisposed`/`SessionId`, `Exited`, `WhenSessionEndedAsync(sessionId)` (the launch chain's one wait: it carries `ExitCode` — `int?`, null when there is none — and `Reason`, so "the command failed" is never confused with "we could not tell"), `Copy`/`ClearSelection`/`HasSelection`/`SelectionChanged`, `Palette`, `ScrollbackCapacity`, `RedrawShellOnResize`, `ForwardCtrlVWhenClipboardHasNoText`. The startup script is handed to `RestartAsync` rather than typed with `SendText`, and the chain waits on `WhenSessionEndedAsync` rather than `WhenNotRunningAsync` — neither is called from here any more. **`Kill()` is deliberately unused**: it only asks the child to die, and the exit is reported when it actually does, so anything that kills and then starts races that report. `RestartAsync` sequences kill → wait → start and serialises overlapping restarts; `Dispose` ends the tile for good. Note this does *not* avoid the stall — `RestartAsync` calls `Kill()` itself and it blocks the UI thread for as long as the child takes (up to 2s). That is Open risk #3 in the library's ROADMAP, not something the host can fix.
  - Ctrl+C copies when there is a selection and sends SIGINT otherwise; Ctrl+V / Ctrl+Shift+V / Shift+Insert paste clipboard **text** (filtered). Keys go out as win32 INPUT_RECORDs whenever the child enabled `?9001`.
  - Ships the open-source console host (`conpty/<arch>/OpenConsole.exe`), which is what fixed opencode taking the shell down with it. `Terminal.Pty` delivers it through `buildTransitive`, so it copies to the app's output automatically — **verified in `bin/`, not assumed**. The files must stay next to the app: without them the session silently falls back to the in-box `conhost.exe`.
- **The dictation stack** (see Dictation below) — three packages, all of which carry native binaries:
  **PortAudioSharp2** (microphone; the one wrapper shipping prebuilt portaudio for win-x64 *and*
  linux-x64), **Whisper.net** + **Whisper.net.Runtime** (whisper.cpp), and **Microsoft.ML.OnnxRuntime**
  (Parakeet). Their natives land in `runtimes/<rid>/` — whisper's directly under the RID rather than in
  `native/`, which is its own loader's convention. **Verified in `bin/`, not assumed** — and now on
  every release by `.github/workflows/verify-natives.ps1`, which fails the build if portaudio, whisper,
  ggml, onnxruntime, OpenConsole or `THIRD-PARTY-NOTICES.md` is missing from the publish — the notices
  check moved in there because the workflow was carrying three hand-copied versions of it. It matches by
  file name anywhere in the tree (a self-contained publish flattens `runtimes/`) but **rejects foreign
  architectures**: a non-RID build carries every runtime, so name-only matching would pass by showing the
  arm64 copy — which is the failure it exists to catch, reported as a success. Each entry takes a *list*
  of acceptable names (the ggml trio's `-whisper` suffix is Whisper.net's own renaming, not a platform
  convention, and it has changed before). Deliberately **not** a leading wildcard such as `ggml*.dll`:
  that would let the base library satisfy the entry for the dispatcher, so all three would pass on one
  file — the trailing wildcards on the Linux names are safe precisely because they come after the part
  that tells the three apart.
- **Notepad.Avalonia** — its `MarkdownViewer` renders what the AI tool writes in the Goal tile's
  transcript. Wrapped in the transcript's own `ScrollViewer` on purpose: the control scrolls itself
  only when given a finite height and sizes to its content when it is not, which is what a message
  in a list needs. `ColorTheme="None"` is load-bearing — any other value makes it assign its own
  brushes over the tokens, and its default is Light — which is why the wrapper is
  `Views/GoalMarkdownView.cs` rather than a page of attributes: the control assigns those brushes in its
  own constructor, as local values, before any markup runs. The wrapper also refuses to open a link
  without asking, showing the address rather than the words.
- **AvaloniaEdit** — text editor. Requires `StyleInclude` in App.axaml. Text sync via `Document.Changed`.
- **Material.Icons.Avalonia** — Material Design icons. Requires `<MaterialIconStyles />` in `App.axaml` Styles. Usage: `<mi:MaterialIcon Kind="Close" />`.

## Design rules

The look the UI was brought to, as rules rather than history. **New UI follows these; changing one is a
decision about the whole application, not about the screen being worked on.** Each was paid for by
something that looked wrong on screen.

**Ground and shape**

- The window is **one canvas** (`BgCanvas`) with **cards** on it. A card is `BgBase`/`BgSurface`,
  `RadiusTile`, a 1px `BorderSubtle` hairline. The workspaces panel and every tile are cards.
- **One gutter width**, and it goes round the outside too (8px: the panel/content column, `TileNodeView.TileGap`,
  `WorkspaceView`'s padding). A card clipped by the window frame is not a card.
- **A card that draws an outline must not also clip.** `ClipToBounds` on the drawing `Border` clips to
  the rounded-down bounds, so at 125%/150% desktop scale the right and bottom edges vanish and the
  outline comes out as an L. Put the clip on an inner `Border` at `RadiusTileInner` (= outer radius less
  the border width, or the two rounded rectangles are not concentric).
- **One radius per role**: `RadiusTile` cards, `RadiusRow` list rows, `RadiusSm`/`RadiusMd` controls.
  Three radii in one 240px column read as a rendering accident.
- **Full-bleed by default.** Only a terminal's content is inset from its card (`LeafTileView.ContentInset`);
  a tile whose content is its own chrome runs to the edge and takes the card's corners from the clip.
  An inset leaves a square-cornered rectangle floating in a rounded card.

**Colour**

- Every UI colour is a **role token** via `DynamicResource`, derived from the terminal theme in
  `ThemeBridge`. No literal hex in a view.
- **The longer the line, the quieter the accent.** A short bar (a selected row's leading edge) takes
  `AccentHover`; a whole perimeter takes `AccentOutline`. The same colour on ten times the length stops
  being a marker and becomes a frame.
- **A colour has to work at the size it is drawn.** The `TileAccent*` values were raised out of their
  40%-lightness band when they went from a 3px bar to a 13px glyph. **Known gap:** they were chosen
  against a dark `BgElevated` and `ThemeBridge` does not derive them, so on a light theme a header glyph
  is a pale colour on a pale ground. The fix is the one the phase markers already use
  (`ThemeBridge.Marker`, which pulls a colour toward the foreground when `IsDark` is false); it has not
  been applied here yet.
- **Selected is not hover.** Two states, two treatments — selected gets its own ground plus an accent
  leading edge. The same brush for both makes the selection unfindable while the pointer is in the list.

**Rows and lists**

- **One left edge per panel.** Filter, heading and rows share a margin; scrollbars go on the right.
- **One row height in a list.** Reserve the secondary line even when it is empty. Two heights
  interleaved leave nothing to line up and no rhythm to scan by.
- **The name never gives way.** In a `DockPanel` the docked child takes what it wants and the fill child
  gets what is left, which is how a row ends up as `B…` beside a fully spelled-out branch, or a tile
  header shows five buttons and no title. Either give the secondary thing its **own line**, or stand
  *it* down below a width (`LeafTileView.SplitButtonsNeedWidth`).
- **Chips are for the rare exception** — `Error`, `NOT FOUND`, `CUSTOM` — and work because you see one
  at a time. A value present on **every** row is metadata: plain text, small, muted, on the name's own
  left margin. Twenty boxed outlines give a column a zigzag edge and no rhythm.
- **Say what it is, or offer to fix it.** A row that can be acted on carries the action (Create
  repository), not a label describing the lack.

**Controls and reuse**

- **A button label is primary text.** `TextSecondary` is the shade for a fact *beside* something; used
  on a control it made every secondary button look disabled — a state those buttons also have and could
  no longer be told apart from. A label on the accent uses `AccentForeground`, picked from the accent's
  own luminance in `ThemeBridge`, because the theme's foreground is chosen to be read on the terminal's
  background and comes out as grey on blue. A control's edge is `BorderStrong`, one step above the card
  it sits on.
- **A heading row's actions go above the list and share one class** (`Button.header-action`: Add,
  Re-detect, Test All, Detect). Below the list, an add button moves every time the list changes length
  and is off screen exactly when the list is long enough for the user to want another entry. Two styles
  side by side read as unrelated controls that happen to be adjacent.
- **An overflow is a second route, not the only one.** What is a button and what is only a menu item
  follows how often it is pressed — a fact about use, not about the code. Restart shell and New session
  were put behind the `…` on the reasoning that they are used "once a session"; they are among the most
  pressed things in the application. Everything in the tile header's button strip is in its menu too, so
  the buttons can stand down at narrow widths (`LeafTileView.ApplyHeaderWidth`) without anything
  becoming unreachable — splits first, because dragging one tile onto another does the same job.
- **One writer per property.** A code-behind rule and an `IsVisible` binding both write at the same
  priority, so the last one to fire wins and neither reliably: the tile header's Restart button was
  visible or not depending on whether the tile had been resized or its content had changed more
  recently. Whichever writes it does so alone, and reads the view model itself
  (`LeafTileView.ApplyHeaderWidth`).
- **A modal takes the keyboard when it opens.** Focus the first field, or the first thing a user does
  after asking for a new entry is reach for the mouse.
- **Name a class for what it is, not where it sat.** `add-row` described a button's old position; when
  the position changed the name became a trap for the next reader. It is `choice-row` (a full-width
  option) and `header-action` (something a heading row does to its list).
- **Reuse the class, do not restate it.** `TextBlock.section`, `Button.outlined-sm`, `Border.keycap`,
  `StackPanel.workspace-meta` exist so a heading is a heading everywhere. A local set of font properties
  is a second definition that will drift.
- **Keep the state-carrying control visible; hide the rest.** An overflow `…` takes the once-a-session
  actions; a toggle whose state the header must show (the microphone) stays out of it, because a light
  behind a menu is not a light.
- **A tri-state answer needs three states.** `bool?` where the check is asynchronous, or every item
  asserts the negative until the first pass finishes.
- **Writing to the user's disk asks first**, and an unwired `ConfirmAction` answers **no**.

**Adding an entry to a list opens a form, it does not grow the list.** Three do: the manual database
connection, an agent instance and a provider instance. They share one overlay in `SettingsView`
(`SettingsViewModel.IsEditingAnything`, `CancelEditing`, and `BeginEditing` which puts every other form
down first, or two would be drawn stacked in it), on the same `Border.modal-card` a dialog uses.
As rows they were unusable in a way that only shows at the keyboard: the form is taller than the
viewport, so opening one pushed the list it came from off screen and put Save below the fold. Escape and
the scrim close the form, and only then the dialog — the innermost thing first, or the user cannot tell
which of the two they just cancelled.

## Tiles

Every tile's content implements **`ITile`** (`KindId`, plus change notification and disposal — nothing
else), and announces what it can do by which of seven interfaces extending it it implements: `IBusyTile`
(the workspace row's working light), `IFileContent` (the file follows the tile's name), `ITileActions`
(the header's buttons and what a paired phone may press), `ITextInputTile` (where a dictated sentence and
an Enter land), `ICustomBackgroundTile` (the terminal's inset and its own background colour), `IProcessTile` (the process
it started, which is what the workspace row's memory reading is measured from), `IDescribedTile` (what
the tile is *running*, beside its name in the header — an agent tile answers with its instance and model,
and a kind with nothing to add simply does not implement it). **One class
per kind** — `Services/Tiles/*TileKind.cs` — says what it is called, what it looks like, how it is built
from saved state and what it writes down; **one line per kind** in `App.BuildTileCatalog` registers it
together with the view that draws it, and `LeafTileView` resolves that view by a dictionary lookup on
`KindId` rather than by switching on a view model type. A kind also says **what to call its next tile**
(`NameFor`, numbered by default and an adjective-and-animal for the terminal) and **what to ask before
one exists** (`SetupOptions` — the shell chooser and the agent-instance chooser), so neither the workspace
nor the empty tile branches on which kind it is holding.

`TileContentType` is closed and kept only as the exhaustive record of what is on people's disks:
`TileNode` reads it through `TileKindIds.FromLegacy` and **writes it back beside the new format**, along
with the other old per-kind fields, so an installation Velopack has rolled back still opens its layouts
instead of reading every leaf as an empty tile and saving the emptiness over them on the first splitter
drag. **A layout written before this existed opens unchanged**, and there are five rules and a
golden-file test making that true, including a one-time `{id}.pre-kind.json` copy and a refusal — for the
whole session, on every save, not just the one a migration asks for — to write a layout holding a kind
this build does not know. The dual write is a bridge with an end: when no supported build reads the old
fields, the getters go.

**A tile can take the whole workspace, and five kinds may** (`IMaximizableTile` — terminal, agent, note,
todo, goal). The gesture is a header button that changes shape (`Fullscreen` → `FullscreenExit`, lit while it
is on), Ctrl+Shift+F, and an overflow entry. It is drawn by `TileMaximizeScope` — one per workspace, like
`TileActivationScope` — writing `SplitTileNodeViewModel.Solo` on every split between the root and that
leaf, so each of them draws one child at full size: the same `LeafTileView` and the same
`TerminalControl` the layout already held, because a full-screen view built as a *second* view of the
same tile hands the tile back with an empty shell. **Nothing is persisted and nothing is re-parented**,
and every way out — closing the tile, splitting it, clearing the root — restores first, from a
*remembered* path rather than a re-walked one: each of those leaves the leaf pointing at parents it no
longer has, and a split soloed on an unreachable child is half a workspace invisible for the session.
The goal tile qualifies for the same reason the first four do rather than as an exception: it is a
conversation in one column with nothing docked beside it, so the room buys more transcript — and a plan,
a diff and a review full of file paths are what it has to show in the 300px column a workspace of four
tiles leaves it. The kinds that lay themselves out in panes of their own (git, database, usage)
implement nothing — more room stretches their whitespace and would put a splitter inside a tile with no
splitter around it.

**Restart shell is `IsDestructive`, so it is a header action and not a phone one.** It kills whatever the
shell is running, which is why the header asks first — and a phone that cannot be shown what is about to
die must not be able to press it, confirmation or no (`PhoneTileActions`).

**Everything else is in [`docs/TILES.md`](docs/TILES.md)** — the interfaces and why each earns its place,
the catalog and the one layering boundary it keeps, the persistence format and its migration, tile
actions and the destructive filter that stands between them and a phone. Read it before touching
`ViewModels/ITile*.cs`, `ViewModels/I*Tile.cs`, `Services/Tiles/`, `Models/TileNode.cs` or
`Views/LeafTileView.axaml.cs`.

## Split tiles architecture

Recursive binary tree: `LeafTileNodeViewModel` (terminal/editor) or `SplitTileNodeViewModel` (H/V + two children). `TileNodeView` manages views manually (not DataTemplate); rebuilding the tree re-parents live terminals with no bracketing, because detaching one does not end its session.

`LeafTileNodeViewModel.IsActive` — `TileActivationScope` (per-workspace instance) guarantees that only one tile is active. `LeafTileView` reacts to `IsActive` — the card's own outline turns `AccentOutline` (`TileCard`) — a muted accent, because the longer the line the quieter it has to be to carry the same weight, and the full accent that suited a 2px strip read as a blue frame once it went all the way round, the toolbar lifts to `BgElevated`, and an inactive tile's header recedes to 0.55 opacity. The outline replaced a 2px strip along the top of the toolbar, which was the right marker for a square tile in a grid of splitters and the wrong one the moment the tile became a rounded card: the radius eats the strip's ends, and what is left is a short line floating inside a corner rather than an edge. The header only — the content of an inactive tile is still being read, and dimming a running terminal because the focus is elsewhere makes every split worse than no split.

**The window is one canvas with cards on it.** `MainWindow` is painted `BgCanvas`; the workspaces panel and every tile are cards on it — same ground, same `RadiusTile`, same `BorderSubtle` hairline — separated by one gutter width, which is the 8px splitter column between panel and content, `TileNodeView.TileGap` between tiles, and the padding `WorkspaceView` puts around the outside. That last one is the whole point: without it the outermost ring of cards is cut off by the window frame, and a card clipped by the title bar is not a card.

**A tile is a card.** `WorkspaceView` is painted `BgCanvas` (below `BgBase`, derived in `ThemeBridge` and going the other way on a light theme, because the canvas is only ever seen in the gaps); `LeafTileView`'s outermost element is a `Border` with `RadiusTile` and a `BorderSubtle` outline, wrapping a second `Border` at `RadiusTileInner` that does the `ClipToBounds`, so nothing inside — a terminal's own background included — has to know the radius. **The two borders are not one border.** `ClipToBounds` on the border that also draws the outline clips to the rounded-down bounds rectangle, so at a fractional desktop scale (125%, 150%) the right and bottom edges fall outside their own clip and the outline renders as an L along the top and left. The panel's card has the same pair for the same reason. `TileNodeView.TileGap` is the canvas showing between two tiles and is the splitter's whole hit area, which is why that splitter carries `GridSplitter.tile-gutter`: transparent and stretched, because the gap *is* the divider and a drawn bar on top of it would be a second one (transparent rather than unset — an unset background is not hit-testable and it would stop being draggable). **A class, not the base `GridSplitter` style**, which stays a visible 2px bar: the splitters *inside* tiles — the git tile's list against its diff, the diff view's two editors — sit in `Auto` columns and take their width from it, so making the base style width-less collapsed them to zero and made them impossible to grab.

**A splitter cannot squeeze a tile out of sight** (`TileMinimumSize`, 50px along either axis). The minimum is a property of the whole subtree rather than of the pane being dragged: a star-sized column takes the size the splitter gives it and never grows to what its content needs, so a column squeezed to 50px that holds a further split lays *its* two tiles out past its own edge, under the opaque card next door — the tile is gone, by the route the minimum exists to close. Every leaf along the axis wants its 50px and every split along it also spends a `TileGap`, which is what makes the gutter part of the minimum's arithmetic and not just a look: widening it makes every layout containing a split wider. `TileNodeView.ShowSplit` sets the sum on both definitions and `TileMinimumSize.Fit` scales the pair back proportionally when the grid is narrower than the two together — a floor the layout will not go below does not shrink anything when it does not fit, it pushes the far pane past the edge and clips it, which is the same disappearing tile reached by narrowing the window. The guarantee is about the splitter, not about a window too small to hold the tiles at all. `UpdateSplitRatio` therefore stores the ratio from the definitions' actual sizes, so what is persisted is where the splitter ended up after the minimum had its say.

**Only a terminal's content is inset from the card** (`ICustomBackgroundTile`, which only `TerminalTileViewModel` implements — the view asks the content rather than testing its type). A terminal is text against an edge and wants the gap; every other tile's content is its own chrome — bars, lists, a composer — and drawing that inside an inset left a square-cornered rectangle floating in a rounded card, with a sliver of card colour round the bottom corners where the two shapes disagreed. Those tiles run to the card's edge and take its corners from `ClipToBounds`.

**The header gives up its split buttons before it gives up the tile's name.** In a `DockPanel` the name gets whatever the docked buttons leave, which in a narrow column was nothing: four tiles in a stack showed a row of icons each and not one name between them. `LeafTileView.ApplyHeaderWidth` stands the two split buttons down below `SplitButtonsNeedWidth`, because closing and the overflow have no other route while a split is also a drag away.

Each tile wears its kind's icon in the header — the same one the empty tile's chooser offers, because both are drawn from the same `ITileKind` — in its `TileAccent*` colour, set from the code-behind because both follow the kind (`Views/TileIcons.cs` maps the kind's `IconId` to a `MaterialIconKind`), and `Views/ModelSearch.cs` is
the matching rule the model fields complete by — every typed word anywhere in the id, in any order, which
is what makes a catalogue of hundreds usable and is an opinion, so it is pure and argued in a table test. Those six accents were raised out of the 40%-lightness band they were picked in: they used to be drawn only as a 3px bar and a 22px chooser icon, and at 13px on `BgElevated` the old values were dark smudges. The header's actions are **buttons *and* menu items**: the `…` overflow holds Restart shell, New session and both splits and never stands down, while the buttons are the fast path and give way as the tile narrows (`ApplyHeaderWidth` — splits at 260px, restart and new session at 190px). Splits go first because dragging a tile onto another does the same job. The microphone is neither: it is a toggle whose state the header has to show, and a light behind a menu is not a light.

`TileActivationScope.SuppressActivation()` — guard (IDisposable) blocking the GotFocus → Activate cascade during programmatic Focus() and Rebuild. Used in `LeafTileView.FocusContent()` and `TileNodeView.Rebuild()`.

## Tile ID

Each tile has a persistent `TileId` (`Guid.NewGuid().ToString()`, hyphenated format). Generated on creation, saved in `TileNode.TileId` in workspace JSON. Propagated to `TerminalTileViewModel.TileId`.

In startup script `${tileId}` is replaced with the current `TileId` — both on first launch and on restart.

## Terminal key handling

`TerminalClipboardCoordinator` (static, window-level) handles **Ctrl+C / Ctrl+Shift+C** copy across all tiles: a single tunnel KeyDown handler on `MainWindow` copies from whichever terminal holds a selection (focused first → most recent selection owner, tracked via the control's `SelectionChanged` → any live terminal from the weak registry). Without a selection Ctrl+C falls through and keeps SIGINT semantics. Text-editing controls (TextBox, AvaloniaEdit) are never hijacked. Terminals register in `TerminalTileView` right after construction and unregister in `TerminalTileViewModel.Dispose`. Ctrl+C is marked handled only if the copy succeeded — a refused clipboard must not cost the user the interrupt as well.

**Ctrl+V** is handled by the control: clipboard **text** is pasted (filtered, bracketed when the app asked for it). **Ctrl+Shift+V** / **Shift+Insert** paste as well. **Alt+key** and everything else travel as win32 INPUT_RECORDs whenever the child enabled `?9001` (PSReadLine does, for every prompt).

**Image paste into a TUI** works through `ForwardCtrlVWhenClipboardHasNoText = true` (set in `TerminalTileView`): the control pastes text when there is text, and otherwise sends the Ctrl+V keystroke on to the child, so Claude Code reads the image off the clipboard itself. The control's default is off (Windows Terminal parity); this app opts in because hosting AI agents is what it is for.

**Text wins when the clipboard holds both.** A copy from a browser or a screenshot tool often puts text *and* an image on the clipboard; the rule above asks only about text, so the text is pasted and the agent never gets the chance to take the image. Deliberate — the alternative is guessing which one the user meant — and **Alt+V** remains the way to hand Claude Code the image regardless (the control never intercepts it; it goes through as `ESC v`).

### One thing the old control did and this one does not

**A left-drag no longer always selects locally.** mTiles used to set `SelectionOverridesMouseTracking`; `Terminal.Avalonia` rejects a one-way override (see its `docs/MTERMINAL-COMPAT.md` → *Deliberately not adopted*) because it leaves an application with no way to receive the mouse at all — mc, vim, opencode click targets. Inside a full-screen app that grabbed the mouse, selection now needs **Shift** held: the xterm convention, but a habit users have to learn. Recorded so nobody rediscovers it as a bug.

## Alt-buffer cleanup (TUI apps)

Handled by the control, no app-side code: leaving the alternate screen releases the mouse grab, and **Shift** overrides a grab that is still latched. A TUI killed with Ctrl+C therefore no longer floods the shell with SGR mouse sequences.

## ThemeBridge — UI synchronization with terminal theme

`ThemeBridge.Apply(TerminalTheme)` in `App.axaml.cs` dynamically derives UI colors (backgrounds, borders, text, accents) from the active terminal theme. Dark/Light mode is derived from `TerminalTheme.IsDark` — no separate theme selector. Called on startup and on every `SettingsChanged`. Thanks to `DynamicResource` the entire UI reacts immediately to theme changes.

## The launch chain

**Shell profiles are gone.** A profile was a name, a shell, a startup script, a fallback and a required
AI binary — everything an AI CLI needed, written out by hand in Settings and kept working by hand. Those
are the agent's own business now (`Services/Agents/`), so a terminal tile is a shell and nothing else,
and the Settings dialog has neither a Profiles tab nor an AI Tools one. What is left of a profile on
disk is read by exactly one thing: `AgentTileMigration` matches a saved leaf's `userProfileId` against
`AppSettings.ShellProfiles` to work out which of somebody's terminal tiles were an AI CLI in a shell, and
turns those into agent tiles. **Nothing seeds, edits or deletes a profile**, which is why the key is
still in the settings file — clearing it would take the migration's evidence with it, a launch before the
workspace holding those tiles is even opened. Both go a release from now.

The chain itself stayed, because it is what makes an agent tile survive its CLI crashing.

**DirectLaunchSession** (`Services/DirectLaunchSession.cs`): when a profile has `FallbackScript` → `LaunchScripts.RunsCommandChain` is true. Commands are run via `shell -c "command"` (not interactively). Chain: startup → fallback → plain interactive shell. Each command is started and then **awaited to its end** (`TerminalControl.WhenSessionEndedAsync(sessionId)`), so the verdict is the **exit code plus how long it ran** — there is no "it survived N seconds, so it worked" window any more:

| Outcome | Meaning | What the chain does |
|---|---|---|
| spawn throws | tool not installed, bad cwd | next command |
| non-zero, ran < `Established` (2 min) | the command does not work | **next** command — never the same one |
| non-zero, ran ≥ `Established` | a working tool crashed | **same** command again |
| exit 0, ran ≥ `MinLifetimeForRelaunch` (10s) | the user quit the tool | whole chain from the start |
| exit 0, ran < 10s | it did not stick | **next** command (the fallback is what an agent names for this) |
| no exit code at all | connection lost | as non-zero |

Every relaunch is rate-limited **for the chain as a whole**, systemd-style: at most **3 in 10 minutes** (`RelaunchBudget`), after which the chain carries on to the next command instead. One relaunch is free: a **clean** exit after at least `Established` is the user closing their tool on purpose, and quitting it four times in a morning must not have the tile refuse to bring it back (`CountsAgainstBudget`). A rate over a window, not a running total — a total would give up on a tool used daily once its fourth crash came round, however many months apart the four were. Chain-wide, not per command, and that part is structural: a per-command budget was renewed every time the chain moved on, so a chain whose fallback exits cleanly looped forever between fallback and top, renewing its budget on each lap. Nothing resets this but time.

The rules live in `ChainPolicy` (thresholds + `Decide` + `CountsAgainstBudget`), separate from the chain that carries them out and pure, so they are readable in a table test without a terminal, a dispatcher or a stopwatch. The lifetime is **not** measured by the host: `SessionExitedEventArgs.Lifetime` comes from the terminal, which stamps the session when it spawns the child — timing it around the host's own `await` measures the wait instead.

One loop is deliberately unbounded: a command that exits **cleanly** after `Established`, for ever, is restarted for ever. Every lap costs at least two minutes of a real session, so it is not a spin, and it is indistinguishable from a user quitting and reopening their tool by hand — bounding it would stop the tile honouring the very gesture it exists to honour.

Off the end of the chain is the interactive shell, which is not watched, so a tile is never left dead. Both thresholds are load-bearing: judging on time alone made `claude -r <unknown-id>` (**21 s** to print "Invalid session ID" and exit 1) look adopted, read its failure as the user quitting, and relaunch it every 21 s for good with the fallback unreachable — while judging on the code alone would demote a tile permanently to a bare shell the first time a long-running tool crashed. Without `FallbackScript` → classic mode: shell starts interactively with the startup script as the session's startup input.

`LaunchScripts` (returned by `TerminalTileViewModel.ResolveCurrentScripts`) decides which of the two paths a tile takes. `RunsCommandChain` is true exactly when `Fallback` is non-blank — something that names a fallback is something that launches commands. It was a stored third value every caller computed the same way, so the type could hold combinations nothing can produce; deriving it also removed a dead disjunct (`Startup is not null || …`) that made the rule look like it had two halves. Blank is normalised to null in the `init` setters, so `with` cannot slip a script of spaces past it.

The instance **owns** the tile's chain: it relaunches only the session whose `SessionId` it started (taken from `RestartAsync`, which returns it — reading `SessionId` afterwards can describe a session someone else opened), and whoever replaces the chain (restart, tile close) disposes it first — `TerminalTileViewModel.ReplaceLaunchSession`, which owns that invariant so no caller can break it. Without both, a restart leaves two chains fighting over one tile and closing a tile resurrects its shell as an orphan process.

Tile creation flow:
1. Empty tile → click Terminal → if this machine has more than one shell, the kind's own setup step appears (Back / Default shell / one card per detected shell). Click Agent and the step lists the configured instances this machine can run. Both steps are `ITileKind.SetupOptions`, not something the empty tile knows about terminals or agents
2. The choice → `TerminalTileKind.Create(context, { "shellName": … })` or `AgentTileKind.Create(context, { "agentInstanceId": …, "agentId": … })`. **The same call a saved layout makes** — choosing *is* handing a new tile its initial state
3. `TileLauncher.Launch` → `LaunchScripts.RunsCommandChain` → `DirectLaunchSession.Start()`, else → `ShellStarter.StartAsync()` with the startup script

### Session resume

A tile's `TileId` is the agent's session id, so a restart reopens the same conversation. **Claude Code** and **pi** take an id outright, so `IAiAgent.SessionIdForTile` hands the tile's own id straight through — pi through `--session-id`, which creates and resumes alike, and Claude Code through `--resume <id>` with `--session-id <id>` as the fallback: measured on 2.1.251 the creating flag refuses an id already in use, so leading with it meant every launch after the first fell through to a fallback carrying no id at all.

**OpenCode cannot be told one**: `opencode --session <id>` only ever *continues* a session (unknown id → `Session not found`, exit 1 after ~1.4 s, which the chain reads as "next command"), and the TUI creates no session at all until the first message — so there is nothing to observe at startup and pick up either. The way in is `opencode import`, which takes a JSON document and keeps its `id` verbatim; `ses_${tileId}` is legal, so no tile→session map exists anywhere. `OpenCodeSession` writes that document, `TileScript` expands `${opencodeSessionFile}` to its path (a pure function of the tile id, so the launcher can ask whether a script refers to one without writing anything), `TileLauncher` writes it before either launch path runs, and `OpenCodeAgent.Resume` answers `opencode --session ses_<tileId>` falling back to `opencode import "${opencodeSessionFile}" ; opencode --session ses_<tileId>`.

Measured against **opencode 1.18.14**, all load-bearing: the document's `projectID`/`directory` are **ignored** — the session lands in the project of the import's *cwd*, which is why the import runs as one of the tile's own commands; re-importing an existing id is **non-destructive** (title and messages kept), which makes it create-if-missing rather than a way to wipe the conversation being resumed; **every** field is required (`id`+`time` alone is rejected with `Missing key`, which does not say which key); `version` is not validated. It is opencode's *export* format, not an API — when it moves, the import fails, the resume finds nothing, and the chain ends at an interactive shell: a tile without its history rather than no tile. `OpenCodeSessionTests` pins the shape so that surfaces as a failing build.

The commands are the agent's own, so a user who had the old (never-working) seeded profile gets the fix by their tile becoming an agent tile — there is nothing left to migrate a script into.

**Codex** and **agy** name their own session; see `SessionStrategy.CapturedAfterStart` under *Agents*.

Shell persistence in layout: the tile's state carries `shellName`, and `TerminalTileKind.Create` resolves it through `ShellTerminalCatalog`. A shell that is no longer installed falls through to the default.

## Settings UI

Settings dialog as a modal overlay with responsive sizing (50% window width / 80% window height, min 420×400). Four tabs:
- **General** — Default Shell, Appearance (color theme, font), Terminal (font), and the settings file
  itself: **Export** and **Import** (`SettingsPortability`). On this tab rather than a page of its own
  because what it carries is the whole dialog. **Secrets do not travel** — every field encrypted at rest
  is written out empty, since a DPAPI blob is bound to this user on this machine and would not work
  anywhere else, and the alternative is plain-text keys in a file somebody is about to share. `ExtraEnv`
  is the documented exception (nothing here can tell a proxy address from a token in it), so the warning
  is shown *before* the file is written. An import is a replacement rather than a merge — a mixture
  nobody chose is worse — except that `SettingsService.Replace` **keeps every secret already set up
  here**, matched by id (provider keys *and* manual database connection passwords, one restore for each
  field the export blanks, or a file exported from this machine and imported back into it would empty
  half of them): a file meant to add configuration must not remove the one part of it nobody can retype
  from memory. `Replace` also runs the **same seeding and legacy migration the constructor does** — an
  imported file can be older than this build, and an agent it has never heard of would otherwise have no
  instance at all until the next restart, missing from both choosers with nothing on screen saying why.
  Afterwards `ReloadFromSettings` puts **every** page back in step, not the three the import obviously
  touches: the other pages save as you type, so a Speech tab still showing the old shortcut writes it
  straight back over the imported one the first time any control on it is touched, and nothing on screen
  says that happened. Speech, Phone and the default shell reload through the same methods the
  constructor uses — which write the backing *fields*, so the notification is raised once, for
  everything, rather than as a hand-kept list of two dozen property names
- **AI** — the agent instances a tile can be created from and the providers they authenticate through.
  An agent row carries its name, its CLI, a `NOT INSTALLED` chip and, when an `InstallPlan` exists,
  **Install…** — which shows the command, then runs it in a **terminal tile in the current workspace**
  (`TerminalTileKind.StartupScriptKey`, set at creation, never saved and **consumed at the first launch**,
  so neither reopening the layout nor Restart shell installs anything again). A provider row is edited on the same overlay the manual database connection
  uses, with Test (`IAiProvider.TestAsync`), Models and — for a local server — Discover
  (`LocalProviderDiscovery`, on demand, network sweep opt-in). The model field is an `AutoCompleteBox`
  over the provider's own list rather than a combo box, because that list runs to hundreds. The agent
  form's account chooser is an `AccountChoice` (kind, id **and** label), never the label alone: nothing makes
  an instance's name unique — a new one is seeded with the provider's own display name — so two keys for
  the same service, which is the case several instances exist for, are two identically spelled rows, and
  a chooser keyed by name saved and reopened as the first of them, authenticating the agent as the wrong
  account. **The chooser holds only the providers that agent can speak to** (`AiProviderCatalog.IsCompatible`,
  rebuilt when the agent changes, and it drops a selection the new agent cannot use): a pairing stored
  here makes the instance unavailable everywhere — `AiAgentCatalog.IsAvailable` refuses it, so it is gone
  from the Agent tile's chooser and from the Goal tile's list — and the row is the only place that can
  say why, which is what its `UNAVAILABLE` chip is for — one sentence from `AgentAvailability`, the same
  rule the choosers hide on, so what is hidden and what is explained cannot drift apart. The effort chooser is the same idea one level
  down: the agent's own `SupportedEfforts` narrowed by the chosen model's (`NarrowEfforts`, fed by the
  models fetched for the chosen account), and a level neither accepts falls back to the tool's own
  default.
  **`bypass` asks once before it is stored here too**, and an unwired `ConfirmAction` answers no
  **Under the two model fields, what the provider says about the model, said in tokens.** The Model and
  Fast model fields each show `N tokens context` as soon as the chosen model is one the account
  describes — answered free from the list already fetched (OpenRouter, LM Studio), or, for a model the
  list does not describe (Ollama's naming is all it does), by one debounced per-model call. **The
  readouts race the form**, and every path through the lookup cancels the one in flight first — a
  cleared field, an answer straight off the list, a newer keystroke — because the one that answers
  after the field moved on would otherwise pass the cancellation test and write its number under a
  question nobody is asking any more. The **Auto-compact** field is Claude Code's only (hidden on the
  other five, which read none of this) and is the manual `CLAUDE_CODE_AUTO_COMPACT_WINDOW`: empty is
  the fallback for the launch to work out from the model's context (see `ModelContextWindow`), typed is
  a decision and is handed over unchanged.
  A third list, **Sign-ins**, is the CLI's own logins — a second subscription and a third. A row asks
  for one thing, what you call it; saving makes the directory and **Sign in** shows what will run and
  then opens a tile with the agent's environment already set, where the user runs the tool's own login
  command. It asks the way an install asks, and for a reason beyond symmetry: the sentence naming the
  login command is in the plan's note, the route a plan takes to a tile carries the command alone, and
  without the question that note was built and thrown away — leaving a tile with the environment set, an
  empty prompt, and a row still saying "not signed in". That command goes
  through the *startup script* rather than the process environment, which is the opposite of what a
  launch does and deliberately: the rule exists because a script lands in the scrollback and the shell's
  history, which is fatal for a key and harmless for a directory the user just named. It reaches the tile
  through `InstallCommand.For` — the same route an install takes, and **not** `InstallPlan.CommandLine`,
  whose quoting is for reading: every part of a shell line has a space in it, so the tile was handed the
  whole command inside quotes and printed it instead of running it, while the row went on saying "not
  signed in". The row's status
  is **read from the CLI's own files every time the page loads** (`SignInStatus`, and for Claude Code
  the address and plan out of `.claude.json`/`.credentials.json`, never the token) — a remembered
  "signed in" keeps saying so after a logout in a terminal. Those reads are **walked, not parsed**
  (`AiAgent.ReadJsonString`): `.claude.json` carries a Claude Code installation's per-project history
  and grows into the megabytes, and the read runs on the UI thread once per sign-in row and once per
  account-chooser rebuild — a DOM of the whole file is more than naming who a row belongs to may cost,
  so the reader stops at the answer and everything else is skipped token by token. The section hides itself where no
  installed agent supports one — unless sign-ins already exist, which stay reachable, so a CLI dropping
  off `PATH` cannot strand rows nobody can then rename or remove. The agent form's **Provider** field is now **Account** and holds both
  kinds, because they are one question. **There is no List button**: the model list is fetched when the
  account changes, which is the one event that changes the answer, and a subscription simply fetches
  nothing — it has no catalogue to ask. The field narrows by every typed word in any order
  (`Views/ModelSearch.cs`), because an id is punctuated by whoever published it and the separator is the
  part nobody remembers.
- **Database** — enable service, HTTP port, SQL Server/PostgreSQL credentials, scan interval, manual connections
- **Speech** — dictation on/off, shortcut (captured by pressing it), push-to-talk vs toggle, microphone,
  language, auto-Enter, vocabulary, and the model list with download/delete and progress; plus a **Phone**
  section (keep the bridge running, preferred port, and the phone's own auto-Enter). Those last two are
  the only settings on this tab that restart a running service, which is why the bridge debounces what it
  hears from here instead of acting on every intermediate value the spinner produces

`SettingsViewModel.SelectedTab` controls tab visibility, and the pages are named in `ViewModels/SettingsTabs.cs` (`General`, `Ai`, `Database`, `Speech`) — used by the view model, by the database tile's "open my settings" button, and from XAML through `{x:Static vm:SettingsTabs.…}`, which replaced the `Zero`…`Four` boxed-int resources. Constants rather than an enum: the selection is bound as an `int` to command parameters in two AXAML files, and the numbers were the problem, not the type. The Database tab has its own sub-tabs (`DbSubTab`: 0=Config, 1=Databases). Tab button styles: `settings-tab` / `settings-tab-active` in `Controls.axaml`; a bordered button on a settings row is `outlined-sm` there, not ten inline properties.

**The database form is the only one that is not saved as you type**, because applying it restarts the database service. `SettingsView` is therefore a `DockPanel` with a pinned Save & Apply bar at the bottom and the `ScrollViewer` *inside* it — docking to the bottom within a scroller pins to the bottom of the content, which is no pinning at all. The bar shows whenever `HasUnsavedDatabaseChanges`, which compares the form against the stored settings rather than remembering that something was typed, so undoing an edit puts it away.

Leaving the tab does not discard the edits, and closing the dialog (button, Escape, click outside — all through `MainWindowViewModel.CloseSettingsAsync`) or the application (`ConfirmShutdownAsync`) asks first. Answering yes discards; answering no reopens the dialog on the Database tab, because "you have unsaved changes" is no use without showing which.

## Where AI tools went

There is no AI Tools tab and no `AiToolDetector`. What that table could say about a CLI was whether it
was installed and what `--version` printed, which is not enough to launch one: an agent has to know how
to resume its own conversation, which flags mean "read-only" here and how to read what comes back, and
none of that can be written into a row of a settings grid. So it is a class (`Services/Agents/`), the
list of them is closed, and what the user configures is an **instance** of one.

**A new agent class carries its own front door: `InstallUrl` and `InstallPlan`.** Both live on the
agent, never on the instance. `InstallUrl` is the tool's own page — the Settings AI row renders it as a
link that opens the browser. `InstallPlan` is the install command, offered by the row's **Install…**
button only while the CLI is not on this machine (`CanBeInstalled`), shown to the user before it runs,
and run in a terminal tile — never a hidden process. Leaving either null hides that agent's link or
button and is a decision, not an omission.

Two pieces of the old code were kept because the mechanism was right: `ExecutableFinder.Anywhere` is its
scan of `PATH` and the handful of places a global npm, go or cargo install puts a binary — a GUI process
does not inherit the `PATH` a login shell builds — and `AiAgentCatalog.Locate` holds that answer for
thirty seconds, which is the same window the table's own detection cache had.

## Goal tile

Iterative AI-driven development workflow tile (inspired by Karpathy's autoresearch). Automates the loop:
**user goal → AI clarifying questions → user answers → AI creates plan → user approves/rejects → AI
implements code changes → AI reviews at four severities → iterate until the completion criteria set on
the tile are met → summary**. A goal can also be worked out from the uncommitted changes rather than
typed.

**The working tree is not the tool's to undo.** The review is handed the whole of `git diff HEAD` as
"the changes that were just made", which is untrue whenever the user is working in the terminal tile next
door — so the reviewer reported their parallel change as a finding, the loop passed it back as something
to fix, and the next attempt reverted their files and deleted the ones they had not committed. Two
sentences in the prompts close it (`GoalPromptBuilder.OtherPeoplesWork` and its counterpart in the review
prompt) and `GoalBaseline` photographs the tree as the goal starts, so that when a prompt is ignored — as
the same failure against other agents shows it can be — the loss is one `git checkout` rather than an
afternoon. Neither a stash nor a commit: a private `GIT_INDEX_FILE`, `commit-tree` beside the history and
a ref under `refs/mtiles/`, so nothing the user can see moves. **Untracked files are the point** — no
form of `diff` shows one and `checkout HEAD` cannot bring one back. Read *docs/GOAL.md* before touching
it; four details there were measured and each is load-bearing.

**The tile is a conversation and nothing is docked to the bottom of it.** One `ScrollViewer`, one
column: the transcript, and then whatever the tile is asking for — the round of questions, the plan
box, the finished-run actions, the composer with the detect buttons under it — each as a block where the next thing
in a conversation goes. A round is *replaced by the record of itself* when it is answered, in place,
rather than being asked in a docked panel and recorded as a numbered paragraph several screens above
it. Anything in the conversation can be copied on its own — a message, one finding, one question with
its answer — through one handler and one builder, so a finding copied alone reads exactly as it does
inside the review it came from.

**Everything else is in [`docs/GOAL.md`](docs/GOAL.md)** — the phase machine, the prompts and how they
are fitted to a command line, the structured review and its severities, the completion criteria, the
per-goal SOLID switches and the two health checks, what a run remembers between attempts, Continue,
the `@` file mentions, the persistence rules, and the reasoning behind each. The section grew to a third of this file, which is the same argument that moved
dictation out: read it before touching `Services/Goal*`, `Services/WorktreeReader.cs`,
`Services/CommandDisplay.cs`, `Services/*FileMention*` / `Views/FileMentionBehavior.cs` or
`ViewModels/Goal*` — the last of
which is a wider glob than it looks: `GoalCriteriaEditor`, `GoalBadge`, `GoalSolidToggle` and
`GoalQuestionAnswer` are view models of their own, not part of the tile's.

## Database tile

Per-workspace bridge that lets LLM agents (Claude Code, OpenCode, etc.) query local databases directly via HTTP — without manual connection setup. The tile auto-generates context files (`claude.local.md`, `AGENTS.md`) so agents discover available databases and how to call them.

**Purpose:** LLM agent running in a terminal tile sends `GET /query/{server}/{database}?sql=SELECT ...` to the local HTTP server → gets JSON results back. No credentials exposed to the agent; access is controlled by the user in the tile UI.

**Write protection (SQL Guard):** INSERT/UPDATE/DELETE blocked by default. User unlocks per-database with the RW toggle. DROP/TRUNCATE/ALTER always blocked regardless. If the agent sends a write query and write is disabled, a confirmation dialog appears — the user approves or denies in real time. Block comments (`/* */`) and line comments (`--`) are stripped before keyword scanning to prevent bypass attempts.

**Tile UI:** List of selected databases with RW/RO toggle, list of all discovered databases with add button. Context file generation is automatic — driven by global database service setting (Settings) and whether any databases are selected in the tile. Tile reacts to `DatabaseServiceManager.StateChanged` and `SettingsChanged`.

**Architecture:** `DatabaseServiceManager` (singleton in App) manages `DbRegistry`, `DbLogger`, `DiscoveryService` and `DbHttpServer`. Tile registers its workspace with the manager (`RegisterWorkspace`/`UnregisterWorkspace`).

**Access control:** HTTP server exposes only databases selected in at least one workspace tile. `IsDatabaseAllowed(key)` checks the union of grants across all workspaces. `GET /databases` returns only allowed databases. Host header validated to `localhost`/`127.0.0.1`/`::1` — blocks DNS rebinding attacks from browser tabs.

**Database discovery:** SQL Server via UDP broadcast on port 1434 (SQL Browser). PostgreSQL via port scanning (default 5432, 5433, 5434) on localhost and the local network. Manual connections also supported. Discovery runs periodically (default every 30 min).

**HTTP Server:** `DbHttpServer` on a configurable port (default 18090). Endpoints:
- `GET /databases` — list of allowed databases (filtered by grants)
- `GET/POST /query/{server}/{database}?sql=...` — SQL queries (allowed databases only)
- `GET/POST /query/{server}/{instance}/{database}` — with instance
- POST body limit: 512KB. Result limit: 50k rows / 16MB.

**Context file generation:** `ClaudeLocalMdWriter` writes the `# Database access` section to `claude.local.md` (Claude Code) and `AGENTS.md` (OpenCode, Codex). Existing content in these files is preserved — only the database section is replaced.

**Workspace config:** `.mtiles/databases.json` — `WorkspaceDatabaseTileConfig` with `Databases` (list). Context files are generated when database service is running and the list is non-empty.

**Settings:** Database tab in Settings — enable service, HTTP port, SQL Server (Windows Auth / SQL Auth), PostgreSQL (credentials, ports), scan interval, manual connections (CRUD with inline edit form, test connection). Save & Apply restarts the service automatically. Passwords encrypted with DPAPI.

**Logs:** `DbLogger` — HTTP query and discovery logs in memory (max 500) + daily files in `%APPDATA%/mTiles/db-logs/`.

**Services:** `Services/Database/` — IDbProvider, SqlServerProvider, PostgreSqlProvider, SqlGuard, SqlGuardProfile, QueryHandler, DbRegistry, DiscoveryService, DbHttpServer, DbLogger, SubnetScanner, DatabaseServiceManager, ClaudeLocalMdWriter.

## Usage tile

A read-only dashboard: for every account this machine can **actually ask**, how much of the limit window
is gone, when it comes back, whether the week is being spent faster than the week is passing, and what
what is left on it where the answer is money. It starts nothing, kills nothing and holds no state
a user would miss — which is why `UsageTileKind.Save` answers `null` and the tile implements `IBusyTile`
and none of the other tile interfaces.

**Two questions, each asked of the thing that knows.** `IAiAgent.UsageAsync(AiSignIn?, ct)` and
`IAiProvider.UsageAsync(AiProviderInstance, ct)`, both defaulting to `null`. Measured 2026-09-01, and
only three of the twelve answer at all: **Claude Code** through `GET api.anthropic.com/api/oauth/usage`
with the OAuth token out of the CLI's own `.credentials.json` (`ClaudeUsageReader`), **codex** out of the
last `token_count` event in the newest `~/.codex/sessions/**/rollout-*.jsonl` — there is no endpoint,
`backend-api/codex/usage` answers 403 at the edge (`CodexUsageReader`) — and **OpenRouter** through
`api/v1/key` plus `api/v1/credits`. z.ai, the Anthropic API, ccs, LM Studio and Ollama publish nothing.

**Three distinctions the whole tile rests on, and each is a card that would otherwise lie:**

- **`null` is not a failure and a failure is not a zero.** `null` means *there is no such question
  here* — an agent that publishes no limits, a default account nobody has logged into on this machine —
  and the tile draws no card. An `AiUsageReport` carrying a `Problem` is an account that exists and
  could not be asked, and the sentence stands where the figures would have been, the rule
  `AgentAvailability` set. A zero for either reads as an account that has run out.
- **A subscription answers in percent and a provider in money**, and they are not two views of one
  number: there is no rate to convert with, so `AiUsageWindow` carries both and a card draws whichever
  it was given. Every figure on it is nullable and `null` is *did not say*. What a metered account's card
  says under its windows is **what is left and nothing else** — a row of daily bars and a note saying how
  long this application had been watching was a second line answering a question nobody asks of a key.
- **Codex's numbers are as fresh as its last reply**, so `AiUsageReport.MeasuredAt` is the event's own
  timestamp rather than the moment of the read, and a reading older than the window it describes is
  stamped and dimmed (`UsageDisplay.Age`) rather than shown as current.

**An account that could not be asked gets no card** (`UsageTileViewModel.Rebuild` keeps only
`AiUsageReport.Answered`), **and its reason goes to the log instead** (`AiUsageService.Explain`, once per
round). Dropping the card is deliberately the opposite of what `Problem` was built for, and it is a
decision about this screen rather than about the type: most of these failures are an account the user
does not reach through this machine — a CLI's default login on a machine where they only use sign-ins —
and a dashboard whose permanent top line is a sentence about one of them is a dashboard they stop
reading. The logging is the other half of it and is not optional: without it every sentence these
readers take trouble to write was constructed and thrown away, and a genuinely broken account vanished
in silence. The layers underneath log their own failures, but only the ones that are a failed call —
nothing down there knows that eight rollouts in a row carried no reading.

**One login reached two ways is one card.** The same subscription can be logged into twice — the CLI's
own default account and an mTiles sign-in — and then it is two directories holding two unrelated
`.credentials.json` files that answer with one set of figures. `AiUsageReport.AccountKey` is what says
they are the same, and for Claude Code it is the account's own id: `oauthAccount.accountUuid` out of
`.claude.json`, prefixed so it cannot collide, compared in memory and never stored, shown or logged.
Measured on a machine with three logins — the default and one sign-in carry the same uuid, which was
exactly the pair the tile drew twice. Where the id is not there (a directory logged into whose
`.claude.json` the CLI has not written yet) the canonicalised path is the fallback, and it is honest
about being weaker: two rows on one path are certainly one login, which is the case `CLAUDE_CONFIG_DIR`
produces and is what codex's key still is. The read is `AiAgent.ReadJsonString`, which stops at the
answer — that file carries a Claude Code installation's whole per-project history. **A report with no key
is never merged with anything** — two accounts wrongly folded together is a subscription missing from
the screen, which is worse than the repetition. Which name survives is decided by
`UsageSources.AccountsOf` listing an agent's sign-ins *before* its default account: the row the user
named and can find in Settings is the better of the two to keep, and a machine with no sign-ins still
gets its default because nothing came before it.

**A 200 in the wrong shape is a failure, not an empty card.** `OpenRouterProvider.UsageAsync` answers
`AiUsageReport.Failed` when the answer carries no `data` object: built as an ordinary report it came out
`Answered`, with three window labels and not one figure under them — which is the "card that says
nothing" the type exists to prevent, reached by the quiet half of the same fault whose loud half
(`TryGetProperty` throwing on a non-object) was guarded first.

`UsagePace` is the pure part and is argued in a table test: elapsed time comes from **`ResetsAt -
Length`**, never from the day of the week — Claude's and codex's seven-day windows roll, so "it is
Wednesday, therefore 43%" is wrong by up to a day, and the same subtraction then serves the five-hour
window for free. Three states with a **dead band** of three points, because without it the label flips
between two words every refresh for the one account there is nothing to say about. The projection is
answered only where the rate runs out **inside** the window: a slower rate outlasts its own reset, so
there is nothing to warn about — and that is also what keeps a rate of almost nothing from overflowing a
`TimeSpan` on its way to a date in the year 40 000.

`AiUsageService` is one asker for the whole application, built in `App.axaml.cs` beside
`DatabaseServiceManager`: it enumerates the accounts (`UsageSources`, behind `IUsageSource` so the
service knows nothing about agents, providers or sign-ins), asks them in parallel, caches for three
minutes, records into `UsageHistory` and raises `Changed`. Two usage tiles in two workspaces are one set
of calls. **The timer runs only while at least one tile is attached** (`Attach`), the rule
discovery already follows: nothing here polls a service the user is not looking at. The in-flight handle
is a `TaskCompletionSource` published *before* the work starts, because with every source answering from
a cache the work finishes before it returns — a handle assigned from the return value is one assigned
after the run has already cleared it, and the service then reports a refresh in flight for good.

**Three rules there are about the clock and all three were wrong once.** The timer ticks at **half**
`RefreshInterval`, because a timer whose period equals the guard's window drops every other tick: the
period runs from one firing to the next while `_lastRefresh` is stamped when the work *finishes*, so at
the following tick the elapsed time is the interval less the round's duration, the guard says "still
current", and a dashboard documented as its interval was twice it. A round has its own deadline
(`RoundTimeout`) because the answers are published together and one account on a hanging socket held
every card at its previous figures for as long as *that instance's* timeout allowed — an OpenRouter
instance can be configured to a minute, and its usage call is two requests. And a **forced refresh
queues behind the round in flight rather than joining it**: joining is what made the button look
broken, since a round that began before whatever the user just changed — a sign-in they finished
logging into, a key they pasted — answers a different question, and its result reads on screen as a
press that did nothing. Not started alongside it either, or two rounds write `_reports` and the winner
is whichever finishes last rather than whichever asked last.

**Nothing asked is not nothing found.** `UsageTileViewModel.IsEmpty` is false until `LastRefresh` is
set, so "No account here reports limits." — a statement about the machine — is not the first thing every
usage tile says while the first round is still running.

**codex is read newest-first until one file answers, and by the newest line that _parses_.**
`rate_limits` is a substring, so a conversation *about* rate limits puts it in a message event, which
then stood in for the reading and had the card report no limits with the figures a line above it; and a
session opened a minute ago has written its file and had no reply yet, so asking the newest file alone
threw away the good reading from the session before it. The walk is bounded (`RolloutsExamined`) —
that directory holds every conversation ever had on this machine.

On screen it is **a readout, not a document**: full-bleed in the card, everything in the terminal's own
`TerminalFontFamily`, no chips and no second frame. Monospace is what puts the figures in a column down
the card without a grid holding them there, and it makes the tile read as part of the same instrument as
the terminal beside it. An account's name is a **section heading with a rule running to the edge**,
which is also what separates one account from the next — no boxes, no gap doing the job.

**A bar is a row of cells and the clock's share is one of them** (`Views/UsageBar.cs`, a control rather
than converters doing arithmetic in the markup): cells of a fixed size, so two bars are compared by
counting rather than by measuring, and any spending at all lights the first one — rounding would
otherwise swallow every figure under half a cell, which on a sixteen-cell bar is three per cent. Cells
past the clock's mark are the danger colour and are the only colour on this tile carrying meaning, so
one glance answers *am I overspending* without a second widget. **A money account has no
bar at all**, so what its row carries is the amount, beside the window it belongs to — summarising one
window into a line under the card left the other two nowhere on screen. Empty
state is a fact rather than an error — most CLIs and most services publish nothing — with a button
opening Settings → AI.

**The name gets its own line; every window shares the next one, at any width.** The rule under the name
does the separating a box or a gap would otherwise have to, and it is also what lets the figures start
at the tile's own left margin instead of after however long the account happens to be called. Below it
the windows share the line in equal proportions (`UniformGrid Rows="1"`), so two accounts with the same
windows line their figures up down the tile — and proportional is what makes "one line" true without a
threshold anybody had to choose: everything narrows together, and the bar, being the only part of a
window with nothing else in it, is what runs out first. `UsageBar` draws whole cells and simply stops
drawing when there is no room for one, so a narrow tile loses the picture and keeps every figure — no
visibility rule, no width to pick.

**Below the width that trick runs out at, the windows go down the card instead** (`Views/UsageLayout.cs`,
`Views/UsageWindowsPanel.cs`). Losing the bar buys room down to about a window's label and its widest
figure (`13% · 2h 26m`); past that the shared line starts cutting the figure itself — which is the one
part of the row the rule above promises to keep — and in a column beside a terminal it was cutting it
mid-character. Down the card, a window gets the tile's whole width and the bar comes back with it.

**Down the card is not one window per line, and the bar is what decides.** A window with a bar takes a
line to itself, because the bar is the only part of the row with nothing in it and therefore the part a
shared line starves first. A window answered in money has no bar at all — `today: $0.41` is eighty pixels
of text — so it **wraps** beside the last one, which is four lines of a metered key's card turned into
two. **What is left on the key is the last item of that same flow**, not something docked to the end of
it: docked is where it belongs and is exactly why it was the one figure that could not wrap, so a
stacked metered card put its windows on two lines and then spent a third on `left: $4.14`. It is
written the way the windows beside it are — a muted name, a bright figure — so it is now the same kind
of thing. `UsageWindowsPanel` is where all three shapes live (equal shares across one line, a line to itself,
wrapped) because equal shares are the one thing a `WrapPanel` cannot do: it packs to the left, and equal
shares are what line two accounts' figures up down the tile. It asks the **item** whether it has a bar
rather than the container it sits in — a child there is the item's presenter, whose own alignment says
nothing about what the template drew, and both shapes measure to much the same width when asked with no
constraint.

The threshold is **derived rather than chosen**: what has to fit is a window's parts times however many
things the busiest account puts on its line — a bar's worth of width where anything on the tile draws
one, and a metered row's where nothing does, so a machine whose only account is a key does not stack at a
width its rows fit in. An account with two windows therefore stays horizontal in a column where one with
four cannot. The rule is pure and argued in a table test; the view reads its own width and the view model
says only how many items there are and whether any draws a bar, because those are facts about the answers
and not about the drawing. The tile is the only thing that
knows how much room it was given, so nothing above it is asked and nothing is persisted — the shape is
recomputed from `Bounds`, and a control that has not been measured yet stays horizontal rather than
starting stacked and springing sideways on its first layout pass.

What came off the rows is the point of the tile being a dashboard rather than a report. **The pace has
no words on screen at all** — "on pace" and "13 points spare" under every bar was a line of prose per
window per account, a number on every point, which is the thing a reader stops seeing; the state worth
acting on is already there without words, as fill past the tick in the danger colour and the figure
beside it in the same, and the sentence is in the row's tooltip. **The reset shows the countdown and
not the clock time** (`3h 43m`, with `resets 13:10 · in 3h 43m` in the tooltip): the instant is the half
that survives the card being looked at later, the wait is the half a glance is for. The percentage and
the countdown are **one string in one column**, because three columns to align carried two facts. The
per-card timestamp is gone — the tile's own header has the refresh time, and repeating it on four cards
buried the one stamp that differs, which is the stale one. And there is no "Usage" heading inside the tile: the tile header above already says it.

## Dictation

Speak into a tile instead of typing: a microphone button in the terminal tile's header and a
push-to-talk shortcut (**Alt+Space** by default). Recognition runs **entirely on this machine** — no
audio and no transcript leaves it. Ported from [cjpais/Handy](https://github.com/cjpais/Handy).

Microphone → 16 kHz mono `float` → speech engine → cleaned text → `TerminalControl.SendText`. Two
engines behind `ISpeechToTextEngine`, chosen by the model: **Parakeet TDT 0.6B v3** on ONNX Runtime
(the default — 25 languages worked out by itself, and faster on a CPU than any whisper of comparable
accuracy) and **whisper.cpp** through Whisper.net for the ggml models. Nothing ships with the
application; a three-step wizard (model → microphone → test) sets it up on a first run and from
Settings → Speech. The last step is where the **shortcut** is taught, by being used — *Hold `Alt`
`Space` and say something* — rather than on a page of its own: the transcript that comes back proves the
model, the microphone and the shortcut at once, and a page that only let somebody type a combination and
click Next would prove nothing about it. Changing it there is a capture mode lasting exactly one
keystroke; "no shortcut" is offered out loud; the Record button stays as the fallback for a shortcut the
desktop has taken.

**Everything else is in [`docs/DICTATION.md`](docs/DICTATION.md)** — the pipeline in detail, the
threading rules, the download and unpacking, the shortcut's state machine, the model comparison for
Polish, and the reasoning behind each. Read it before changing anything under `Services/Speech/`: most
of what is written there is a bug that has already been paid for once.

## Dictation from a phone

Speak into a tile from a phone on your network, or from a browser on the machine in front of you — which
is what makes dictation usable over Remote Desktop, where the microphone is next to *you* and mTiles is
on the far machine. A QR button beside Settings, in the workspaces panel, opens a panel with the codes.

The entry point is **window-level, not per-tile**, and that is a correction rather than a preference: it
began in the tile header beside the microphone, where it read as "dictate into *this* tile" — a promise
the feature does not make and cannot. The phone sends to whichever tile is active when you speak, exactly
as the keyboard shortcut does.

The page also carries **Enter, the four arrows and Escape** (`PhoneKeys`, one `{"type":"key"}` message on
the same socket), because dictating a command is only half of driving an agent from the sofa — the other
half is the prompt it stops on, and backing out of the screens it puts up. They route by the transcript's
own rule (focused text control first, then the active tile's terminal) so that the Enter lands where the
sentence did, and they are delivered as a synthesised `KeyDown` rather than bytes: what Up means on the
wire depends on DECCKM and win32-input-mode, both of which the terminal control owns and neither of which
it exposes. Gated on nothing dictation is gated on — a machine with no model and no microphone can still
be driven this way. That **is** a new
grant, and the note in `docs/DICTATION.md` says so rather than the reverse: a paired device could always
type a line into the terminal, but with the phone's auto-Enter off (the default) it could not run one.
Deliberately not put behind that setting — it governs mTiles pressing Enter *for* the user, sight unseen,
which is the larger act, and the smaller explicit one must not need consent to it. The boundary is
pairing, as it always was.

Three things are worth knowing before touching `Services/Phone/`:

- **`IAudioCapture` is the seam.** `PhoneAudioCapture` implements it and `RoutedAudioCapture` picks
  between it and the microphone per recording, so `DictationService` gained a second input without
  gaining a line of code. Handles are tagged with the backend that made them, because `Detach`/`Finish`
  are split and a new recording can start on the other backend mid-close.
- **TLS is not optional** — a browser hands out no microphone outside a secure context. That is why this
  one server is Kestrel rather than `HttpListener` (which needs `netsh http sslcert` and administrator
  rights for HTTPS), and why Tailscale is the recommended path: its MagicDNS name gets a real
  certificate, while a LAN address can only ever have a self-signed one and a warning to click through.
- **The firewall fails silently, so the panel reads it rather than guessing.** `PhoneFirewall` holds one
  verification string used by both the elevated repair and an unelevated check the panel runs when it
  opens: a block rule Windows wrote when its own prompt was dismissed, no allow rule *for this program*
  (never by rule name — Windows' prompt names rules after the program, and asking by name called a
  working machine broken and offered to delete its rules), a rule whose profiles do not cover the network
  a phone would be on (filtered by default route, or a Tailscale/Hyper-V adapter answers for the real
  Wi-Fi), group policy ignoring local rules, and "could not ask" as its own answer. On Linux nothing is
  opened — there is no elevation prompt worth invoking — but `systemctl is-active` names which firewall
  is running so the panel gives one command instead of two guesses.
- **The QR code holds one URL and the machine has half a dozen addresses.** `PhoneEndpointRanker` is
  pure and decides which, from what worked last time (measured, so it outranks everything), whether the
  session is console or RDP (`SM_REMOTESESSION` — the phone is next to the *user*), and whether the
  adapter has a default route (which alone sorts real network cards from Hyper-V/WSL/Docker). The pin is
  **per session location**, because one machine gets used both locally and remotely and a single
  remembered winner would be wrong every time the user switched. Both audiences are always on screen;
  the session only decides the order.

What is protected is the **keyboard**, not the audio: whoever reaches the bridge can type into the
terminal. Hence a short-lived single-use pairing token in the QR code, exchanged for a session token
that never appears anywhere visible. The bridge is off by default and, unless Settings says otherwise,
listens only while the panel is open or a phone is paired.

**Everything else is in [`docs/DICTATION.md`](docs/DICTATION.md) → *Dictating from a phone***, including
the firewall's silent-block trap, the certificate lifecycle and the wire format.

## Restart shell

`RestartTerminalAsync` in `LeafTileNodeViewModel` — relaunches the tile through `TileLauncher.Launch` (which replaces the session; nothing kills it first). Available via the Restart icon in the tile header and Ctrl+Shift+R.

It was introduced as a workaround for the ConPTY hang after Ctrl+C in TUI apps (an opencode bug on Windows). **That reason is gone** — it was the in-box `conhost.exe` crashing, and the terminal control now ships OpenConsole. The command stays as a feature.

## Scrollbar Fluent theme fix

`AppTheme.axaml` overrides `VerticalSmallScrollThumbScaleTransform` / `HorizontalSmallScrollThumbScaleTransform` to `none`. Without this, the Fluent theme scales the thumb to 12.5% on machines with the default Windows "auto-hide scrollbars" setting.

## Crash handling and logging

`CrashHandler` catches exceptions from three sources: `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, `Dispatcher.UIThread.UnhandledException`. Initialized in `Program.Main()` before Avalonia starts.

`FileLogWriter` writes logs to `%APPDATA%/mTiles/logs/mtiles-YYYY-MM-DD.log` with automatic cleanup of files older than 7 days. `LogTraceListener` redirects `Trace` to log files.

## Persistence

- `%APPDATA%/mTiles/` (Windows) or `~/.config/mTiles/` (Linux). Renamed from `MTerminal`, and `AppPaths` **moves** the old directory into place on first use rather than leaving it: everything the user has is in there, and the first run *saves*, so a fresh path would have written defaults over a reachable installation within milliseconds. A move that fails keeps using the old path — a locked file must not become a lost installation
- `settings.json` — everything in Settings, the configured `AiProviderInstances` (**not** seeded — an empty list means nothing has been set up, rather than six services none of which work — with the key encrypted the way the database passwords are), the seeded `AiAgentInstances` (one per agent, added and
  never replaced, so a rename or a repointed provider survives every launch and an agent shipped by a
  later version still gets its row, and seeded on the CLI's **own** permission default — `ToolDefault`,
  which passes no behaviour flag at all, because a row nobody has been asked about must not start
  every agent tile with the tool's own asking switched off, and the first symptom of that is an edit
  that already happened; its `DefaultBehaviour`/`DefaultEffort` are read through the tolerant converters
  for the reason `GoalPermissionMode` is, the behaviour falling to `ToolDefault` rather than `Auto` so an
  unreadable answer is never *more* permissive than the one it replaced), the configured `AiSignIns` (**not** seeded, and the one list here with **no secret in it** — a name
  and a location, so nothing to blank on export and nothing to restore on import; the login itself stays
  in the CLI's own directory under `agents/`), plus window state and the database configuration (passwords DPAPI-encrypted). Renaming a key here is a migration: the old one stops being read and the user silently gets the new default, which is why `GitHideMTerminalDir`, `GitIgnoreMTerminalDir` and `Speech.HotkeyEnabled` are still parsed once (`SettingsService.MigrateLegacySettings`). The first two are the same question under three names — the application was renamed under it — and they are applied oldest first so the **newest** answer wins: with three generations, "the oldest wins" stops being caution and becomes an answer nobody can change. Every section and collection **refuses a null in its own setter**: a property initialiser does not survive deserialisation, and `"Speech": null` is not an error the load's own catch would see — it is a `NullReferenceException` while the main window is being built, so the application does not start and says nothing about why. The guard is on the property rather than a normalisation pass after loading, because a pass only ever covers the level somebody remembered: `"Speech": { "CustomWords": null }` walked straight past one and stopped startup just the same. **Strings are covered by type rather than one at a time** — `NullToEmptyStringConverter`, registered on `JsonDefaults.SettingsOptions` (the settings file's own options, not the shared ones, because elsewhere a null string may be meant), with `ProtectedStringConverter.HandleNull` covering the encrypted ones a property-level converter would otherwise hide. Hand-guarding had reached four properties out of dozens. `SettingsNullGuardTests` now *walks* the settings graph from `AppSettings` (through collection element types too) instead of listing three types, which is how `PostgreSqlDiscoverySettings.Ports` — a startup crash one hop below where anyone was looking — stayed unguarded. The converter also **overrules `string?`**, and cannot do otherwise: it is chosen by type and never told which property it fills, so a nullable property still arrives empty from the file. Both such properties (`LastWorkspaceId`, `RequiredAiToolBinaryName`) are read through `IsNullOrEmpty`, so it costs nothing — and the list is pinned by a test, because that is true by inspection rather than by construction.
  **DPAPI is Windows-only, so on every other platform those keys and passwords are in this file as plain
  text** — which is why it, and every `settings.bad-*` copy of it, is written owner-only through
  `PrivateFile` (`0600`; a no-op on Windows, where the file inherits `%APPDATA%`'s ACL), and why the
  words on the AI page come from `SecretStorage` rather than from the markup: the key field used to
  promise "stored encrypted on this machine" on Linux too, with nothing but a `Trace` line saying
  otherwise, and that sentence is what the user weighs the risk against.
  **An unreadable file is copied aside before it is overwritten** (`settings.bad-<timestamp>.json`, newest five kept). "Treat it as a first run" is only half the story: the first-run steps save, so within milliseconds the user's agent instances, provider keys and database passwords are replaced by defaults — and "unreadable" is often a truncation with most of the content intact
- `workspaces.json` — list of workspaces (id, name, path)
- `workspaces/{id}.json` — tile layout per workspace (shell name, agent instance id, tile id, tile name). Backward compat: `RootPane` → `RootTile` migration in `WorkspaceState`, and `AgentTileMigration` turning the terminal leaves that were an AI CLI in a shell into agent leaves — `{id}.pre-agents.json` is the copy taken before the first save in the new shape, and it has the same expiry date as the migration
- `logs/` — application logs (daily files, 7-day retention)
- `sessions/opencode/ses_<tileId>.json` — the import document an OpenCode tile creates its session from (see Session resume). Rewritten on every launch; a few hundred bytes, and deliberately never pruned — while the file exists, a session the user threw away can be recreated on the next launch
- `opencode/<instanceId>.opencode.json` — the provider document an opencode instance on a **local**
  server is launched with (`OpenCodeProviderConfig`, pointed at by `OPENCODE_CONFIG`). Same category and
  same rules as the session import above it: derived from the instance's id, rewritten on every launch
  so an address edited in Settings takes effect without anything having to notice, and never pruned. No
  secret of *ours* in it — the providers that need one have no key. It is the user's own opencode
  config with our provider block added, though (overwriting it would take their default model, MCP
  servers and instructions away from the tile), so a key they keep in **their** file is copied here:
  owner-only, on this machine, and outside what `SettingsPortability` exports
- `agents/<agentId>/<signInId>/` — one AI CLI login each, written by the CLI itself and pointed at
  through its own environment variable. **Contains a refresh token and the whole conversation history
  that came with the account**, so it is created owner-only and nothing in this application ever deletes
  one: removing the sign-in row removes the row
- `usage/history.json` — the daily spending snapshots
  (`UsageHistory`): `{ sourceId: { "2026-09-01": 18.09, … } }`, 60 days kept, owner-only through
  `PrivateFile` because it is a record of what somebody's accounts cost. **These are ours and nobody
  else's** — OpenRouter's `api/v1/activity` answers 403 for an ordinary key, so there is no per-day
  history to fetch from anyone. **Nothing on screen reads it**: the card shows what is *left* on a key,
  not what each of the last seven days cost. The recording stays because it is the only per-day history
  that exists at all, it costs a few kilobytes, and a row of bars that starts empty is worth having
  already filled in if it ever comes back. The day is **UTC**, because that
  is the boundary the counter being sampled resets on, and the **maximum** seen for a date wins: the
  value is a running daily total, so a poll landing just after midnight would otherwise write a fresh
  small number over a finished day. An unreadable file is a fresh start — what is lost is a row of bars
- `models/` — downloaded speech-to-text models (hundreds of MB each; `.partial` while downloading)
- `phone/` — the phone bridge's TLS material and its paired devices. `bridge.pfx` **contains a private key**; kept rather than regenerated per launch, because a new certificate every launch means a new browser warning every launch, and reissued when the machine's set of addresses changes (a certificate is only accepted for a host in its SANs). `sessions.json` holds SHA-256 of each paired device's token — never the token, so the file records *who* is paired without being usable to authenticate. Shutting the application down does not clear it; turning the bridge off does
- Auto-save with debounce

## What is not built yet

[`docs/ROADMAP.md`](docs/ROADMAP.md) — the things known to be missing, each with what is wrong now, what
the stopgap is and what would settle it. Two of them are decisions somebody will otherwise make again
from scratch: why the model fields are an `AutoCompleteBox` and not an editable `ComboBox` (measured,
both are wrong in opposite directions), and what a tile header of an index, a kind and a description
would take. The user-facing half of the same list is the **Roadmap** section of `README.md`; this one
carries the reasoning.

## Conventions

- **Workspace** (not "project") — working directory with terminal/editor tiles. Right-click on workspace → context menu (Show in Explorer, Remove).
- **Tile** (not "pane"/"panel") — a single tile in a workspace (terminal, note, or todo), split into a binary tree
- **Note** (not "editor") — tile with text editor (AvaloniaEdit), kind id `note`
- **Todo** — tile with task list, kind id `todo`
- ViewModels in `ViewModels/`, views in `Views/`
- **Agent** — a terminal whose commands are an AI CLI's own rather than a script the user wrote, kind
  id `agent`. `AgentTileViewModel` derives from `TerminalTileViewModel` and overrides two answers:
  where the commands come from (`IAiAgent.Interactive`, asked at every launch so an instance edited
  in Settings takes effect on the next restart) and what the layout calls it. The tile stores the
  `AiAgentInstance`'s id, the agent's id beside it (a deleted instance leaves a tile that still
  starts, on the same agent) and — for `SessionStrategy.CapturedAfterStart` only — the session id
  the agent named itself, together with the tile identity it was captured under, so "New session"
  cannot reopen the conversation the user has just left. **A tile that could not be built as it was
  configured says so and keeps asking for what it was** (`AgentSubstitution`, set by
  `AgentTileKind.Resolve`, shown once as `TerminalTileViewModel.LaunchNotice` — dismissible, because
  unlike `LaunchProblem` this tile *is* running): the last two links of the fallback chain can land on a
  different agent, and a Codex tile that quietly comes back as Claude is a different program working in
  somebody's repository. `Save` therefore writes the **requested** ids rather than the substitute's — the
  layout is saved for any reason at all, so the substitution would otherwise become permanent within
  seconds of the tile opening and restoring the instance in Settings would no longer bring it back — and
  it writes no session id while a tile is substituted, since that id belongs to the agent standing in.
  `TileKindIds.ToLegacy` answers `terminal`
  for it: a build Velopack has rolled back opens the leaf as a plain shell on the shell it was
  running (hence a `shellName` in its state this build never reads) rather than as an empty tile.
  `TileLauncher` gained the two moments a session needs — `PrepareForLaunchAsync` before the
  commands are resolved (agy's pre-create, which *makes* the conversation the tile then resumes)
  and `OnLaunched(startedAt)` after they start (codex's rollout file, polled for half a minute
  because it does not exist until the session does — `IAiAgent.CapturesWhileRunning` is which of
  the two an agent needs). A capture that fails costs a conversation, never a tile.
- **Git** — tile with change viewer (diff, commit, stash, push, fetch, tags, undo, context menu, discard), kind id `git`
- **Database** — tile with database management (SQL Server, PostgreSQL), HTTP bridge, query logs, kind id `database`
- **Usage** — a read-only dashboard of what every account this machine can actually ask has left, kind
  id `usage`. See *Usage tile* below
- No DI container — manual injection in `App.axaml.cs`, where `BuildTileCatalog` is also the one place a kind of tile is registered
- **ConfirmAction pattern** — destructive actions (discard, remove workspace, undo commit) use `Func<string, Task<bool>>? ConfirmAction` in ViewModel, wired from View as `MessageBox.Avalonia` dialog (YesNo). **An unwired dialog normally lets the action through** — except in Settings, where it does not. `SettingsView.ConfirmAction` answers **no** when there is no window to ask in, and that covers *every* confirmation on that dialog: deleting a manual database connection, a downloaded speech model. An unanswered question is not a yes, and nothing on that dialog is cheap to undo — a speech model is hundreds of megabytes and, on a slow connection, hours. The speech model's own chain says no at all three links (the row, the tab that wires it, the view), and all three had to change together: a `?? Task.FromResult(true)` in the middle made the row's own refusal unreachable
- **PromptInput pattern** — `Func<string, string, IEnumerable<string>?, Task<string?>>? PromptInput` in ViewModel, wired from View as `InputDialog` (title + text input + suggestions list). Used e.g. when creating a tag.
- **ShowError pattern** — `Func<string, string, Task>? ShowError` in ViewModel, wired from View as `MessageBox.Avalonia` (Ok). Used for push/fetch/tag/undo errors.

## Git tile — details

`GitDirectoryWatcher` watches both `.git/` and the entire working directory (worktree). The list of ignored directories is retrieved from `git ls-files --ignored` and updated on every refresh. `Error` handlers on watchers log buffer overflow and trigger a refresh.

`ReconcileChanges` in `GitTileViewModel` preserves checkbox state (`IsChecked`) between refreshes based on key (FilePath + Status + mtime). Two-level cache (currentState + previousState) protects against state loss with "flickering" files. On first load checkboxes = false, on subsequent refreshes new/changed files = true.

Context menu (right-click) on file list: Show in Explorer, Open in default program, Copy filename/folder/filepath, Discard changes (with confirmation dialog). Multi-select: right-click shows only Discard with file count. Space toggles checkboxes of selected files.

Context menu (right-click) on commit list: Add tag..., Copy commit hash.

**Push/Fetch/Undo:** Buttons in the Git tile tab bar. Push detects upstream (missing → `push -u origin`). Fetch runs `fetch --all --prune`. Undo = `reset --soft HEAD~1`, available only when the last commit is local (unpushed). All with error dialog.

**Tags:** Displayed in commit history (color `TagColor`). Created via context menu → `InputDialog` with list of recent tags. Name validation with regex `[a-zA-Z0-9._/\-]+`.

**Unpushed commits:** Marked with `*` (color `DangerText`) in history. Counter `(N)` next to the Push button. Logic: `git log upstream..HEAD`.

**Commit suggestions:** Popup at the commit message field (clock icon). Top-3 most frequent + 10 most recent unique from `git log --format=%s -50`.

**`.mtiles/` in `.gitignore`:** Setting `GitIgnoreWorkspaceDir` (default **on**) keeps `.mtiles/` listed in the workspace's `.gitignore`, applied on every Git tile refresh (`GitIgnoreFile`, `GitTileViewModel.ApplyWorkspaceIgnoreSettingAsync`, which also removes the old `.mterminal/` entry unconditionally — `WorkspacePaths` has moved that directory, and a `.gitignore` line for a directory that is not there is litter this application put in somebody else's repository). It replaced `GitHideMTerminalDir`, which only hid those files in this tile's list — leaving them untracked *and* unignored, so they were invisible here and waiting in every other git client.

Consequences worth knowing: **the app edits a file in the user's repository, and creates one where there is none** (a blank line, a `# mTiles workspace state` comment and the entry, appended; turning the setting off removes exactly those and nothing else). `GitIgnoreFile` works on raw bytes and only ever appends, so a BOM and any non-UTF-8 content survive; removal rewrites through a temporary file and a move. An emptied `.gitignore` is left in place rather than deleted — this cannot tell one it created from an empty one the user committed. It is written by the **Git tile**, so a workspace without one is untouched. Files already *committed* under `.mtiles/` now appear in the changes list, correctly — ignoring something git already tracks changes nothing. A user who had the old setting off keeps it off: `SettingsService.MigrateLegacySettings` reads the old `GitHideMTerminalDir` once and drops it. That case is the only one in which the app would edit a repository against a decision the user had already made.

**DiffFontSize:** Diff panel uses 80% of font size (`FontSize * 0.8`).

## Workspace view caching

`MainWindow` caches `WorkspaceView` instances in `Dictionary<string, WorkspaceView>`. Switching workspaces via `IsVisible` toggle instead of DataTemplate — terminals are not killed/recreated. `WorkspaceRemoved` event clears the cache and removes the view from the visual tree.

## Workspace panel

`WorkspaceItemViewModel` — wrapper for `Workspace` with `ObservableProperty BranchName`. `DispatcherTimer` polls `GitService.GetBranchNameAsync` every 30s (static method, creates a temporary `GitCommandRunner`). Dispose in `MainWindowViewModel.OnClosing`.

**The panel has one left edge**: the scrollbar is on the right, where it does not push the list out of line with the filter box above it.

**The panel is a card**, laid on the same canvas as the tiles and with the same margin, radius and hairline. It used to be the one flat slab in the application, running from the title bar to the taskbar beside a column of cards, which read as two applications sharing a window. The bottom actions (Settings, phone bridge, update) are the application's rather than the list's, so a hairline separates them; the heading uses the application's own `TextBlock.section` class rather than a local set of font properties; and both levels of row share one `RadiusRow`, because nesting is said by the indent and the guide beside it and three radii in a 240px column say nothing at all.

**Selected is not hover.** Both were `InteractiveHover`, so pointing at a neighbour made it impossible to say which workspace was open — the one thing the list exists to tell you. Selected now gets `BgElevated` and an accent down its leading edge, the marker the tiles already use for the same idea.

**A row says whether something is working in there, and can be pinned.** The working light is a small turning arc beside the name (`MaterialIcon` `Loading`, 11px, `Controls.axaml` → `workspace-busy`), fed by `WorkspaceViewModel.IsBusy` — any leaf whose content is an `IBusyTile`, which is a terminal that has produced output inside `ActivityWindow.DefaultWindow` (2s) or a Goal tile mid-run. The terminal's half is `OutputActivityLight` (subscription, window, expiry timer) rather than fields on the tile: the tile owns one and re-exports what it says, so the threshold or the signal can change without touching the class that runs a shell. Output rather than "the process is alive": a shell at its prompt is alive and doing nothing, which is the state this exists to tell apart from a build running. Smoothed over a window because the raw signal fires many times a second and a light wired straight to it is a flicker, not an answer; the expiry timer only runs while the light is on. It **turns** rather than sitting still, because what it reports is work in progress and a still mark cannot be told apart from a state somebody left switched on. The animation hangs off a second class (`.spinning`, applied from `IsBusy`) rather than off the base one: a style that matched always would keep an infinite animation ticking on every hidden spinner in the list — one per workspace, for the whole session. Only workspaces that have been opened have a view model, so an unopened one stays dark — truthfully, since nothing of it is running. The star writes `Workspace.IsFavorite` through `WorkspaceService.SetFavorite` and pinned rows sort to the top (`WorkspaceDisplayOrder`, pure and pinned by a test). Re-ordering uses `ObservableCollection.Move`, **never remove-and-re-add**: a removal from that collection is how `MainWindowViewModel` learns a workspace is gone, and it would answer a re-sort by disposing the workspace's tiles — which is why that handler now tests for `Remove` rather than for `OldItems != null` (a Move carries `OldItems` too).

**A row says whether it is loaded, and what that costs.** A workspace holds its tiles — and their
shells — from the first time it is opened until the window closes, so a day's work ends with six agents
resident and nothing on screen saying so. A loaded row is drawn at full strength and an unloaded one a
shade back (`WorkspaceItemViewModel.IsLoaded`, `Border.workspace-item-unloaded`): most rows in a long
list have never been opened, and a marker on nearly every row marks nothing, while a shade says it
without asking for a column. The reading itself sits at the right-hand end of the branch's line, which is
the one place on the row a number does not compete with a name — three characters wide, and the branch is
the fill child, so it trims rather than being run under. It is sampled every five seconds
(`MainWindowViewModel.SampleMemoryAsync`): the tree walk that collects the tiles' process ids is on the
UI thread because the tile tree is the UI thread's, and the reading of the machine's process table is not,
because that is a few hundred processes opened one at a time. **One reading answers every loaded
workspace** (`IProcessMemoryProbe.WorkingSetsOf` takes them all at once): a call per workspace would scan
the machine once per workspace every five seconds, and the figures would describe different instants.

**Unload is on the context menu, under Copy path, and it asks first.** It closes the workspace's tiles
without losing the workspace: the layout is on disk, so the same tiles come back on the next click — but
not the same sessions, which is why an unwired `ConfirmAction` answers no. The selection is cleared first
when it is the workspace on screen, or the row stays highlighted with its view gone and nothing able to
bring it back, since re-selecting an already-selected workspace raises no change. The menu item is dead
for a workspace that was never opened, which has nothing to give back.

A workspace is **one row: its name, and one line under it saying what it sits in** — and deliberately nothing else. What has stood there and been taken back out — an open/closed marker, a disclosure chevron and a count of the tiles under it, with the list of those tiles under that — was in every case either already on screen somewhere better or competing with the name for a row 240px wide, and the row ended up showing a count and an ellipsis where the name should have been. That second line is the branch where there is one, the offer to create a repository where one can be, and **the path where neither is true** — the home directory, a drive root, a system folder: those rows were the one place it came out blank, and a reserved line saying nothing is height spent on silence. The path is shown rather than a word for the kind of place, because on exactly those rows the *name* is already the word ("Home directory" is an alias) and the path is what says which profile, which drive. It trims, which is why it is in a `DockPanel` behind a docked glyph and not in a horizontal `StackPanel` — one of those measures its children with infinite width, so `TextTrimming` in it never fires and a long path runs out of the row and is cut mid-character by the scroller. The full path is still in the row's tooltip.

**The second line is what settles the competition.** Side by side, the name and the branch bid for the same 240px and the name kept losing — it was the one that had to give way, so a row came out as "B…" beside a fully spelled-out `feature/ui-redesign`. A line each costs a few pixels of height and ends the argument, which is why there is no longer any width below which the branch is hidden.

**The meta line is always there and always the same height** (a fixed-height `Panel` holding it), whatever it has to say. Showing it only for repositories gave the list two row heights interleaved at random, which is the one thing a column of twenty names cannot afford — nothing lines up and the eye has no rhythm to scan by. Reserving the line also covers the moment before the check has answered.

A workspace that is **not** a repository gets an offer in that line rather than a label: **Create repository** runs `git init` after a confirmation (`WorkspacesPanelViewModel.CreateRepositoryCommand`). Saying only "no repository" would leave the user to go and find a terminal to type the answer into; asking first is because `git init` writes into somebody's folder from a row they are otherwise clicking to switch workspaces, and an unwired `ConfirmAction` answers **no**. `WorkspaceItemViewModel.HasRepository` is `bool?` and the third state is the one that matters: the check is asynchronous, and a plain `bool` would have every repository in the list announce it had none until the first pass finished.

**Not every directory without a repository gets the offer.** `SpecialDirectories.Kind` names what kind of place a path is — home, Desktop, Documents, Downloads, Pictures, Music, Videos, the root of a drive, a system directory, an ordinary project folder, or one nothing could read — and `AllowsRepository` is *derived* from it (`Kind(path) == Ordinary`) rather than deciding again, so the glyph on a row and the offer on it cannot reach different conclusions about the same path. A repository at `~` tracks every download and every application's configuration, and its first `git status` takes minutes; the drive root and the system directories are that mistake one step larger, the user's own file folders one step smaller — and those are the folders somebody browsing for a workspace lands in by accident. **The user folders match only themselves, never their children** — that is the whole difference from the system directories: a project under `~/Documents` is an ordinary project. **Two of them are guessed by name** under the home directory, both because the platform will not say: Downloads, which has no `SpecialFolder` at all, and — on Unix only — Documents, whose `SpecialFolder` answers with `$HOME` there (`MyDocuments` is `Personal`), which left `~/Documents` the one of these six offered a repository on Linux. That mapping is also why `Kind` answers `Home` and not `Documents` for `MyDocuments` on Linux, which is correct: the path *is* the home directory. A guess that misses — a localized `Dokumenty`, a relocated Downloads — is simply not found, and a folder that is not found is an ordinary one, which is the safe way round. **Those rows show their path instead** (`WorkspaceItemViewModel.ShowsDirectoryPath`, the complement of `HasNoRepository` among the rows with no repository): the meta line is reserved on every row, so leaving it blank was height spent on silence — and these are the rows the name covers for least, because on exactly these it is a kind of place rather than which one. "Home directory" is an alias this application chose, and it is the path that says which profile; a word for the kind would be the name a second time. **The glyph belongs to that line, not to the name** (`SpecialDirectoryIcon.Kind`, a converter in `Views/` for the reason `TileIcons` is there — which picture stands for a kind of place is a fact about the drawing): in front of the name a house said what the name already said, while the line that needed a mark carried a generic folder. One picture per kind — a house, a monitor, a document, a download, an image, a note, a film, a disk, a cog — and an unrecognised kind falls to a plain folder rather than throwing, because a wrong glyph is legible and an empty row is not. The path gets its own line and trims, which is what the row could not offer it beside the name — the reason it is in the tooltip everywhere else. A row nothing has checked yet still says nothing: `HasRepository` is null until the first pass answers, and a path there would be the row claiming to have been looked at. The rule is pure and shared, because the same "is this the home directory" also decides the workspace's name.

**A first run opens on one workspace holding one terminal** (`DefaultWorkspace.SeedFirstRun`, called from `App.axaml.cs` before the main window is built) — at the home directory, which is the one place every machine has and the user can certainly write to. **The condition is that there is no `workspaces.json` at all, not that the list is empty** (`WorkspaceService.HasStoredList`): `Load` answers every read failure with an empty list, so a file locked by another instance or truncated by a power cut is indistinguishable from a first run — and seeding writes, which would replace the user's whole list with this one workspace and orphan their layouts. The file is also what remembers the answer, so a user who removes their last workspace does not get it back on the next launch. It fails soft, because a home directory that cannot be written to is a reason to start on an empty panel and not a reason not to start.

**The home directory is displayed as "Home directory", not as the login.** A workspace takes its name from the last part of its path, which for `C:\Users\andrz` is `andrz` — the account, not the place. `WorkspaceDisplayName.For` is a display rule and not a rename: `workspaces.json` keeps whatever is stored, so the same file on a machine with a different login, or the workspace moved elsewhere, shows the directory's own name again. The row also **wears a house**, because a row in a list of folders is read as a folder and the words alone can be taken for one somebody made — but on the **meta line**, not in front of the name. In front of the name it said what the name already said; on the line under it, what kind of place this is *is* the thing being said, and the house is one of ten glyphs (`Views/SpecialDirectoryIcon.cs`, drawn from `WorkspaceItemViewModel.SpecialKind`) rather than the only one. **The list sorts by the alias, not by the glyph** — `WorkspaceDisplayOrder` compares `WorkspaceItemViewModel.Name`, so `Home directory` reads under H rather than under the login, and every alias `WorkspaceDisplayName` grows follows the same line; ordering on the mark as well would bunch the aliased rows at one end and override the alphabet the rest of the column is read by. Pinning stays the one thing that outranks the name. The filter matches the alias *and* the path, so the row is still found by typing the login.

**Plain text, not a chip** (`StackPanel.workspace-meta`). A chip is the language this application uses for the rare exception — the AI tool that is NOT FOUND, the database tile's Error — and it works because you see one at a time. A value present on every row is not an exception, it is metadata: twenty boxed outlines down the column gave the list a zigzag right edge and no rhythm to read it by, and put a third frame inside a card inside a canvas. It sits on **the same left margin as the name**, which is what leaves the panel one left edge to read down; right-aligned it had the same disconnected look the chip did.

**Selected is not hover.** Both were `InteractiveHover`, so pointing at a neighbour made it impossible to say which workspace was open — the one thing the list exists to tell you. Selected gets `BgElevated` and an accent down its leading edge, the marker the tiles already use for the same idea.

## InputDialog

Reusable modal dialog (`Views/InputDialog.axaml`): title, TextBox with placeholder, optional suggestions list (ListBox). Enter = OK, Escape = Cancel. Clicking a suggestion enters it into the TextBox. `ShowDialog<string?>` returns trimmed text or null.
