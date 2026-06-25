# mTiles

Cross-platform terminal manager — .NET 10 + Avalonia 12.

## Building and running

```bash
dotnet build
dotnet run --project src/mTiles
```

## Structure

- `src/mTiles/` — the only project in the solution
- `Models/` — DTOs and data models (Workspace, TileNode, AppSettings, AppDefaults, ShellProfile, UserShellProfile, TerminalTheme, GitFileChange, CommitLogEntry, AiToolInfo, UserAiTool, WorkspaceItemViewModel, DatabaseSettings, DatabaseInstance, WorkspaceDatabaseConfig)
- `ViewModels/` — MVVM with CommunityToolkit.Mvvm (source generators)
- `Views/` — Avalonia AXAML + code-behind
- `Styles/` — design tokens (`AppTheme.axaml`) and global control styles (`Controls.axaml`, including GridSplitter). UI colors exclusively via `DynamicResource`, terminal ANSI colors separately in `TerminalTheme`
- `Services/` — JSON persistence (PersistenceService, SettingsService, WorkspaceService), shell detection (ShellDetector), AI tools detection (AiToolDetector), ThemeBridge, JsonDefaults, AppPaths, GitService/GitCommandRunner/GitDirectoryWatcher, DiffFormatter, FileHelper, TileFactory, TileTreeSerializer, UpdateService, CrashHandler, FileLogWriter, LogTraceListener
- `Services/Database/` — DatabaseServiceManager, DbHttpServer, DiscoveryService, DbRegistry, DbLogger, QueryHandler, SqlGuard, SqlGuardProfile, SqlServerProvider, PostgreSqlProvider, SubnetScanner, IDbProvider, ClaudeLocalMdWriter
- `Views/PtyWriter.cs` — static helper for writing to PTY via reflection (`TerminalView._ptyConnection.WriterStream`). Used by `TerminalKeyHandler` and startup script. `AttachStartupScript` hooks a ShellReady handler with `${tileId}` substitution
- `ViewModels/TileActivationScope.cs` — per-workspace tile activation scope with suppression mechanism

## Key libraries

- **Iciclecreek.Avalonia.Terminal** — terminal with built-in PTY (Porta.Pty).
  - `BeginReparent()`/`EndReparent()` prevents process killing when moving in the visual tree
  - `Process = string.Empty` blocks auto-launch of the default shell
  - Class handlers (`OnKeyDown`) ignore `e.Handled` — they cannot be blocked by regular handlers
- **AvaloniaEdit** — text editor. Requires `StyleInclude` in App.axaml. Text sync via `Document.Changed`.
- **Material.Icons.Avalonia** — Material Design icons. Requires `<MaterialIconStyles />` in `App.axaml` Styles. Usage: `<mi:MaterialIcon Kind="Close" />`.

## Split tiles architecture

Recursive binary tree: `LeafTileNodeViewModel` (terminal/editor) or `SplitTileNodeViewModel` (H/V + two children). `TileNodeView` manages views manually (not DataTemplate) and calls `SuspendTerminals()`/`ResumeTerminals()` around Rebuild to preserve live terminals.

`LeafTileNodeViewModel.IsActive` — `TileActivationScope` (per-workspace instance) guarantees that only one tile is active. `LeafTileView` reacts to `IsActive` — colored strip (`ActiveStrip`, 2px) at the top of the toolbar + brighter background (`BgElevated`).

`TileActivationScope.SuppressActivation()` — guard (IDisposable) blocking the GotFocus → Activate cascade during programmatic Focus() and Rebuild. Used in `LeafTileView.FocusContent()` and `TileNodeView.Rebuild()`.

## Tile ID

Each tile has a persistent `TileId` (`Guid.NewGuid().ToString()`, hyphenated format). Generated on creation, saved in `TileNode.TileId` in workspace JSON. Propagated to `TerminalTileViewModel.TileId`.

In startup script `${tileId}` is replaced with the current `TileId` — both on first launch and on restart.

## Terminal key handling

`TerminalKeyHandler` (separate class, SRP) handles:
- **Ctrl+V** — paste via `PasteAsync()`
- **Ctrl+C** — copy selected text (pre-captured in `PointerReleased`, because TerminalView clears selection on every keydown)
- **Alt+key** — sends `ESC+char` directly to PTY stream (fix for missing Alt handling in TerminalView)

PTY stream reflection extracted to `PtyWriter` (static helper, shared between key handler and startup script).

## Alt-buffer cleanup (TUI apps)

`AttachAltBufferCleanup` in `TerminalTileView` resets mouse tracking (reflection on `XTerm.Terminal._mouseTracker`) when `IsAlternateBuffer` changes from `true` to `false`. Without this, after exiting opencode/vim the terminal is flooded with SGR mouse sequences.

## ThemeBridge — UI synchronization with terminal theme

`ThemeBridge.Apply(TerminalTheme)` in `App.axaml.cs` dynamically derives UI colors (backgrounds, borders, text, accents) from the active terminal theme. Dark/Light mode is derived from `TerminalTheme.IsDark` — no separate theme selector. Called on startup and on every `SettingsChanged`. Thanks to `DynamicResource` the entire UI reacts immediately to theme changes.

## Shell Profiles

Users define shell profiles in Settings → Profiles tab. Each `UserShellProfile` has: `Id` (GUID), `Name`, `ShellName` (reference to detected shell), `StartupScript` (commands sent to PTY after startup), `FallbackScript` (executed when StartupScript fails), `RequiredAiToolBinaryName` (optional — binary name of the AI tool required to display the profile).

**Default profile seeding:** `SettingsService.SeedDefaultProfiles()` adds 4 profiles (Claude Code, OpenCode, Codex, Pi Agent) if no profile with that name exists (case-insensitive). Never overwrites existing profiles.

**Profile filtering:** A profile is visible on an empty tile only if:
- `RequiredAiToolBinaryName` is empty OR the AI tool is installed (`AiToolDetector.Detect`)

Filtering is implemented in `WorkspaceViewModel.GetAvailableProfiles()` with cache (30s TTL) on `AiToolDetector.Detect()` results.

**DirectLauncher** (`Views/DirectLauncher.cs`): when a profile has `FallbackScript` → `IsDirectLaunch = true`. Commands are run via `shell -c "command"` (not interactively). Chain: startup → fail (<5s) → fallback → fail → normal interactive shell. If the command survives >5s → success → auto-relaunch on exit (when lifetime >10s). Without `FallbackScript` → classic mode: shell starts interactively, startup script written via `PtyWriter`.

Terminal creation flow with profile:
1. Empty tile → click Terminal → if profiles exist, ProfileChooser appears (Back / Default / profile buttons)
2. Profile selection → `TileFactory.CreateContent(..., UserShellProfile)` → `ShellDetector.ResolveFromUserProfile()` → `TerminalTileViewModel` with shell + startup script
3. `IsDirectLaunch` → `DirectLauncher.LaunchWithFallback()`, else → `PtyWriter.AttachStartupScript()` + `LaunchProcess`

Profile persistence in layout: `TileNode.UserProfileId` → during deserialization `TileFactory.CreateTerminalFromDto` looks up the profile by Id in `AppSettings.ShellProfiles`. If the profile was deleted — graceful fallback to `ShellName`.

## Settings UI

Settings dialog as a modal overlay with responsive sizing (50% window width / 80% window height, min 420×400). Four tabs:
- **General** — Default Shell, Appearance (color theme, font), Terminal (font)
- **Profiles** — Shell profile CRUD (list + inline edit with accent border)
- **AI Tools** — auto-detection of CLI AI coding tools, version testing, custom tools
- **Database** — enable service, HTTP port, SQL Server/PostgreSQL credentials, scan interval, manual connections

`SettingsViewModel.SelectedTab` controls tab visibility (0=General, 1=Profiles, 2=AI Tools, 3=Database). Tab button styles: `settings-tab` / `settings-tab-active` in `Controls.axaml`.

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

**AI tool integration:** Uses `AiProcessRunner` with `IAiToolRunner` interface (OCP). `ClaudeToolRunner` launches `claude -p "prompt" --model <model> --output-format text --max-turns 20` via `ProcessStartInfo.ArgumentList` (no shell injection). New tools (OpenCode, Pi Agent) implement `IAiToolRunner`.

**Model configuration:** Per-tool default model stored in `AppSettings.GoalDefaultModels` (Dict<string, string>, key = binary name). UI has ComboBox selectors for tool and model. Model changes persist immediately.

**Persistence:** State saved to `.mterminal/goals/{guid}.json` — goal text, messages, phase, tool/model selection. `TileNode.GoalFilePath` for layout persistence.

**UI:** Chat-like scrollable log with message bubbles (user/assistant/system roles, different styles). Header bar with tool/model selectors and phase label. Input bar at bottom with Send and Reset. Stop button visible during AI execution.

**Services:** `AiProcessRunner` (static, `Services/AiProcessRunner.cs`) — process launcher with `ArgumentList`-based arg passing, concurrent stdout/stderr reading (deadlock-safe). `IAiToolRunner` / `ClaudeToolRunner` — per-tool process configuration and output parsing.

## Database tile

Per-workspace bridge that lets LLM agents (Claude Code, OpenCode, etc.) query local databases directly via HTTP — without manual connection setup. The tile auto-generates context files (`claude.local.md`, `AGENTS.md`, `GEMINI.md`) so agents discover available databases and how to call them.

**Purpose:** LLM agent running in a terminal tile sends `GET /query/{server}/{database}?sql=SELECT ...` to the local HTTP server → gets JSON results back. No credentials exposed to the agent; access is controlled by the user in the tile UI.

**Write protection (SQL Guard):** INSERT/UPDATE/DELETE blocked by default. User unlocks per-database with the RW toggle. DROP/TRUNCATE/ALTER always blocked regardless. If the agent sends a write query and write is disabled, a confirmation dialog appears — the user approves or denies in real time. Block comments (`/* */`) and line comments (`--`) are stripped before keyword scanning to prevent bypass attempts.

**Tile UI:** `Enabled` checkbox (controls context file generation), list of selected databases with RW/RO toggle, list of all discovered databases with add button. Tile reacts to `DatabaseServiceManager.StateChanged` and `SettingsChanged`.

**Architecture:** `DatabaseServiceManager` (singleton in App) manages `DbRegistry`, `DbLogger`, `DiscoveryService` and `DbHttpServer`. Tile registers its workspace with the manager (`RegisterWorkspace`/`UnregisterWorkspace`).

**Access control:** HTTP server exposes only databases selected in at least one workspace tile with `Enabled = true`. `IsDatabaseAllowed(key)` checks the union of grants across all workspaces. `GET /databases` returns only allowed databases. Host header validated to `localhost`/`127.0.0.1`/`::1` — blocks DNS rebinding attacks from browser tabs.

**Database discovery:** SQL Server via UDP broadcast on port 1434 (SQL Browser). PostgreSQL via port scanning (default 5432, 5433, 5434) on localhost and the local network. Manual connections also supported. Discovery runs periodically (default every 30 min).

**HTTP Server:** `DbHttpServer` on a configurable port (default 18090). Endpoints:
- `GET /databases` — list of allowed databases (filtered by grants)
- `GET/POST /query/{server}/{database}?sql=...` — SQL queries (allowed databases only)
- `GET/POST /query/{server}/{instance}/{database}` — with instance
- POST body limit: 512KB. Result limit: 50k rows / 16MB.

**Context file generation:** `ClaudeLocalMdWriter` writes the `# Database access` section to `claude.local.md` (Claude Code), `AGENTS.md` (OpenCode, Codex), and `GEMINI.md` (Gemini CLI). Existing content in these files is preserved — only the database section is replaced.

**Workspace config:** `.mterminal/databases.json` — `WorkspaceDatabaseTileConfig` with `Enabled` (bool) and `Databases` (list). On change, context files are generated (only when `Enabled = true`).

**Settings:** Database tab in Settings — enable service, HTTP port, SQL Server (Windows Auth / SQL Auth), PostgreSQL (credentials, ports), scan interval, manual connections (CRUD with inline edit form, test connection). Save & Apply restarts the service automatically. Passwords encrypted with DPAPI.

**Logs:** `DbLogger` — HTTP query and discovery logs in memory (max 500) + daily files in `%APPDATA%/MTerminal/db-logs/`.

**Services:** `Services/Database/` — IDbProvider, SqlServerProvider, PostgreSqlProvider, SqlGuard, SqlGuardProfile, QueryHandler, DbRegistry, DiscoveryService, DbHttpServer, DbLogger, SubnetScanner, DatabaseServiceManager, ClaudeLocalMdWriter.

## Restart shell

`RestartTerminal` in `LeafTileNodeViewModel` — kill + relaunch PTY. Available via the Restart icon in the tile header and Ctrl+Shift+R. Workaround for ConPTY hang after Ctrl+C in TUI apps (known opencode bug on Windows).

## Scrollbar Fluent theme fix

`AppTheme.axaml` overrides `VerticalSmallScrollThumbScaleTransform` / `HorizontalSmallScrollThumbScaleTransform` to `none`. Without this, the Fluent theme scales the thumb to 12.5% on machines with the default Windows "auto-hide scrollbars" setting.

## Crash handling and logging

`CrashHandler` catches exceptions from three sources: `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, `Dispatcher.UIThread.UnhandledException`. Initialized in `Program.Main()` before Avalonia starts.

`FileLogWriter` writes logs to `%APPDATA%/MTerminal/logs/mterminal-YYYY-MM-DD.log` with automatic cleanup of files older than 7 days. `LogTraceListener` redirects `Trace` to log files.

## Persistence

- `%APPDATA%/MTerminal/` (Windows) or `~/.config/MTerminal/` (Linux)
- `settings.json` — settings (fonts, terminal theme, default shell, shell profiles, custom AI tool paths/tools, window state)
- `workspaces.json` — list of workspaces (id, name, path)
- `workspaces/{id}.json` — tile layout per workspace (shell name, user profile id, tile id, tile name). Backward compat: `RootPane` → `RootTile` migration in `WorkspaceState`
- `logs/` — application logs (daily files, 7-day retention)
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
- **ConfirmAction pattern** — destructive actions (discard, remove workspace, undo commit) use `Func<string, Task<bool>>? ConfirmAction` in ViewModel, wired from View as `MessageBox.Avalonia` dialog (YesNo)
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

**`.mterminal/` filtering:** Setting `GitHideMTerminalDir` (default true) hides `.mterminal/` files in the Git tile changes list.

**DiffFontSize:** Diff panel uses 80% of font size (`FontSize * 0.8`).

## Workspace view caching

`MainWindow` caches `WorkspaceView` instances in `Dictionary<string, WorkspaceView>`. Switching workspaces via `IsVisible` toggle instead of DataTemplate — terminals are not killed/recreated. `WorkspaceRemoved` event clears the cache and removes the view from the visual tree.

## Workspace panel — branch names

`WorkspaceItemViewModel` — wrapper for `Workspace` with `ObservableProperty BranchName`. The workspace panel displays the branch name next to the path (SourceBranch icon + name). `DispatcherTimer` polls `GitService.GetBranchNameAsync` every 30s (static method, creates a temporary `GitCommandRunner`). Dispose in `MainWindowViewModel.OnClosing`.

## InputDialog

Reusable modal dialog (`Views/InputDialog.axaml`): title, TextBox with placeholder, optional suggestions list (ListBox). Enter = OK, Escape = Cancel. Clicking a suggestion enters it into the TextBox. `ShowDialog<string?>` returns trimmed text or null.
