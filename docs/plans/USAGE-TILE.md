# A tile that says what is left

## What this is

An eighth kind of tile (`usage`), showing — for every account this machine can actually ask — how much of
the subscription window is gone, when it comes back, whether the week is being spent faster than the week
is passing, and what the last seven days cost where the answer is money rather than a percentage.

It is a **read-only dashboard**. It starts nothing, kills nothing, and holds no state a user would miss.

## What can honestly be shown

Measured on this machine, 2026-09-01. Each row is a route that was run, not a hope.

| Account | Route | 5h | 7d | Reset | Money |
|---|---|---|---|---|---|
| Claude Code (subscription) | `GET api.anthropic.com/api/oauth/usage`, Bearer = `claudeAiOauth.accessToken` from `.credentials.json`, `anthropic-beta: oauth-2025-04-20` | `five_hour.utilization` | `seven_day.utilization` | `resets_at`, ISO | only `extra_usage`, usually off |
| Codex (subscription) | last `token_count` event in the newest `~/.codex/sessions/**/rollout-*.jsonl` | `primary.used_percent`, `window_minutes:300` | `secondary`, `window_minutes:10080` | `resets_at`, unix | `credits.balance` |
| OpenRouter | `GET /api/v1/key` + `GET /api/v1/credits` | — | `usage_weekly` | `limit_reset`, when a limit is set | `usage_daily`, `usage_weekly`, `usage_monthly`, `total_credits - total_usage` |
| z.ai, Anthropic API, LM Studio, Ollama, ccs | none | — | — | — | — |

Three facts the design has to obey rather than paper over:

- **A subscription answers in percent, a provider in money.** They are not two views of one number and
  must not share a bar. One card shape per kind.
- **`GET /api/v1/activity` is 403 for a normal key** (`Only management keys can fetch activity`). There is
  no per-day history to fetch, from anybody, without asking the user for a second and stronger key. The
  seven bars therefore come from **our own daily snapshots**, which means they start empty and fill in —
  and the tile says so, rather than drawing six zero-height bars that read as six free days.
- **Codex's numbers are as fresh as its last reply.** No endpoint answers them
  (`backend-api/codex/usage` → 403 at the edge), only the transcript does. The card carries the age of the
  reading, and a reading older than the window it describes is dimmed and stamped, never shown as current.

## The parts

### 1. `Models/AiUsageWindow.cs`, `Models/AiUsageReport.cs`

```
AiUsageWindow(string Label, TimeSpan Length, double? UsedPercent,
              decimal? UsedAmount, decimal? LimitAmount, DateTimeOffset? ResetsAt)

AiUsageReport(string SourceId, string SourceName, string? Plan,
              IReadOnlyList<AiUsageWindow> Windows, decimal? RemainingCredit,
              string? Currency, DateTimeOffset MeasuredAt, string? Problem)
```

`null` is *did not say* everywhere here, for the reason `AiModelInfo.ContextWindowTokens` and
`ProviderCheck.Balance` already carry: a limit shown as 0 tells a user whose key works perfectly well that
they have run out. `Problem` is a sentence, because a card that cannot answer has to say why in the place
the answer would have been — the rule `AgentAvailability` set.

### 2. Two questions, each asked of the thing that knows

- `IAiAgent.UsageAsync(AiSignIn? signIn, CancellationToken)` → `AiUsageReport?`. Default `null`;
  `ClaudeAgent` and `CodexAgent` override. Per sign-in **and** for the default account, because a second
  subscription is a second set of limits — which is the whole reason `AiSignIn` exists.
- `IAiProvider.UsageAsync(AiProviderInstance, CancellationToken)` → `AiUsageReport?`. Default `null`;
  `OpenRouterProvider` overrides, through the same `GetJsonAsync`/`HandlerFactory` seam `TestAsync`
  already uses, so it is testable without a live key.

Neither throws — a failure is the answer, the rule `AiProvider` is built on.

### 3. `Services/UsagePace.cs` — pure, table-tested

The thing that was actually asked for. Given a window (`Length`, `ResetsAt`, `UsedPercent`):

```
elapsed  = Length - (ResetsAt - now)                     clamped to [0, Length]
expected = elapsed / Length * 100
delta    = used - expected
empty at = now + (100 - used) / (used / elapsed)         only when used > 0
```

Derived from `ResetsAt - Length`, **not** from the day of the week: Claude's and Codex's seven-day windows
are rolling, so "Wednesday, therefore 43%" is wrong by up to a day — and the same formula then serves the
5h window for free. The `day × ~14%` that was asked for is exactly what this reduces to for a window that
happens to reset weekly.

Answers one of `Behind` / `OnPace` (|delta| ≤ 3 points) / `Ahead`, plus the delta and the projected
exhaustion instant. Three states with a dead band — without it the label flickers between two words on
every refresh.

### 4. `Services/UsageHistory.cs` — where the seven bars come from

`%APPDATA%/mTiles/usage/history.json`: `{ sourceId: { "2026-09-01": 18.09, … } }`, 60 days kept, written
through `PrivateFile` (owner-only — it is a spending record, and outside Windows `settings.json` is
already plain text for the same reason).

The value is `usage_daily` and the day is **UTC**, because that is the boundary OpenRouter's own counter
resets on; the bars are labelled with a local date rather than a weekday, so the offset is visible rather
than silently applied. The **maximum** seen for a date is kept, so a poll landing just after midnight
cannot write a fresh small number over a finished day.

A source with no money figure (Claude, Codex) contributes no bars, and its card shows the weekly window
instead. That asymmetry is deliberate and is stated on screen.

### 5. `Services/AiUsageService.cs` — one asker for the whole application

A singleton built in `App.axaml.cs` beside `DatabaseServiceManager` and handed to `UsageTileKind`. It
enumerates the sign-ins and the provider instances, asks each in parallel, caches for **5 minutes**,
records into `UsageHistory` and raises `Changed`. Refreshed when the tile is built, by a manual button,
and by a 5-minute timer that **runs only while at least one usage tile is loaded** — the same rule
discovery follows: nothing here polls a service the user is not looking at.

Two usage tiles in two workspaces are one set of calls.

### 6. The tile

`TileKindIds.Usage = "usage"`, `ToLegacy` → `null`: a build Velopack has rolled back reads it as an empty
tile, which costs nothing, because it holds no work. `UsageTileKind.Save` answers `null` — there is
nothing per-tile worth remembering. `NamePrefix` "Usage", `IconId` `gauge` →
`MaterialIconKind.SpeedometerSlow`, and a new `TileAccentUsage` in `AppTheme.axaml`.

`UsageTileViewModel` implements `ITile` and `IBusyTile` (the workspace row's light turns while a refresh is
in flight) and nothing else — not `IProcessTile`, since it runs nothing.

## What it looks like

Full-bleed in the card (its content is its own chrome), one scrolling column of account cards, one gutter,
nothing docked. Per card:

```
  ◆ Claude Code · Max 20x                                   11:42
  5h   ▓▓░░░░░░░░░░░░░░░░░░   11%     resets 03:00 · in 2h 14m
  7d   ▓░░░░░░░░░░░░░░░░░░░    2%     resets Fri 10:00 · in 2d 6h
       ────────────────┊─────         28% expected · 26 points spare
```

- **A bar is a bar and the pace is a tick on it.** The `┊` sits at `expected`; fill past it turns
  `DangerText`, fill behind it stays on the accent. One glance answers *am I overspending* without a
  second widget — which is what the pace line was asked for.
- The line under it is the words: `26 points spare`, or `12 points ahead — empty by Thu 18:00`. The
  projection appears only when there is a rate to project from.
- **The reset is absolute and relative at once.** `resets 03:00 · in 2h 14m` — the relative half is what
  you act on, the absolute half is what survives being looked at ten minutes later.
- A money card takes the same head and, below it, seven day bars scaled to the tallest, with
  `today €4.18 · 7 days €22.06 · €4.60 left`. Days before the first snapshot are a hairline baseline with
  `collecting since 1 Sep` under the row.
- A card that failed shows its sentence and its timestamp. A Codex card older than its 5h window says
  `from a session 6h ago` in `TextSecondary` and dims its bars.
- Empty state: `No account here reports limits.` and a button opening Settings → AI
  (`TileContext.OpenSettings`, `SettingsTabs.Ai`).

The minimalism rule for this tile: **no chips, no boxes, no second frame.** Figures are plain text on the
name's own left margin, bars are 3px, one radius, and the only colour carrying meaning is the overspend.

## Risks, said plainly

- **`api/oauth/usage` is undocumented.** It is the endpoint `/usage` in Claude Code reads, and it can move
  without notice. A non-200 becomes `Problem` on the card and nothing else breaks; every field is read
  defensively and absent means `null`.
- **The rollout format is somebody else's too**, and `SessionCapture` already depends on it — this reads
  the same files and adds no new fragility.
- **Reading `.credentials.json` puts an OAuth token in this process's memory.** It is read at call time,
  sent only to `api.anthropic.com`, never logged, never persisted, never shown. Worth writing down: it is
  the first thing here that handles a token this application did not issue.
- **The seven bars are ours, not OpenRouter's.** Clearing `%APPDATA%` starts them again. Said on screen;
  not worth a second source of truth.
- Clock skew moves `expected` by the skew. Nothing to do about it, and at 5h scale it is noise.

## Tests

- `UsagePaceTests` — table: mid-window, just reset, about to reset, zero use, past 100%, `ResetsAt` null.
- `AiUsageParseTests` — the three payloads recorded verbatim as fixtures, plus a truncated one and one
  with a renamed field, asserting `null` rather than a throw.
- `UsageHistoryTests` — same-day maximum wins, UTC rollover, 60-day pruning, unreadable file is a fresh
  start rather than a crash.
- `UsageTileKindTests` — builds from `null` state, saves `null`, round-trips through `TileTreeSerializer`.
- `AiUsageServiceTests` — one call per source per window, driven through `AiProvider.HandlerFactory`.

## Documentation this changes

`CLAUDE.md` (the kind list, the `Services/` inventory, persistence — `usage/history.json`),
`docs/TILES.md` (the new kind, and why it implements only `IBusyTile`), and the README roadmap line if one
is claimed for it.

## Open question

Whether the tile should cover spending that did not come from mTiles. The OpenRouter figures are per
*key*, so a key used elsewhere shows up here too. I think that is correct and should not be filtered: the
question this tile answers is *how much is left*, not *how much did this application use*.
