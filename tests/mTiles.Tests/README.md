# mTiles.Tests

About 3 600 cases, run serially (parallelisation is off for the whole assembly — see `AssemblyInfo.cs`).
The suite stays fast only while the rules below hold; `scripts/test-budget.py` enforces the first one
on CI.

## Rules

1. **No test waits on a real clock.** A debounce, a quiet window, a poll, a countdown or a timeout is
   the application's interval, not the thing under test. Every such interval in `src/` is a settable
   property, shortened for the whole run in `Kit/TestTimings.cs`; a new one gets a line there. A test
   that is *about* an interval states its waits relative to that property
   (`SkillChangePolicy.QuietWindow * 3`), never as a literal. Where code measures how long something
   took, hand it a `ManualClock` and `Advance` it instead of sleeping (see `DirectLaunchSessionTests`).
   A test slower than **2 s** fails CI unless it carries `[Trait("Category", "Slow")]` and a sentence
   saying why it cannot be faster.
2. **A rule is tested once, as a table.** Pure policies (`ChainPolicy`, `ActivityPolicy`,
   `GoalReviewGatePolicy`, …) get a `[Theory]` with one row per case. The view model then gets **one**
   wiring test per feature that proves the policy is consulted — it does not repeat the rows. Near-
   identical `[Fact]`s that differ only by data are one `[Theory]`.
3. **The headless UI thread only for what needs it** — building, measuring or clicking a control, or a
   view model that dispatches. Everything else is a plain test. Use `Ui.Run(...)`/`Ui.Pump()`; do not
   copy a helper into your class.
4. **Real processes are expensive; share what they build.** git: `GitTestRepo` (a copy of a template
   made once per run, never `git init` per test); `RequiresGit.OrFail` is cached. Certificates, key
   derivations and anything else CPU-heavy: build once per class (`IClassFixture`) or per run.
5. **Scratch files go under `Path.GetTempPath()` and are not your problem to clean up.**
   `Kit/TestTempRoot.cs` points the process's temp directory at one directory per run and deletes it two
   runs later, so nothing leaks into `%TEMP%`. Prefer `TempDirectory` / `TempSettings` / `TempAppData`
   over a hand-written `_dir` and `Dispose`.
6. **Static seams are reset by the class that sets them** (`GoalTileViewModel.AiRunnerFactory`,
   `AiProvider.HandlerFactory`, …) — in `Dispose`, or through the fixture that owns them.
7. **Comments say what the test protects, in a sentence.** The history of the bug lives in git; a row
   of a table carries at most one line.

## Kit

| | |
|---|---|
| `Ui` | `Ui.Run(Action)`, `Ui.Run(Func<Task>)`, `Ui.Run<T>(Func<T>)`, `Ui.Pump()` |
| `TempDirectory` | a scratch directory, `dir["name"]` for a path inside it |
| `GitTestRepo` | a repository copied from a per-run template; `Write`, `CommitAll`, `Git` |
| `ManualClock` | a `TimeProvider` that moves on `Advance` |
| `TestFiles.WriteWhenFree` | a write that waits out a watcher's reader holding the file |
| `TestTimings` | every application interval, shortened once |
| `TestTempRoot` | the per-run temp directory |
| `LiveAgentTheory` | a theory skipped unless `MTILES_LIVE_AGENTS` is set |
| `RepoSources` | the repository's `.cs`/`.axaml` files, read once per run, for tests that scan source |
| `ControlThemes`, `AppTokens` | Fluent control themes, and this application's `AppTheme` tokens, for a headless view |
| `RectAssert` | rectangles equal within a pixel |

Outside `Kit/`, by area — look here before writing a fake:

| | |
|---|---|
| `GoalTileFixture` | base class for Goal tile tests: owns and resets every Goal seam; `NewTile`, `Script`, `RunToSummary`, … |
| `AgentSessions/SessionFakes` | `NoSessionStarter`, `ReadyStarter`, `FakeAgentSession`, `TestStore`, `ConversationTiles.New` |
| `AgentSessions/HeadlessTheme` | the theme an Agent tile view needs, added and removed around a test |
| `HttpStub` | owns `AiProvider.HandlerFactory`; canned and recording HTTP handlers |
| `TestMainWindow`, `TestWorkspace` | a `MainWindowViewModel`, and a workspace with tiles in it |
| `SilentSpeech`, `SpeechFakes` | microphones, speech engines, model files on disk, an inline UI dispatcher |
| `PhoneTestCertificate` | one TLS certificate per run, copied into each test's directory |
| `FakePty`, `StubAgent`, `TarGzFixture` | a pseudo-terminal, an agent, an archive |

## Measuring

```bash
dotnet test tests/mTiles.Tests --logger "trx;LogFileName=r.trx" --results-directory TestResults
python scripts/test-budget.py TestResults/r.trx   # slowest tests, and the ones over budget
```
