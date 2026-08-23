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
- `Models/` — DTOs and data models, no behaviour (Workspace, WorkspaceState, TileNode, AppSettings, AppDefaults, ShellProfile, LaunchScripts, UserShellProfile, TerminalTheme, GitFileChange, CommitLogEntry, AiToolInfo, UserAiTool, GoalTileState, DatabaseSettings, DatabaseInstance, ManualDatabaseConnection, WorkspaceDatabaseConfig, SpeechSettings, PhoneSettings)
- `ViewModels/` — MVVM with CommunityToolkit.Mvvm (source generators)
- `Views/` — Avalonia AXAML + code-behind
- `Styles/` — design tokens (`AppTheme.axaml`) and global control styles (`Controls.axaml`, including GridSplitter). UI colors exclusively via `DynamicResource`, terminal ANSI colors separately in `TerminalTheme`
- `Services/` — JSON persistence (PersistenceService, SettingsService, WorkspaceService), shell detection (ShellDetector), AI tools detection (AiToolDetector), ThemeBridge, JsonDefaults, AppPaths, AppInfo, GitService/GitCommandRunner/GitDirectoryWatcher/GitIgnoreFile, DiffFormatter, FileHelper, ProtectedStringConverter, TileFactory, TileTreeSerializer, TileNameGenerator, the Goal tile's engine (AiProcessRunner, GoalWorkflowEngine, GoalPromptBuilder, GoalStatePersistence, GoalLoopPolicy, GoalTilePolicy, GoalDiffContext, WorktreeReader), UpdateService (its Velopack manager is built lazily and fails soft — an installation it cannot ask about must not stop the main view model being built), CrashHandler, FileLogWriter, LogTraceListener
- `Services/Database/` — DatabaseServiceManager, DbHttpServer, DiscoveryService, DbRegistry, DbLogger, QueryHandler, SqlGuard, SqlGuardProfile, SqlServerProvider, PostgreSqlProvider, SubnetScanner, IDbProvider, ClaudeLocalMdWriter
- `Services/ShellStarter.cs` — one call that replaces whatever session a `TerminalControl` holds and hands the shell its startup script (`${tileId}` substituted, one line per `\r`). The control owns the rest: killing the old session, waiting for it, and gating the script on `ShellReady` for *that* session
- `Services/TileLauncher.cs` — launching a terminal tile: disposes the previous launch, picks the profile's current scripts, then either the direct-launch chain or a plain interactive shell. First launch and "restart shell" both go through it. It reads `TileId`, it never assigns it
- `Services/DirectLaunchSession.cs` — one tile's command chain (see Shell Profiles below); disposable, and disposing it is what stops it relaunching
- `Services/TerminalClipboardCoordinator.cs` — window-level Ctrl+C across tiles (see Terminal key handling)
- `Services/Phone/` — dictation from a phone (see `docs/DICTATION.md` → *Dictating from a phone*): PhoneEndpoint/IPhoneEndpointSource with NetworkEndpointSource, TailscaleEndpointSource and MulticastDnsEndpointSource, PhoneEndpointRanker (pure — the one part whose behaviour is an opinion, so it is argued in a table test), PhoneEndpointDirectory, SessionLocationProbe, PhonePairing, PhoneCertificates, PhoneFirewall, PhoneAudioCapture + RoutedAudioCapture, PhoneBridgeServer (Kestrel — the only server here that faces the network), PhoneBridgeManager, QrCodeImage, UiDispatcher
- `Services/Speech/` — dictation (see `docs/DICTATION.md`): IAudioCapture/PortAudioCapture, AudioResampler, ISpeechToTextEngine with ParakeetSpeechEngine (+ParakeetVocabulary) and WhisperSpeechEngine, SpeechEngines (the one map from model kind to engine and to what it looks like on disk), SpeechModelCatalog, SpeechModelStore, TarGzExtractor, DictationService, TranscriptPostProcessor, DictationTextSink, HotkeyGesture, HotkeyCapture (what a keystroke means to something reading a new shortcut — shared by the Speech tab and the setup wizard, and pure, because it lived in view code where the "mark it handled only where it is taken" rule had no test), HotkeyAdvice, DictationHotkeyMachine, DictationHotkeys
- `Services/ShellCommandLine.cs` — wraps a command for the shell that runs it (`-c` / `/c` / `-Command`). Deliberately without the profile's own args: those are the interactive-startup flags. **A command chain never runs in `cmd`**: `ShellDetector.ResolveForCommands` swaps it for PowerShell (or a POSIX shell) and `DirectLaunchSession` traces the swap, because `cmd` does not parse its command line by the `CommandLineToArgvW` rules the PTY backend quotes with, runs only the first line of a multi-line command, and does not treat `;` as a separator — all measured, and the last of those silently reduced the seeded OpenCode profile to a bare shell. Only the *commands* move; the tile's interactive shell stays whatever the user chose, so `/c` is still reachable when nothing else is installed
- `Services/ChainPolicy.cs` + `Services/RelaunchBudget.cs` — the launch chain's rules and its rate limit, pure and separate from the loop that carries them out
- `Services/TileScript.cs` — the one place that expands a profile script's placeholders (`${tileId}`, `${opencodeSessionFile}`), and the only thing that decides what an acceptable tile id is — a rule `OpenCodeSession` asks for rather than copies, because the same value also becomes a file name
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
- **AvaloniaEdit** — text editor. Requires `StyleInclude` in App.axaml. Text sync via `Document.Changed`.
- **Material.Icons.Avalonia** — Material Design icons. Requires `<MaterialIconStyles />` in `App.axaml` Styles. Usage: `<mi:MaterialIcon Kind="Close" />`.

## Split tiles architecture

Recursive binary tree: `LeafTileNodeViewModel` (terminal/editor) or `SplitTileNodeViewModel` (H/V + two children). `TileNodeView` manages views manually (not DataTemplate); rebuilding the tree re-parents live terminals with no bracketing, because detaching one does not end its session.

`LeafTileNodeViewModel.IsActive` — `TileActivationScope` (per-workspace instance) guarantees that only one tile is active. `LeafTileView` reacts to `IsActive` — colored strip (`ActiveStrip`, 2px) at the top of the toolbar + brighter background (`BgElevated`).

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

## Shell Profiles

Users define shell profiles in Settings → Profiles tab. Each `UserShellProfile` has: `Id` (GUID), `Name`, `ShellName` (reference to detected shell), `StartupScript` (commands sent to PTY after startup), `FallbackScript` (executed when StartupScript fails), `RequiredAiToolBinaryName` (optional — binary name of the AI tool required to display the profile).

**Default profile seeding:** `SettingsService.SeedDefaultProfiles()` adds 4 profiles (Claude Code, OpenCode, Codex, Pi Agent) if no profile with that name exists (case-insensitive). Never overwrites existing profiles.

**Profile filtering:** A profile is visible on an empty tile only if:
- `RequiredAiToolBinaryName` is empty OR the AI tool is installed (`AiToolDetector.Detect`)

Filtering is implemented in `WorkspaceViewModel.GetAvailableProfiles()` with cache (30s TTL) on `AiToolDetector.Detect()` results.

**DirectLaunchSession** (`Services/DirectLaunchSession.cs`): when a profile has `FallbackScript` → `LaunchScripts.RunsCommandChain` is true. Commands are run via `shell -c "command"` (not interactively). Chain: startup → fallback → plain interactive shell. Each command is started and then **awaited to its end** (`TerminalControl.WhenSessionEndedAsync(sessionId)`), so the verdict is the **exit code plus how long it ran** — there is no "it survived N seconds, so it worked" window any more:

| Outcome | Meaning | What the chain does |
|---|---|---|
| spawn throws | tool not installed, bad cwd | next command |
| non-zero, ran < `Established` (2 min) | the command does not work | **next** command — never the same one |
| non-zero, ran ≥ `Established` | a working tool crashed | **same** command again |
| exit 0, ran ≥ `MinLifetimeForRelaunch` (10s) | the user quit the tool | whole chain from the start |
| exit 0, ran < 10s | it did not stick | **next** command (the fallback is what a profile names for this) |
| no exit code at all | connection lost | as non-zero |

Every relaunch is rate-limited **for the chain as a whole**, systemd-style: at most **3 in 10 minutes** (`RelaunchBudget`), after which the chain carries on to the next command instead. One relaunch is free: a **clean** exit after at least `Established` is the user closing their tool on purpose, and quitting it four times in a morning must not have the tile refuse to bring it back (`CountsAgainstBudget`). A rate over a window, not a running total — a total would give up on a tool used daily once its fourth crash came round, however many months apart the four were. Chain-wide, not per command, and that part is structural: a per-command budget was renewed every time the chain moved on, so a profile whose fallback exits cleanly looped forever between fallback and top, renewing its budget on each lap. Nothing resets this but time.

The rules live in `ChainPolicy` (thresholds + `Decide` + `CountsAgainstBudget`), separate from the chain that carries them out and pure, so they are readable in a table test without a terminal, a dispatcher or a stopwatch. The lifetime is **not** measured by the host: `SessionExitedEventArgs.Lifetime` comes from the terminal, which stamps the session when it spawns the child — timing it around the host's own `await` measures the wait instead.

One loop is deliberately unbounded: a command that exits **cleanly** after `Established`, for ever, is restarted for ever. Every lap costs at least two minutes of a real session, so it is not a spin, and it is indistinguishable from a user quitting and reopening their tool by hand — bounding it would stop the tile honouring the very gesture it exists to honour.

Off the end of the chain is the interactive shell, which is not watched, so a tile is never left dead. Both thresholds are load-bearing: judging on time alone made `claude -r <unknown-id>` (**21 s** to print "Invalid session ID" and exit 1) look adopted, read its failure as the user quitting, and relaunch it every 21 s for good with the fallback unreachable — while judging on the code alone would demote a tile permanently to a bare shell the first time a long-running tool crashed. Without `FallbackScript` → classic mode: shell starts interactively with the startup script as the session's startup input.

`LaunchScripts` (returned by `TerminalTileViewModel.ResolveCurrentScripts`) decides which of the two paths a tile takes. `RunsCommandChain` is true exactly when `Fallback` is non-blank — a profile that names something to fall back to is one that launches commands. It was a stored third value every caller computed the same way, so the type could hold combinations no profile can produce; deriving it also removed a dead disjunct (`Startup is not null || …`) that made the rule look like it had two halves. Blank is normalised to null in the `init` setters, so `with` cannot slip a script of spaces past it.

The instance **owns** the tile's chain: it relaunches only the session whose `SessionId` it started (taken from `RestartAsync`, which returns it — reading `SessionId` afterwards can describe a session someone else opened), and whoever replaces the chain (restart, tile close) disposes it first — `TerminalTileViewModel.ReplaceLaunchSession`, which owns that invariant so no caller can break it. Without both, a restart leaves two chains fighting over one tile and closing a tile resurrects its shell as an orphan process.

Terminal creation flow with profile:
1. Empty tile → click Terminal → if profiles exist, ProfileChooser appears (Back / Default / profile buttons)
2. Profile selection → `TileFactory.CreateContent(..., UserShellProfile)` → `ShellDetector.ResolveFromUserProfile()` → `TerminalTileViewModel` with shell + startup script
3. `TileLauncher.Launch` → `LaunchScripts.RunsCommandChain` → `DirectLaunchSession.Start()`, else → `ShellStarter.StartAsync()` with the startup script

### Session resume

A tile's `TileId` is the agent's session id, so a restart reopens the same conversation. **Claude Code** and **pi** take an id outright (`--session-id`), so their profiles just substitute `${tileId}`.

**OpenCode cannot be told one**: `opencode --session <id>` only ever *continues* a session (unknown id → `Session not found`, exit 1 after ~1.4 s, which the chain reads as "next command"), and the TUI creates no session at all until the first message — so there is nothing to observe at startup and pick up either. The way in is `opencode import`, which takes a JSON document and keeps its `id` verbatim; `ses_${tileId}` is legal, so no tile→session map exists anywhere. `OpenCodeSession` writes that document, `TileScript` expands `${opencodeSessionFile}` to its path (a pure function of the tile id, so the launcher can ask whether a profile resolves without writing anything), `TileLauncher` writes it before either launch path runs, and the seeded profile is `opencode --session ses_${tileId}` falling back to `opencode import "${opencodeSessionFile}" ; opencode --session ses_${tileId}`.

Measured against **opencode 1.18.14**, all load-bearing: the document's `projectID`/`directory` are **ignored** — the session lands in the project of the import's *cwd*, which is why the import runs as one of the tile's own commands; re-importing an existing id is **non-destructive** (title and messages kept), which makes it create-if-missing rather than a way to wipe the conversation being resumed; **every** field is required (`id`+`time` alone is rejected with `Missing key`, which does not say which key); `version` is not validated. It is opencode's *export* format, not an API — when it moves, the import fails, the resume finds nothing, and the chain ends at an interactive shell: a tile without its history rather than no tile. `OpenCodeSessionTests` pins the shape so that surfaces as a failing build.

Seeding never overwrites an existing profile, so the resume reaches existing users through `SettingsService.MigrateOpenCodeProfile` — and only when both scripts are still exactly what an older version seeded (`opencode --session ${tileId}` / `opencode`), a pair that could never work because the id lacked opencode's `ses` prefix. A profile the user has touched is left alone.

**Codex** is the same problem with a different CLI and is still open (README roadmap).

Profile persistence in layout: `TileNode.UserProfileId` → during deserialization `TileFactory.CreateTerminalFromDto` looks up the profile by Id in `AppSettings.ShellProfiles`. If the profile was deleted — graceful fallback to `ShellName`.

## Settings UI

Settings dialog as a modal overlay with responsive sizing (50% window width / 80% window height, min 420×400). Five tabs:
- **General** — Default Shell, Appearance (color theme, font), Terminal (font)
- **Profiles** — Shell profile CRUD (list + inline edit with accent border)
- **AI Tools** — auto-detection of CLI AI coding tools, version testing, custom tools
- **Database** — enable service, HTTP port, SQL Server/PostgreSQL credentials, scan interval, manual connections
- **Speech** — dictation on/off, shortcut (captured by pressing it), push-to-talk vs toggle, microphone,
  language, auto-Enter, vocabulary, and the model list with download/delete and progress; plus a **Phone**
  section (keep the bridge running, preferred port, and the phone's own auto-Enter). Those last two are
  the only settings on this tab that restart a running service, which is why the bridge debounces what it
  hears from here instead of acting on every intermediate value the spinner produces

`SettingsViewModel.SelectedTab` controls tab visibility, and the pages are named in `ViewModels/SettingsTabs.cs` (`General`, `Profiles`, `AiTools`, `Database`, `Speech`) — used by the view model, by the database tile's "open my settings" button, and from XAML through `{x:Static vm:SettingsTabs.…}`, which replaced the `Zero`…`Four` boxed-int resources. Constants rather than an enum: the selection is bound as an `int` to command parameters in two AXAML files, and the numbers were the problem, not the type. The Database tab has its own sub-tabs (`DbSubTab`: 0=Config, 1=Databases). Tab button styles: `settings-tab` / `settings-tab-active` in `Controls.axaml`; a bordered button on a settings row is `outlined-sm` there, not ten inline properties.

**The database form is the only one that is not saved as you type**, because applying it restarts the database service. `SettingsView` is therefore a `DockPanel` with a pinned Save & Apply bar at the bottom and the `ScrollViewer` *inside* it — docking to the bottom within a scroller pins to the bottom of the content, which is no pinning at all. The bar shows whenever `HasUnsavedDatabaseChanges`, which compares the form against the stored settings rather than remembering that something was typed, so undoing an edit puts it away.

Leaving the tab does not discard the edits, and closing the dialog (button, Escape, click outside — all through `MainWindowViewModel.CloseSettingsAsync`) or the application (`ConfirmShutdownAsync`) asks first. Answering yes discards; answering no reopens the dialog on the Database tab, because "you have unsaved changes" is no use without showing which.

## AI Tools

The AI Tools tab in Settings detects installed CLI AI coding tools and allows managing custom tools.

**Models:** `AiToolInfo` (runtime DTO from detection), `UserAiTool` (persisted custom tool with Id/Name/BinaryName/VersionArgs/CustomPath).

**AiToolDetector** (static, modeled after `ShellDetector`):
- `Detect(customPaths, userTools)` — scans PATH + known home directories (`~/.local/bin`, `~/go/bin`, `~/.{tool}/bin`, `%APPDATA%/npm`, `~/.cargo/bin`) with `.exe`/`.cmd`/`.bat` extensions on Windows. Custom paths take priority over auto-detect. User tools merged with the built-in list of 18 tools.
- `TestAsync(AiToolInfo)` — runs version command with 5s timeout, returns the first line of stdout.
- `FindInHomeDirs` — fallback when the tool is not on the system PATH (GUI app does not see paths from shell profile).

**AiToolViewModel** — MVVM wrapper with independent commands per tool (TestCommand, OpenFolderCommand, BrowsePathCommand, OpenUrlCommand, DeleteCommand). `BrowseFile` callback wired from View (file picker). `OnCustomPathSet` callback saves to settings.

**Lazy loading:** Detection is triggered on first visit to the AI Tools tab (`OnSelectedTabChanged`), not at application startup.

**Sorting:** Installed tools at the top (alphabetically), undetected below (alphabetically).

**Persistence in AppSettings:**
- `CustomAiToolPaths` (Dict<string,string>) — overridden paths for built-in tools
- `CustomAiTools` (List<UserAiTool>) — user-defined tools with CRUD in UI

**Tool card UI:** Left status strip (3px, green/gray), name + version, binary in monospace + path, badge (CUSTOM/NOT FOUND), buttons (delete/browse/folder/url/test). "Add Custom Tool" as `add-row` at the end of the list.

## Goal tile

Iterative AI-driven development workflow tile (inspired by Karpathy's autoresearch). Automates the loop: **user goal → AI clarifying questions → user answers → AI creates plan → user approves/rejects → AI implements code changes → AI reviews → iterate if needed → summary**.

**Workflow phases** (`GoalPhase` enum): Goal → Clarify → Plan → Implement → Review → Summary. The Clarify↔Plan cycle repeats until user approves. Clarify verifies the goal is specific, measurable, and achievable. Plan creates a concise implementation plan with clear steps and success criteria. User types "ok" to approve or describes what to change (→ back to Clarify). The implement-review loop runs up to 5 iterations automatically. After VERDICT: PASS or max iterations, shows summary. All prompts enforce Clean Code and SOLID (S, O) principles.

**AI tool integration:** Uses `AiProcessRunner` with `IAiToolRunner` interface (OCP). `ClaudeToolRunner` launches `claude -p "prompt" --output-format text --max-turns 20` via `ProcessStartInfo.ArgumentList` (no shell injection). New tools (OpenCode, Pi Agent) implement `IAiToolRunner`.

**No model selection.** The tile picks a tool, not a model: each tool uses its own default, because they front many providers and there is no command to list what a given one can reach (`AiProcessRunner`). `AppSettings.GoalDefaultModels` and `GoalTileState.SelectedModel` are what is left of an attempt at it — neither is read or written by anything, and they are kept only because removing a settings key is a migration. This paragraph used to describe the feature as though it shipped.

**Persistence:** State saved to `.mterminal/goals/{guid}.json` — goal text, messages, phase, tool/model selection. `TileNode.GoalFilePath` for layout persistence. **The write happens with every message and with every phase change** (`AddMessageAsync` and `SyncFromEngine`), not at the end of a phase. Messages go through a **debounce** (`SaveStateSoon`, `AppDefaults.SaveDebounceMs`), phase changes and the end of a run through `SaveStateNow`: a save serialises the whole transcript, and doing that on the UI thread for each of a hundred long answers is a hitch the user feels, while the points a restart has to land on exactly are the phase changes, which are rare. `Dispose` flushes whatever the debounce was still holding — but only when there is something to write or a file already there to keep current, since a Goal tile opened and closed without a word otherwise left an empty session in a directory nothing ever prunes — and sets `_disposed`, which stops `SaveStateSoon` arming a fresh timer afterwards: the workflow keeps unwinding after the tile is closed, and each of its last messages asked for a save that would have fired after the final flush, on a timer nobody would dispose. The timer's write is wrapped, for the reason spelled out on `SettingsService.DebouncedSave`: an unhandled exception on a thread-pool thread ends the process. The implement/review loop used to save only in its `finally`, so closing the application between approving a plan and the summary left the file holding the state from *before* the approval — no `ApprovedPlan`, iteration 0, and none of the tool's answers. Messages alone were not enough either: approving a plan moves the engine into Implement and then waits on the tool for minutes, and nothing is said in that time, so the file still read *Plan, nothing approved* for the whole of the first implementation. The save sits in `SyncFromEngine` because a phase change is exactly what a restart has to have seen. `GoalTileState` and everything it holds **refuse a null in their own setters**, the rule `AppSettings` follows and for the same reason — a property initialiser does not survive deserialisation, so `"Messages": null` replaced the fresh list and threw inside `LoadFrom`. That landed in the view model's catch of last resort, which stops the tile saving for good: a goal file with one null in it was punished more harshly than one of corrupt bytes, which is at least set aside so the tile can start again. `GoalStateNullGuardTests` walks the graph from `GoalTileState` rather than listing types. `GoalStatePersistence` writes through a uniquely named temporary file and a move, under a lock: at this write rate the truncate-then-write window of `File.WriteAllText` is opened often enough to matter, and a debounce timer on a pool thread can ask for a save at the same moment the UI thread does. The lock covers that; the unique name covers what it cannot, since the lock is per instance while the path is not. The **whole** snapshot — engine included, not only the messages — is taken on the UI thread, or `ToState` enumerates `ClarificationHistory` on a pool thread while the workflow adds to it, and that transient race used to light the permanent "this tile could not save its state". It also tells the two read failures apart, which are not interchangeable. Temporary files older than an hour are swept once per tile, on its first save — what they are is litter from a previous run, and this directory is never pruned, so sweeping per save would have every message pay for a scan of every goal ever set and damaged copies are kept five deep, because this directory is inside the user's repository and "rare but permanent" fills one up. A file that **parses** as nothing is damaged — including one holding the four characters `null`, which deserialises to nothing without complaint and would otherwise be indistinguishable from a file that was never written: it is moved to `<name>.bad-<timestamp>` — never over an earlier copy, since the stamp is only accurate to the second and two damaged loads within one would have had the second rescue destroy the first — and the tile may start fresh over the top of it. A file that could not be **opened** — locked, a failing disk — is almost certainly intact, so it is left exactly where it is and the tile **stops saving for the rest of its life** (`_saveRefused`). A state that loads only *part* of the way refuses to save for the same reason as one that could not be opened: the catch is reached with the transcript missing, and the next save would put that emptiness on top of the session. A file that is simply *gone* by the time it is read is neither: there is nothing there to protect, so it is the same answer as no file at all — treating it as unavailable stopped a tile writing for good over a file that no longer existed. Refusing to write costs the user the session in front of them; not refusing costs them the one already on disk. Both are said out loud in the transcript, because a log line would be the only trace that a session had existed — and so is a failed **write**, once, for the same reason: the tile that cannot save is the one whose user most needs to know before they keep working in it.

**Resuming an interrupted run:** there is no Continue button, and there does not need to be one. `GoalWorkflowEngine.WasInterrupted` decides, and it reads the whole state rather than the phase. `IsMidRun` names the two phases the tool works in (Implement, Review) and those count unconditionally; **Clarify and Plan cannot be answered by the phase at all**, because one value covers both *asking* the tool and *waiting* for the user's answer — calling both interrupted would have every tile ever closed at a question come back claiming to be paused, with Resume asking the same questions again. The transcript tells them apart: an answer that arrived is the last thing in it, so a run that was cut off leaves the *user's* own message last. `LoadFrom` sets `IsPaused` for a state that was interrupted — in the engine rather than in the view model, because it is a fact about a loaded state and there is then no second caller left to forget it — which is what the existing Resume button is bound to, and `ResumeAsync` already re-runs the loop from the top of an iteration. Without it the tile came back mid-run with nothing running and `Submit` answered *"AI is working, please wait"* for ever — the only way out being `+`, which throws the goal away. A pause the user asked for and a run the application was closed in the middle of are deliberately **not** told apart: distinguishing them costs a flag in the saved state to say something the user can already see. The label differs only by phase (`Stopped during implement…` vs `Paused.`).

For that to hold, a cancelled run must not look like a finished one. `RunAiAsync` returns null both when the tool answers nothing and when it is cancelled, and the loop used to answer both by summarising — which moved the tile to Summary, a phase `ResumeAsync` has no case for and `IsMidRun` does not recognise. Pausing an implementation was therefore a one-way door. `RunAiAsync` tells them apart itself and reports the answer as a verdict; every one of its exits goes through `GoalLoopPolicy.Judge`, so the rule has one home rather than being restated at each `return`. **Resume checks `IsRunning` as well as `IsPaused`**, and its button is disabled while the run unwinds: cancelling takes as long as the tool takes to die and Resume is offered for the whole of it, so clicking it there started a second implement/review loop alongside the first — two AI processes on one working tree, both writing the same file. `GoalTileViewModel.AiRunnerFactory` is the seam the loop is tested through — the same trick `TerminalControl.PtyFactory` gives the launch chain, and for the same reason: every bug in this loop needed a real AI process and a real worktree to reach. `GoalWorkflowLoopTests` drives Goal→Clarify→Plan→implement/review on the headless dispatcher against a stubbed tool, with a custom AI tool pointing at the test assembly so the tests do not pass by doing nothing on a machine with no tool installed. `RunAiAsync` returns an `AiRun` — the text and the verdict together — rather than a string plus three flags the caller had to remember to pass on; each flag had been added after its own bug, and each addition left a call site behind. `GoalLoopPolicy.Judge` turns a cancellation, an empty answer, a missing tool and a tool that threw into one of five verdicts. A cancelled run stops where it stands. **Empty** pauses too, everywhere: a tool that returned nothing once may answer the next time, which is the argument that has `Failed` pause. In Clarify and Plan it used to fall back a phase instead, and that was worse than it looked — from Clarify it landed in Goal, where the next thing sent clears the transcript and starts a new goal, so one empty reply put the session a keystroke from being thrown away. Only an **Answered** run puts anything in the transcript — asking whether the text was null instead put an empty bubble there whenever a tool replied with whitespace. Reaching a summary clears the pause, whichever way it was reached: a Summary that still called itself paused labelled the tile "Paused. Click Resume" over a Resume with nothing to do, and said so again after every restart. **NoTool** and **Failed** do the same and pause the tile — a process that would not start, or died halfway, may well work on the next click, so ending the goal over it throws away an approved plan for a transient fault; that was the same trap NoTool was in, reached through the generic catch in `RunAiAsync`. Cancellation is asked about before failure, because killing a process is a normal way to make it throw and what the user meant is the more important of the two facts. NoTool pauses the tile, because a tool that is not installed is something the user can go and install — and `RunAiAsync` scans again before giving up, since detection otherwise happens once when the tile is built. That re-scan is `RediscoverSelectedToolAsync`, not `DetectTools`: the latter is a first-run routine that clears `AvailableTools` — resetting the bound combo box's selection, which writes back through the binding — and falls back to *any* installed tool, so calling it mid-run silently swapped the tool a goal was being carried out with. The re-scan adds to the list, substitutes nothing, and runs on a background thread because it walks PATH and several home directories and the advice to install something and click Resume sent the user round the same loop for ever — summarising it instead put the tile in Summary, where the only way on is to type a new goal, so a binary being off PATH cost an approved plan and a transcript. `IsRunning` means *this tile is working*, and `WorkingAsync` holds it — and the run's one `CancellationTokenSource` — around a whole phase or loop rather than `RunAiAsync` doing either around one process. One token per call left it null in the gaps, where Pause had nothing to cancel, and left the git commands before each call uncancellable, so a pause taken while the working tree was being read waited for both processes — set around the process it went false in the gaps between an implementation ending and the review starting, and in those gaps the Pause button, bound to it, disappeared from a loop that was very much alive. Pause in such a gap also had no token to cancel, so the loop carried on and the pause was lost; `PauseRequested` is checked at each hand-over, and twice inside `RunLoopPhaseAsync` — on entry and again after the working tree is read, before the tool is launched, because reading the tree is two short git processes but the run after it is minutes. `Submit` moves the phase **before** writing the message that causes it, so the two are never on disk apart: between them the file said Clarify with the user's answer last, which reads as an interrupted Clarify, and a restart there resumed by asking the questions again instead of planning. `Submit` asks for the discard **before** it clears and writes the pause, so answering "no" leaves the tile exactly as it was. Both ways the question can go unasked answer **no** — `ConfirmAction` unwired in the view model, and no window to show a dialog in, in the view — because there is no undo for a discarded session. Reading the working tree is `WorktreeReader`, not the view model: a repository is not the view model's business, and while it lived there every test that drove the loop spawned four git processes a lap against a directory that was not a repository. `WorktreeReader.Factory` is the seam that stops it. The prompt fences every piece of borrowed text — the tree, the approved plan, the previous review — with a run of backticks one longer than the longest inside it (`GoalPromptBuilder.Block`): a fixed three-backtick fence is closed by the first fence in any diff that touches a markdown file, and everything after it — the rest of the diff included — reads as prose. The heading says *working tree* rather than *git diff*, because the block also carries untracked file names and, when git could not be read, a note saying so. The rules about the tile rather than the loop live in `GoalTilePolicy` — when answering spends a pause, when closing counts as one, and when a transcript is worth a dialog — pulled out beside `GoalLoopPolicy` because each was an inline condition and each was wrong at least once. `Submit` in those phases says *"This run is stopped. Click Resume…"* once rather than once per keystroke, and hands back what the user typed rather than swallowing it — the guard at the top of `Submit` returns while `IsRunning`, so a tile reaching that case is by definition not working, and the *"AI is working, please wait"* it used to answer was both unreachable and false.

Both phases of a lap go through one `RunLoopPhaseAsync` — they differed in a name, a label and which prompt to build, and were otherwise the same twenty lines twice, which is how the NoTool case came to be added to each by hand and the cancelled case to be fixed in one of them first. **Answering is resuming**: typing into a paused tile clears the pause, except in the working phases where the composer has nothing to send — without that, the run started, happened, and was thrown away at the first hand-over that asks about a pause. Resume itself has a `default:` that writes the cleared pause for the phases it cannot resume, or the file kept saying paused and the button came back after every restart to do nothing again. Resuming an interruption **after** the implementation finished starts at the review (`startAtReview`), rather than implementing again — and a pause is honoured only after the phase has been moved to whatever is actually owed next — Review when the implementation has just finished, the next attempt's Implement when the review has just asked for another pass. Stopping where the loop happened to be had Resume redo the run that had already completed, against an unchanged worktree and for a second copy of the same answer: the tool's answer is already in the transcript and its changes are already on disk, so re-running it asks the tool to redo work it can see it has done — usually a no-op, sometimes a duplicate, and always in the user's own worktree. Both `Pause` and `Dispose` record the pause *before* cancelling, and only when something was running — unconditionally is worse than not at all, since every idle tile then came back claiming to be paused and Resume in Clarify asked its questions a second time. In that order because: a bare cancellation is reported as a system message, which then becomes the last thing in the transcript, and `WasInterrupted` reads a Clarify or Plan whose last message is not the user's as one that already has its answer — so the tile came back saying "answer the questions above" with no questions above it. Falling out of the loop is the budget running out, which is **not** the review passing, and is summarised as such; it used to say "goal completed after 5 iterations" for it. Resuming finishes the attempt that was interrupted instead of opening a new one, so the budget of five is five attempts at the goal rather than five per launch; the resume question is asked *before* the budget question, so an attempt interrupted as the last of the budget is still finishable — `spent < max` alone would refuse to reopen the loop and lose it half-done. Both that and the verdict live in `GoalLoopPolicy`, pure and beside `ChainPolicy` for the same reason: neither was reachable by a test while it sat inside a loop that needs an AI process and a git worktree to turn over once.

The resume is from the top of an iteration, never from the middle of a prompt — the tool's process is gone and its output with it. The one piece of state that survives is the working tree, which is why the git diff now goes into **every** implement prompt rather than only those following a review: on a resume it is the only thing telling the tool that half of its own work is already applied. It is `git diff HEAD`, not `git diff`: a tool that stages its work as it goes leaves a plain `git diff` empty, and a resumed run would be told the tree was clean and would do the work again. Untracked files are invisible to every form of `diff`, and a new file is most of what an implementation produces, so `git ls-files --others --exclude-standard` lists their **names** alongside it — at a line each rather than a whole file, and it reads the index without writing to it, because nothing here may touch the user's repository. `GoalDiffContext.Compose` assembles the two, and it is pure and separate from the git calls because the assembly is where the bug was: the list used to be appended and the *whole thing* then truncated, so the moment the diff passed the cap the list vanished — in exactly the case it exists for, a resume after a large implementation. Each part is now capped on its own and joined afterwards. Both git commands run with `throwOnError: true` and are caught one at a time, and a failure is carried **into the prompt** rather than only into the log: they used to swallow a broken git, a missing repository or a bad `GitPath` into an empty string, indistinguishable from a clean tree, and a tool told nothing has changed when nobody could find out writes straight over work it cannot see. Each part is cut on a line boundary, because a path cut in half is a filename that does not exist. `WorktreeReader` returns null on a clean tree and the prompt builder omits the section, so this costs nothing when there is nothing to say. **The caps on it are a transport limit, not a token budget**: every tool is handed its prompt as a command-line argument, and Windows stops at 32 767 characters — 8 191 through the `.cmd` shim npm installs and `AiToolDetector` looks for first. Past that `Process.Start` throws, the run is judged `Failed`, and Resume reproduces the failure for ever, in exactly the resume-after-a-large-implementation this feature exists for. The caps are **6 000 characters for the diff, 1 000 for the untracked names** and `GoalPromptBuilder.MaxBorrowedChars` = **2 000** for each other borrowed piece — the goal, the clarifications, the approved plan, the previous review. The plan and the review are the tool's own output and have no natural size, so capping only the tree was capping the smaller half. **They bound the prompt; they do not promise it fits.** Six blocks at those sizes plus the quality rules can still pass 8 191, and numbers low enough to promise otherwise would mean a diff too small to be worth sending. `AiProcessRunner` refuses what still will not fit with a message naming the cause — measured **as quoted**, since a prompt of code grows on the way onto a command line, and only on Windows, since a POSIX system allows some two megabytes. Without it the refusal surfaced as a `Win32Exception` about nothing in particular.

The real fix is stdin, and **Claude Code now uses it**: `IAiToolRunner.AcceptsPromptOnStdin` is opt-in and false by default, true only for `ClaudeToolRunner`, because `claude -p` with nothing after it reads standard input. It is per tool rather than assumed for all four — a claim about somebody else's CLI, and a tool that does *not* read stdin would sit waiting for input that never comes. Which is also why an unrecognised binary now falls back to `GenericToolRunner` (prompt as a plain argument, no stdin) rather than to `ClaudeToolRunner`: that fallback was survivable while Claude ran on the command line and became a hang the moment it did not — a custom tool launched with Claude's flags, no prompt anywhere on its command line, and a pipe it had never agreed to read. The prompt is written and the pipe **closed at once**, or the tool waits for end-of-input that never arrives; the readers start first, or a prompt large enough to fill the pipe deadlocks against a child writing output nobody is draining; the cancellation registration is in place before the write, or a pause during one has nothing to interrupt it; and a broken pipe is logged rather than thrown, because letting it out skipped the awaits on stdout and stderr and threw away the tool's own account of what went wrong. The caps stay where they are while three of the four tools are still on the command line. `LoadState` adopts the saved tool name **only if it is still installed**, and says which one it is using instead otherwise: assigning it regardless ran `OnSelectedToolNameChanged`, which found nothing, cleared the working tool `DetectTools` had just picked, and left a name showing in a combo box that does not list it — so a tile saved with a since-uninstalled tool came back unable to run anything at all. Approving a plan adopts what the tool **proposed** (`GoalWorkflowEngine.ProposedPlan`), which a new planning run clears before it starts — a rejected plan otherwise outlived the rejection, and "ok" after a second run that produced nothing approved the plan the user had just turned down. Not the last assistant message in the transcript: once an empty or failed run could leave the Plan phase paused with no answer in it, "ok" approved the clarifying questions as the plan, or approved an empty string and started implementing in silence. With nothing proposed it now says so. `GoalResumeTests`, `GoalLoopPolicyTests`, `GoalTilePolicyTests`, `GoalDiffContextTests`, `GoalPromptBuilderTests`, `GoalStateNullGuardTests`, `GoalStatePersistenceTests`, `GoalWorkflowLoopTests` and `AiProcessRunnerTests` pin the rules. Goal files under `.mterminal/goals/` are **never pruned** — a tile removed from a layout leaves its session behind, which is deliberate for now: nothing distinguishes a goal file whose tile was closed from one whose workspace is simply not open.

**UI:** a terminal transcript, not a chat window. The tile borrows the terminal's own monospace face and size (`TerminalFontFamily`, `UiFontSize`/`UiFontSizeSm`) and the app's colour tokens, which `ThemeBridge` derives from the active terminal theme — so it follows the colour scheme like every other tile. Each message is a fixed 16px gutter glyph plus its text (`>` you, a dot the tool, a corner a note from the tile), aligned to one column. The bubbles it replaced aligned every message differently and made a two-line answer look like a different kind of thing from a twenty-line one; the gutter also gives the transcript a single left edge to read down. Message text is a `SelectableTextBlock`, so a transcript can be copied out — which is why `TerminalClipboardCoordinator.HandlesItsOwnCopy` has to know that type: this tunnel handler runs before the control's own Ctrl+C, and without it a selection made here was answered with text from whichever terminal in another tile still held one. The other half of that change is deliberate too: with focus in a Goal transcript and **nothing** selected, Ctrl+C no longer copies from a terminal in another tile — the same rule a focused `TextBox` has always had.

Three strips: a status strip on top (tool selector · phase dot · phase label — a `DockPanel`, because a horizontal `StackPanel` measures with infinite width and `TextTrimming` there is inert), the transcript, and a composer at the bottom drawn as one field with the prompt glyph inside it — the `Border` owns the border and focus ring, the `TextBox` gives up its own, and a click anywhere in it puts the caret in the box. Enter sends, Shift+Enter breaks a line. Buttons are the app-wide `tile-btn`. Pause/Resume appear while the tool is running; `+` starts a fresh goal.

What a phase says while it waits comes from `GoalWorkflowEngine.GetPhaseLabel` and nowhere else: the same four sentences used to exist in three places — the engine, the labels passed into `RunPhaseAsync`, and the summary — with nothing keeping them in step. The phase is the one saturated colour in the tile, and it is set as a style class (`phase-clarify`…) rather than a brush resolved in code-behind, so it repaints on a theme change. Its five colours come from the ANSI palette (`ThemeBridge`) and are pulled toward the foreground on a light theme — a six-pixel dot in ANSI yellow is invisible on white.

**Services:** `AiProcessRunner` — process launcher with `ArgumentList`-based arg passing and concurrent stdout/stderr reading (deadlock-safe); `IAiToolRunner` / `ClaudeToolRunner` live beside it. `GoalWorkflowEngine` holds the phase machine, `GoalPromptBuilder` the prompts, `GoalStatePersistence` the `.mterminal/goals/*.json` round trip, `WorktreeReader` the git commands — with `.mterminal` excluded by pathspec on **both** commands: the listing would otherwise hand the agent the path to this tile's own transcript, and a goal file that has been committed — nothing adds `.mterminal/` to `.gitignore` except the Git tile — shows up in `diff HEAD` with its contents — and `GoalDiffContext` the assembly of what they return, `GoalLoopPolicy` and `GoalTilePolicy` the rules — the view model drives them and owns none of the rules.

## Database tile

Per-workspace bridge that lets LLM agents (Claude Code, OpenCode, etc.) query local databases directly via HTTP — without manual connection setup. The tile auto-generates context files (`claude.local.md`, `AGENTS.md`, `GEMINI.md`) so agents discover available databases and how to call them.

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

**Context file generation:** `ClaudeLocalMdWriter` writes the `# Database access` section to `claude.local.md` (Claude Code), `AGENTS.md` (OpenCode, Codex), and `GEMINI.md` (Gemini CLI). Existing content in these files is preserved — only the database section is replaced.

**Workspace config:** `.mterminal/databases.json` — `WorkspaceDatabaseTileConfig` with `Databases` (list). Context files are generated when database service is running and the list is non-empty.

**Settings:** Database tab in Settings — enable service, HTTP port, SQL Server (Windows Auth / SQL Auth), PostgreSQL (credentials, ports), scan interval, manual connections (CRUD with inline edit form, test connection). Save & Apply restarts the service automatically. Passwords encrypted with DPAPI.

**Logs:** `DbLogger` — HTTP query and discovery logs in memory (max 500) + daily files in `%APPDATA%/MTerminal/db-logs/`.

**Services:** `Services/Database/` — IDbProvider, SqlServerProvider, PostgreSqlProvider, SqlGuard, SqlGuardProfile, QueryHandler, DbRegistry, DiscoveryService, DbHttpServer, DbLogger, SubnetScanner, DatabaseServiceManager, ClaudeLocalMdWriter.

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

Three things are worth knowing before touching `Services/Phone/`:

- **`IAudioCapture` is the seam.** `PhoneAudioCapture` implements it and `RoutedAudioCapture` picks
  between it and the microphone per recording, so `DictationService` gained a second input without
  gaining a line of code. Handles are tagged with the backend that made them, because `Detach`/`Finish`
  are split and a new recording can start on the other backend mid-close.
- **TLS is not optional** — a browser hands out no microphone outside a secure context. That is why this
  one server is Kestrel rather than `HttpListener` (which needs `netsh http sslcert` and administrator
  rights for HTTPS), and why Tailscale is the recommended path: its MagicDNS name gets a real
  certificate, while a LAN address can only ever have a self-signed one and a warning to click through.
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

`FileLogWriter` writes logs to `%APPDATA%/MTerminal/logs/mterminal-YYYY-MM-DD.log` with automatic cleanup of files older than 7 days. `LogTraceListener` redirects `Trace` to log files.

## Persistence

- `%APPDATA%/MTerminal/` (Windows) or `~/.config/MTerminal/` (Linux)
- `settings.json` — everything in Settings plus window state and the database configuration (passwords DPAPI-encrypted). Renaming a key here is a migration: the old one stops being read and the user silently gets the new default, which is why `GitHideMTerminalDir` and `Speech.HotkeyEnabled` are still parsed once (`SettingsService.MigrateLegacySettings`). Every section and collection **refuses a null in its own setter**: a property initialiser does not survive deserialisation, and `"Speech": null` is not an error the load's own catch would see — it is a `NullReferenceException` while the main window is being built, so the application does not start and says nothing about why. The guard is on the property rather than a normalisation pass after loading, because a pass only ever covers the level somebody remembered: `"Speech": { "CustomWords": null }` walked straight past one and stopped startup just the same. **Strings are covered by type rather than one at a time** — `NullToEmptyStringConverter`, registered on `JsonDefaults.SettingsOptions` (the settings file's own options, not the shared ones, because elsewhere a null string may be meant), with `ProtectedStringConverter.HandleNull` covering the encrypted ones a property-level converter would otherwise hide. Hand-guarding had reached four properties out of dozens. `SettingsNullGuardTests` now *walks* the settings graph from `AppSettings` (through collection element types too) instead of listing three types, which is how `PostgreSqlDiscoverySettings.Ports` — a startup crash one hop below where anyone was looking — stayed unguarded. The converter also **overrules `string?`**, and cannot do otherwise: it is chosen by type and never told which property it fills, so a nullable property still arrives empty from the file. Both such properties (`LastWorkspaceId`, `RequiredAiToolBinaryName`) are read through `IsNullOrEmpty`, so it costs nothing — and the list is pinned by a test, because that is true by inspection rather than by construction.
  **An unreadable file is copied aside before it is overwritten** (`settings.bad-<timestamp>.json`, newest five kept). "Treat it as a first run" is only half the story: the first-run steps save, so within milliseconds the user's profiles, tool paths and database passwords are replaced by defaults — and "unreadable" is often a truncation with most of the content intact
- `workspaces.json` — list of workspaces (id, name, path)
- `workspaces/{id}.json` — tile layout per workspace (shell name, user profile id, tile id, tile name). Backward compat: `RootPane` → `RootTile` migration in `WorkspaceState`
- `logs/` — application logs (daily files, 7-day retention)
- `sessions/opencode/ses_<tileId>.json` — the import document an OpenCode tile creates its session from (see Session resume). Rewritten on every launch; a few hundred bytes, and deliberately never pruned — while the file exists, a session the user threw away can be recreated on the next launch
- `models/` — downloaded speech-to-text models (hundreds of MB each; `.partial` while downloading)
- `phone/` — the phone bridge's TLS material and its paired devices. `bridge.pfx` **contains a private key**; kept rather than regenerated per launch, because a new certificate every launch means a new browser warning every launch, and reissued when the machine's set of addresses changes (a certificate is only accepted for a host in its SANs). `sessions.json` holds SHA-256 of each paired device's token — never the token, so the file records *who* is paired without being usable to authenticate. Shutting the application down does not clear it; turning the bridge off does
- Auto-save with debounce

## Conventions

- **Workspace** (not "project") — working directory with terminal/editor tiles. Right-click on workspace → context menu (Show in Explorer, Remove).
- **Tile** (not "pane"/"panel") — a single tile in a workspace (terminal, note, or todo), split into a binary tree
- **Note** (not "editor") — tile with text editor (AvaloniaEdit), TileContentType.Note
- **Todo** — tile with task list, TileContentType.Todo
- ViewModels in `ViewModels/`, views in `Views/`
- **Git** — tile with change viewer (diff, commit, stash, push, fetch, tags, undo, context menu, discard), TileContentType.Git
- **Database** — tile with database management (SQL Server, PostgreSQL), HTTP bridge, query logs, TileContentType.Database
- No DI container — manual injection in `App.axaml.cs`, `TileFactory` as the tile content factory
- **ConfirmAction pattern** — destructive actions (discard, remove workspace, undo commit) use `Func<string, Task<bool>>? ConfirmAction` in ViewModel, wired from View as `MessageBox.Avalonia` dialog (YesNo). **An unwired dialog normally lets the action through** — except in Settings, where it does not. `SettingsView.ConfirmAction` answers **no** when there is no window to ask in, and that covers *every* confirmation on that dialog: deleting a manual database connection, a profile, a custom AI tool, a downloaded speech model. An unanswered question is not a yes, and nothing on that dialog is cheap to undo — a speech model is hundreds of megabytes and, on a slow connection, hours. The speech model's own chain says no at all three links (the row, the tab that wires it, the view), and all three had to change together: a `?? Task.FromResult(true)` in the middle made the row's own refusal unreachable
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

**`.mterminal/` in `.gitignore`:** Setting `GitIgnoreMTerminalDir` (default **on**) keeps `.mterminal/` listed in the workspace's `.gitignore`, applied on every Git tile refresh (`GitIgnoreFile`, `GitTileViewModel.ApplyMTerminalIgnoreSettingAsync`). It replaced `GitHideMTerminalDir`, which only hid those files in this tile's list — leaving them untracked *and* unignored, so they were invisible here and waiting in every other git client.

Consequences worth knowing: **the app edits a file in the user's repository, and creates one where there is none** (a blank line, a `# mTiles workspace state` comment and the entry, appended; turning the setting off removes exactly those and nothing else). `GitIgnoreFile` works on raw bytes and only ever appends, so a BOM and any non-UTF-8 content survive; removal rewrites through a temporary file and a move. An emptied `.gitignore` is left in place rather than deleted — this cannot tell one it created from an empty one the user committed. It is written by the **Git tile**, so a workspace without one is untouched. Files already *committed* under `.mterminal/` now appear in the changes list, correctly — ignoring something git already tracks changes nothing. A user who had the old setting off keeps it off: `SettingsService.MigrateLegacySettings` reads the old `GitHideMTerminalDir` once and drops it. That case is the only one in which the app would edit a repository against a decision the user had already made.

**DiffFontSize:** Diff panel uses 80% of font size (`FontSize * 0.8`).

## Workspace view caching

`MainWindow` caches `WorkspaceView` instances in `Dictionary<string, WorkspaceView>`. Switching workspaces via `IsVisible` toggle instead of DataTemplate — terminals are not killed/recreated. `WorkspaceRemoved` event clears the cache and removes the view from the visual tree.

## Workspace panel — branch names

`WorkspaceItemViewModel` — wrapper for `Workspace` with `ObservableProperty BranchName`. The workspace panel displays the branch name next to the path (SourceBranch icon + name). `DispatcherTimer` polls `GitService.GetBranchNameAsync` every 30s (static method, creates a temporary `GitCommandRunner`). Dispose in `MainWindowViewModel.OnClosing`.

## InputDialog

Reusable modal dialog (`Views/InputDialog.axaml`): title, TextBox with placeholder, optional suggestions list (ListBox). Enter = OK, Escape = Cancel. Clicking a suggestion enters it into the TextBox. `ShowDialog<string?>` returns trimmed text or null.
