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
- `Models/` — DTOs and data models, no behaviour (Workspace, WorkspaceState, TileNode, AppSettings, AppDefaults, ShellProfile, LaunchScripts, UserShellProfile, TerminalTheme, GitFileChange, CommitLogEntry, AiToolInfo, UserAiTool, GoalTileState, GoalCommit, GoalFinding, GoalReviewResult, GoalClarifyResult, GoalCompletionCriteria, GoalStopReason, GoalImageAttachment, SolidPrinciples, AiPermissionMode, AiEffort, DatabaseSettings, DatabaseInstance, ManualDatabaseConnection, WorkspaceDatabaseConfig, SpeechSettings, PhoneSettings)
- `ViewModels/` — MVVM with CommunityToolkit.Mvvm (source generators)
- `Views/` — Avalonia AXAML + code-behind
- `Styles/` — design tokens (`AppTheme.axaml`) and global control styles (`Controls.axaml`, including GridSplitter). UI colors exclusively via `DynamicResource`, terminal ANSI colors separately in `TerminalTheme`. `BgCanvas` is the odd one out: it is what the tiles are laid on and the only colour here not meant to be looked at (see Split tiles architecture)
- `Services/` — JSON persistence (PersistenceService, SettingsService, WorkspaceService), shell detection (ShellDetector), AI tools detection (AiToolDetector), ThemeBridge, JsonDefaults, AppPaths, AppInfo, GitService/GitCommandRunner/GitDirectoryWatcher/GitIgnoreFile, DiffFormatter, FileHelper, ProtectedStringConverter, TolerantEnumConverter, TileFactory, TileTreeSerializer, TileNameGenerator, TileMinimumSize, SpecialDirectories, DefaultWorkspace, the Goal tile's engine (AiProcessRunner, AiPermissionModes, AiEfforts, GoalWorkflowEngine, GoalPromptBuilder, GoalStatePersistence, GoalLoopPolicy, GoalTilePolicy, GoalCompletionPolicy, GoalBaseline, GoalCommitter, GoalCommitPlan, GoalDiffContext, CommandDisplay, CommandLineLength, ElapsedDisplay, GoalStageDisplay, RejectedFlag, GoalResponseParser, GoalStateStore, GoalTranscript, GoalImageStore, GoalImageMarker, SolidPrincipleCatalog, WorktreeReader, and its `@` file mentions — IFileMentionSource/WorkspaceFileMentionSource, FileSuggestionIgnore, FileMentionToken, FileMentionMatcher, FileMentionCorpus), UpdateService (its Velopack manager is built lazily and fails soft — an installation it cannot ask about must not stop the main view model being built), CrashHandler, FileLogWriter, LogTraceListener
- `Services/Database/` — DatabaseServiceManager, DbHttpServer, DiscoveryService, DbRegistry, DbLogger, QueryHandler, SqlGuard, SqlGuardProfile, SqlServerProvider, PostgreSqlProvider, SubnetScanner, IDbProvider, ClaudeLocalMdWriter
- `Services/ShellStarter.cs` — one call that replaces whatever session a `TerminalControl` holds and hands the shell its startup script (`${tileId}` substituted, one line per `\r`). The control owns the rest: killing the old session, waiting for it, and gating the script on `ShellReady` for *that* session
- `Services/TileLauncher.cs` — launching a terminal tile: disposes the previous launch, picks the profile's current scripts, then either the direct-launch chain or a plain interactive shell. First launch and "restart shell" both go through it. It reads `TileId`, it never assigns it
- `Services/DirectLaunchSession.cs` — one tile's command chain (see Shell Profiles below); disposable, and disposing it is what stops it relaunching
- `Services/TerminalClipboardCoordinator.cs` — window-level Ctrl+C across tiles (see Terminal key handling)
- `Services/AiPermissionModes.cs` — the one map from `AiPermissionMode` to the `--permission-mode` flag and to the words the strip shows. It also recognises the tool *rejecting* that flag: the spellings are somebody else's CLI contract and it has moved once already, so an older Claude Code fails **every** run on the default setting, and "the AI tool reported a failure" over a usage message about a flag the user never typed names no cause at all. The setting itself is read tolerantly (`TolerantAiPermissionModeConverter`) because it lives in `settings.json`: a mode written by a newer build and read after a Velopack rollback would otherwise quarantine the profiles, the tool paths and the DPAPI-encrypted database passwords along with it. **`bypass` asks once before it is stored** — it is the largest single grant here, it applies to every Goal tile, and a combo box is a thin control for a decision whose first symptom is an unattended run that already happened
- `Services/AppPaths.cs` + `Services/WorkspacePaths.cs` — the two directories this application owns, and the one-time move each performs from the name it used before the rename. **Both fail soft**: a move that cannot be made leaves the old directory in use rather than presenting a first run, because the first run saves. `WorkspacePaths` is the one inside the user's repository, so its move shows up as a rename in their next `git status` — visible and reversible, which is the most it can be
- `Services/Phone/` — dictation from a phone (see `docs/DICTATION.md` → *Dictating from a phone*): PhoneEndpoint/IPhoneEndpointSource with NetworkEndpointSource, TailscaleEndpointSource and MulticastDnsEndpointSource, PhoneEndpointRanker (pure — the one part whose behaviour is an opinion, so it is argued in a table test), PhoneEndpointDirectory, SessionLocationProbe, PhonePairing, PhoneCertificates, PhoneFirewall, PhoneAudioCapture + RoutedAudioCapture, PhoneBridgeServer (Kestrel — the only server here that faces the network), PhoneBridgeManager, PhoneKeys (the three keys the page can press, and where they land), QrCodeImage, UiDispatcher
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

**Adding an entry to a list opens a form, it does not grow the list.** The three that make one — a shell
profile, a custom AI tool, a manual database connection — share one overlay in `SettingsView`
(`SettingsViewModel.IsEditingAnything`, `CancelEditing`), on the same `Border.modal-card` a dialog uses.
As rows they were unusable in a way that only shows at the keyboard: the form is taller than the
viewport, so opening one pushed the list it came from off screen and put Save below the fold. Escape and
the scrim close the form, and only then the dialog — the innermost thing first, or the user cannot tell
which of the two they just cancelled.

## Split tiles architecture

Recursive binary tree: `LeafTileNodeViewModel` (terminal/editor) or `SplitTileNodeViewModel` (H/V + two children). `TileNodeView` manages views manually (not DataTemplate); rebuilding the tree re-parents live terminals with no bracketing, because detaching one does not end its session.

`LeafTileNodeViewModel.IsActive` — `TileActivationScope` (per-workspace instance) guarantees that only one tile is active. `LeafTileView` reacts to `IsActive` — the card's own outline turns `AccentOutline` (`TileCard`) — a muted accent, because the longer the line the quieter it has to be to carry the same weight, and the full accent that suited a 2px strip read as a blue frame once it went all the way round, the toolbar lifts to `BgElevated`, and an inactive tile's header recedes to 0.55 opacity. The outline replaced a 2px strip along the top of the toolbar, which was the right marker for a square tile in a grid of splitters and the wrong one the moment the tile became a rounded card: the radius eats the strip's ends, and what is left is a short line floating inside a corner rather than an edge. The header only — the content of an inactive tile is still being read, and dimming a running terminal because the focus is elsewhere makes every split worse than no split.

**The window is one canvas with cards on it.** `MainWindow` is painted `BgCanvas`; the workspaces panel and every tile are cards on it — same ground, same `RadiusTile`, same `BorderSubtle` hairline — separated by one gutter width, which is the 8px splitter column between panel and content, `TileNodeView.TileGap` between tiles, and the padding `WorkspaceView` puts around the outside. That last one is the whole point: without it the outermost ring of cards is cut off by the window frame, and a card clipped by the title bar is not a card.

**A tile is a card.** `WorkspaceView` is painted `BgCanvas` (below `BgBase`, derived in `ThemeBridge` and going the other way on a light theme, because the canvas is only ever seen in the gaps); `LeafTileView`'s outermost element is a `Border` with `RadiusTile` and a `BorderSubtle` outline, wrapping a second `Border` at `RadiusTileInner` that does the `ClipToBounds`, so nothing inside — a terminal's own background included — has to know the radius. **The two borders are not one border.** `ClipToBounds` on the border that also draws the outline clips to the rounded-down bounds rectangle, so at a fractional desktop scale (125%, 150%) the right and bottom edges fall outside their own clip and the outline renders as an L along the top and left. The panel's card has the same pair for the same reason. `TileNodeView.TileGap` is the canvas showing between two tiles and is the splitter's whole hit area, which is why that splitter carries `GridSplitter.tile-gutter`: transparent and stretched, because the gap *is* the divider and a drawn bar on top of it would be a second one (transparent rather than unset — an unset background is not hit-testable and it would stop being draggable). **A class, not the base `GridSplitter` style**, which stays a visible 2px bar: the splitters *inside* tiles — the git tile's list against its diff, the diff view's two editors — sit in `Auto` columns and take their width from it, so making the base style width-less collapsed them to zero and made them impossible to grab.

**A splitter cannot squeeze a tile out of sight** (`TileMinimumSize`, 50px along either axis). The minimum is a property of the whole subtree rather than of the pane being dragged: a star-sized column takes the size the splitter gives it and never grows to what its content needs, so a column squeezed to 50px that holds a further split lays *its* two tiles out past its own edge, under the opaque card next door — the tile is gone, by the route the minimum exists to close. Every leaf along the axis wants its 50px and every split along it also spends a `TileGap`, which is what makes the gutter part of the minimum's arithmetic and not just a look: widening it makes every layout containing a split wider. `TileNodeView.ShowSplit` sets the sum on both definitions and `TileMinimumSize.Fit` scales the pair back proportionally when the grid is narrower than the two together — a floor the layout will not go below does not shrink anything when it does not fit, it pushes the far pane past the edge and clips it, which is the same disappearing tile reached by narrowing the window. The guarantee is about the splitter, not about a window too small to hold the tiles at all. `UpdateSplitRatio` therefore stores the ratio from the definitions' actual sizes, so what is persisted is where the splitter ended up after the minimum had its say.

**Only a terminal's content is inset from the card** (`LeafTileView.ContentInset`). A terminal is text against an edge and wants the gap; every other tile's content is its own chrome — bars, lists, a composer — and drawing that inside an inset left a square-cornered rectangle floating in a rounded card, with a sliver of card colour round the bottom corners where the two shapes disagreed. Those tiles run to the card's edge and take its corners from `ClipToBounds`.

**The header gives up its split buttons before it gives up the tile's name.** In a `DockPanel` the name gets whatever the docked buttons leave, which in a narrow column was nothing: four tiles in a stack showed a row of icons each and not one name between them. `LeafTileView.ApplyHeaderWidth` stands the two split buttons down below `SplitButtonsNeedWidth`, because closing and the overflow have no other route while a split is also a drag away.

Each tile wears its type's icon in the header (`Views/TileTypeIcon.cs` — the same six the empty tile's chooser offers) in its `TileAccent*` colour, set from the code-behind because both follow `ContentType`. Those six accents were raised out of the 40%-lightness band they were picked in: they used to be drawn only as a 3px bar and a 22px chooser icon, and at 13px on `BgElevated` the old values were dark smudges. The header's actions are **buttons *and* menu items**: the `…` overflow holds Restart shell, New session and both splits and never stands down, while the buttons are the fast path and give way as the tile narrows (`ApplyHeaderWidth` — splits at 260px, restart and new session at 190px). Splits go first because dragging a tile onto another does the same job. The microphone is neither: it is a toggle whose state the header has to show, and a light behind a menu is not a light.

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
- `Detect(customPaths, userTools)` — scans PATH + known home directories (`~/.local/bin`, `~/go/bin`, `~/.{tool}/bin`, `%APPDATA%/npm`, `~/.cargo/bin`) with `.exe`/`.cmd`/`.bat` extensions on Windows. Custom paths take priority over auto-detect. User tools merged with the built-in list of 17 tools.
- `TestAsync(AiToolInfo)` — runs version command with 5s timeout, returns the first line of stdout.
- `FindInHomeDirs` — fallback when the tool is not on the system PATH (GUI app does not see paths from shell profile).

**AiToolViewModel** — MVVM wrapper with independent commands per tool (TestCommand, OpenFolderCommand, BrowsePathCommand, OpenUrlCommand, DeleteCommand). `BrowseFile` callback wired from View (file picker). `OnCustomPathSet` callback saves to settings.

**Lazy loading:** Detection is triggered on first visit to the AI Tools tab (`OnSelectedTabChanged`), not at application startup.

**Sorting:** Installed tools at the top (alphabetically), undetected below (alphabetically).

**Persistence in AppSettings:**
- `CustomAiToolPaths` (Dict<string,string>) — overridden paths for built-in tools
- `CustomAiTools` (List<UserAiTool>) — user-defined tools with CRUD in UI

**Tool card UI:** Left status strip (3px, green/gray — it carries a fact, which is why it survived the sweep that took the decorative rails out), name + version, binary in monospace + path, badge (CUSTOM/NOT FOUND), buttons (delete/browse/folder/url/test). **Add** is a `header-action` on the section's heading row, not a row at the end of the list.

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

The page also carries **Enter and the two arrows** (`PhoneKeys`, one `{"type":"key"}` message on the same
socket), because dictating a command is only half of driving an agent from the sofa — the other half is
the prompt it stops on. They route by the transcript's own rule (focused text control first, then the
active tile's terminal) so that the Enter lands where the sentence did, and they are delivered as a
synthesised `KeyDown` rather than bytes: what Up means on the wire depends on DECCKM and win32-input-mode,
both of which the terminal control owns and neither of which it exposes. Gated on nothing dictation is
gated on — a machine with no model and no microphone can still be driven this way. That **is** a new
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
- `settings.json` — everything in Settings plus window state and the database configuration (passwords DPAPI-encrypted). Renaming a key here is a migration: the old one stops being read and the user silently gets the new default, which is why `GitHideMTerminalDir`, `GitIgnoreMTerminalDir` and `Speech.HotkeyEnabled` are still parsed once (`SettingsService.MigrateLegacySettings`). The first two are the same question under three names — the application was renamed under it — and they are applied oldest first so the **newest** answer wins: with three generations, "the oldest wins" stops being caution and becomes an answer nobody can change. Every section and collection **refuses a null in its own setter**: a property initialiser does not survive deserialisation, and `"Speech": null` is not an error the load's own catch would see — it is a `NullReferenceException` while the main window is being built, so the application does not start and says nothing about why. The guard is on the property rather than a normalisation pass after loading, because a pass only ever covers the level somebody remembered: `"Speech": { "CustomWords": null }` walked straight past one and stopped startup just the same. **Strings are covered by type rather than one at a time** — `NullToEmptyStringConverter`, registered on `JsonDefaults.SettingsOptions` (the settings file's own options, not the shared ones, because elsewhere a null string may be meant), with `ProtectedStringConverter.HandleNull` covering the encrypted ones a property-level converter would otherwise hide. Hand-guarding had reached four properties out of dozens. `SettingsNullGuardTests` now *walks* the settings graph from `AppSettings` (through collection element types too) instead of listing three types, which is how `PostgreSqlDiscoverySettings.Ports` — a startup crash one hop below where anyone was looking — stayed unguarded. The converter also **overrules `string?`**, and cannot do otherwise: it is chosen by type and never told which property it fills, so a nullable property still arrives empty from the file. Both such properties (`LastWorkspaceId`, `RequiredAiToolBinaryName`) are read through `IsNullOrEmpty`, so it costs nothing — and the list is pinned by a test, because that is true by inspection rather than by construction.
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

A workspace is **one row: its name, and one line under it saying what it sits in** — and deliberately nothing else. What has stood there and been taken back out — an open/closed marker, a disclosure chevron and a count of the tiles under it, with the list of those tiles under that — was in every case either already on screen somewhere better or competing with the name for a row 240px wide, and the row ended up showing a count and an ellipsis where the name should have been. That second line is the branch where there is one, the offer to create a repository where one can be, and **the path where neither is true** — the home directory, a drive root, a system folder: those rows were the one place it came out blank, and a reserved line saying nothing is height spent on silence. The path is shown rather than a word for the kind of place, because on exactly those rows the *name* is already the word ("Home directory" is an alias) and the path is what says which profile, which drive. It trims, which is why it is in a `DockPanel` behind a docked glyph and not in a horizontal `StackPanel` — one of those measures its children with infinite width, so `TextTrimming` in it never fires and a long path runs out of the row and is cut mid-character by the scroller. The full path is still in the row's tooltip.

**The second line is what settles the competition.** Side by side, the name and the branch bid for the same 240px and the name kept losing — it was the one that had to give way, so a row came out as "B…" beside a fully spelled-out `feature/ui-redesign`. A line each costs a few pixels of height and ends the argument, which is why there is no longer any width below which the branch is hidden.

**The meta line is always there and always the same height** (a fixed-height `Panel` holding it), whatever it has to say. Showing it only for repositories gave the list two row heights interleaved at random, which is the one thing a column of twenty names cannot afford — nothing lines up and the eye has no rhythm to scan by. Reserving the line also covers the moment before the check has answered.

A workspace that is **not** a repository gets an offer in that line rather than a label: **Create repository** runs `git init` after a confirmation (`WorkspacesPanelViewModel.CreateRepositoryCommand`). Saying only "no repository" would leave the user to go and find a terminal to type the answer into; asking first is because `git init` writes into somebody's folder from a row they are otherwise clicking to switch workspaces, and an unwired `ConfirmAction` answers **no**. `WorkspaceItemViewModel.HasRepository` is `bool?` and the third state is the one that matters: the check is asynchronous, and a plain `bool` would have every repository in the list announce it had none until the first pass finished.

**Not every directory without a repository gets the offer.** `SpecialDirectories.Kind` names what kind of place a path is — home, Desktop, Documents, Downloads, Pictures, Music, Videos, the root of a drive, a system directory, an ordinary project folder, or one nothing could read — and `AllowsRepository` is *derived* from it (`Kind(path) == Ordinary`) rather than deciding again, so the glyph on a row and the offer on it cannot reach different conclusions about the same path. A repository at `~` tracks every download and every application's configuration, and its first `git status` takes minutes; the drive root and the system directories are that mistake one step larger, the user's own file folders one step smaller — and those are the folders somebody browsing for a workspace lands in by accident. **The user folders match only themselves, never their children** — that is the whole difference from the system directories: a project under `~/Documents` is an ordinary project. **Two of them are guessed by name** under the home directory, both because the platform will not say: Downloads, which has no `SpecialFolder` at all, and — on Unix only — Documents, whose `SpecialFolder` answers with `$HOME` there (`MyDocuments` is `Personal`), which left `~/Documents` the one of these six offered a repository on Linux. That mapping is also why `Kind` answers `Home` and not `Documents` for `MyDocuments` on Linux, which is correct: the path *is* the home directory. A guess that misses — a localized `Dokumenty`, a relocated Downloads — is simply not found, and a folder that is not found is an ordinary one, which is the safe way round. **Those rows show their path instead** (`WorkspaceItemViewModel.ShowsDirectoryPath`, the complement of `HasNoRepository` among the rows with no repository): the meta line is reserved on every row, so leaving it blank was height spent on silence — and these are the rows the name covers for least, because on exactly these it is a kind of place rather than which one. "Home directory" is an alias this application chose, and it is the path that says which profile; a word for the kind would be the name a second time. **The glyph belongs to that line, not to the name** (`SpecialDirectoryIcon.Kind`, a converter in `Views/` for the reason `TileTypeIcon` is there — which picture stands for a kind of place is a fact about the drawing): in front of the name a house said what the name already said, while the line that needed a mark carried a generic folder. One picture per kind — a house, a monitor, a document, a download, an image, a note, a film, a disk, a cog — and an unrecognised kind falls to a plain folder rather than throwing, because a wrong glyph is legible and an empty row is not. The path gets its own line and trims, which is what the row could not offer it beside the name — the reason it is in the tooltip everywhere else. A row nothing has checked yet still says nothing: `HasRepository` is null until the first pass answers, and a path there would be the row claiming to have been looked at. The rule is pure and shared, because the same "is this the home directory" also decides the workspace's name.

**A first run opens on one workspace holding one terminal** (`DefaultWorkspace.SeedFirstRun`, called from `App.axaml.cs` before the main window is built) — at the home directory, which is the one place every machine has and the user can certainly write to. **The condition is that there is no `workspaces.json` at all, not that the list is empty** (`WorkspaceService.HasStoredList`): `Load` answers every read failure with an empty list, so a file locked by another instance or truncated by a power cut is indistinguishable from a first run — and seeding writes, which would replace the user's whole list with this one workspace and orphan their layouts. The file is also what remembers the answer, so a user who removes their last workspace does not get it back on the next launch. It fails soft, because a home directory that cannot be written to is a reason to start on an empty panel and not a reason not to start.

**The home directory is displayed as "Home directory", not as the login.** A workspace takes its name from the last part of its path, which for `C:\Users\andrz` is `andrz` — the account, not the place. `WorkspaceDisplayName.For` is a display rule and not a rename: `workspaces.json` keeps whatever is stored, so the same file on a machine with a different login, or the workspace moved elsewhere, shows the directory's own name again. The row also **wears a house**, because a row in a list of folders is read as a folder and the words alone can be taken for one somebody made — but on the **meta line**, not in front of the name. In front of the name it said what the name already said; on the line under it, what kind of place this is *is* the thing being said, and the house is one of ten glyphs (`Views/SpecialDirectoryIcon.cs`, drawn from `WorkspaceItemViewModel.SpecialKind`) rather than the only one. **The list sorts by the alias, not by the glyph** — `WorkspaceDisplayOrder` compares `WorkspaceItemViewModel.Name`, so `Home directory` reads under H rather than under the login, and every alias `WorkspaceDisplayName` grows follows the same line; ordering on the mark as well would bunch the aliased rows at one end and override the alphabet the rest of the column is read by. Pinning stays the one thing that outranks the name. The filter matches the alias *and* the path, so the row is still found by typing the login.

**Plain text, not a chip** (`StackPanel.workspace-meta`). A chip is the language this application uses for the rare exception — the AI tool that is NOT FOUND, the database tile's Error — and it works because you see one at a time. A value present on every row is not an exception, it is metadata: twenty boxed outlines down the column gave the list a zigzag right edge and no rhythm to read it by, and put a third frame inside a card inside a canvas. It sits on **the same left margin as the name**, which is what leaves the panel one left edge to read down; right-aligned it had the same disconnected look the chip did.

**Selected is not hover.** Both were `InteractiveHover`, so pointing at a neighbour made it impossible to say which workspace was open — the one thing the list exists to tell you. Selected gets `BgElevated` and an accent down its leading edge, the marker the tiles already use for the same idea.

## InputDialog

Reusable modal dialog (`Views/InputDialog.axaml`): title, TextBox with placeholder, optional suggestions list (ListBox). Enter = OK, Escape = Cancel. Clicking a suggestion enters it into the TextBox. `ShowDialog<string?>` returns trimmed text or null.
