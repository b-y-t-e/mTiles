# 0004 — A rung below the default review

Date: 2026-09-22

## Context

[ADR 0003](0003-effort-by-role-in-a-goal-run.md) made a Goal run think by role and put one word in the
strip for it. Its default, `balanced`, was *plan medium · work low · review high*, and the review's
level was not an opinion: this repository's own goal logs measure it — twenty-one reviews at `medium`
returned one finding or none with eight empty, against twenty-one at `high` where none was empty and the
median was four (`docs/GOAL.md`).

What that measurement says is what a deep review **catches**. What it does not say is that every run is
worth paying for one. A review reads the whole of `git diff HEAD` and thinks about it at the dearest
level in the preset; on a two-line change that is most of the run's cost for the step with the least to
read. The ladder had no rung for "read it back, but do not spend the afternoon on it": the next one down
was `cheap`, which also drops planning to `low` — and planning is the step that decides whether the run
is working on the right thing at all.

## Decision

**A fifth preset, between `cheap` and the old default**, and the new one takes the default's name:

| preset | plan | work | review |
|---|---|---|---|
| `cheap` | low | low | low |
| `balanced` (the default) | medium | low | **medium** |
| `careful` | medium | low | **high** |
| `thorough` | high | medium | high |
| `default` | — passes no flag anywhere — |

`careful` is `balanced` as ADR 0003 defined it, levels untouched, one rung up and one click away.

**The word moved, so the stored answer is migrated.** The preset is written to `settings.json` by name,
so a file saying `Balanced` cannot be read as either spelling without knowing which build wrote it. The
live value therefore moves to a key of its own (`GoalEffortPresetV2`) and the old key is read once into
`AppSettings.LegacyGoalEffortPreset`: a stored `Balanced` becomes `Careful` and every other word passes
through unchanged. That is the rule the three `.gitignore` keys already follow, and it runs after the
older `GoalEffort` migration so that the newer of two generations in one file wins.

## Consequences

- Somebody who chose the old `balanced` keeps the run they chose, under a name they did not. The
  picker's row spells its three levels out, which is where they find out.
- **A user who never chose gets a shallower review than before**, which the measurement above says can
  cost findings. That is the trade this ADR makes deliberately: the cheaper default is the one most runs
  want, the deep one is one click away, and a run that has already been got wrong once has `careful` and
  `thorough` above it.
- A rollback past this build reads neither the new key nor the migrated one and opens on its own
  default — `balanced` as ADR 0003 defined it, which is the dearer of the two, so nothing gets cheaper
  behind the user's back in that direction either.
- Five rows in a picker rather than four. That is the ceiling: what makes one control enough is that
  each row spells its levels out, and a list nobody reads to the end stops doing that.

## History

Amends [0003](0003-effort-by-role-in-a-goal-run.md), which is otherwise unchanged: effort by role, the
commit as a constant, and the `planned by` slot all stand.
