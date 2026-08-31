![Windows](https://img.shields.io/badge/Windows-0078D4?style=flat&logo=windows&logoColor=white)
![Linux](https://img.shields.io/badge/Linux-FCC624?style=flat&logo=linux&logoColor=black)

# mTiles

Terminal manager built for AI-assisted development. Workspaces, split tiles, database bridge for LLM agents, git — in one window.

![mTiles](assets/screen1.png)

## What makes it different

Unlike Warp, Wave, or WezTerm — mTiles doesn't try to be an AI itself. It manages the environment your AI tools run in.

**Database bridge for LLM agents** — lets Claude Code, OpenCode, or any agent query SQL Server / PostgreSQL directly via a local HTTP server. No credentials are ever exposed to the agent.

**SQL Guard** — write protection enabled by default. INSERT/UPDATE/DELETE require explicit per-database unlock. DROP/TRUNCATE/ALTER always blocked. If the agent attempts a write in read-only mode, a real-time confirmation dialog appears. Keyword scanning strips comments to prevent bypass.

**Agent tiles** — a tile whose commands are an AI CLI's own rather than a script you had to write: Claude Code, OpenCode, Codex, pi and agy, each a class that knows how to resume its own conversation, what it may do without asking and how hard it may think. Pick a configured *instance* — "Claude Code", or "Claude Code on GLM 5.3 via OpenRouter" — and the launch chain falls back to the next command when one fails and brings the agent back when it exits. Only agents this machine actually has are offered. The named shell profiles and the AI Tools table they were configured through are gone; the terminal tiles you had that ran an AI CLI become agent tiles on the first launch, and a copy of every layout is kept as `{id}.pre-agents.json` first.

**Conversations that survive a restart** — a tile's id *is* the agent's session id, so reopening mTiles drops you back into the same conversation. Claude Code and pi take an id directly; **OpenCode** cannot be told one, so mTiles writes the session document `opencode import` creates a session from and the fallback command imports it and resumes; **Codex** and **agy** name their own session and mTiles reads it back — codex from the rollout file it leaves behind, agy by asking. Delete the conversation behind a tile and it is recreated on the next launch — the tile keeps its identity either way.

**Dictation, including from your phone** — speak into a tile instead of typing; recognition runs entirely on this machine, with no account and nothing sent anywhere. A QR button beside Settings opens a panel: scan the code and your phone becomes the microphone —
hold a button on it and speak, and the text lands in the active tile just as the keyboard shortcut would.
This is what makes dictation usable over Remote Desktop, where the microphone is next to *you* and mTiles is on the far machine. mTiles works out which of its own addresses your phone can actually reach — it has several — and remembers which one worked, separately for local and remote sessions.
The panel also reads the firewall rather than guessing at it: on Windows it says which of the four things is in the way — no rule, a block rule Windows wrote when its own prompt was dismissed, a network it treats as Public, or a policy that ignores local rules — and offers to fix the ones that can be fixed; on Linux it names the firewall that is actually running and gives the one command for it.

**ThemeBridge** — terminal ANSI palette drives the entire UI. Change the color theme and every surface updates — not just the terminal background. 17 themes (Catppuccin, Tokyo Night, Gruvbox, Rosé Pine, One Dark, Solarized, and more), dark and light.

**Git tile** — staging, diff (unified + side-by-side), commit with message suggestions, stash, push/fetch, tags, undo last commit, unpushed commit markers. No need for a separate Git GUI.

**Workspaces** — each workspace is a directory with its own tile layout, database config, and git branch display. Switch instantly — terminals stay alive (cached views, no respawn).

**Split tiles** — recursive binary tree. Split any tile horizontally or vertically, nest arbitrarily. Each tile can be a terminal, note, todo, git, or database.

## Running

```
git clone https://github.com/b-y-t-e/mTiles.git
cd mTiles
dotnet run --project src/mTiles
```

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

### Linux (AppImage)

Releases ship a self-contained `mTiles-linux-x86_64.AppImage`. Two things it cannot bring with it:

**FUSE 2.** AppImages mount themselves through `libfuse.so.2`, and Arch-based systems — CachyOS, Omarchy
— ship only FUSE 3 by default. Without it the application does not start at all, and the error mentions
`libfuse.so.2` rather than mTiles. Either install it (`sudo pacman -S fuse2`) or skip the mount
entirely:

```
./mTiles-linux-x86_64.AppImage --appimage-extract-and-run
```

**A hole in the firewall, if you dictate from a phone.** mTiles opens one on Windows, with your consent;
on Linux it only tells you which command to run, because there is no desktop-wide elevation prompt worth
invoking and a GUI that shells out to `sudo` teaches the wrong habit. The phone panel names the firewall
it finds running (`ufw` or `firewalld`) and the command for that one. Distributions differ: Ubuntu leaves
`ufw` inactive, Fedora and CachyOS enable `firewalld`, Omarchy configures `ufw` to deny inbound, and
SteamOS runs neither.

Also worth knowing: Avalonia is an X11 application, so on Wayland desktops (Hyprland, KDE) it runs
through **XWayland**; and dictation needs ALSA (`libasound.so.2`, provided by `alsa-lib`/`pipewire-alsa`)
for the microphone — the phone bridge does not, since that audio arrives over the network.

## Tech

.NET 10, Avalonia 12, CommunityToolkit.Mvvm, AvaloniaEdit, and **Terminal.Avalonia** — our own terminal control (VT engine, ConPTY/forkpty, rendering), written for this app and published on NuGet.

## Roadmap

Not promises with dates — the things known to be missing or wrong, roughly in the order they bother us.

**Known limitations**

- **Restarting a shell can stall the UI** for as long as the child takes to die (up to ~2 s). The terminal control kills the old session on the UI thread; fixing it belongs in `Terminal.Avalonia`, not here.
- **Selection inside a full-screen TUI needs Shift held.** mc, vim and opencode grab the mouse, and the terminal control deliberately refuses a one-way override that would leave those apps unable to receive clicks. It is the xterm convention, but a habit to learn.
- **`cmd` is not offered as a shell.** It cannot run what an agent tile's launch chain is made of: it does not parse its command line by the rules the PTY backend quotes with, runs only the first line of a multi-line command, and does not treat `;` as a separator, so a chain of more than one command cannot work there. It used to be offered and then silently swapped for PowerShell behind your back, which meant a shell that was neither the one you picked nor the one running your commands. The shells are PowerShell, Git Bash, bash, zsh and fish; a settings file naming `CMD` falls back to the default.
- **Shell profiles and the AI Tools table are gone.** A profile was a name, a shell, a startup script, a fallback and the binary that had to be installed for it to appear — everything an AI CLI needed, written out by hand and kept working by hand. Those are now the agent's own business, so Settings has neither tab. Your existing tiles are migrated: a terminal running one of the four seeded AI profiles becomes an agent tile on the first launch of that workspace, and a copy of the layout as it was is kept beside it as `{id}.pre-agents.json`. A profile you wrote yourself is **not** migrated — it names no agent this application knows — and its tile comes back as a plain shell on the shell it was running, without the script. If that is a loss, the file above is the copy of what it was.
- **A shell nominated by path is gone.** The old Settings → General "custom shell" (path + arguments) has been removed: a shell is now one of the known kinds, because mTiles has to know how to quote for it, how to run a single command in it and how to unset a variable in it — none of which it can work out from a path to an arbitrary binary. If you had pointed it at nushell, a bash outside the usual places or a WSL wrapper, your terminals now start in the default shell instead, and the log (`%APPDATA%/mTiles/logs/`) says once what was dropped. The setting is not coming back in that form; a shell arriving as a class is. **The same applies to a default shell picked from a list**: on Linux and macOS that list used to hold whatever `$SHELL` pointed at, of any kind, so if yours was `nu`, `ksh` or `dash` it is no longer known either — your terminals start in the default shell and the log says once what was named. On Windows the same line covers `CMD`.
- **Text wins over images on paste.** When the clipboard holds both (a browser copy, a screenshot tool), Ctrl+V pastes the text and the agent never sees the image. Alt+V still hands it over.
- **Codex's session is worked out rather than told, and it can be wrong.** codex names its own conversation and never says what it chose, so mTiles finds the rollout file it left behind: the newest one started since this tile did, recording this tile's working directory, and not already held by another open tile. Two codex tiles started in the same second in the same workspace can still, in principle, take each other's — in which case one of them resumes the wrong conversation on the next launch. agy is asked outright and has no such ambiguity.
- **OpenCode's session file is an undocumented format.** Resume works by handing `opencode import` a small JSON document mTiles writes — opencode's own *export* format, not an API, measured against **1.18.14**. If a future opencode changes it, the import fails, the resume after it finds no session, and the tile falls through to a plain shell: history lost, tile intact. `OpenCodeSessionTests` is what turns that into a failing build rather than a surprise.

- **Dictating from a phone on a LAN means accepting a certificate warning once.** A browser hands out no microphone outside a secure context, and no public authority will ever certify `192.168.1.20`, so the bridge serves a certificate it signed itself and the phone objects the first time. Accept it and it is remembered — but only for the addresses that certificate names: joining a network this machine has not been on before forces a new certificate, and the phone asks again. mTiles carries the previous names forward, so the set converges and a network you have used before does not ask twice. The exception is **Tailscale**, whose MagicDNS name gets a real certificate — which is why it is the recommended path for remote work, and the only one that reaches a phone that is not on this machine's own network at all.
- **The phone bridge does not always get the port you configured.** On Windows the kernel reserves blocks of ports for Hyper-V, WSL and Docker at boot, and a port inside one can never be bound however free it looks — `netstat` blames PID 4, the kernel. The default 18091 landed inside such a block on the first machine it ran on. The bridge falls back to a free port and the panel says which; nothing you type depends on the number, because the QR code carries it.
- **The promise that audio never leaves the machine now has an exception you opt into.** Dictating from a phone means the audio crosses from your phone to your computer — encrypted, and to a device you own. Recognition still runs on the machine mTiles is on; nothing reaches a third party. Dictating from the local microphone is unchanged.

**Planned**

Two of these are worked out in detail in [`docs/ROADMAP.md`](docs/ROADMAP.md) — what is wrong now, what
the stopgap is, and what would settle it — so that picking one up does not begin by rediscovering why it
is there.

- **A model field that shows it has a list.** The agent instance form's Model and Fast model fields
  complete against the provider's own catalogue — hundreds of entries, matched by every typed word in
  any order — but `AutoCompleteBox` has no arrow and nothing to click, so a field with a full list
  behind it looks like an empty text box. `ComboBox IsEditable` has the arrow and cannot filter. What is
  wanted is one control that does both; the options, in cost order, are in the roadmap document.
- **A tile header that is an index, a kind and a description.** The tile's name is doing two jobs at
  once: the label somebody typed and the generated `Agent#1` nobody chose. The end state is three
  separate things — a position label assigned in reading order, the kind (already a glyph), and a
  description the user writes — with `Ctrl`+the label focusing that tile, on a shortcut that has to be
  configurable because `Ctrl+1` belongs to whatever is running inside the tile.
- **Collapsible workspace panel** — collapse to a narrow icon strip with workspace initials rather than hiding outright: click to switch, drag the splitter past its minimum to collapse. Reclaims the panel's width without losing quick switching.
- **Activity in the workspace panel** — the list on the left says nothing about what is happening inside a workspace you are not looking at, so a build finishing or an agent waiting for an answer goes unnoticed until you switch to it. The panel should show, per workspace, that its terminals are working: which ones are running something and which are sitting at an idle prompt. Open questions: what counts as "working" when the signal available is a live PTY rather than a process tree, and how it is shown — a dot, a count of busy tiles, or something that also distinguishes "needs you" from "busy".
- **Goal tile — the rest of the line.** Attachments are now done: a screenshot pasted into the composer with Ctrl+V (or Alt+V when the clipboard also holds text) is written beside the goal and handed to every prompt of the run as a path, with an `[Image #N]` marker left where the caret was. Everything else on this line is done too: the tile is drawn as a terminal transcript with phase colours from the ANSI palette; the review returns structured findings at four severities — blocker, error, warning, suggestion — rather than the substring `VERDICT: PASS`; when a goal is finished is set on the tile itself — tolerated errors and warnings (blockers never are), attempts, which of the SOLID principles apply, and whether the work has to leave the project building and its tests passing; the clarification round can be skipped when the goal is already clear and repeated while it is not; a goal can be worked out from the uncommitted changes instead of typed; a run that used up its attempts can be given more without losing the conversation; and each attempt starts a fresh tool process but is handed what the earlier ones changed and decided against.
- **A warning while you type, not after you send.** The Goal tile's composer takes two things that name something outside the text — `@` file mentions and `[Image #N]` markers — and both fail silently when they are left half-finished. Backspacing into `[Image #4]` leaves `[Image #4`, which stops being a marker: the picture is correctly dropped from the run, but nothing on screen says so, and the goal is sent describing an image the tool was never given. An `@` mention is looser still, because mTiles attaches nothing for it — the path is plain text in the prompt — so `@tests/mTiles.Tests/AssemblyIn` costs the run a tool call that finds nothing. The composer should say so before Send: a quiet line under the box naming what will not resolve, on a debounce so it does not flicker per keystroke, checking three things — that every `[Image #N]` is whole and has an image behind it, that every `@` path exists on disk, and that the brackets and quotes a mention needs are balanced. Open questions: whether it is a warning or a refusal (it must not be a refusal — a path can legitimately name a file the tool is about to create); whether the file check is worth a stat per mention on every pause in typing, or should reuse the mention source's own cached listing; and how it reads for a goal that deliberately mentions something that does not exist yet.
- **Favourite tiles.** Nothing marks the two or three tiles you actually live in, so finding them in a workspace that has grown means reading every header. Tiles should be markable as favourites and reachable directly — a short list, keyboard-first, ordered by the user rather than by the tree. Open questions: whether a favourite is scoped to its workspace or global, and what happens to one whose tile is closed — dropped silently, or kept as a way to reopen it.
- **Review tile.** Code review belongs beside the code, not in a browser: a tile that shows a diff — the working tree, a branch against another, or a pull request — and lets comments be written against lines and handed to an AI tool or pushed back to the forge. The Git tile already renders diffs and knows the repository, so the question is whether this is a second tile or a mode of that one, and how much of a pull request it can honestly show without becoming a GitHub client.
- **A first run with no agent installed should offer to install one.** The agent tile's chooser lists the agents that are actually on this machine, and on a fresh machine that list is empty — which is the correct answer and a useless screen. A short wizard at startup should name the agents mTiles knows, say which are installed, and offer to install one, running the install in a visible terminal tile rather than silently. Open questions: whether it appears once or whenever nothing is installed; how much it should promise on Linux and macOS, where each agent's installer differs; and whether an installation that needs elevation is offered at all or only described.


## License

MIT
