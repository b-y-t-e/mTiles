# ADR 0003 — A Goal run thinks by role, and the work is the cheap one

- **Status:** accepted
- **Date:** 2026-09-21
- **Where:** `src/mTiles/Models/GoalRole.cs`, `src/mTiles/Models/GoalEffortPreset.cs`,
  `src/mTiles/Services/GoalRoles.cs`, `AppSettings.GoalEffortPreset`,
  `GoalTileViewModel.EffortFor` / `AgentFor` / `PlanningAgentInstanceId`
- **Supersedes** the reasoning in `AiEffort`'s own remarks — "hence `High` as the default rather than
  the tool's own" — which was written when a Goal run had one effort level for everything.

## Context

A Goal run makes four kinds of AI call — working the goal out and planning it, carrying the plan out,
reviewing what came back, and deciding which changed files belong in which commit — and until now it
made all four at one effort level, from one setting, defaulting to `high`. The argument for that
default was written down and is worth restating, because this reverses it: *the tile is meant to be
left alone for an hour on work the user has already decided is worth an hour, the budget is in
attempts, and a shallow attempt spends as much of that budget as a careful one.*

That argument is strongest where it was least examined — the implementing phase — and what is measured
does not support it there:

- On agentic coding benchmarks, accuracy against reasoning effort rises in the cheap range and then
  **saturates, and is sometimes non-monotonic**: one agent resolved 171/300 at medium against a
  baseline's 172/300 at a lower cost, while high effort helped the baseline (185/300) and not the
  agent. Token-consumption analysis of failed expensive runs finds the extra spend going into repeated
  viewing and re-editing of the same files — inflated context, no proportional progress.
  ([OpenHands' benchmark survey](https://www.openhands.dev/blog/ai-coding-benchmarks-explained),
  [*How Do AI Agents Spend Your Money?*](https://arxiv.org/pdf/2604.22750))
- **A weaker author's errors are the ones a review actually catches.** Strong generators produce
  internally consistent reasoning chains where an early mistake propagates coherently, yielding
  well-structured wrong answers that verifiers wave through; weaker generators' errors are easier to
  detect. ([*Understanding Verification Dynamics*](https://arxiv.org/pdf/2509.17995))

The obvious correction — spend the saved budget on a deeper review — is **not** supported either.
Under a fixed inference budget, generation beats verification for every practical budget: generative
verification needs roughly 4×–64× more compute merely to match plain solution sampling, and 128× to
beat it by 3.8%. ([*When To Solve, When To
Verify*](https://arxiv.org/abs/2504.01005), COLM 2025)

What that argument does *not* touch is the level the review itself runs at, and there this
repository has a measurement of its own (`docs/GOAL.md` → *What the review is asked to do*): read back
from its goal logs, twenty-one reviews at `medium` returned one finding or none with **eight of them
empty**, against twenty-one at `high` where not one came back empty and the median was four. An empty
review is not a cheap review — it passes `RequireGoalMet` on the first attempt and reports a goal with
an unfixed bug in it as met. So the saving comes out of the implementing phase, and the review stays at
`high`.

What *is* supported, strongly, is that the reviewer should be a **different agent**. A model that
confidently catches an error in external content routinely fails to find the identical error in its own
reasoning; re-presenting byte-identical claims under an external role lifts correction rates by 23–93
percentage points, and intrinsic self-correction without an external signal degrades reasoning on
average. ([*The Self-Correction Illusion*](https://arxiv.org/html/2606.05976v1),
[Huang et al., ICLR 2024](https://arxiv.org/pdf/2310.01798),
[Kamoi et al., TACL 2024](https://arxiv.org/html/2406.01297v3))

## Decision

**Effort is per role.** `GoalRole` — `Planning`, `Work`, `Review`, `Commit` — is derived from
`GoalPhase` (`GoalRoles.For`) and persisted nowhere; `GoalPhase` is untouched, because it is written
into every goal file, decides `AiUsage`, and gates `GoalTilePolicy.CanResume`. `Commit` is the one role
with no phase of its own and is named at its call site.

**The setting is a preset, not three levels.** Four rows — `balanced` (the default: plan medium, work
low, review high), `thorough`, `cheap`, `default` — behind the one picker the strip already had. The
three levels live in the picker row's own description, so the strip carries one control where the
feature has three. `AppSettings.GoalEffort` becomes `LegacyGoalEffort`, read once by
`SettingsService.MigrateLegacySettings` through `GoalRoles.FromLegacyEffort` and dropped.

**The commit is `low`, always, as a constant.** It is in no preset and has no field on any screen, so
nothing can set it wrong. The single exception is the `default` preset, whose whole meaning is "pass no
flag" and which has to reach every call or a CLI older than the flag still fails on the commit alone.

**A third agent slot: `planned by`.** Alongside the existing `reviewed by`, empty meaning "the agent
doing the work", in the same panel and for the same reason — a once-per-goal setting is not strip
furniture. Work and commit stay the execution agent's: the one writes the code and the other decides
how to record it.

## Consequences

**Gained.** Existing users who never touched the effort setting get cheaper implementing runs without
losing depth where it pays. Two of the four roles can now run on a different CLI — codex reviewing what
Claude Code wrote — which is the change the evidence supports most strongly. The commit plan stops
being run at whatever effort the surrounding phase happened to carry, and stops being attributed to the
reviewer.

**Cost.** Some goals will need more attempts because the implementation was cheaper; `thorough` is the
answer, and it is one word away. The preset hides the per-role levels behind a row description, so
somebody who wants plan-high-work-low cannot say it — deliberate, and the reason is in
`GoalEffortPreset`'s own remarks: three pickers do not fit a strip that already carries an agent, a
mode and a status in a tile that is often 300px wide.

**Migration.** A stored level of exactly `high` — the old default — reads as the new default rather
than as a request for high everywhere: it is indistinguishable from a file nobody touched, the rule
`SettingsService.AdoptEmbeddedFont` already follows. Every other stored level is honoured in the
direction it was typed, so nobody's runs get dearer than they asked for.

**Not done.** The strip does not yet say, beside the execution agent, that the review or the planning
is somebody else's — that needs a sixth control in `GoalStripLayout`'s retreat order and is a change to
the strip's own arithmetic. Until it exists, a second agent is visible only in the panel it was chosen
in.
