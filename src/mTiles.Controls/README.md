# mTiles.Controls

Controls, and nothing else. No reference to the application — the same one-way rule
`mTiles.AgentSessions` keeps, and for a related reason: what is in here has to be arguable on its own. A
control that knows about an agent instance, a tile or a workspace is not a control, it is a piece of that
screen with a template around it.

**Colours are named, never defined.** Every brush is a `DynamicResource` by role — `BgElevated`,
`BorderSubtle`, `TextPrimary`, `AccentDefault` — and this project defines none of them. The host supplies
them (`mTiles/Styles/AppTheme.axaml`), so a control drawn in this application takes this application's
theme without being told. The cost is that these controls render colourless with no host, which is the
honest state of a library that refuses to have opinions about somebody else's palette.

One line wires it up:

```xml
<StyleInclude Source="avares://mTiles.Controls/Themes/Picker.axaml" />
```

## `Picker`

A button that says what is chosen, and opens a list to change it.

### Why it exists

A `ComboBox` and an `AutoCompleteBox` were both tried against a provider catalogue of nearly four hundred
models, and each is wrong in the opposite direction:

| | filters as you type | shows a list exists |
|---|---|---|
| `ComboBox` | no — its typing is jump-to-first-letter | yes, the chevron |
| `ComboBox IsEditable` | no — same | yes |
| `AutoCompleteBox` | yes | **no affordance at all** |
| `Picker` | yes | yes |

There is a subtler failure under the obvious one: an `AutoCompleteBox` filters on *its own text*, so
opening it with a value already in it narrows the list to the one row the user already has. The press asks
"what are my options" and the answer is "the one you are on".

The way out is the one t3code takes. **The trigger only ever displays, and the typing happens in a search
field inside the list.** Two jobs, two controls, no argument between them.

### One control for every menu

The same thing draws a three-row permission menu with a sentence under each mode, a grouped effort menu
with `Default` beside a row, and a model list with a provider rail down its left and a search box at the
top. What differs is which of `PickerOption`'s optional parts the caller fills in — never a second control,
because three lists is three sets of padding, highlight and keyboard handling that drift apart.

```xml
<c:Picker Options="{Binding ModelOptions}"
          SelectedId="{Binding Model}"
          Text="{Binding ModelLabel}"
          IsSearchEnabled="True"
          SearchPlaceholder="Search models…"
          DropDownWidth="340" />
```

### Loading it with data

`Options` takes **anything**. An item that is already a `PickerOption` is used as it is; anything else goes
through `OptionSelector`, one line saying how one of the host's own models reads:

```csharp
ModelPicker.OptionSelector = item =>
    item is string id ? new PickerOption { Id = id, Title = id } : null;
```

Returning `null` leaves the item out, which is how a caller filters without building a second collection.
An `ObservableCollection` is followed, so a catalogue that arrives from a provider after the list was built
shows up with nobody re-binding.

`PickerOption` carries `Title`, and then only what a given list needs: `Description` (a sentence under the
name), `Detail` (where the row comes from), `Badge` (`Default`), `Shortcut`, `Icon`, `Group` (a heading is
drawn when the group changes, so the caller's order is the whole of the grouping), `CategoryId` (which rail
entry shows it), `IsAction` (a row that does something rather than naming a place) and `Tag`.

### A row that cannot be picked is still a row

`IsEnabled = false` draws it dimmed with `DisabledReason` under it and refuses the click. It is never left
out of the list and **never handed to Avalonia as a disabled item**, which drops out of the hit test and
takes the very sentence explaining the refusal with it. That is also why the list is an `ItemsControl` and
not a `ListBox`.

### Styling it

Every part carries a class, so a host restyles without replacing anything: `picker-trigger`,
`picker-chevron`, `picker-card`, `picker-search`, `picker-rail`, `picker-rail-item`, `picker-row` (with
`.selected`, `.highlighted`, `.refused`), `picker-row-title` (`.action`), `-detail`, `-description`,
`-reason`, `-badge`, `-shortcut`, `picker-heading`, `picker-empty`.

Behaviour has knobs too: `Filter` (what matching means — `PickerSearch`'s rule by default),
`ClearsSearchOnOpen`, `ClosesOnSelection`, `ShowChevron`, `Placement`, `DropDownWidth`,
`DropDownMinWidth`, `MaxDropDownHeight`, `EmptyText`, `Placeholder` (what the trigger says while `Text` is empty).

### Keyboard

Up and down move the highlight over pickable rows, stepping over headings and refusals; Enter takes it;
Escape closes. **The highlight does not wrap** — at the moment it happens, a list jumping from its end to
its start looks exactly like a list that scrolled, and this one has headings in it, so there is nothing at
the top to recognise as the top.

**Selected is not highlighted.** Selection is the row the picker is on; highlight is the row the keyboard is
over. One treatment for both makes the current row unfindable the moment the pointer enters the list.

### `PickerSearch`

Every word, anywhere, in any order: `glm 5.3` finds `z-ai/glm-5.3-flash`, and so does `5.3 glm`. A dot is
deliberately *not* a separator — it is how version numbers are written. Pure, and argued in a table test.

Not ranked. t3code scores its matches and sorts by the score; this keeps the caller's order, because in
every list here that order already means something — conversations are most-recent-first, efforts run low
to high — and a relevance sort would scramble both.
