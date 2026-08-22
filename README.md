![Windows](https://img.shields.io/badge/Windows-0078D4?style=flat&logo=windows&logoColor=white)
![Linux](https://img.shields.io/badge/Linux-FCC624?style=flat&logo=linux&logoColor=black)

# mTiles

Terminal manager built for AI-assisted development. Workspaces, split tiles, database bridge for LLM agents, git — in one window.

![mTiles](assets/screen1.png)

## What makes it different

Unlike Warp, Wave, or WezTerm — mTiles doesn't try to be an AI itself. It manages the environment your AI tools run in.

**Database bridge for LLM agents** — lets Claude Code, OpenCode, or any agent query SQL Server / PostgreSQL directly via a local HTTP server. No credentials are ever exposed to the agent.

**SQL Guard** — write protection enabled by default. INSERT/UPDATE/DELETE require explicit per-database unlock. DROP/TRUNCATE/ALTER always blocked. If the agent attempts a write in read-only mode, a real-time confirmation dialog appears. Keyword scanning strips comments to prevent bypass.

**AI tool profiles** — named shell profiles tied to specific AI binaries (Claude Code, OpenCode, Codex, Pi Agent). Auto-detection of 18+ CLI tools, startup/fallback scripts, and a launch chain that falls back to the next command when one fails and brings the tool back when it exits. Profiles appear only when the tool is installed.

**Conversations that survive a restart** — a tile's id *is* the agent's session id, so reopening mTiles drops you back into the same conversation. Claude Code and pi take an id directly; **OpenCode** cannot be told one, so mTiles writes the session document `opencode import` creates a session from, and the profile's fallback imports it and resumes. Delete the conversation behind a tile and it is recreated on the next launch — the tile keeps its identity either way.

**Dictation, including from your phone** — speak into a tile instead of typing; recognition runs entirely on this machine, with no account and nothing sent anywhere. A QR button beside Settings opens a panel: scan the code and your phone becomes the microphone —
hold a button on it and speak, and the text lands in the active tile just as the keyboard shortcut would.
This is what makes dictation usable over Remote Desktop, where the microphone is next to *you* and mTiles is on the far machine. mTiles works out which of its own addresses your phone can actually reach — it has several — and remembers which one worked, separately for local and remote sessions.

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

## Tech

.NET 10, Avalonia 12, CommunityToolkit.Mvvm, AvaloniaEdit, and **Terminal.Avalonia** — our own terminal control (VT engine, ConPTY/forkpty, rendering), written for this app and published on NuGet.

## Roadmap

Not promises with dates — the things known to be missing or wrong, roughly in the order they bother us.

**Known limitations**

- **Restarting a shell can stall the UI** for as long as the child takes to die (up to ~2 s). The terminal control kills the old session on the UI thread; fixing it belongs in `Terminal.Avalonia`, not here.
- **Selection inside a full-screen TUI needs Shift held.** mc, vim and opencode grab the mouse, and the terminal control deliberately refuses a one-way override that would leave those apps unable to receive clicks. It is the xterm convention, but a habit to learn.
- **Launch chains never run in `cmd`.** A profile whose shell is `cmd.exe` has its *commands* run by PowerShell — or Git Bash / a POSIX shell — instead, and the swap goes to the log. `cmd` does not parse its command line by the rules the PTY backend quotes with, runs only the first line of a multi-line command, and does not treat `;` as a separator, so a profile built from more than one command cannot work there. Only the commands move: the shell you end up typing into is still the one you chose.
- **Text wins over images on paste.** When the clipboard holds both (a browser copy, a screenshot tool), Ctrl+V pastes the text and the agent never sees the image. Alt+V still hands it over.
- **Codex does not resume its conversation after a restart.** A tile keeps its `TileId` for ever and the default profiles hand it to the tool as the session id, so closing mTiles and opening it again drops you back into the same conversation — for **Claude Code** (`--session-id` to create, `--resume` to reattach), **pi** (`--session-id` for both) and now **OpenCode** (see below). **Codex** generates its own id and never tells the shell what it was, so its seeded profile starts a fresh conversation. The tile comes back; what was said in it does not.
- **OpenCode's session file is an undocumented format.** Resume works by handing `opencode import` a small JSON document mTiles writes — opencode's own *export* format, not an API, measured against **1.18.14**. If a future opencode changes it, the import fails, the resume after it finds no session, and the tile falls through to a plain shell: history lost, tile intact. `OpenCodeSessionTests` is what turns that into a failing build rather than a surprise.

- **Dictating from a phone on a LAN means accepting a certificate warning once.** A browser hands out no microphone outside a secure context, and no public authority will ever certify `192.168.1.20`, so the bridge serves a certificate it signed itself and the phone objects the first time. Accept it and it is remembered — but only for the addresses that certificate names: joining a network this machine has not been on before forces a new certificate, and the phone asks again. mTiles carries the previous names forward, so the set converges and a network you have used before does not ask twice. The exception is **Tailscale**, whose MagicDNS name gets a real certificate — which is why it is the recommended path for remote work, and the only one that reaches a phone that is not on this machine's own network at all.
- **The phone bridge does not always get the port you configured.** On Windows the kernel reserves blocks of ports for Hyper-V, WSL and Docker at boot, and a port inside one can never be bound however free it looks — `netstat` blames PID 4, the kernel. The default 18091 landed inside such a block on the first machine it ran on. The bridge falls back to a free port and the panel says which; nothing you type depends on the number, because the QR code carries it.
- **The promise that audio never leaves the machine now has an exception you opt into.** Dictating from a phone means the audio crosses from your phone to your computer — encrypted, and to a device you own. Recognition still runs on the machine mTiles is on; nothing reaches a third party. Dictating from the local microphone is unchanged.

**Planned**

- **Collapsible workspace panel** — collapse to a narrow icon strip with workspace initials rather than hiding outright: click to switch, drag the splitter past its minimum to collapse. Reclaims the panel's width without losing quick switching.
- **Activity in the workspace panel** — the list on the left says nothing about what is happening inside a workspace you are not looking at, so a build finishing or an agent waiting for an answer goes unnoticed until you switch to it. The panel should show, per workspace, that its terminals are working: which ones are running something and which are sitting at an idle prompt. Open questions: what counts as "working" when the signal available is a live PTY rather than a process tree, and how it is shown — a dot, a count of busy tiles, or something that also distinguishes "needs you" from "busy".
- **Goal tile — refactor and rework.** The feature works but has not been revisited since it was built, and three things are outstanding. **Attachments:** a goal can only be described in words today, so a screenshot of the bug or the mockup being aimed at cannot be handed to the tool. **A pass over what it actually does:** the phase machine, the prompts and the five-iteration implement/review loop deserve a review against how the tile is really used, rather than more features bolted on. **Its UI:** the type is too small and inconsistent with the rest of the app — the view leans on the smallest token (`FontXs`) for most of the chat log and mixes it with `FontSm` without a rule.
- **Session resume for Codex.** OpenCode now resumes (see *AI tool profiles* above); Codex is the same problem with a different CLI (`codex resume <id>`) and no known way to name a session up front. Whatever comes out of it should generalise: a small per-tool description of how to create a session, how to resume one, and how the id is chosen or learned — not one branch per tool spread through the launch code. `OpenCodeSession` is deliberately the shape of one such description rather than a framework for three; the second tool is what should turn it into an abstraction, not the first.
- **Favourite tiles.** Nothing marks the two or three tiles you actually live in, so finding them in a workspace that has grown means reading every header. Tiles should be markable as favourites and reachable directly — a short list, keyboard-first, ordered by the user rather than by the tree. Open questions: whether a favourite is scoped to its workspace or global, and what happens to one whose tile is closed — dropped silently, or kept as a way to reopen it.
- **Review tile.** Code review belongs beside the code, not in a browser: a tile that shows a diff — the working tree, a branch against another, or a pull request — and lets comments be written against lines and handed to an AI tool or pushed back to the forge. The Git tile already renders diffs and knows the repository, so the question is whether this is a second tile or a mode of that one, and how much of a pull request it can honestly show without becoming a GitHub client.


## License

MIT
