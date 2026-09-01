# Local agent install — the plan

**Status:** plan. A second install route beside the existing one: the Settings AI row's
**Install…** runs the command in a terminal tile in the current workspace and answers "no workspace"
by printing the command — which is correct about what it can do, but leaves the user to run the
install by hand the one time they most want it done for them. This plan adds a button that installs
locally, in the application's own process.

The existing route stays exactly as it is: `Install…`, the confirmation that shows the command before
it runs, `MainWindowViewModel.RunInstallPlan` (`MainWindowViewModel.cs:129`), `InstallCommand.For`
(the shell's own sentence, never `InstallPlan.CommandLine`, whose quoting is for reading). Nothing
here touches it.

## 1. `Services/AgentLocalInstaller.cs`

New service, no UI — the same shape as `AiProcessRunner`.

- `Task<LocalInstallResult> RunAsync(InstallPlan plan, CancellationToken ct, IProgress<string> output)`
  where `LocalInstallResult` carries the exit code and the captured output.
- **Prerequisite checked before the button is ever shown**: `ExecutableFinder.Anywhere(plan.Executable)`
  must find the package manager (`npm`). A local install whose `npm` cannot be found ends in an
  error the user cannot fix from the dialog, so the button is simply not offered — `Install…` and the
  `InstallUrl` link remain the routes.
- **Full path, not a bare name** in `ProcessStartInfo`: `ExecutableFinder.Anywhere` already finds
  `npm.cmd` on Windows (`ExecutableFinder.cs:55`), and a windowed process does not inherit the `PATH`
  a login shell builds — the reason the finder exists. `UseShellExecute = false`.
- stdout and stderr captured and streamed through `output` while the process runs, kept for the
  failure dialog after it ends.
- **No elevation from here.** On Windows a global npm install writes to `%APPDATA%\npm` and needs no
  admin; on Linux `npm -g` usually does need sudo, and we do not run sudo — the non-zero exit and the
  captured output come back to the dialog as the failure, which is a sentence the user can act on
  rather than a UAC prompt nobody asked for.

## 2. `InstallAgentLocallyCommand` in `SettingsViewModel.Ai.cs`

- The **same confirmation** as `Install…` (`ConfirmedAsync`) — the command and its note in the
  question, because this writes outside every directory the application owns. An unwired
  `ConfirmAction` answers **no**, like every other confirmation on this dialog.
- Per-row state `IsInstalling`: while true, the button reads "Installing…" (spinner), the other
  install buttons on the row are disabled, and only one install runs at a time.
- **Cancellation**: the command is cancellable, killing the process tree. Closing the Settings dialog
  mid-install asks first through the existing `ConfirmAction`.
- Success → refresh the row. Failure → `ShowProblemAsync` with the captured output.

## 3. Cache invalidation after success

`AiAgentCatalog.Locate` holds its answer for thirty seconds (`AiAgentCatalog.cs:66`). Add
`InvalidateLocation(binaryName)`, called after a successful local install, then re-`Locate`.

- **Exit 0 with no binary found** (an npm prefix outside everything the finder scans) is reported as
  a message, not silence — the NOT INSTALLED chip stays, which is the truth, and the user is told
  where the install claims to have put things.
- `AiAgentInstanceViewModel.IsInstalled` becomes a refreshable answer (`RefreshInstallState()`)
  rather than a field read once at construction.

## 4. View (`SettingsView.axaml`, the agent row template, beside `Install…`)

A second button — **Install locally…** — beside `Install…`, visible when `CanBeInstalled` **and** the
package manager is present, tooltip carrying the command. `Install…` keeps its place and its meaning:
the route through a tile you can watch, needing an open workspace.

## 5. CLAUDE.md

After it works: one sentence in *Where AI tools went* on the two install routes — the tile
(visible, needs a workspace) and the local one (the application's own process, output shown on
failure) — beside the existing paragraph about `InstallUrl` and `InstallPlan`.

## Out of scope

- Installing the package manager itself (Node/npm). Without it the button does not appear; `Install…`
  and the link remain the routes.
- Agents without an `InstallPlan` (Antigravity, `GenericAgent`) — nothing changes for them.
