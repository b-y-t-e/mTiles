# Switching an agent tile to another instance of the same agent

Status: implemented. Kept for the reasoning the code does not carry — the routes considered and turned
down, and the one open question at the end.

## What this is

A tile running Claude Code on a Max subscription should be switchable, in place, to Claude Code on an
API key or on a second subscription — without closing the tile, without losing its place in the layout,
and without becoming a different program. The tile header already names the instance
(`AgentTileViewModel.HeaderNote` = instance name · model), so the switch is visible the moment it lands.

**The constraint that makes it safe: only instances with the same `AgentId`.** Same `IAiAgent` class,
same `SessionStrategy`, same resume flags, same `SupportedBehaviours`/`SupportedEfforts`, same shape of
environment. Switching to another *agent* is the failure `AgentSubstitution` exists to announce, and it
stays impossible here.

## Why most of it is already built

`AgentTileViewModel` never captures its instance: `Instance` is looked up in settings at every launch,
and everything downstream is derived from it per launch — `Runtime`, `LaunchEnvironment`
(`IAiAgent.EnvFor`), `ResolveCurrentScripts` (`IAiAgent.Interactive`), `ResolveModelAsync`,
`ModelContextWindow`. Editing an instance in Settings and restarting the tile already takes effect.
What is missing is only that `InstanceId` cannot change, and that the change is not written down.

Persistence needs nothing new either: `AgentTileKind.Save` writes `agentInstanceId` + `agentId` into
`workspaces/{id}.json`, `AgentTileKind.Resolve` finds the instance by id on load, and
`TileContext.RequestSave` (→ `WorkspaceViewModel.ScheduleSave`) is already handed to the tile and used
by the session capture. So a switch survives a restart of mTiles as soon as it calls that.

## The work

### 1. `AgentTileViewModel.InstanceId` becomes writable

Today `public string InstanceId { get; }`. It must be settable through one method — not a plain setter —
because three things have to happen together (see 2 and 3). Raise `OnPropertyChanged(nameof(HeaderNote))`
after it changes; the header reads the new instance's name and model live.

### 2. The substitution has to be cleared

`AgentTileKind.Save` writes `tile.Substitution?.RequestedInstanceId ?? tile.InstanceId`. A tile that
opened substituted (its instance was deleted in Settings) and is then deliberately pointed at a new
instance would still save the old *requested* id, and the user's choice would be gone on the next load.
So `Substitution` must become clearable, and the switch clears it: the user has just answered the
question the notice was asking. Clear `LaunchNotice` with it if it is still showing that sentence.

### 3. The conversation, by strategy

The rule is about the **account** (`SignInId`, and only that): a sign-in relocates the CLI's own state
directory, which is where the conversation lives. Changing only `ApiAccountId` (provider) or the model
leaves the directory alone.

| Strategy | Agents | On a sign-in change |
|---|---|---|
| `Fixed` | claude, pi | Nothing to do. The session id is the tile id; `--resume` finds nothing in the new directory, the chain falls through to `--session-id`, a fresh conversation starts. **Reversible** — switching back finds the old one again. Say it in the confirmation. |
| `ImportedFixed` | opencode | Nothing to do. `OpenCodeSession`'s import document is per tile and rewritten every launch, so the session is re-created in the new store. `OPENCODE_CONFIG` is per instance id and also rewritten every launch. |
| `CapturedAfterStart` | codex, agy | **The stored id must be dropped.** It is only meaningful inside its own `CODEX_HOME` / `~/.gemini`. Handed on, `codex resume <unknown>` stops on an interactive picker — a tile waiting for a keystroke nobody knows it wants — and `agy --conversation <unknown>` warns, silently starts a *different* conversation and exits 0. |

Concretely, for the captured case: cancel a capture in flight (`CancelCapture`), clear
`_capturedSessionId`, `CapturedSessions.ReleaseAllOf(_capturedForTileId)`. This is the same reset
`ReleaseSessionOfPreviousIdentity` performs, keyed on the **account** rather than on `TileId` — the two
are independent triggers and both have to exist.

### 4. What the switcher offers

`settings.AiAgentInstances.Where(i => i.AgentId == AgentId && AiAgentCatalog.IsAvailable(i, settings))`,
current one marked. `IsAvailable` is the same rule the tile chooser and the Goal tile's list filter on,
so a pairing `AgentModelResolver` would refuse cannot be picked here either. Fewer than two entries →
nothing to offer, hide the item.

Built when the menu opens rather than held as a live collection: instances are added, renamed and
deleted in Settings while the tile lives, and a subscription per agent tile to `SettingsChanged` buys
nothing a menu that is about to be drawn does not already get for free.

### 5. It is destructive, and a phone must not have it

Switching kills whatever the shell is running. So:

- Ask first, through the leaf's `ConfirmAction`, and an unwired one answers **no**.
- The sentence names the consequence when the account changes: on a captured agent, that the current
  conversation will not be resumed; on the others, that a new one starts and switching back restores it.
- **Not** exposed through `ITileActions`. That list goes to a paired phone, which cannot be shown what
  is about to die — the same reasoning that keeps Restart shell off it (`PhoneTileActions`,
  `TileAction.IsDestructive`). This is a header/menu command, not a tile action.

`DefaultBehaviour = bypass` on the target instance is not a new grant: it was confirmed when it was
stored in Settings.

### 6. Restart

Reuse the existing route: `InvokeActionAsync(TileActionIds.Restart)` → `TileLauncher.Launch`, whose
`BeginLaunch`/`IsCurrentLaunch` claim already prevents two chains fighting over one tile. Order: set the
instance and reset the session **first**, then restart — `PrepareForLaunchAsync` reads `Instance` and
resolves the model against the new account.

Then `_requestSave()`, so the layout carries the new id.

## Where it lands in the UI

The overflow `…` in the tile header, as a submenu — "Run as ▸" — with one item per instance and a check
on the current one. Once-a-session actions live in the overflow (the header's buttons are the fast path
and stand down as the tile narrows), and this is one.

**The one friction point.** `Views/LeafTileView.axaml`'s menu binds to `LeafTileNodeViewModel`, not to
the content, so the leaf needs a list and a command — and the leaf is deliberately kept from knowing
what its content is. There is one precedent, `LeafTileNodeViewModel.HasSession`, and its remark says so
out loud. Two ways:

- **Follow the precedent**: `Content as AgentTileViewModel` on the leaf, as `HasSession` does. Smallest
  change, one more line in the place that already admits to being the exception.
- **A new interface** (`ISwitchableAccountTile : ITile`) alongside the ones in `docs/TILES.md`, asked of
  the content by capability rather than by type. Cleaner against the stated rule, and it is a whole
  interface for one implementer — `docs/TILES.md` says an interface has to earn its place.

Recommendation: the precedent, with a remark pointing at `HasSession`; promote to an interface if a
second kind ever wants it.

Avalonia mechanics: give each entry its own small view model carrying label, `IsChecked` and an
`ICommand` (the pattern `GoalSolidToggle` and friends already use), rather than binding a
`CommandParameter` through `$parent[MenuItem]` inside an `ItemsSource` template.

## Obstacles, honestly

1. **The leaf/content layering above.** Real, small, and already breached once by `HasSession`.
2. **`Substitution` and `InstanceId` are init-only today** — both carry XML remarks explaining *why*
   they are what they are; those remarks have to be updated, not just the modifiers.
3. **The captured-session reset is the only genuinely dangerous part.** Getting it wrong is a codex tile
   hanging on a picker. It wants a test.
4. **Nothing here changes the Goal tile**, which chooses its own instance per run.
5. **A tile substituted *and* switched** is the one interaction worth thinking through twice: the switch
   is the user overruling the substitution, and after it the layout must carry the new choice and no
   session id.

## Tests

`tests/mTiles.Tests/AgentTileTests.cs` is where this belongs:

- Switching writes the new `agentInstanceId` through `AgentTileKind.Save`, and a reload resolves onto it.
- Switching on a substituted tile clears the substitution, so the new id is what is saved.
- Switching to an instance with a different `SignInId` on a `CapturedAfterStart` agent drops the stored
  session id and releases the `CapturedSessions` claim; the saved state carries no `sessionId`.
- Switching to an instance with the same `SignInId` but a different provider keeps it.
- The offered list is filtered to the same `AgentId` and to `AiAgentCatalog.IsAvailable`.
- An unwired `ConfirmAction` does not switch.

## Open: could the conversation travel with the switch?

Nothing here moves a conversation between accounts, and the reset is the honest answer rather than the
only possible one. The conversation is not owned by the account — it is a **file in the directory the
sign-in relocates** — so in principle it can follow. Three routes, none taken yet:

1. **Ask before dropping.** For codex, look for `rollout-*-<id>.jsonl` under the new `CODEX_HOME` before
   forgetting the id — `SessionCapture.NewestSessionId` already walks that tree. Turns "the conversation
   is always lost when the account changes" into "lost only when it really is not there", and touches
   nobody else's files. The cheapest of the three and the one to do first.
2. **opencode has a supported route.** `opencode import` is documented, non-destructive on an existing
   id, and this application already writes such documents (`OpenCodeSession`). Exporting from one store
   and importing into the other is the one case where a CLI itself offers the move.
3. **Copying a transcript between config directories** (claude, codex). Probably works — both keep the
   conversation locally as JSONL and resend the history each turn — but **unmeasured**, in a format that
   is somebody else's and has moved before, and it would have this application writing into a CLI's
   private state rather than only setting its environment.

Two consequences worth stating whichever route is taken: resuming replays the whole history under the
new key, so a long conversation is billed to the new account in one turn; and a transcript carries the
model it was held on, which the new account may not serve.
