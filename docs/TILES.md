# Tiles — one interface, a few capabilities, one kind registry

**Status: this is the code.** It was written as a plan before anything moved, and the reasoning below is
kept because it is why the shape is what it is rather than what it used to be.

The architecture is one a programmer new to C# can hold in their head: **one base interface that every
tile implements, a handful of interfaces extending it that announce optional abilities, and one class per
kind of tile.** Adding a seventh kind is one new class and one line of registration in
`App.BuildTileCatalog`.

## What was wrong before

There was no tile interface. The only thing the six kinds shared was `ObservableObject`. Two capability
interfaces already existed and already worked exactly as described below — `IBusyTile` (Terminal, Goal)
and `IFileContent` (the markdown tiles) — so this finished a pattern the codebase had started rather
than importing a new one.

The kind of a tile was a value in a closed enum rather than an object, and that value was switched on in
about thirteen places:

| Where | What it did |
|---|---|
| `Models/TileContentType.cs` | the enum |
| `TileFactory.CreateContent` (two overloads) | switch |
| `TileFactory.CreateFromDto` | second switch |
| `TileFactory.SerializeSettings` / `RestoreSettings` | `is GitTileViewModel` |
| `TileFactory.AllocateTileName` | switch, plus a `ref int` counter per kind |
| `Models/TileNode.cs` | `NoteFilePath`, `TodoFilePath`, `GoalFilePath` — a field per kind |
| `TileTreeSerializer.Serialize` | five `as XTileViewModel` casts |
| `WorkspaceViewModel.InitCountersFromDto` | `else if` chain over five counter fields |
| `Views/TileTypeIcon.cs` | `Kind()` and `AccentKey()` |
| `LeafTileView.axaml.cs` → `SetContent` | switch from view model type to view |
| `LeafTileView.axaml.cs` → `ContentInset` | `is TerminalTileViewModel` |
| `LeafTileView.axaml.cs` → `ApplyHeaderWidth`, `OnTileKeyDown` | `== TileContentType.Terminal` |
| `LeafTileView.axaml` | six chooser cards written out by hand |
| `Services/DefaultWorkspace.cs` | `ContentType = Terminal` |

`TileFactory` and `TileTypeIcon` are gone. `TileContentType` survives, **closed**, for one reason: it is
the exhaustive record of what is on people's disks, so it is what `TileNode`'s compatibility property
reads and what the "every historical layout still opens" test is written against.

Two empty classes existed only to feed that machinery: `NoteTileViewModel` and `TodoTileViewModel` were
bodyless subclasses of `MarkdownTileViewModel`, distinguishable by nothing but their CLR type — which is
what the view's type switch needed in order to pick between `NoteTileView` and `TodoTileView`. They are
now real classes over an abstract `MarkdownTileViewModel`, each saying what it is (`KindId`) and where
its files go, which is what the two have always differed by. The `.md` files and their folders are
unchanged.

## The whole architecture

```csharp
// The base. Everything that can be the content of a tile.
public interface ITile : INotifyPropertyChanged, IDisposable
{
    string KindId { get; }              // "terminal", "git", "note"…
}

// The capabilities. A tile says what it can do by what it implements.
public interface IBusyTile : ITile
{
    bool IsBusy { get; }                // the turning light on the workspace row
}

public interface IFileContent : ITile
{
    void RenameFile(string newName);    // the tile owns a file; the file follows the tile's name
}

public interface ITileActions : ITile
{
    IReadOnlyList<TileAction> Actions { get; }        // header buttons, and the phone
    Task<TileActionResult> InvokeAsync(string id);
}

public interface ITextInputTile : ITile
{
    bool TrySendText(string text, bool submit);       // where a transcript lands
    bool TryPressKey(TileKey key);                    // Enter and the arrows from a phone
}

public interface ICustomBackgroundTile : ITile
{
    Thickness ContentInset { get; }     // how far the content sits inside the card
    string ContentBackground { get; }   // hex — what that inset is painted in
}

public interface IProcessTile : ITile
{
    int? ChildProcessId { get; }        // the process it started; null between sessions
}
```

### `IDescribedTile` — what the tile is *running*

```csharp
public interface IDescribedTile : ITile
{
    string HeaderNote { get; }
}
```

**Beside the name, never instead of it.** The name is the user's — typed, or generated as `Agent#1` —
and it is what they navigate by. This answers a different question the header could not answer at all:
two tiles both called `Agent#N` may be Claude Code on a subscription and Codex on OpenRouter, and
nothing on screen told them apart. `AgentTileViewModel` answers with its instance and the model the
launch settled on (`Claude Code · glm-5.3-flash`); no other kind implements it yet, and a kind with
nothing to add simply does not.

Drawn as metadata in the panel's own sense — plain, small, muted — and it is the **first** thing to give
way when the header runs out of room (`LeafTileView.HeaderNoteNeedsWidth`, wider than either button
threshold): a button that stands down is still in the overflow menu, while this has nowhere else to be
shown but is also the one thing nobody is trying to click. The full note is always the tooltip.

Changes arrive through `ITile`'s own change notification, so a tile relaunched on a different instance
redraws its header without the view knowing why.

```csharp
// One class per kind, registered once in App.axaml.cs.
public interface ITileKind
{
    string Id          { get; }         // "git" — this is what goes into the JSON
    string DisplayName { get; }         // "Git"
    string IconId      { get; }         // "source-branch"
    string AccentKey   { get; }         // "TileAccentGit"
    string NamePrefix  { get; }         // "Git", as in Git#1

    string NameFor(IReadOnlySet<string> used);            // what to call the next one
    IReadOnlyList<TileSetupOption> SetupOptions(TileContext ctx);  // what to ask first, if anything

    ITile Create(TileContext ctx, JsonObject? state);
    JsonObject? Save(ITile tile);
}
```

Who implements what:

| Kind | `IBusyTile` | `IFileContent` | `ITileActions` | `ITextInputTile` | `ICustomBackgroundTile` | `IProcessTile` | `IDescribedTile` |
|---|---|---|---|---|---|---|---|
| Terminal | ✔ | | ✔ Restart shell (header only) | ✔ | ✔ | ✔ | ✔ Agent tiles only |
| Note | | ✔ | | | | | |
| Todo | | ✔ | | | | | |
| Git | | | ✔ Refresh, Commit, Push | | | | |
| Database | | | | | | | |
| Goal | ✔ | | ✔ Continue, Pause, Commit work | | | ✔ | |

`IProcessTile` is the root of a tree and not a process: a terminal knows the shell it spawned and nothing
about the agent that shell went on to start, which is where the memory actually is. A Goal tile
answers with the AI tool it has running, which is usually the heaviest thing in the workspace. Finding the rest is
`ProcessTreeMemory`'s job — the tile answers with one number and stays ignorant of operating systems. It
is `null` between sessions rather than stale, because a process id the system has reclaimed is a number
that now belongs to somebody else.

`TileContext` carries what every kind needs in order to build a tile: the working directory, the
`SettingsService`, the tile's own identity, and the callbacks a tile reports through (`RequestSave`,
`OpenSettings`). It is the right home for `TileSettingsChanged` and `OpenDatabaseSettings`, which were
wired by hand and are dependencies rather than capabilities — the second of them by the database tile's
*view*, which walked up the visual tree looking for a window whose data context was the main view model.

**`TileContext.Shells` is detected at most once every 30 seconds per context, which is once per
workspace.** Only the terminal kind reads it, and it reads it twice — to resolve the profile a tile was
created from, and to find again the shell a profile-less tile was last running. `ShellDetector.Detect()`
walks every directory on `PATH` and stats a handful of fixed locations, on the UI thread, while a
workspace is being restored, so a kind calling it per tile turns a workspace holding eight saved
terminals into eight scans. Asked for lazily, so a workspace with no terminal in it never pays; and held
in a field on the record rather than passed in as a value, so the `with` a terminal makes when it binds
its own `TileId` carries the same cache.

**A window, not the life of the workspace**, and that is the correction: the same list also answers for
a terminal the user adds by hand, and a workspace stays open for days — cached outright, a shell
installed this afternoon was missing from the chooser until the application was restarted, which is not
a connection anybody would make. It is `WorkspaceViewModel.GetAvailableProfiles`' own TTL, because the
two answer the same question about the same machine. The cache is a small class rather than two fields
on the record for a reason `with` makes: fields are copied by value, so two of them would be copied at
the moment a tile made its own context and would then expire independently — a cache per tile wearing
the name of a cache per workspace. A reference is copied as a reference.

**`TileContext.TileId` is a `Func<string>`, not a string.** The id belongs to the
`LeafTileNodeViewModel` that holds the content and moves under it: "New session" replaces the id of a
tile whose terminal keeps running. While it was a settable property on `TerminalTileViewModel`, four
places had to cast the content back to a terminal and push the new value in — the serializer, both
creation paths and the drag-and-drop swap — and any one of them forgotten meant a tile launching under
somebody else's session id. All four are gone.

**The function is bound to the tile it was built for, so content never moves between two tiles.**
Dropping one tile onto the middle of another exchanges the two leaves' places in the tree
(`TileDragDrop.SwapPlaces`) instead of trading their `Content`, kind, name and `TileId`. Trading them
looks identical on screen and is not: four values changed hands and the fifth — the closure, which
answers with the id of the leaf that *created* the content — could not, so each terminal came out
reading its neighbour's id and "Restart shell" reopened the wrong `--session-id`. Swapping places has
nothing to keep in step: content, id, name and the leaf that owns all three never come apart.
`TileDragDropTests` asserts the pairing rather than the movement, because the pairing is what broke.

### Why `ITile` is this thin

The thinness is the design, not a shortcut. Three things were considered for it and rejected, and the
reason is the same each time: a member half the implementations cannot honour is a signature that lies.

- **`Refresh()`** — a note has nothing to refresh. An empty body or a `NotSupportedException` is exactly
  the workaround this whole change exists to remove.
- **`WorkingDirectory`** — Terminal, Git, Database and Goal keep it; the markdown tiles take it in the
  constructor to compute a file path and then forget it. It is an argument to creation, not state of a
  tile.
- **`Title`** — the tile's name belongs to `LeafTileNodeViewModel`. Putting it here as well gives one
  value two writers, which this codebase has already paid for once (see *One writer per property* in
  `CLAUDE.md`).

What is left — change notification and disposal — costs nothing, because all six kinds already implement
both. `LeafTileNodeViewModel.Dispose` therefore no longer asks `if (Content is IDisposable)`: a tile that
forgets to clean up stops being a thing anyone can write.

### The rule for adding a capability

Something earns its own interface only when all three are true:

1. **It is optional.** If every tile has it, it belongs in `ITile` or nowhere. Styling is the example that
   belongs *nowhere*: every tile is styled, through `DynamicResource` and `ThemeBridge`, globally. There
   is no capability there. `ICustomBackgroundTile` is not about theming — it is about the terminal being
   the one tile that sits inset from its card and paints that inset in its own ANSI background
   (`TerminalTheme.Background`, a literal hex the UI palette does not derive).
2. **It varies while the tile is alive.** If it does not, it is data on the kind, not an interface member.
3. **Somebody has to ask "can you do this?"** — that question is written `is` / `as`, which is what an
   interface is for.

`ITileActions` is the one that was close. `Actions` alone could sit on `ITile` returning an empty list —
an empty list is not a lie. But `InvokeAsync` travels with it, and on a tile with no actions that is a
method which can only fail. Kept as a capability. If all six kinds end up implementing it, promoting it
into `ITile` is a one-line change; the reverse is not.

## The catalog, and the one layering boundary

A tile's view is a `Control`, so a kind that built its own view would drag `Views/` into what the view
model can see — and `ViewModels/` never referencing `Views/` is a rule this project keeps. The catalog
therefore holds both halves in **one entry registered by one call**, while the interface the view model
sees knows nothing about views:

```csharp
public sealed record TileCatalogEntry(
    ITileKind Kind,
    Func<ITile, Control> CreateView);

public sealed class TileCatalog
{
    public TileCatalog Register(TileCatalogEntry entry);
    public ITileKind? Kind(string? id);          // the view model side
    public TileCatalogEntry? Entry(string? id);  // the view side
    public IReadOnlyList<TileCatalogEntry> Entries { get; }
}
```

One registration per kind is the load-bearing part. Two parallel lists — kinds here, views there — is the
arrangement that has already cost this codebase a bug: the comment on `ConfigureNewLeaf` records how a
list of callbacks copied by hand in `Split` left every tile after the first without dictation. A
duplicate id throws rather than resolving: one of the two would be unreachable, and which one would
depend on the order of two lines in a startup method.

`LeafTileView` resolves its content view through `Entry(content.KindId)`, **never by switching on the view
model's type**. A dictionary lookup is simpler than a six-arm switch, and it does not care whether two
kinds ever share a view model class. The empty tile's chooser is built the same way, from
`AvailableKinds` — six blocks of hand-written markup meant a seventh kind was a class, a line of
registration and a page of XAML nobody would think to look for.

An instance, not a static: there is no DI container here, so the catalog is built in `App.axaml.cs` and
handed down through `MainWindowViewModel` → `WorkspaceViewModel` → each tile. A mutable global registry
is the kind of thing two tests fight over.

The icon is a `string` rather than a `MaterialIconKind`, and that is not a concession to layering: the
phone needs an icon name on the wire anyway, so a string is what this value actually is. The map from
`IconId` to a `MaterialIconKind` lives on the view side (`Views/TileIcons.cs`), where `TileTypeIcon` used
to keep it, and an unrecognised name falls back rather than throwing — a wrong glyph is legible, and the
names come from kinds this file may never have heard of. `AccentKey` is a string for the reason it was
one before: the view hands it to `GetResourceObservable`, so a theme switch reaches it.

### Three factory methods became one

`TileFactory` had three ways in: create, create-with-profile, and create-from-DTO. The second and third
were the same thing seen twice — choosing a profile in the chooser *is* handing a new tile its initial
state:

```csharp
kind.Create(ctx, null);                                              // a fresh tile
kind.Create(ctx, new JsonObject { ["userProfileId"] = profile.Id }); // chosen from the profile chooser
kind.Create(ctx, savedState);                                        // restored from disk
```

Two branches that must produce identical results, with nothing checking that they do, became one branch.

### Saving belongs to the kind, not to the tile

Restoring has to happen on the kind — Goal takes its file path in the constructor, Terminal needs its
shell resolved before it starts — so putting `Save` on the tile would split one JSON shape across two
classes. Both on the kind keeps the format for one kind in one testable place, and keeps
`System.Text.Json` out of the view models entirely; the tiles expose ordinary properties (`Shell.Name`,
`FilePath`, `ShowDiffPanel`), as they already did.

The cast that implies lives in exactly one line, in a generic base:

```csharp
public abstract class TileKind<T> : ITileKind where T : ITile
{
    protected abstract T Create(TileContext ctx, JsonObject? state);
    protected virtual JsonObject? Save(T tile) => null;

    ITile ITileKind.Create(TileContext ctx, JsonObject? s) => Create(ctx, s);
    JsonObject? ITileKind.Save(ITile tile) => Save((T)tile);   // the one cast, and always sound:
}                                                              // this class built that instance
```

`TileState` (`String`, `Bool`) is how a kind reads that state: `TryGetValue` rather than `GetValue`,
because the file is on the user's disk and a number where a string was expected throws — a layout that
will not open is a far worse answer than a tile that comes back with its default.

## Persistence and migration

**The layout must come back looking exactly as it went in.** That is the acceptance criterion for the
whole change, and everything in this section serves it.

One piece of good news set the cost: `JsonDefaults.Options` registers `JsonStringEnumConverter`, so
`ContentType` was **already a string on disk**. Going from an enum to a kind id is a change of type in C#
over identical bytes in the file.

What changed in `workspaces/{id}.json`:

```jsonc
// before                          // after
"ContentType": "Terminal",         "Kind": "terminal",
"ShellName": "PowerShell",         "Settings": { "shellName": "PowerShell",
"UserProfileId": "abc-123",                      "userProfileId": "abc-123" }
"NoteFilePath": "…/x.md",          "Settings": { "filePath": "…/x.md" }
```

What did not change at all: `SplitOrientation`, `SplitRatio`, `First`, `Second`, `TileId`, `TileName`,
`IsActive` — the whole geometry and identity of the tree. **No file outside `workspaces/{id}.json` is
touched**: notes, todos and goal files stay exactly where they are, and only the path recorded for them
moves within the JSON.

`Settings` is a `JsonObject` rather than `Dictionary<string, object?>`. Same bytes on disk, and it removes
the `val is JsonElement el` dance `RestoreSettings` used to do. It is omitted entirely when a tile has
nothing to say.

**`Empty` is not a kind.** It is the absence of one, and it stays that way: an empty `KindId` means a tile
that has not been given content yet, and the chooser and its placeholder glyph are what the view draws
for that. Registering a pseudo-kind for "nothing" would put a class in the catalog that can never build a
tile.

Five rules make the migration safe:

1. **The old fields stay as compatibility properties**, in the shape `WorkspaceState.RootPane` has for
   the `RootPane` → `RootTile` rename: the setter copies the value into its new home. A **blank** old
   field is not adopted — every leaf in an old layout carries all of them, so copying unconditionally
   would give a note a shell name of nothing. And `Settings`' own setter **merges rather than replaces**,
   because every old layout has `"Settings": null` *after* the per-kind fields and a plain setter would
   wipe what they had just put there.
2. **They are written as well as read** — the getters say the same values back out of `Settings`, so
   every layout this build saves carries both formats at once. Reading the old fields covers the update;
   writing them covers the **rollback**, and this application treats that as a real event: Velopack can
   put an older build back, `settings.json` is already written to survive it, and that build knows
   nothing of `Kind` or of `Settings`' per-kind keys. Given only those it reads every leaf as an empty
   tile — and then the first splitter drag saves the emptiness over the user's layout, which rule 3
   protects the *forward* direction from and nothing protected the backward one from. `.pre-kind.json` is
   a copy nothing tells the user about; this needs telling nobody. Three of the old fields are the same
   key (`filePath` for a note, a todo and a goal), so the getter is gated on the kind — writing all three
   would tell an older build that one tile is three kinds at once — and a kind that enum never had writes
   no `ContentType` at all, which is honest: that build could not have built it either, so it gets the
   same empty tile this build gives an unregistered kind. It is a bridge, not a format: when no supported
   build reads the old fields any more, the getters go and rule 1 stands alone.
   `TileNode.IsLegacyFormat` therefore asks **both** halves — an old field *and* no `Kind` — because an
   old field stopped being evidence of an old file the moment this build started writing one.
3. **`"Terminal"` → `"terminal"` is `ToLowerInvariant()`**, because the enum was already serialised by
   name. There is no number-to-name conversion anywhere in this migration.
4. **Nothing is saved for a workspace holding a kind the catalog does not know** — not the write a
   migration asks for, and not one of the ordinary ones either. That tile is shown as empty, never as a
   blank card, and its `TileId` is kept, so the empty card is still the tile that was there. This is the
   one route by which a user could lose a layout for good, and the migrating write is only the first of
   the writes that take it: a splitter dragged, a tile renamed, split or closed serialises the same tree,
   and a leaf whose kind is unknown serialises as an empty one — with no `.pre-kind.json` taken ahead of
   any of them. The refusal therefore sits on `WorkspaceViewModel.ScheduleSave`, where every one of those
   routes meets, and lasts the session: nothing in a running application can give the catalog a kind it
   was not built with, so nothing can make the file safe to write again before the user is back on the
   build that wrote it.
5. **A copy before the first migrated write** — `{id}.pre-kind.json`, once, never overwritten
   (`PersistenceService.BackupBeforeKindMigration`). `settings.json` has this rule already
   (`settings.bad-<timestamp>.json`); layouts did not, and this is the only moment at which every one of
   those files is rewritten at once. A tile layout is the one thing in this application a user cannot
   reconstruct from anything else. It fails soft: a copy that cannot be taken is a reason to log, not a
   reason to refuse to open the workspace.

**The test that enforces the acceptance criterion** is a golden file
(`TileLayoutMigrationTests.A_layout_written_before_kinds_existed_opens_unchanged`): a pre-migration
`workspaces/{id}.json` holding all six kinds and nested splits, loaded by the new code and compared node
by node — kind, name, `TileId`, orientation, split ratio, which tile is active, and which view model
class was built for each leaf. It is written out by hand rather than generated, which is the point of a
golden file: what has to keep working is the bytes on somebody's disk, and those cannot be regenerated
once the code that wrote them is gone.

### Naming, and the step before a tile exists

Two things a kind decides for itself, because the workspace and the empty tile used to decide them by
asking `kindId == TileKindIds.Terminal` — the branch on a kind that the whole registry exists to remove.
A seventh kind that wants generated names, or a question of its own before it is built, is a class and a
line of registration, not an edit to two view models.

**`NameFor(used)`** is handed every name this workspace has already given a tile of that kind — saved
layout included, and nothing is ever taken back out of it, so closing a tile does not free its number for
the next one. The default in `TileKind<T>` numbers after `NamePrefix`, one past the highest number
already in use; `TerminalTileKind` overrides it with `TileNameGenerator`, because several terminals are
open at once and `Terminal#3` says nothing about which is which. `WorkspaceViewModel` keeps the names and
nothing else — where there used to be five `int` fields, five parameters and a five-armed `else if`
reading them back out of a saved layout. `DB#…` is why `NamePrefix` is separate from `DisplayName`:
renaming that would rename tiles in layouts already on disk.

**`SetupOptions(ctx)`** is the profile chooser, generalized to the one shape a step like that has: a row
of cards, each of which is a label, a glyph and the state the tile is then built from.

```csharp
public sealed record TileSetupOption(string Label, string IconId, string AccentKey, JsonObject? State);
```

Picking one calls `Create(ctx, option.State)` — the same route the kind chooser and a saved layout
already share, so there is still one way in. Empty means nothing to ask and the tile is built on the
click, which is every kind but the terminal, and the terminal too when the workspace offers no profiles.
The kind describes the options; the empty tile owns only Back, because leaving a step is the tile's
business and not the kind's. The profiles themselves come through `TileContext.AvailableProfiles`, a
function rather than a list because the answer changes while a workspace is open — and it is the
workspace's *filtered* list, the one that leaves out a profile whose AI tool is not installed.
Restoring a saved tile deliberately does not use it: a tile that was running a profile must come back
running it, whatever the detector says today.

A kind cannot draw its own step, and is not meant to: `ITileKind` lives on the view-model side of the one
layering boundary the catalog keeps, so it describes what to ask and `LeafTileView` draws it with the
same loop it draws the kind cards with.

## Tile actions, and the phone

`PhoneBridgeManager` already held a `Func<LeafTileNodeViewModel?>` for the active tile. What was missing
was anything to ask it for.

```csharp
public sealed record TileAction(
    string Id, string Label, string Icon, bool IsEnabled = true, bool IsDestructive = false);
```

The phone now asks the active tile what it can do — Git → *Refresh, Commit, Push*; Goal → *Continue,
Pause, Commit work* — alongside the three fixed keys, which are unchanged. The same list drives the tile
header's own Restart button and Ctrl+Shift+R (`LeafTileNodeViewModel.CanRestart`), which is what removed
the last `is TerminalTileViewModel` from `DoRestartTerminal`.

**A terminal offers the phone nothing, and that is the rule below applied to the tile that raised it.**
Restart shell was on this list, unmarked, and it was the one action guarded on one screen and not on the
other: the header asks *Restart shell?* first, because restarting kills whatever the shell is running — a
build, an agent halfway through a task — while a pocket sending the same id reached
`InvokeAsync` with nothing in between. That is `IsDestructive` by this file's own definition, so it
carries the flag and `PhoneTileActions` withholds it. Not a confirmation added to the phone: the phone
cannot be shown what the restart would cost, which is exactly the case the flag exists for.

**New session is deliberately not an action.** It replaces the tile's persistent identity — the session
an agent would otherwise resume — which belongs to the tile rather than to its content, and it is not
something to do from a screen that cannot show which conversation is about to be left behind. It stays a
command on `LeafTileNodeViewModel`, which is the object that owns the id.

**This does not weaken the doctrine `PhoneKeys` is written to.** That doctrine is: *what a paired device
can cause is decided in this process, not by the message*. It survives — the phone sends an id, the
manager looks it up in the current `Actions` of the tile it addressed, and an unknown id gets the same
answer malformed JSON gets, which is none. In one respect it is stricter than the keys are: an action is
gated on `IsEnabled` for this tile in this state, whereas Enter can always be pressed.

**One tile answers for the caption, the list and the press** (`PhoneBridgeManager.AddressedTile` — the
tile a phone-driven dictation is aimed at while an utterance is in flight, and the active tile
otherwise). The caption and the list were built from the streaming tile while the press went to whichever
tile happened to be active, and the action buttons are deliberately *not* disabled during a recording:
switch tiles at the computer mid-sentence and the phone went on showing Git #1 and Git's buttons while a
tap ran the Goal tile's command. Ids are not unique across kinds — `commit` is Git's and the Goal tile's
— so that was "Commit" under a Git tile's name starting a Goal run, and the destructive filter could not
catch it, because it was being asked about the tile the press had already been routed to. Asking one
function is what makes what a phone sees and what it presses the same thing by construction rather than
by two call sites agreeing. The hold ends with the utterance (`PublishState` releases it the moment
dictation is idle), so nothing goes on aiming at a tile somebody dictated into an hour ago — and it is
also what makes an Enter land where the sentence did when the active tile moved in between.

The genuinely new risk is that the set is no longer closed *by kind* — a future tile could expose
something like Discard changes, and Git has `DiscardChanges` and `UndoLastCommitAsync` today. Hence
`IsDestructive`, and the hard rule: **a destructive action is not offered to the phone at all.** Not "with
a confirmation" — confirming on a phone something you cannot see is theatre, and this codebase already
holds that an unwired `ConfirmAction` answers no. The filter lives in `PhoneTileActions`, pure and
tested, never in the page — and it is the *same* function that decides what may be shown and what may be
pressed, so the two cannot drift.

Three constraints from the existing code, each already paid for once:

- **The action list is assembled on the UI thread and published as an immutable snapshot.**
  `PhoneBridgeManager` keeps `private volatile string _tileName` for exactly this reason, with a comment
  calling it *the one place in this class that reached into the UI graph from the network*.
  `_actionsJson` sits beside it, and more urgently: building it walks the active tile's content and asks
  each action whether it is enabled right now.
- **A refusal is its own message type** — `actionError`, not `error` — for the reason `keyError` is its
  own: the page treats `error` as the answer to *its* dictation attempt and unwinds its optimistic
  microphone state on one. A refused action must not cancel somebody's recording.
- **An action is started off the receive loop, never awaited on it**
  (`PhoneBridgeServer.RunActionAsync`). It is the one thing a phone can ask for that is not short —
  Continue on a Goal tile runs the whole implement/review loop, and a Git push that fails ends in a
  message box somebody has to walk over to the computer and click. Awaited in the pump it stops
  `ReceiveAsync` being called for that connection at all: the audio frames of the sentence spoken
  meanwhile go nowhere, `begin`, `key` and the next `action` are never parsed, and the phone — still
  being sent state down the independent write chain — looks perfectly alive. That is the sofa this
  feature was built for, so the refusal is *posted* when it arrives rather than returned. Nothing here
  serialises two presses, because the tile already does: the id is checked against what it offers **now**
  (`PhoneTileActions.IsAllowed`), so an action already running is refused for being disabled.

**"Pushed" needs something to push it,** and the manager cannot see it happen: it holds a `Func` that
reads the active tile and nothing else of the view model tree. So the tree says when. A workspace raises
`ActiveTileChanged` for a tile becoming active, for the active tile's own `Actions` or `TileName` moving,
and for a root replaced — the last one because "nothing is active" is a state a listener has to be told
about, being the difference between a stale set of buttons and none. `MainWindowViewModel` follows
whichever workspace is on screen and re-raises, and `App` wires that to
`PhoneBridgeManager.NotifyActiveTileChanged` beside the `Func` itself: the bridge keeps no reference to
the view models and they keep none to it. Without it the list only ever moved when somebody dictated —
a phone kept Git's buttons under Git's name after the user had clicked into a Goal tile, and a run that
finished left Continue greyed out on the phone, which is the one thing the feature is for.

A tile republishes its `Actions` on **any** change to its content, deliberately (see
`LeafTileNodeViewModel.OnContentPropertyChanged`), so a running Goal tile raises this once a second as
its elapsed time ticks. The broadcast is therefore compared against what was last sent and dropped when
it is the same message: a phone that has just connected is answered from the snapshot directly, so
holding a repeat back loses nothing. A tile nobody is aimed at raises nothing at all.

Wire format, pushed rather than polled:

```jsonc
// server → phone, when the active tile or its state changes
{ "type": "actions", "tile": "Git#1",
  "actions": [ { "id": "refresh", "label": "Refresh", "icon": "refresh", "enabled": true } ] }

// phone → server
{ "type": "action", "id": "refresh" }
{ "type": "actionError", "message": "…" }
```

`PhoneKeys` keeps only the wire names and the routing. Enter and the arrows are not tile actions — they
are the keyboard, routed by `DictationTextSink`'s rule (a focused text control first, then the active
tile's own input surface), and that rule has to stay one rule or a dictated sentence and the Enter that
submits it can part company. What a key *is* to a control moved onto the tile
(`ITextInputTile.TryPressKey`), because the answer depends on DECCKM and win32-input-mode — two modes the
terminal control owns and does not expose.

**The `TileKey` → `Key` map is one map** (`ViewModels/TileKeyPress.cs`), used by both destinations: the
focused text control `PhoneKeys` raises the event at, and the tile the key otherwise reaches. It was
briefly a copy in each, with the same `default:` throw and the same comment arguing that a fourth key
missed *in this one place* would go out as Enter — an argument that stops holding the moment "this one
place" is two. The throw stays, and it is safe to reach: every press is wrapped, and the phone is told
the key could not be delivered.

`DictationTextSink.LiveTerminal` is gone: it used to reach into `TerminalTileViewModel.CachedControl` and
ask whether the shell was still running, which meant everything on that route had to know what a terminal
is. It is `TileInput(tile)` returning an `ITextInputTile`, and the tile answers both questions itself.

## What deliberately stays out

- `CachedControl`, `AttachControl`, `ReplaceLaunchSession` — `internal`, terminal-only. `TileLauncher`,
  `ShellStarter` and `DirectLaunchSession` go on depending on the concrete class, because they are
  services of a terminal and not of a tile.
- `TileSettingsChanged` (Git, Database) and `OpenDatabaseSettings` (Database) — dependencies handed in
  through `TileContext`, not capabilities a consumer interrogates.
- `LeafTileNodeViewModel.HasProfile` — the one place left where the tile knows what a terminal is, and it
  is about the tile's own identity: "New session" generates a fresh `TileId`, which is only ever *used*
  by a profile script that puts `${tileId}` on a command line.

That nothing else qualifies is the result, not a gap. An `ITile` grown to eight members would be one that
had started absorbing what only one kind needs, and the empty implementations would be back.

## Tests

- `TileCatalogTests` — every kind has a unique id; every historical `TileContentType` name maps to a
  registered kind (the test that catches a user's layout opening as a row of empty tiles); `Save` →
  `Create` → `Save` round-trips to the same JSON; a kind builds a tile that agrees about what it is; a
  terminal follows the tile id it was given rather than a copy of it.
- `TileLayoutMigrationTests` — the golden file above, the one-time backup, a kind nothing is registered
  under leaving the file untouched, and the two properties of the format nobody controls: a blank old
  field is not adopted, and a trailing `"Settings": null` does not undo what the fields before it put
  there. Then the same claim the other way round: a layout this build writes, deserialised into the DTO
  the build before it had, comes back with its kinds, shells, profiles and file paths — and carrying both
  formats does not make the file look like an old one.
- `PhoneTileActionsTests` — nothing destructive is sent to a phone or reachable by naming it; a disabled
  action is shown and refused; and Restart shell is the *only* thing a shipped tile withholds, written as
  the exhaustive list so that a seventh action has to be thought about before the build goes green.

They all run through `TestTiles.Catalog`, which is the application's own `App.BuildTileCatalog` rather
than a list kept in step with it: a test catalog would answer questions about itself.
