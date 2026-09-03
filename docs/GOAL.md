# Goal tile

Iterative AI-driven development in one tile. **Read this before changing anything under the Goal tile's
services or view model**: nearly every paragraph here is a bug that has already been paid for once, and
the reasoning is the part that stops it being reintroduced.

**Workflow phases** (`GoalPhase` enum): Goal → Clarify → Plan → Implement → Review → Summary. Plan creates a concise implementation plan with clear steps and success criteria. User types "ok" to approve or describes what to change (→ back to Clarify). All prompts enforce Clean Code and whichever SOLID principles the tile's criteria panel has switched on — all five by default; see *SOLID is per goal* below — and **every prompt ends with exactly one worked example of the answer it wants** — three of them ask for JSON, and a model shown a schema in prose invents a neighbouring one. One example, never two: they are fixed overhead in every prompt and the prompt has a command-line budget.

**Two ways in.** Typing a goal describes work that has not started. **Detect goal** reads the uncommitted changes and works out what the user was in the middle of — which is the commoner way somebody arrives at a tile like this, half-finished and wanting it finished. It is offered only where a goal is what is wanted next (Goal or Summary) and only when `git status --porcelain` says there is something to read (`WorktreeReader.HasChangesAsync`, asked at named moments — when the tile is built, when a run ends, when a detection finds nothing, when a fresh goal is started — and **not** continuously): a button that reads the working tree has nothing to say about a clean one. It is deliberately not a watcher: the changes it asks about are made in the terminal tiles next door, so between those moments the answer can be stale in either direction, and both cases cost a click. A filesystem watcher over a whole worktree, per Goal tile, is not a price worth paying for a button's visibility — and the run itself re-reads the tree rather than trusting this. The tree is read **before** the transcript is cleared, and that order is load-bearing: the button is shown on the strength of `git status`, which is a different command from the one that builds the prompt, so a commit made in between — or a repository with no HEAD — ended with nothing to detect from and the user having paid for it with their session. **Both detect buttons adopt the goal**; they differ only in what comes next. **Detect goal** works the sentence out, makes it the goal, says it in the transcript as the user's own turn and carries on into **Clarify** — still inside goal-setting, where the tool can ask what it cannot decide and the user can still change what this is about before a line of code is written. It used to park the sentence in the composer and wait for Send, on the argument that a detected goal is the tool's reading of half-finished work and should be edited first. That cost a click and a phase — the tile sat saying it was waiting for a goal it had already written — and it bought editing that Clarify does better. It also carried a set of careful rules about never overwriting what the user had typed, all of which existed to protect a draft nobody had asked to keep. What does **not** move is `OriginalGoal`: it is fixed at that sentence and carried by every later prompt, so a badly detected goal is corrected with **+** rather than talked round. **Detect & run** skips the conversation and enters the loop with `startAtReview: true` — the parameter the interrupted-resume already needed — because the changes are on disk and what is owed first is a judgement of them rather than another implementation.


**Five ways in, under one button.** The composer used to carry a bare send arrow with a row of three
conditional buttons under it — `Detect goal`, `Review`, `Detect & run` — which is four controls, three
of which appear and vanish with `CanDetectGoal` as the user types. They are now one split button
(`GoalTileView.axaml`, the composer's `run-split`): a primary segment whose label follows the box
(`GoalTileViewModel.PrimaryActionLabel` — **Set goal** where something is typed, **Detect goal** where
the box is empty and there is something uncommitted to read) and a caret opening the five entries.
**The label follows the phase before it follows the box**, and that is a correction: the composer is up
in four phases and only two of them adopt what is sent as the goal, so in Clarify the same box sends an
answer to a round of questions and in Plan it approves or corrects a plan. A button reading `Set goal`
there names an act it does not perform — nothing is adopted, `StartFreshGoal` is never reached — which
the bare arrow it replaced could not get wrong because it named nothing at all. It reads **Send answer**
in Clarify and **Send** elsewhere mid-conversation, the tooltip moves with it (`PrimaryActionHint`), and
the menu's own `Set goal` entry is down (`CanSetGoal`) wherever the composer is answering rather than
starting.
Enter goes through that primary segment (`GoalTileView.axaml.cs`, `InputBox_KeyDown`) and **only with
something typed**: on an empty box it stays the no-op it has always been. Wired to the segment
unconditionally it stopped being one — an empty box beside uncommitted changes reads as `Detect
goal`, and a fresh tile has no transcript to discard, so `ConfirmDiscardAsync` waves it through and
a stray Enter starts a paid AI run nobody asked for. Detection is a click; the key sends what is in
the box. **Every entry keeps its own gate and none of
them is hidden or greyed out by what is in the box**: typed words beside a detection are a *scope*
narrowing it — see *Typed beside a detection, the composer becomes a scope* below — so disabling the
detect entries while the composer has text would close that route from the only control that offers it.

**`Set goal & run` is the typed twin of `Detect & run`**, and it is the one new capability the menu
brought with it. A typed goal always went through Clarify and then waited for a plan to be approved by
hand, so "I have written what I want, now just do it" had no control at all — the nearest thing to it,
`Detect & run`, reads the goal out of the diff, which is how a user who typed a feature description
with a design doc uncommitted in the tree had the *doc* adopted as "the changes that were just made".
It runs through `SubmitCore` with one argument rather than beside it (`TypedGoalStart`): the confirmation before a
transcript is discarded, the pause, the `@` scope read off the typed text and the baseline are every bit
as owed here as to the plain send. What `TypedGoalStart.Unattended` changes is only what happens after the goal is set —
Clarify is told **nobody is waiting** (`GoalPromptBuilder.BuildClarify`'s `noQuestions`, which asks for
`needsClarification:false` and carries its own worked example, since the ordinary one shows the opposite
and an example contradicting the instruction above it is the one thing a model follows — which is why
that example asks for the assumptions as a paragraph *before* the json block rather than for "one
fenced json block and nothing else": the assumptions are the only thing the user sees before a plan
written without questions, and the example is what decides whether they are written at all), and the plan is
approved as it arrives. A tool that asks anyway is not a reason to stop a run started with one click:
what it asked goes into the transcript **and** into the clarification history, so the plan is written
knowing what is still open. The approval itself is `AdoptProposedPlan`, the same step a typed "ok"
takes, so the automatic path and the hand-approved one cannot drift — and it is silent about a plan
that never arrived, since a planning run that failed or was paused has already written its own
explanation. The transcript says the plan was approved automatically, because nobody was asked.

**Review is contextual, and it is one entry rather than two.** With an empty composer it is what it
always was — work the goal out of the changes, then judge the tree against it once. With something
typed it adopts those words as the goal and judges the tree against **that**, with
`ReviewsExistingWork` set for the same reason the detect path sets it. The question the user is asking is
the same either way; reading a second goal out of the diff when they have just said what the work was
meant to be answers one nobody asked. **All three typed entries are one method** — `SubmitCore` with a
`TypedGoalStart` — because adopting a typed goal is eight steps (the confirmation before a transcript is
discarded, the blank-answer guard, the pause, `StartFreshGoal`, the scope read off the text, the
transcript entry, the baseline) and every one of them is owed whichever entry was pressed. Written out a
second time beside it they were a second set to keep in step, and had already begun to drift; the mode
carries the only thing the three actually differ in, which is what happens once the goal is set.

**Clarify is a loop with a budget, and it can be skipped entirely.** The prompt asks for `{"needsClarification": …, "questions":[{question, why, options}]}`; `needsClarification:false` plans immediately, so a goal that is already precise no longer costs a round trip and a reply. An answer goes back for *another* round rather than straight to the plan, so the tool decides when it has enough — bounded by `GoalWorkflowEngine.MaxClarifyRounds` (3, deliberately not configurable: a model that keeps finding one more thing to ask is not a setting the user should have to discover). **The questions decide whether a round is a question**, and the flag does not get a vote: a tool that says it needs clarification and then asks nothing has asked nothing, and the tile used to print the raw JSON as the question, file it in the clarification history, hand it to the planner and then wait for a reply to it. Questions are rendered numbered and the composer is **prefilled with the numbering and nothing else**, so answering is editing rather than transcribing — the tool's first option used to be filled in beside each number, which turned Enter into "send the tool's own guess back as my answer", from a box the user may not have read; the options are still printed under the question, where they are an offer rather than a default — a blank box under three questions asks the user to reproduce the numbering, and the ones who do not leave answers nothing can be matched to. Both spellings are `1.`, because an answer is matched to its question by eye. **The questions go into `ClarificationHistory` alongside the answers, labelled with who said what**: that list is joined into the next Clarify prompt and into the Plan prompt, and holding answers alone was survivable while both were prose and stopped being so the moment answers became numbered — the next round was handed `1. appsettings.json` with no record of what question 1 had been, which unties the very thing the numbering exists to tie. And an answer that is **only** the numbering is refused rather than sent (`GoalTranscript.IsBlankAnswer`): the composer is prefilled, so Enter alone used to spend one of the three rounds on `1.\n2.`. Prefilling never overwrites something already in the box, either — a round takes as long as the tool takes, and a user who spent it typing had it deleted on arrival. Questions win over a contradicting flag: a tool that says it needs nothing and then asks three questions has asked three questions. Staying in Clarify is also what makes an interrupted answer resumable — the file says Clarify with the user's message last, `WasInterrupted` reads that as a run that was cut off, and re-asking with the answer in hand is exactly what was owed.

**Review returns findings, not a verdict string.** `VERDICT: PASS` was a substring search, and it was wrong three ways: "I cannot say VERDICT: PASS until…" passed, "VERDICT: PASSED" failed, and one word carried both "the code is sound" and "the code does what was asked", so a clean implementation of the wrong thing passed. The prompt now asks for a fenced JSON block — `{"goalMet": bool, "findings":[{severity, category, file, line, title, detail}]}` — with **four severities**: `blocker`, `error` (wrong: broken, incorrect or missing), `warning` (works but should not stay — a risk, or a Clean Code/SOLID violation) and `suggestion` (never counted, never blocking). **`blocker` is the level for a change that works and still must not stand** — it breaks a stated constraint or assumption of the goal, or fails outside the case in front of the reviewer: a platform limit, a race, data loss, a security hole. Its own level rather than a loud `error`, because it answers a different question — an error says the code is *wrong*, a blocker says it is *unacceptable* — and forcing that into the other two made the choice bad either way: "error" claims something is broken when it demonstrably runs, "warning" invites it to be tolerated. It is also the one severity with **no threshold, in the panel or anywhere else**: a tolerance for blockers would be a setting whose only use is to ship what the reviewer said must not ship. The prompt draws the line explicitly and tells the tool not to reach for `blocker` to add weight to an error. `goalMet` is a **separate axis rather than a fourth severity**, because "no bugs" and "does what was asked" are different questions. The user's thresholds are deliberately **not** in the prompt: a reviewer told one warning is allowed has been told how to pass, and the severities are the one thing in its answer nothing else can check.

`GoalResponseParser` is where the interesting half is — what happens when the shape is not there. It takes the **last** fenced block (a tool routinely shows an example or a diff first), accepts fences of four or more backticks (which is what its own prompt emits), falls back to an unfenced object, and treats a block with neither key as not-a-review. **A schema is a request, not a protocol**: prose falls back to the `VERDICT: PASS` rule and an empty finding list, which is how this tile behaved before — and **the review prompt asks for that line as well as the block**, because a fallback whose trigger phrase nothing requests is not a fallback: while nothing asked for it, a tool that ignores the schema could never say the goal was met and burned the whole budget on every goal. The rule itself is no longer a substring search but *what the last verdict-bearing line ends with*, so "I cannot say VERDICT: PASS until the null check is fixed" fails while "Everything checks out. VERDICT: PASS" passes — the first of those is the sentence this feature was built to stop passing, and it matters more now that the prompt invites tools to quote the phrase. It asks whether the `findings` **key** is present, not whether the list has anything in it: asking about the count read `{"findings": []}` — a clean review, and the shape of every successful one — as no review at all, dropped it into prose parsing, found no `VERDICT: PASS` in a reply that had been asked for as JSON, and marked the goal unmet on every attempt for ever. A structured review that omits `goalMet` still does not claim the goal is met, but says so in the transcript rather than spending the budget in silence. **A review that came back as prose always honours its verdict, whatever `RequireGoalMet` says**: findings only exist for a structured review, so with them empty every count passes on any answer at all — turning the requirement off in front of a tool that ignores the schema removed every gate at once, and a goal finished on its first attempt over a review that said it had failed. The setting relaxes one criterion among several; it cannot relax to nothing. An unrecognised severity becomes a **warning**, not a suggestion — guessing downwards lets an unexamined label through a "no errors, no warnings" gate. A quoted boolean (`"goalMet": "true"`) counts, because a model shown a schema in prose quotes values as readily as keys. And the fence regex tolerates `\r`: `$` in multiline mode matches before a line break's `\n` but *after* its `\r`, so a CRLF answer with one more word after the block matched nothing and fell silently back to the substring rule — on the platform most of this runs on.

Only `error` and `warning` findings go back into the next implement prompt (`GoalTranscript.Feedback`). The whole review used to, nits and prose included, so an attempt could be spent renaming a variable while the null dereference above it stayed where it was.

**Each attempt starts with a fresh tool process, and that is deliberate for the review and a cost for the implementation.** A reviewer that remembers writing the code it is judging is a reviewer with a reason to like it, so the review's amnesia stays. The implementation's does not come free: the code survives (it is on disk and in every prompt) but the *reasoning* does not, so attempt 2 could rediscover the dead end attempt 1 backed out of, and an oscillating review (X→Y on one lap, Y→X on the next) is invisible to `LastReviewFingerprint`, which only compares consecutive reviews. The answer is **structured note-taking**, not a persistent tool session: only two of the four seeded tools can resume one at all (see *Session resume*), a window carrying five to twenty full diffs recalls worse rather than better, and a session living in a tool's own database is exactly what `GoalStateStore` exists to not depend on. So the implement prompt asks for one closing line of what changed **and** one starting `Rejected:`, the engine keeps the last five (`AttemptLog`, capped per entry, persisted, cleared with the goal), and the prompt carries them back under *What earlier attempts did and decided*. The note is what the tool is asked to *finish* with, so `RecordAttempt` keeps the **end** of the answer, not the beginning: it took the first 300 characters at first, which filed the preamble ("I'll start by reading Cart.cs") and cut off both asked-for lines every single time — the mechanism recorded everything except what it had asked for, and the tests missed it because they fed short strings and one long run of a single repeated character, where head and tail are indistinguishable. It now takes the `Rejected:` line and the line before it, falling back to the tail. When the prompt will not fit, the **working tree gives way before the note** — a reversal of the first rule written here, and the argument is recoverability: these tools run in the workspace with their own tools, so a dropped diff is one `git diff HEAD` away, while a note about a path an earlier attempt tried and abandoned is recoverable by nothing at all. `Fit` only descends that ladder on a large working tree after several attempts, which is exactly the run where the note is worth most and was the only one in which it was being thrown away. The prompt also says which attempt this is out of how many: a model that does not know it is nearly out of attempts keeps experimenting, when the last one should be the safe version.

**Completion criteria are set on the tile** (`GoalCompletionCriteria`, persisted in `GoalTileState`, edited through `GoalCriteriaEditor` — its own view model, because running a workflow and editing a handful of settings are not the same job — panel behind the tune button — plain text boxes rather than `NumericUpDown`, because the panel is drawn as a config file and a spinner is the one control that would make it a dialog; the cost is that nonsense typed into a number field is ignored rather than refused, which the bounds at the point of use absorb — and where those bounds bite, the attempts row says so beside the field rather than snapping the value back, which would fight anyone typing "10" one digit at a time). Edits are **debounced and never written over nothing**: these are text boxes, so a handler runs per keystroke, and saving on each would both stutter and create — on a tile nobody has set a goal in yet — the empty session file in the user's repository that `Dispose` and `NewGoalAsync` both go out of their way to avoid. Criteria set before a goal exists live in memory until one does. The settings themselves: tolerated errors (0) and warnings (0), whether the review must say `goalMet` (yes), attempts (5, was a `const`), **which SOLID principles apply** (all five) and **whether the project has to build and its tests to pass** (both yes; see the paragraph below). The defaults reproduce the old behaviour for anyone who never opens the panel. Blockers have no tolerance and no field: a blocker is the finding that says the change is unacceptable rather than merely wrong, and a setting for those would only ever be used to ship something the reviewer said must not ship. **The project builds** and **its tests pass** are the two criteria that are not scored out of the review's findings: they are stated in the implement and review prompts, and the reviewer is told to establish them by running this project's own commands rather than by reading the diff. A failure the changes caused comes back as an `error` finding and is counted like any other; one that was already failing does not, and is reported in the reasoning instead.

**SOLID is per goal, not per installation.** The five principles are five switches on the criteria panel, drawn as the letters `S O L I D` — a row of chips rather than five checkboxes with five long names, which would have been a third of the panel for a setting most goals never touch. The acronym is already the interface: the letters spell what the row is, each carries its full name in a tooltip **and as its accessible name** — a tooltip is a pointer gesture, so without the second one anything reading the panel aloud announced five buttons called S, O, L, I and D, and the letters only spell something to a reader who does not need them read out — and lit means *held to it*. All five are lit by default, so nobody who never opens the panel loses a rule; unlit is the deliberate act, which is why the off state is the quiet one — a letter switched off should read as absent from the word rather than as a control waiting to be filled in. Hovering changes the **border only**: written as one `:pointerover` the pointer lit the letter up, so an off chip under the cursor looked exactly like an on one at the only moment anyone is looking closely at it — while deciding whether to click. Each chip also **carries its own principle** rather than being matched to one by position; the row and the catalog were walked in step by index, which is correct only while two lists stay the same length in the same order, and one insertion would have lit the wrong letters and written the answer onto the wrong principles without failing anything.

They live on the goal rather than in Settings because that is where the answer differs: the same person wants all five in a library they will maintain for years and none of them in a one-page script, and a global switch would make them choose once for both. They are on the *criteria* panel, beside the tolerances, because they are a completion criterion in the only sense that matters — a violation comes back as a `warning`, against a tolerance of zero by default, so switching one off is the difference between a run that finishes and one that spends its remaining attempts arguing about an abstraction the user does not want.

What the prompt says is the load-bearing part, and it is not simply the list. Every principle that is on is now **stated outright** — the constant this replaced named two of the five and waved at the rest with *especially*, leaving the reviewer to decide for itself what it was reviewing against. Every principle that is off is named as **out of scope**, because silence is not the same as switching it off: a model reviewing C# reports a fat interface whether it was asked to or not, and the finding lands as a warning against a tolerance of zero. With all five on there is nothing to exclude and the sentence is omitted; with none on there is no list for "the ones not listed" to point at, so the exclusion is stated over the whole of SOLID — and the one sentence that gives the reviewer a reason to reach for `warning` stops naming SOLID too, so the review prompt cannot contradict the scope it was just given. Clean Code is not one of the switches and is in every one of these prompts regardless.

The rules go into the **plan, implement and review** prompts and are fixed overhead there — never trimmed by `Fit`, like the answer-language line, and one line per principle for that reason. The builder reads the switches through a function rather than a captured value: `GoalWorkflowEngine.Criteria` is replaced wholesale by every keystroke in the panel and the builder outlives it, so a value read once would have a change to this row take effect on the next tile instead of the next attempt. `SolidPrincipleCatalog` is the one map from a principle to its letter, its name, its prompt line and its switch — the model, the panel, the prompts and the tests all read it, because five members spelled out in four places is the shape where three agree and one does not and a chip silently does nothing. `SolidPrinciples` is five plain booleans rather than a `[Flags]` enum, and that is a serialisation decision: enums in these files are written as names, and the tolerant readers that stop an unknown name destroying a goal file are built on `Enum.IsDefined`, which answers false for every combination of flags that is not itself a declared member. A goal file written before the row existed has no `Solid` key, keeps the initialisers and comes back with all five on, which is what it ran under.

**The build and the tests are asked for in the prompt, not run by the tile.** Two switches on the criteria panel — *the project still builds* and *the tests still pass*, both on — put a sentence into the implement and review prompts saying what has to be true when the work is done, and leave the *how* to the tool. It is standing in the repository and knows how this project is built; the panel is not and does not.

What this replaces was a **verify command**: the clarification round proposed a shell line, the user approved it in a consent dialog, the tile ran it after every attempt, and its exit code was a hard gate on completion — the one criterion that was not the tool's opinion of its own work. The argument for it was sound and the premise was not. It only ever worked in a repository that was already green, and a project whose suite has failures nobody has got to yet is the ordinary case rather than a broken one. There the gate spent every attempt of every goal on failures the work had not caused, and then reported the goal as not reached. Around it sat a shell command proposed by a model, a dialog to approve it, a provenance flag deciding which of two questions to ask, a runner, a timeout, a stop reason, a reserved finding category and a Continue path that existed only to recover from it — all of it downstream of one number the tile could not interpret.

So the checking moves to the tool, and the two questions are split because the answers differ: a red suite is a thing a user may reasonably want left alone while still asking for code that compiles. The sentence that does the work is the one about **pre-existing failures** — *a failure that was already there before you started is not yours to fix: say so instead* — for the same reason the out-of-scope sentence in the SOLID rules does. A tool told the tests must pass, in front of a suite that was already red, goes and fixes somebody else's tests with the attempts meant for the goal. Nothing here reaches a shell that the tool was not already free to run, so there is no consent gate left to ask about.

**The plan is asked for the user's goal tightened, not expanded.** This phase's characteristic failure
is an essay: the goal restated at four times the length, steps grouped under invented headings, and each
one annotated with the principle it serves — the last of those invited by the quality rules being in the
prompt at all. It matters more than it reads, because the plan is what the user approves and what every
implement prompt then carries: scope invented here is scope the run spends its attempts building, and a
goal restated more grandly than it was written is the tile agreeing to something nobody asked for. So
the prompt says it three ways — restate the goal *only tighter*, add no scope or detail the user did not
give, invent no files or constraints, name no principles and justify no steps — and `HealthRules` is
deliberately left out of this one, where it only ever came back as two more steps saying "run the
tests".

**Detecting a goal answers in the language this machine is set up in.** Every other prompt ends with
"answer in the same language as the goal above", which is free and impossible to get wrong. This one is
reached from the + button over an uncommitted working tree: nothing has been typed, so the only thing in
the prompt is a diff, and a diff is written in English whoever wrote it. The answer came back in English
and went straight into the composer *as the user's own goal* — where every later prompt anchored on it,
so one phase with nothing to read from set the language of the whole run.
`GoalPromptBuilder.AnswerInSystemLanguage` reads `CurrentUICulture` — the display language, what Windows
shows its own menus in and what `LANG` says on Linux, not `CurrentCulture`, which is dates and decimal
separators and would answer a machine with English menus and Polish formats in Polish. Named in English
("Polish", from the neutral culture's `EnglishName`, rather than a tag or "Polish (Poland)") because the
rest of the prompt is. English and the invariant culture ask for nothing: the prompt is already in one
and the machine did not answer the question in the other.

**The working tree is not the tool's to undo, and a snapshot is taken in case it does it anyway.** This
is the one data-loss failure this tile has had, and it was nobody's mistake in particular. The review is
handed the whole of `git diff HEAD` under the heading *the changes that were just made*, which is a claim
the tile cannot support: the user works in the terminal tiles next door while a goal runs. A reviewer
shown somebody else's parallel change reported it — *unrelated changes glued onto this one* — as a
`warning`; `GoalTranscript.Feedback` passed it back verbatim under *Fix these findings*; and the next
attempt did the only thing that makes such a finding go away. It reverted the user's files and deleted
the ones they had not committed. Every link behaved correctly, which is why the answer is three
sentences and a photograph rather than a condition somewhere.

`GoalPromptBuilder.OtherPeoplesWork`, in the implement prompt, stops the finding being *acted on*, and
its last clause — *say so in your closing line instead* — is the load-bearing part, by the same argument
as the pre-existing-failure sentence in `HealthRules`: forbidding the repair without offering a way past
it leaves the tool holding something it may neither ignore nor fix. It is unconditional, because it
forbids an action rather than asking for a judgement.

**The better fix was to stop lying to the reviewer, and the sentence that warned it is now a fallback.**
The block was headed *the changes that were just made* over `git diff HEAD`, which is everything the user
left uncommitted from any time at all. Where the goal has a baseline it is read as **that baseline's tree
against a tree written now**, so the block is what happened during the run. That is still not only the
tool's work — the user goes on working next door — but it is a window of minutes rather than one of
weeks, and the difference matters because of what it lets us delete. `OtherPeoplesWorkInReview` asks the
reviewer to make a distinction it has no data for, and the two ways of getting that wrong are **not
symmetrical**: a finding it invents is in the transcript where somebody sees it, while a finding it
swallows leaves no trace anywhere. Trading a loud error for a silent one is a bad trade, so the sentence
survives only where the block really is `git diff HEAD` — a repository whose baseline could not be taken
— and there it is still owed, since a `scope` warning against a tolerance of zero blocks every remaining
attempt over files nothing in the run will ever touch. `WorktreeSnapshot.Scoped` is what carries the
answer from the read to the prompt.

**Tree against tree, never `git diff <baseline>`.** That form looks right and is not: a file untracked
when the baseline was taken is in the baseline tree but not in the index, so git reports it **deleted** —
measured — while it sits untouched on disk, and a tool told a file was deleted may go and put it back.
Two tree objects have no index between them. It also **replaces** the untracked listing rather than
adding to it: a file with no history is in both trees, so the diff shows one the run created as an
addition with its contents, which is strictly more than a bare name. Detection is deliberately left on
`HEAD` — it asks what the user is in the middle of, which *is* their uncommitted work, and it runs before
a goal exists, so the only baseline in reach belongs to the goal being replaced.

**What none of this closes.** For *the changes that were just made* to be a true label, writes would have
to be attributed to the tool's process, which this application does not watch. A per-attempt snapshot
would shorten the window to one iteration, but a per-attempt baseline re-bases onto the previous
attempt's damage and is useless for the thing the baseline exists for. That needs two refs with two
different jobs, and it is a decision rather than a correction.

Neither is a guarantee, and that is not pessimism about this particular prompt. The same failure is on
record against other agents in front of an `AGENTS.md` saying *Do NOT change any files you did not
touch. NEVER do that!!!* A prompt is a request. So `GoalBaseline` photographs the working tree as the
goal starts and the ref it writes is named in the transcript, which turns a destroyed afternoon into one
`git checkout`. It is deliberately neither a stash nor a commit: a stash *takes* the changes out, so the
tool would arrive to a clean tree and write the work again beside it, and a commit moves `HEAD` and
leaves something in the user's history to undo. Instead a copy of `.git/index` is pointed at through
`GIT_INDEX_FILE`, so `git add -A` writes to the copy — measured, `.git/index.lock` is never taken, which
is what keeps this clear of a rebase in the tile next door — and `commit-tree` makes a commit object
*beside* the history rather than in it. **Untracked files are the whole point**: `git diff HEAD` cannot
see one and `git checkout HEAD -- path` cannot bring one back, because it was never in HEAD, and a new
file is most of what an implementation produces. Four things are load-bearing and each was measured: the
index is **copied** rather than rebuilt with `read-tree` (which leaves entries without stat information,
so `add` re-hashes every file in the repository — 0.41s against 0.14s here, and that gap grows with the
repository rather than with the change); the identity is passed on the command line (`commit-tree` fails
outright with *Author identity unknown* where git has never been configured, which is the machine whose
user is least likely to have a second copy of anything); `commit.gpgsign` is turned off explicitly,
because a headless process waiting for a passphrase is the worst failure available here; and the whole
thing is bounded by ten seconds, because `add -A` hashes everything not ignored and somebody's
unignored `node_modules` must not stall the start of a goal. It is also the one place this tile prunes
anything — twenty snapshots per workspace — and that exception is argued rather than assumed: goal files
are kilobytes, while each of these holds a blob for every file that differed from HEAD, in a repository
that belongs to somebody else.

**Where there is no repository the tile says so and points at the fix.** A workspace git knows nothing
about has no way back from a deleted file at all — not even `git checkout HEAD`, which is what everybody
reaches for — so that one outcome gets a sentence, and the workspaces panel has offered **Create
repository** all along. Every other failure of the snapshot is silent and logged: it is a safety net
that did not open somewhere git still works, and a line of apology about it in a transcript about
something else helps nobody. The two are told apart by **asking** git — `rev-parse
--is-inside-work-tree` and `rev-parse --verify HEAD`, both with `throwOnError: false` — rather than by
matching on the wording of an error, which is git's to change and is translated on a localised install.
Deliberately **not** a second backup system for those workspaces: a copy-the-files baseline would need a
built-in list of what not to copy, since there is no `.gitignore` to read, and every guess is wrong in
one of two directions — the quiet one being a user who thinks they have a backup of the file they did
not. Nothing here may stop a goal starting, which is the rule `AppPaths` and `WorkspacePaths` already
follow: a snapshot that cannot be taken is a goal that runs without one.

**A finished run can commit its own work, and only its own.** The switch is on the criteria panel, it
is the only one there that starts **off**, and it is the only one that writes to the user's history —
which is the whole argument for the default. Turning it off does not turn the feature off: the same
conditions put a **Commit** button in the summary, so the ordinary way to use this is to look at what
the run did and then press it. The switch decides *who starts it*, never whether it is confirmed. It
always is: a dialog names every commit, the files, what the last review left unfixed, and what is being
kept back, because a commit is exactly the moment somebody should decide whether shipping three warnings
is all right — by then the transcript has been scrolled past. An unwired `ConfirmAction` means no
commit, the rule the rest of the application follows for anything written to somebody's disk.

**The offer needs zero blockers and zero errors, not a met goal.** A run that spent its budget over three
warnings has still produced work worth keeping, and hiding the button there hides it in exactly the case
where the user most wants to decide for themselves. A review has to have *run*, though — with no counts
at all nothing has looked at this work, and "no errors" would be a claim about an examination that never
happened.

**"Only its own" is a real guarantee against runs that do not overlap, and it is the pair of snapshots
that makes it one.** A run is bracketed: `GoalBaseline.CaptureAsync` photographs the tree as the goal starts
and `CaptureEndAsync` photographs it again as the run reaches its summary, each a commit object beside
the history. `GoalCommitter` then asks git four tree-to-tree questions — what changed *during* the run
(`baseline` against `end`), what the user had already changed before it (`baseline^` against `baseline`),
what anybody has changed *since* it finished (`end` against now), and what is still uncommitted at all
(`HEAD` against now). Tree against tree because the index belongs to the user and untracked files are
invisible to most of the alternatives. **Without a baseline there is no commit at all** — a run that
could not be snapshotted cannot tell its own work from anybody else's, and "commit whatever is dirty" is
the exact mistake this part of the tile exists to stop making.

**The boundaries are times, not authors, and that is the limit of the guarantee.** Everything above
asks *when* a file changed and nothing asks *who* changed it, which answers the sequential case
exactly and the overlapping one not at all. Tile A takes its baseline; tile B starts afterwards,
finishes and writes files; tile A finishes later. B's files fall inside A's `baseline`–`end` window, so
they are in what A "changed"; they are uncommitted, so they survive the filter; and they are in neither
held-back list — `theirs` covers only what was dirty *before* A started, and `touchedSince` only what
moved *after* A finished. A's commit takes them.

The unpleasant part is that this case is confident: `Bounded` is true, so the dialog does **not** print
the paragraph about another Goal tile's work being indistinguishable — the very sentence that would
apply. It is still better than it was, since before the closing snapshot every commit had this problem
and the sequential case is now closed; but it is a limit rather than a guarantee, and
`GoalBaselineTests` pins both — the sequential case it does hold for, and the overlapping one it does
not. Closing it properly means recording *which files this run's own attempts wrote*, rather than
bracketing them in time.

**Both ends, and the upper one was missing.** With only a baseline, "what this run changed" means
"everything that has changed since it started" — which is the run's own work in a workspace with one
Goal tile in it, and the work of every tile in a workspace with three. Measured the hard way: three
tiles finished, Commit was pressed in the first, and all three runs went into the history under the
first one's messages. The closing snapshot is the run's upper bound; what moves after it belongs to
whoever moved it. The two snapshots live in **separate ref namespaces** (`refs/mtiles/goals/` and
`refs/mtiles/ends/`) because the prune keeps the newest twenty of whatever prefix it is given, and
sharing one would halve the baseline history a lost afternoon is recovered from.

Files this run wrote but will not commit are **named and left alone**, because `git commit -- path`
takes the whole file rather than the part this run wrote: committing one would sweep somebody else's
work into a message about something else. There are two such lists and they are kept apart, because the
sentence differs even though the consequence does not — files the user had already changed *before* the
goal started, and files somebody changed *after* the run finished. A goal file written before any of
this was recorded has no closing snapshot; the scope then reaches up to the tree as it stands, exactly
as it used to, and the confirmation says so rather than quietly claiming work it cannot account for.

The tool decides the *grouping*, because that is a judgement about meaning — which change is a feature
and which is the chore that made room for it — and nothing here can make it; grouping by directory
produces a history that is technically a series of commits and tells nobody anything. What it may not
decide is which files: `GoalCommitPlan.Sound` holds every path against the scope and drops the rest, and
a path nothing claimed goes into a `chore` at the end rather than being lost — the worst available
outcome is a run whose work is split between the history and the working tree with nothing saying so.
A file named twice is committed once, since git would put it in the first commit and leave the second
empty. An answer with no plan in it commits **nothing**, and there is deliberately no prose fallback
here as there is for the review: a review must be given a verdict or the run cannot continue, while a
commit that cannot be planned can simply not be made.

`git commit --only -F <file> -- paths` is what runs, once per block. Measured: it commits those paths from
the working tree and leaves everything else in the index where it was, so a file the user had staged in
another tile is still staged afterwards — without `--only` the commit would carry it under a message
about the goal. An untracked path needs `git add -N` first, because `--only` refuses a path git has never
heard of, and a new file is most of what an implementation produces; a refused commit takes those
intent-to-add entries back out, or the user is left looking at files git claims are staged and empty.
The message goes through a file rather than `-m`, and there the difference is correctness: it is
written by a model from a prompt carrying a working tree, and `GitCommandRunner` builds one
command-line string, so a subject holding an unbalanced quote or ending in a backslash would be
mangled or would rewrite the rest of the command. Nothing passes `--no-verify` or `--no-gpg-sign`: a pre-commit hook rejecting this work is the repository
saying no, and the answer is to report it, which is also why the whole thing is bounded — a signing key
with a passphrase and no agent would otherwise wait for a prompt nobody can see. The run stops at the
first refusal rather than pressing on, so what is left is a prefix of the plan with every commit of it
whole, and the transcript lists what was made along with `git reset --soft HEAD~N`.

**A review on its own is a third way in.** The **Review** button shares the detect buttons' first half —
work the goal out of the uncommitted changes — and then judges the tree against it **once**, changing
nothing. It is held to exactly the rules a review inside the loop is, because it is the same prompt: the
SOLID switches and the two health checks live in `ComposeReview` and are read fresh. `RunReviewOnlyAsync`
is deliberately **not** the loop with a flag on it — `docs/GOAL.md` records that every flag added to that
method was added after its own bug, and all of its complexity is about iterating, which a single review
has none of.

It gets its own stop reason. `GoalStopReason.Reviewed` exists because the summary is the sentence the
user reads and none of the other four is true: the closest, `BudgetSpent`, reports a budget running out
where none was ever in play. The run also **records the review's feedback**, though nothing here will
read it — Continue is offered next, and without it the first implement prompt would start over a tree
that had just been reviewed knowing nothing of what was found.

Two buttons follow it. **Re-review** judges the tree again against the *same* goal and deliberately does
not derive a new one: the reason to press it is that something has just been fixed by hand in the
terminal tile next door, and re-deriving from a changed tree answers a different question each time.
**Continue** enters the loop, and it needs no arithmetic of its own — `AttemptsContinueWouldAdd` answers
zero while the budget still has attempts in it, and a review-only run has spent none, so nothing is added,
the label stays a plain *Continue*, and the loop runs the attempts the panel defines.

**The tree for that review is read against `HEAD`, not against the baseline**, and the same correction
applies to the first lap of **Detect & run**. Both judge work that *predates* this run — it is why they
were started — while the baseline was taken moments earlier, so `diff baseline..now` came back empty and
the reviewer was handed a prompt with no working tree in it at all. Scoping is right for every lap after
the tool has been working and wrong for the one that opens on somebody else's changes.

**Everything a finished run offers sits in one bar, and the bar says nothing.** Re-review, Commit and
Continue each appear on their own condition, right-aligned, with no prose beside them. They used to be a
strip each with a line of explanation — *The attempts ran out.*, *This run's changes are not committed.*
— and a third would have stacked three sentences over the composer, each restating the summary message
printed directly above them. That is the same fault the status strip's four idle labels had, and it is
fixed the same way: the tooltips carry what a button does, which is the part not already on screen.

**A run that ran out of attempts can be given more.** The Summary offers **Continue**, which adds as many attempts as the attempts field currently says, re-enters the loop and keeps the transcript. `Met` has nothing to continue towards and `NoProgress` has just established that two reviews running found exactly the same things, so neither offers it: the button there would sell AI runs whose outcome is already known. Continue re-enters at the **implementation**, not the review: the last thing the run did was review, and starting there again spends a run re-judging a working tree nothing has touched since. **The label carries a figure in both states, and the `+` is what tells them apart**: `Continue · +2` raises the ceiling by what the attempts field says right now, `Continue · 5 left` spends what is already in the budget and adds nothing. It used to be a bare `Continue` in the second case, which read exactly backwards — no number where five attempts were waiting, a number where none were — so the available reading was “no figure means nothing left”.

**`NoChange` offers it too, and used not to.** The argument against was that the tool wrote nothing, so another attempt would write nothing again — and the summary said so out loud ("the last attempt changed no files, so the same prompt would change none again"). That prediction holds on neither path that reaches this stop. Where the attempt was *refused*, the summary itself says to change the permission mode and try again, and Continue is that retry with the transcript kept. Where it was not, the unchanged tree is reviewed on the way out and those findings go into the next implement prompt, so the next attempt is handed something this one was not. It is also the stop that most often arrives with the budget **unspent** — an empty attempt ends the loop whatever is left in it — so refusing here left a run with an unmet criterion, attempts still owed and no route at all to spend them but retyping the goal. The summary now states the fact and predicts nothing ("Stopped after 4 attempts: the agent changed no files. Still outstanding: 1 warning (limit 0)."), and it is drawn a step brighter than the tile's other notes (`GoalMessage.IsRunSummary` → `msg-summary` at `TextSecondary`), because an agent that had an unmet criterion in front of it and wrote nothing anyway is the thing to look at. Whether it disagreed with the finding or simply missed it is not something this application can tell, and one button is a cheaper way to find out than starting again.

**How hard the tool is asked to think is the tile's to say too, and the default is not the tool's.**
The strip carries an effort level beside the permission mode (`AiEffort`, mapped to `claude --effort`
by `AiEfforts`), and it defaults to **high** rather than to whatever the tool would choose on its own.
The argument is the budget: this loop is measured in attempts, and an attempt spent on a shallow answer
costs exactly as much of it as a careful one — while the tool's own default is tuned for interactive
use, where a person is watching and can redirect after two sentences. Nobody is watching here.
It lives in `settings.json` beside the permission mode and for the same reason, and needs no
confirmation of any kind: unlike `bypass`, the worst it can do is cost time and tokens, both visible
while they are being spent.

Measured, and the two failure modes are not alike. An unrecognised **value** is forgiving — *Warning:
Unknown --effort value 'bogus' — ignoring it and using the default effort* — so nothing here has to
guard the spellings. An unrecognised **flag** is fatal: a Claude Code from before `--effort` existed
answers *error: unknown option* and runs nothing, so every goal on that machine fails, on the default
setting, over a flag the user never typed and cannot see. That is the trap `AiPermissionModes` was
already built around, which is why `ToolDefault` exists here as well and why
`AiEfforts.LooksLikeRejectedEffort` matches the flag's own name **plus** a word saying it was rejected
— never either alone.

**The status strip says nothing about the states something else already explains.** Four sentences came
off it — *Waiting for goal…* over a composer whose placeholder says exactly that, *Waiting for your
answers.* over a panel of question boxes, *Waiting for your approval.* over a button labelled Approve
plan, and a summary line over a summary. Each could only repeat the control beneath it, on a strip that
also carries the tool, the permission mode, the effort and the finding badges — and the phase itself,
which the dot says in colour. What a run is *doing* is said by the waiting row at the foot of the
transcript, where it lands beside the dots that say something is happening at all, and disappears with
them. The **paused** labels stay, and they are the exception that shows the rule: that is the one state
nothing else on screen accounts for.

**Resume is a labelled button in the conversation, and nothing else.** It began as a play glyph in the
header, which put the one action a stopped run needs at the far end of the tile from
the sentence naming it — the transcript ends "This run is stopped. Click Resume to continue it", and the
Resume it names was 13 pixels of icon in a strip of six, filed nowhere near the questions, the plan,
Continue and Commit, which are how everything else this tile asks for is answered. It now sits first in
the finished-run row (`GoalTileViewModel.ShowResume`, so `HasFinishedRunActions` brings the row up for a
run that is merely stopped as well as one that has finished), and that row moved **under the composer**,
beside the detect row: everything the tile offers is one group at the foot of the conversation, in the
order a chat window puts them. Above the box it was a Resume floating between the transcript and an
input it had nothing to do with, with the two rows of buttons separated by the box. The header's play
glyph is **gone** — once the command is a labelled button in the flow, a thirteen-pixel unlabelled
second route at the opposite end of the tile is not a fallback, it is a thing to explain. Pause stays
there, because what it interrupts is happening now and must be reachable however the transcript is
scrolled.

**The composer stands down in Implement and Review.** There is nothing for it to send: `Submit`'s case
for those two phases hands the text back and says the run is stopped. It was kept on the reasoning that
a phase with no composer and no explanation is a tile that has stopped responding — right about the
explanation, wrong about the box, and now the explanation is a button. It stands down while a round of questions or a plan is on screen: Resume
re-runs the phase, which there means asking again, and answering the block already clears the pause.

**Two properties, one button**: `ShowResume` is whether it is on screen (paused, in a phase
`ResumeAsync` has something to run, with no block already asking) and `CanResume` adds `!IsRunning`, so
a pause that is still unwinding shows the button disabled rather than making the user chase one that
vanishes and comes back. The phase half is what the header's glyph never asked — it read `IsPaused`
alone, which is true in `Goal` (closing a tile mid-detection pauses it there), so a reopened tile
offered an enabled ▶ whose only effect was to clear the pause. That stale pause is now cleared by the
next thing typed, which is what the `Goal` phase is waiting for anyway.

**How much the tool may do without asking is the tile's to say.** The strip carries a permission mode
beside the tool name (`AiPermissionMode`, mapped to `claude --permission-mode` by `AiPermissionModes`),
and it defaults to **auto**. It used to pass no flag at all, so every run inherited whatever the user's
own Claude Code settings happened to be in — and the factory default there is to ask, which a headless
`-p` run has nobody to do: every edit was refused, the implementation wrote no files, and the tile
stopped with "the last attempt changed no files". A true sentence about the wrong thing, and one that
sent the user looking at their goal rather than at their permissions. `ToolDefault` is the way back to
that inheritance for somebody whose configuration already says something deliberate, and it is the only
mode that adds no flag. The setting lives in `settings.json`, **not** in the goal file, for the reason
a note about this session is not persisted either: goal files live in `.mtiles/goals/` inside
user's repository and travel with a branch, so a stored `bypassPermissions` would be a checked-in
instruction to run somebody else's agent unattended.

**A refused tool call is counted, and it changes what a `NoChange` stop says.** A denial arrives as a
user turn carrying the `tool_result` — not as an error line — so nothing in the stream reader used to
see one, and a run refused every edit came back looking like an agent that had read some files and
decided against changing anything. `ClaudeAgent.ParseLine` now emits `AiChunkKind.Denied` for
those blocks and `AiOutput.PermissionDenials` carries the count out, on the streaming path only, which
is the path a Goal run takes. The `is_error` flag is the gate and the wording is the test, because
every failed command and missing file sets that flag too: a false denial would tell a user their
permission mode is wrong when it is not, while a missed one only leaves the old message in place. Where
the count is non-zero the summary names the cause and points at the strip instead of declaring the goal
a dead end — the worktree looks identical in both cases and the reader's next move does not.

**A broken block is repaired here first, and only then asked about.** Every block this tile reads is
composed as text by a model rather than serialised by a library, and the characteristic way that fails
is a double quote inside a string value that was never escaped — measured twice: a finding's detail
quoting a line of C# (2026-09-01), and a clarify round quoting the document it was reading, where the
Polish quotation `„z pytaniem do użytkownika"` closes on an ordinary double quote and ends the JSON
string in the middle of a sentence (2026-09-03). `JsonRepair` is one pure rule inside
`GoalResponseParser`'s own parse, so it serves the review, the clarification, the commit plan and the
detected goal alike: walking the candidate, it escapes a quote inside a string unless what follows it
is somewhere the grammar could go on — `:`, `}`, `]`, or a comma **with a value after it**, which is
the test that tells `…",` at the end of a value from `…", ale …` in the middle of a sentence — and
escapes raw newlines and control characters the same way. It is asked only for text that has already
failed to parse and its answer is used only if it parses, so it can turn a failure into an answer and
never the reverse. What it deliberately will not do is close an answer that was cut off part way
through: the brackets that would make the fragment legal are content nobody wrote, and inventing them
would turn a visible failure into a review with findings quietly missing from it.

**A block the rule cannot mend gets one repair round, and both phases get it now.** The reviewer is asked for a JSON block, and a
block that is not valid JSON — measured live 2026-09-01: a reviewer answering in Polish put a C#
interpolation with its own unescaped quotes inside a finding's detail — dies for every reading this
tile has, fenced, balanced or spanned, and its verdict then falls to the prose fallback, which can say
the opposite of what the JSON said. When the raw answer still *looks* like the requested shape
(`GoalResponseParser.LooksLikeJson`: the keys are there, whatever the syntax), the review phase makes
**one** more AI call — `GoalPromptBuilder.BuildJsonSalvage`, which sends **the answer alone** back to
the tool: no goal, no diff, a few hundred characters rather than a re-run of the phase, because the
only reader who knows what the block meant is its author. The re-send is asked to be strictly valid —
every quote inside a string escaped, every newline as `\n` — and the review prompt itself asks for the
same escaping up front — as, since 2026-09-03, does the clarify prompt, which is the phase most likely
to need it: what a round quotes is the user's own goal in the user's own language. **The clarification
runs the same round**, and its absence there was a hole rather than a decision: a broken review at
least falls back to a prose verdict, while a broken clarification was printed into the transcript as
raw braces for the user to answer and filed in the clarification history for the planner to be handed,
so the block nobody could read became part of what the plan was written against. **It is one round
and not one per phase**: the re-send reads nothing but `IGoalParsedBlock` — whether the block parsed
and the text it was read from — so the entry rule and the fallback cannot drift between two copies,
and a phase that wants the round is given it by passing its own parser. If the re-send fails
too, the original behaviour stands — the raw text is what the transcript shows and the prose fallback
answers — and the round never announces a failure of its own (`RunAiAsync`'s quiet path), because the
phase it belongs to succeeded; only its JSON did not. Prose answers that never named the keys earn no
second call.

**An unchanged worktree still gets a verdict.** A `NoChange` stop used to be the end of it: the
implementation wrote nothing, the loop ended, and the summary declared a dead end — even when the
attempt's own account, right there in the transcript, said the tree already held the work ("everything
the plan asked for is already in place, odrzucam przepisywanie od zera", measured live 2026-09-01, over
a goal whose plan the user had just approved). The dead end and the finished goal leave identical
worktrees and want different sentences, and the tile has an arbiter for exactly that question. So where
there were no refusals, the empty worktree goes to the **reviewer once** (`ReviewUnchangedTreeAsync`,
unscoped — the work predates the run): met, and the stop is `Met` — "Goal completed after 1 attempt"
over a goal that was done; not met, and the `NoChange` sentence carries the review's outstanding list.
Once, either way — Resume cannot re-run the review against a tree nothing has
touched. A refused run skips the call entirely, because a review of work nobody was allowed to do would
answer what the summary already says.

**And where that review says something new, the loop spends the next attempt rather than asking for a
click.** The whole argument for stopping on an unchanged tree is that the same prompt over the same
tree gets the same nothing — which holds only while the prompt *is* the same. The commonest way into
this stop is an attempt opened over a tree that already holds the work: a goal detected from
uncommitted changes, or a plan written against them. The tool reads the tree, answers "this is already
done", writes nothing, and the review that follows is the first thing in the run to name a defect.
Stopping there put a button in front of the user whose only job was to say *yes, carry on* — measured
twice, on two unrelated goals, and Continue fixed the finding on the very next attempt both times. So
a `NoChange` verdict carrying **structured findings the implementation was never given**, with budget
left, becomes the next attempt with those findings in its prompt. Structured only, the rule
`RepeatsPrevious` follows and for the same reason: an unstructured review's "feedback" is its own
prose, which differs from the last one by a comma and would hand the loop a fresh question every lap.
Bounded by construction — an attempt that again writes nothing comes back here holding the feedback it
was given, which is now the feedback it has, and stops as the dead end it is.

**And that verdict is carried, not thrown away.** The review of the unchanged tree produces findings
like any other, and they are recorded (`RecordReviewFeedback`, with the fingerprint beside them) exactly
as a review-only run records its own. Without it the implementation **Continue** starts would begin over
a tree that has just been reviewed knowing nothing of what was found — and this is the one path where
that review is the only new thing there is, since the tree itself did not move.

**A repeat of this stop is not escalated, and that is a decision.** A continued attempt that *does* write
something and is then reviewed to the same conclusion stops as `NoProgress`, which offers no Continue. One
that again writes nothing comes back through `ReviewUnchangedTreeAsync`, where nothing asks
`RepeatsPrevious`, and stops as `NoChange` a second time with Continue still on offer (measured). The
escalation would have to tell *this stop repeats the last one* from *this review repeats the last one*, and
the fingerprint alone cannot: on the first no-change stop it usually matches the previous lap's review
already, because the tree did not move — so escalating on it would replace the one fact the user needs
("the agent changed no files") with a sentence about reviews. Each repeat costs one press of a button, in
front of the user, against a sentence identical to the one above it.

**The transcript follows the run, unless you are reading it.** `GoalTileView` scrolls to the end when a
message arrives, and asks first whether the reader was at the bottom *before* it did — measured on the
old extent, because the message is in the collection but not yet laid out. A run posts a dozen messages
over an hour and yanking the view down while somebody reads back through the plan is worse than not
following at all. The scroll happens twice: `UpdateLayout` forces the new message to be measured so
there is a real extent to scroll to (without it the call used the old one and stopped a message short),
and a posted one at `Loaded` priority catches a rendered markdown answer, which reaches its final height
a pass later. It hangs off the collection as well as the view model's `ScrollToEnd` hook, because a tile
reopened from its file fills the transcript without going through the hook and used to open on the top
of a conversation whose interesting end was several screens down.

**A review is drawn, not printed.** The head — the verdict, the counts, the tool's own reason where
there are no findings — is still text. Each finding is a row: a coloured left edge and a coloured
severity, the file, line and category faint beside it, the title at the transcript's own size and the
detail one step quieter under it. It was a column of monospace, which scans up to a point, and the point
is colour: a blocker and a suggestion were the same grey three lines apart, so the line worth reading
first was the one the eye had to search for. The severities take the badges' own colours, so the strip
at the top and the list below agree. `GoalMessage.Findings` carries them **beside** `Text`, not instead
of it: a message from a goal file written before this has no findings, falls through to the plain
template and is drawn exactly as it always was, and `GoalTranscript.Copyable` assembles the one string for the clipboard and for those files, from the message's own
stored text and findings rather than from a review it no longer has. `GoalTranscript.ReviewHead` and
`AppendFindings` are the two halves it is now made of.

**Three dots say the tool is working.** They sit at the foot of the transcript, where the next message
will land, while `IsRunning` is set. The strip already names the phase and the file being read; what it
cannot say is that anything is happening at all, and a tool that spends four minutes reading before it
writes leaves a transcript that has not changed and a phase label that has not moved. Deliberately not
a progress bar: that would be a claim about how far along the run is, which nothing here knows. One
animation per dot rather than one animation with three delays — two animations selecting the same
element take turns setting the same property, and the dots stop agreeing about where they are in the
cycle. Where the dots sit, and why they are a row rather than a badge, is two paragraphs below.

**Beside them, what the tool is doing and for how long** — `implementing · 2/5 · 4:07`, ticked once a
second by a `DispatcherTimer` that runs only while `IsRunning` is set. The dots say something is
happening; those two are the only other honest things to say about a tool nothing has been heard from,
since nothing here knows how far along it is. The stage is `GoalStageDisplay` and the clock
`ElapsedDisplay`, both pure and beside the other policies: the stage is deliberately *not* the strip's
sentence (two answers to one question, and the stale one is always the one nobody is looking at), and
the clock's interesting part is the two places its notation changes, each one comparison away from
being a second out. The attempt is carried on the two phases that repeat and nowhere else — a fraction
that reads `1/5` on every clarify is a number the reader learns to ignore before the loop where it
moves — and it is dropped when it would not make sense (`0/0` before the first lap, `6/5` from a budget
lowered mid-run). Time is measured with a `Stopwatch` and not two `DateTime`s: the wall clock moves —
daylight saving, an NTP correction, a laptop waking up — and a label that answers `-1:00` is worse than
no label. Both are set in the transcript's own monospace, so the dots do not shuffle sideways once a
second as the digits change width.

The two mechanical stops end runs that were already going nowhere. **They are always on, and are not settings** — they were checkboxes on the panel and are neither now. A switch earns a line there where two users would reasonably choose differently, and nobody reasonably chooses to spend attempts on a tool that just wrote nothing, or on a review that has said the same thing twice; what replaced them is the summary naming which of the two ended the run. Old goal files may still carry the fields, and `System.Text.Json` reads what it does not recognise as absent, which is the right answer now that the behaviour is not switchable. **No progress**: two consecutive reviews with the same fingerprint (severity + file + title, never the detail, which is prose and differs every run) — asked only of a *structured* review, because an unstructured one's fingerprint is the same two words on every lap and the check would otherwise cut every prose-answering tool down to two attempts and blame it on findings it never read. **No change**: an implementation that left the working tree exactly as it found it, checked with its own read of the tree, **before the review**. Reusing the tree the review is handed looked free and was wrong: that one is read after the tool has been at it, so anything that regenerates a tracked file — a build, a formatter, a snapshot test — made the two trees differ and quietly disarmed the stop in exactly the workspaces most likely to have one configured. Two short git processes against a lap costing minutes of AI, and they pay for themselves the moment it fires: there is no sense building and reviewing a change that was never made. **The criteria in force are whatever the panel says, read fresh on every lap.** Half of them used to be captured before the loop while the attempt budget was read live, so raising the attempts mid-run worked and raising the tolerated warnings did nothing — the same panel, two answers. Nothing is decided mid-lap, so a change never lands between an implementation and the review that judges it. The bounds are applied where the numbers are *used* rather than where they are typed — the panel shows what was typed, and a saved file can hold anything — and `GoalCompletionPolicy.Attempts` is the single place that decides how many attempts there are, because a second copy of the arithmetic had the summary report "stopped after 999 attempts" over a run of fifty. The summary counts the attempts that **happened**, not the budget they came out of: the two are the same number until the budget moves, and lowering it mid-run reported two attempts over a transcript containing four. `GoalCompletionPolicy` holds all of it, pure and beside `GoalLoopPolicy`, along with `WhyNotMet` — the loop now says *what* is outstanding, where "review found issues" was equally true of one warning and of nine errors over a failing build.

Findings are shown in the transcript as one scannable column (errors first) and counted as badges in the status strip (`1B 2E 1W 3S`) — one `ItemsControl` over a collection of the severities that found something, so a clean review shows nothing at all rather than four zeroes, and a severity added later needs an enum member and one style rather than a view-model property, a `TextBlock`, a visibility converter and a style, each forgettable on its own. It does not make `GoalSeverity` open — a new level still needs prompt wording describing it and a rule saying what it blocks, and no indirection supplies those — but the mechanical half is gone. Blockers and errors share `DangerText` — both mean "must fix", the letter is what tells them apart, and there is one danger token; inventing a second red for a six-pixel label would leave two shades nobody could rank at a glance, so the blocker is bold instead. Warnings take the ANSI-derived `GoalPhasePlan`, suggestions `TextFaint`. The tool's own prose around the JSON block is not reprinted underneath — it said the same things at greater length, and printing both makes every review something you read twice — **unless there are no findings**, where the prose is the only account of why and its absence left "Goal not met · nothing found" standing as the entire explanation of a failed attempt. The counts are saved with the rest of the state, so a tile paused mid-run comes back with the strip still summarising the review Resume is about to act on.

**Agent integration:** Uses `AiProcessRunner` with the `IAiAgent` interface (OCP). Which agent runs which phase is `GoalAgents`/`GoalAgentChoice`: the tile stores an `AiAgentInstance.Id` for execution and, optionally, a second for review.

**What each supported CLI actually gets**, measured from its own `--help` and pinned by
`AiToolContractTests`, because every line of it is a claim about somebody else's contract:

| tool | prompt | permission | effort | stdin | streaming |
|---|---|---|---|---|---|
| `claude` | stdin | `--permission-mode` | `--effort` | yes | yes |
| `pi` | `-p <prompt> --mode text` | — | `--thinking` | no | no |
| `agy` | `--print <prompt>` | `--dangerously-skip-permissions` (bypass only) | — | no | no |
| `opencode` | `run <prompt>` | — | — | no | no |
| `codex` | `exec <prompt>` | — | — | no | no |
| anything else | the prompt as one argument | — | — | no | no |

Three of those are worth the words. **Antigravity needed a runner rather than the generic fallback, and
getting it wrong does not fail — it hangs**: a bare positional argument opens the interactive session
and never returns, on a path that deliberately has no wall-clock timeout. Only `bypass` maps to its
one permission flag; the finer modes pass nothing rather than being rounded up to it, because asking
for *auto* and being given *nothing is asked about at all* is the one direction this must never round.
It has no effort flag either — Antigravity spends effort through the model name
(`gemini-3.7-flash-high`) — so a level here would have to rewrite whatever model the user configured,
which is a larger claim than this setting makes anywhere else.

**pi understands the tile's effort levels under its own name for them**: `--thinking` takes
`off|minimal|low|medium|high|xhigh|max`, so every level `AiEffort` names exists there under the same
word and the setting means what it means for Claude Code instead of being quietly ignored. `off` and
`minimal` are pi's alone and are not offered: a Goal run is left alone, and the tile has no level below
`low`. It has no permission control — `--approve` is about trusting project-local files, not tool calls
— so that setting goes unused rather than being mapped to something adjacent.

**opencode and codex take the prompt as a positional after a subcommand and have neither setting.**
opencode's `run --format json` would give a streaming path; nothing here reads that schema yet, so it is
a gap rather than a half-built feature.

**Open Claude has been removed, and what happens to it now is not known.** It was mapped to
`ClaudeAgent`, which since then acquired standard input, `--permission-mode`, `--effort` and
`--max-turns` — four claims about a fork nobody here has measured, and the stdin one is the shape that
hangs when it is wrong. Removing that promise is right; **"so it still runs" was not, and it has been
taken out.** It falls to `GenericAgent`, which hands the tool a bare positional argument and
nothing else — which is exactly the shape measured against Antigravity two paragraphs up, where it
does not print and exit but opens an interactive session and never returns, on a path with no
wall-clock timeout. Open Claude is a Claude Code fork, so a positional prompt with no `-p` is more
likely to start a session than a print run. Nobody has measured it, which is the whole reason it lost
its runner, and that cuts both ways: the honest statement is that it is no longer promised Claude
Code's contract and is not promised anything else either.

`SettingsService.RemoveSeededOpenClaudeProfile` takes away the shell profile this application seeded,
and only that one. **A user who added `openclaude` as a custom AI tool keeps it**, and it is that entry
— not the profile — that a Goal run would launch.

**The safeguard is not this paragraph.** A document is not a guard, and the first version of this
section offered nothing else. What actually stops the hang is that **standard input is now redirected
for every tool and closed at once when there is no prompt to send it** (`AiProcessRunner.RunPlainAsync`).
Inherited, it is this application's own standard input — which in a windowed process nobody will ever
type into — so a tool that decides to be interactive does not fail, it stops, on a path that
deliberately has no wall-clock timeout. Closed, it reads end-of-input, exits, and says something the
transcript can show. That is a guard for **every** tool without a measured runner of its own, which is
every custom AI tool a user adds, rather than a special case for this one.

It is not a promise that an unmeasured tool works. Measuring `openclaude -p` and giving it a runner is
still the fix; what changed is that not having done so costs a failure the user can see instead of a
tile that never comes back.

Every runner passes its arguments through `ProcessStartInfo.ArgumentList` rather than building a
command line, so nothing here is quotable into something else; a tool nobody has measured gets
`GenericAgent`, which passes the prompt as a plain argument and claims nothing about stdin,
streaming or flags.

**No model selection.** The tile picks a tool, not a model: each tool uses its own default, because they front many providers and there is no command to list what a given one can reach (`AiProcessRunner`). `AppSettings.GoalDefaultModels` and `GoalTileState.SelectedModel` are what is left of an attempt at it — neither is read or written by anything, and they are kept only because removing a settings key is a migration. This paragraph used to describe the feature as though it shipped.

**Persistence:** State saved to `.mtiles/goals/{guid}.json` — goal text, messages, phase, tool/model selection. `TileNode.GoalFilePath` for layout persistence. **The write happens with every message and with every phase change** (`AddMessageAsync` and `SyncFromEngine`), not at the end of a phase. Messages go through a **debounce** (`SaveStateSoon`, `AppDefaults.SaveDebounceMs`), phase changes and the end of a run through `SaveStateNow`: a save serialises the whole transcript, and doing that on the UI thread for each of a hundred long answers is a hitch the user feels, while the points a restart has to land on exactly are the phase changes, which are rare. `Dispose` flushes whatever the debounce was still holding — but only when there is something to write or a file already there to keep current, since a Goal tile opened and closed without a word otherwise left an empty session in a directory nothing ever prunes — and sets `_disposed`, which stops `SaveStateSoon` arming a fresh timer afterwards: the workflow keeps unwinding after the tile is closed, and each of its last messages asked for a save that would have fired after the final flush, on a timer nobody would dispose. The timer's write is wrapped, for the reason spelled out on `SettingsService.DebouncedSave`: an unhandled exception on a thread-pool thread ends the process. The implement/review loop used to save only in its `finally`, so closing the application between approving a plan and the summary left the file holding the state from *before* the approval — no `ApprovedPlan`, iteration 0, and none of the tool's answers. Messages alone were not enough either: approving a plan moves the engine into Implement and then waits on the tool for minutes, and nothing is said in that time, so the file still read *Plan, nothing approved* for the whole of the first implementation. The save sits in `SyncFromEngine` because a phase change is exactly what a restart has to have seen. `GoalTileState` and everything it holds **refuse a null in their own setters**, the rule `AppSettings` follows and for the same reason — a property initialiser does not survive deserialisation, so `"Messages": null` replaced the fresh list and threw inside `LoadFrom`. That landed in the view model's catch of last resort, which stops the tile saving for good: a goal file with one null in it was punished more harshly than one of corrupt bytes, which is at least set aside so the tile can start again. `GoalStateNullGuardTests` walks the graph from `GoalTileState` rather than listing types. **When** the file is written — the debounce, the lock, the disposal order and the three flags that only ever say "do not write" — is `GoalStateStore`'s, not the view model's: none of it is about a view, and while it lived among the workflow it could only be exercised through a dispatcher, a headless Avalonia session and a full run of the phase machine, for rules that have nothing to do with any of those. What stays in the view model is the one thing only it can supply, the snapshot taken on the UI thread. `GoalStatePersistence` writes through a uniquely named temporary file and a move, under a lock: at this write rate the truncate-then-write window of `File.WriteAllText` is opened often enough to matter, and a debounce timer on a pool thread can ask for a save at the same moment the UI thread does. The lock covers that; the unique name covers what it cannot, since the lock is per instance while the path is not. The **whole** snapshot — engine included, not only the messages — is taken on the UI thread, or `ToState` enumerates `ClarificationHistory` on a pool thread while the workflow adds to it, and that transient race used to light the permanent "this tile could not save its state". It also tells the two read failures apart, which are not interchangeable. Temporary files older than an hour are swept once per tile, on its first save — what they are is litter from a previous run, and this directory is never pruned, so sweeping per save would have every message pay for a scan of every goal ever set and damaged copies are kept five deep, because this directory is inside the user's repository and "rare but permanent" fills one up. A file that **parses** as nothing is damaged — including one holding the four characters `null`, which deserialises to nothing without complaint and would otherwise be indistinguishable from a file that was never written: it is moved to `<name>.bad-<timestamp>` — never over an earlier copy, since the stamp is only accurate to the second and two damaged loads within one would have had the second rescue destroy the first — and the tile may start fresh over the top of it. A file that could not be **opened** — locked, a failing disk — is almost certainly intact, so it is left exactly where it is and the tile **stops saving for the rest of its life** (`_saveRefused`). A state that loads only *part* of the way refuses to save for the same reason as one that could not be opened: the catch is reached with the transcript missing, and the next save would put that emptiness on top of the session. A file that is simply *gone* by the time it is read is neither: there is nothing there to protect, so it is the same answer as no file at all — treating it as unavailable stopped a tile writing for good over a file that no longer existed. Refusing to write costs the user the session in front of them; not refusing costs them the one already on disk. Both are said out loud in the transcript, because a log line would be the only trace that a session had existed — and so is a failed **write**, once, for the same reason: the tile that cannot save is the one whose user most needs to know before they keep working in it.

**Waiting is a row in the transcript, not a badge over it.** While the tool is working the conversation
gets a placeholder message — the gutter marker where a marker goes, three dots where the text will be —
in the same shape as the message it stands in for. It began as a pill floating over the bottom-left of
the scroll area, and two things were wrong with that, only one of them cosmetic: it overlapped whatever
was underneath it, which at the foot of a full transcript is the composer the user is typing into; and
it said a second time what the status strip already says at the top of the tile, with the phase and the
attempt number, in a bar that cannot be scrolled away from. In the flow it collides with nothing, lands
exactly where the next message will, and is carried by the follow-to-the-bottom rule — which has to be
told about it, because that rule is driven by the message collection and this row is not in it
(`GoalTileView.OnVmPropertyChanged`, on `IsRunning`).

**"done when" is not all gates.** The panel groups the completion criteria under one label, and two of
them are not checked by this tile at all: `RequireBuild` and `RequireTestsPass` reach the AI tool as
sentences in the prompt (`GoalPromptBuilder`) and are judged in its own review, while the iteration and
severity limits and `RequireGoalMet` are decided here by `GoalCompletionPolicy.IsMet`. That is the
deliberate outcome of dropping the verify command — the reasoning is at `GoalCompletionCriteria.RequireBuild`
— and it is recorded here because the label reads as a promise the tile keeps, and the next person to
notice the gap will otherwise "fix" it by putting the gate back.

**Resuming an interrupted run:** there is no Continue button, and there does not need to be one. `GoalWorkflowEngine.WasInterrupted` decides, and it reads the whole state rather than the phase. `IsMidRun` names the two phases the tool works in (Implement, Review) and those count unconditionally; **Clarify and Plan cannot be answered by the phase at all**, because one value covers both *asking* the tool and *waiting* for the user's answer — calling both interrupted would have every tile ever closed at a question come back claiming to be paused, with Resume asking the same questions again. The transcript tells them apart, and the rule is stated from the other side: **these two phases wait for the user only ever immediately after the tool has answered**, so anything but an assistant message last means the answer never arrived. It used to ask whether the *user's* message was last, which was the same rule while that was the only route in; a clarification round that ends by planning on its own leaves a note from the tile last instead, so an interrupted automatic plan came back unpaused, in Plan, with no plan in it and no Resume to get one. **The tile's own notes are skipped entirely** — they are asides about the tile, not turns in the exchange, and one can land last at any moment: a note that the answer was blank, or that the saved tool is gone. Counting them was worse than it sounds, because `LoadState` writes some of them on *every* load: each restart appended one, each appended one made the next restart read an interrupted Clarify, and Resume spent a clarification round on it — a tile left alone long enough talked itself out of its own budget. `LoadFrom` sets `IsPaused` for a state that was interrupted — in the engine rather than in the view model, because it is a fact about a loaded state and there is then no second caller left to forget it — which is what the existing Resume button is bound to, and `ResumeAsync` already re-runs the loop from the top of an iteration. Without it the tile came back mid-run with nothing running and `Submit` answered *"AI is working, please wait"* for ever — the only way out being `+`, which throws the goal away. A pause the user asked for and a run the application was closed in the middle of are deliberately **not** told apart: distinguishing them costs a flag in the saved state to say something the user can already see. The label differs only by phase (`Stopped during implement…` vs `Paused.`).

For that to hold, a cancelled run must not look like a finished one. `RunAiAsync` returns null both when the tool answers nothing and when it is cancelled, and the loop used to answer both by summarising — which moved the tile to Summary, a phase `ResumeAsync` has no case for and `IsMidRun` does not recognise. Pausing an implementation was therefore a one-way door. `RunAiAsync` tells them apart itself and reports the answer as a verdict; every one of its exits goes through `GoalLoopPolicy.Judge`, so the rule has one home rather than being restated at each `return`. **Resume checks `IsRunning` as well as `IsPaused`**, and its button is disabled while the run unwinds: cancelling takes as long as the tool takes to die and Resume is offered for the whole of it, so clicking it there started a second implement/review loop alongside the first — two AI processes on one working tree, both writing the same file. `GoalTileViewModel.AiRunnerFactory` is the seam the loop is tested through — the same trick `TerminalControl.PtyFactory` gives the launch chain, and for the same reason: every bug in this loop needed a real AI process and a real worktree to reach. `GoalWorkflowLoopTests` drives Goal→Clarify→Plan→implement/review on the headless dispatcher against a stubbed tool, with `GoalAgents.Factory` standing in for detection so the tests do not pass by doing nothing on a machine with no agent installed. `RunAiAsync` returns an `AiRun` — the text and the verdict together — rather than a string plus three flags the caller had to remember to pass on; each flag had been added after its own bug, and each addition left a call site behind. `GoalLoopPolicy.Judge` turns a cancellation, an empty answer, a missing tool and a tool that threw into one of five verdicts. A cancelled run stops where it stands. **Empty** pauses too, everywhere: a tool that returned nothing once may answer the next time, which is the argument that has `Failed` pause. In Clarify and Plan it used to fall back a phase instead, and that was worse than it looked — from Clarify it landed in Goal, where the next thing sent clears the transcript and starts a new goal, so one empty reply put the session a keystroke from being thrown away. Only an **Answered** run puts anything in the transcript — asking whether the text was null instead put an empty bubble there whenever a tool replied with whitespace. Reaching a summary clears the pause, whichever way it was reached: a Summary that still called itself paused labelled the tile "Paused. Click Resume" over a Resume with nothing to do, and said so again after every restart. **NoTool** and **Failed** do the same and pause the tile — a process that would not start, or died halfway, may well work on the next click, so ending the goal over it throws away an approved plan for a transient fault; that was the same trap NoTool was in, reached through the generic catch in `RunAiAsync`. Cancellation is asked about before failure, because killing a process is a normal way to make it throw and what the user meant is the more important of the two facts. NoTool pauses the tile, because a tool that is not installed is something the user can go and install — and `RunAiAsync` scans again before giving up, since detection otherwise happens once when the tile is built. That re-scan is `RediscoverAgentsAsync`, not `DetectAgents`: the latter is a first-run routine that rebuilds the bound lists and picks a first agent, so calling it mid-run could silently swap the agent a goal was being carried out with. The re-scan adds to the list, substitutes nothing, and runs on a background thread because it walks PATH and several home directories and the advice to install something and click Resume sent the user round the same loop for ever — summarising it instead put the tile in Summary, where the only way on is to type a new goal, so a binary being off PATH cost an approved plan and a transcript. `IsRunning` means *this tile is working*, and `WorkingAsync` holds it — and the run's one `CancellationTokenSource` — around a whole phase or loop rather than `RunAiAsync` doing either around one process. One token per call left it null in the gaps, where Pause had nothing to cancel, and left the git commands before each call uncancellable, so a pause taken while the working tree was being read waited for both processes — set around the process it went false in the gaps between an implementation ending and the review starting, and in those gaps the Pause button, bound to it, disappeared from a loop that was very much alive. Pause in such a gap also had no token to cancel, so the loop carried on and the pause was lost; `PauseRequested` is checked at each hand-over, and twice inside `RunLoopPhaseAsync` — on entry and again after the working tree is read, before the tool is launched, because reading the tree is two short git processes but the run after it is minutes. `Submit` moves the phase **before** writing the message that causes it, so the two are never on disk apart: between them the file said Clarify with the user's answer last, which reads as an interrupted Clarify, and a restart there resumed by asking the questions again instead of planning. `Submit` asks for the discard **before** it clears and writes the pause, so answering "no" leaves the tile exactly as it was. Both ways the question can go unasked answer **no** — `ConfirmAction` unwired in the view model, and no window to show a dialog in, in the view — because there is no undo for a discarded session. Reading the working tree is `WorktreeReader`, not the view model: a repository is not the view model's business, and while it lived there every test that drove the loop spawned four git processes a lap against a directory that was not a repository. `WorktreeReader.Factory` is the seam that stops it. Every prompt whose answer the user reads — clarify, plan, implement, review — ends with one line asking for it **in the language of the goal**. The tool decides this for itself otherwise, and decides inconsistently: the same run would ask its questions in Polish and hand back an English plan, because every instruction around it, the worked examples especially, is written in English. It is anchored on the goal rather than on a setting or a language detector — the goal is in all four prompts already, it is the user's own words, and guessing a language in C# is a thing to get wrong on short text. The carve-out is the load-bearing half and is wider than the json: as well as the keys and the severity values, two **literal markers are parsed rather than read** — the `Rejected:` line kept for the next attempt and the `VERDICT: PASS` line that is the review's fallback when no json arrives. Translate either and the machinery stops seeing it in silence: the note falls back to the last two lines, and a review whose verdict cannot be read counts as not met for the whole budget. **Detection is the one prompt without the instruction**, because it runs on an empty tile — there is no goal yet, so there is nothing of the user's writing to point at. The line is fixed text rather than borrowed, so `Fit` never trims it: the prompts that reach the last rung are the large ones, which is exactly where the user least wants an answer they have to translate. That last point cost something to learn — adding it pushed the review one rung down and cut the note saying git could not be read, a guarantee that turned out to hold by about a hundred characters of arithmetic rather than by construction. The working tree now has a **floor** in the review prompt, as the goal already had, so the note survives a budget no amount of trimming could have met. **The tile asks for one thing at a time, in the shape that thing has.** The composer is up only when
free text is what is wanted — a goal to start, an answer to a question the tool wrote as prose, a new
goal after a summary. Structured questions get a **panel with a box per question**, and the plan gets a
**box and one button**. The three are mutually exclusive by construction (`ShowComposer`,
`ShowQuestions`, `ShowApproval` in `GoalTileViewModel`), because two of them at once is how an answer
ends up somewhere nobody is reading. **None of them is up while the tool is working**: `Submit` returns
while `IsRunning`, so a composer shown there is a box that takes text and does nothing with it — and
the one thing it did do, silently, was hold text that a finishing detection then wrote over.

**An image can be pasted into the composer, and what goes into the prompt is a path.** `Ctrl+V` takes
the clipboard's image when there is no text on it, `Alt+V` takes it regardless — the same pair, and the
same "text wins when the clipboard holds both" rule, that a terminal tile already follows, so the
gesture is the one a user pasting a screenshot at Claude Code already has in their fingers. The image is
written to `.mtiles/goals/images/` (`GoalImageStore`, PNG, never pruned) and a marker — `[Image #1]`,
`GoalImageMarker`, Claude Code's own spelling — is inserted where the caret was, so the picture is
referred to in the sentence it belongs to. The run then carries the pair in **every** prompt that
carries the goal (Clarify, Plan, Implement, Review): the marker and the file it stands for, with the
tool asked to open the ones the work depends on rather than told to read all of them.

Three details are load-bearing. **The path, not the bytes** — a prompt is a command line fitted to a
budget (`GoalPromptBuilder.MaxBorrowedChars`) and base64 would not survive being fitted into one even
once, while every tool here can open a file it is given. **Inside the workspace, not in the temporary
directory** — the path is written into the goal file and read by a tool that may not start until the
user comes back and resumes, so a swept temporary would leave a marker naming nothing. **A new goal
keeps the images its own text refers to** rather than clearing outright (`StartNewGoal`): the markers
are in the text *before* the goal starts — the user pasted them into the composer and then pressed Send
— so an outright clear would strip every image out of the goal that had just been typed. That leaves
gaps in the numbering, which is why `AttachImage` counts from the highest number so far and not from the
length of the list; counting would hand two different files the same marker. A marker left behind in the
composer when its image is dropped goes with it (`GoalImageMarker.DropMarkersExcept`) — `+` and a
detected goal both replace the goal without touching what is typed, so the marker would otherwise be
sent with no path anywhere in the prompt. A failure to write says so in the transcript and inserts
**no** marker, because a marker whose file was never written is one the tool is told to open.

**Everything the tile asks of you is a block in the conversation, and nothing is pinned to its bottom edge.** There used to be four bars docked under the transcript — the composer, the plan box, the two detect buttons and the finished-run actions — with the questions in a fifth behind a draggable splitter. Every one was a fixed slab across the foot of the tile, and between them they took most of a small one: a tile that had been asked three questions was a band of buttons over two lines of the conversation they were about. It was also two shapes for one thing, since a round was *asked* in a panel and *recorded*, afterwards, as a numbered paragraph several screens up — so the control you answered in and the record you read back were laid out differently and neither was where the other was. All of it is now one `ScrollViewer` holding one column: the transcript, then the waiting row, then whatever the tile is offering — the finished-run actions, the round of questions, the plan box, the composer, and under the composer the detect buttons — each appearing where the next thing in a conversation appears, and staying there. The detect row is the one that reads *upwards*: it is the alternative to the box it follows (type a goal, or have one read out of what you have already changed), so above the box it was the first thing the eye landed on with the caret somewhere below it. The finished-run actions stay above everything, because they are what to do with the run that has just ended and belong beside its summary. The price is stated rather than hidden: the composer scrolls too, so reading back through a plan and then typing is a scroll away, which the follow-to-the-bottom rule does for the user on every new message and which is the gesture a terminal asks for anyway. That rule stands down when the reader has scrolled up — right for the dozen messages a run posts, wrong for the handful of moments the tile stops and needs an answer, which while they were docked bars were on screen at any offset. So a block *arriving* — false to true, not merely showing, because `RefreshAsk` announces all three ask flags together on every phase of every lap and the composer has been up all along by then, so reading the current value scrolled the reader down several times a run over nothing appearing — overrules the scroll position (`GoalTileView.Showing` is the pure half and `Appeared` the transition over it, the previous value living in the view; pinned arm by arm — each of the four seen answering yes in a state where its neighbours answer no, because a switch that reads properties by name is the shape a copy-paste survives while everything it could return is false) while a block leaving does not, and neither the waiting dots nor the detect offer count: the first is information about a run rather than a request, and the second is fed by the git watcher, so forcing on it would move somebody's reading position because of an edit made in a terminal tile next door. What it buys is that the tile's height is no longer divided between a conversation and a panel — so the ask needs no cap, no default height, no stored height read back on every pass, and no ratchet to tell a clamp from something the user had dragged. All five of those existed to arbitrate a fight that no longer happens, and `GoalTileView` is a hundred lines shorter for it.

The question block is what the numbered skeleton in the composer used to be, and the difference is who
does the filing. A number in its own grid column is a real hanging indent, so a question that wraps
keeps its indent instead of returning to column zero under its own marker — which is the thing that
made three questions unreadable. Each question owns its box, so an answer cannot be filed against the
wrong number; the offered answers are chips that fill that box, and each chip **carries its own
command** rather than reaching up out of its `ItemsControl` for the question's — a binding nothing
compiles and nothing can check, which fails as a chip that does nothing when clicked. Unanswered
questions are left out of what is sent rather than sent blank: a blank line under a number says "none
of your business" to a model that cannot tell it from a question that was skipped.

**Everything in the conversation can be copied on its own** — a message, a single finding, a single question with what was answered to it — from the same hover-revealed button, through one handler (`GoalTileView.CopyItem_Click`, dispatching on the row's own `DataContext`) and one builder. Every case goes through `GoalTranscript`, so a finding copied alone reads exactly as it does inside the review it came from and a question copied mid-round reads as it does in the record afterwards. That is the whole guarantee and it is not decorative: a message's own `Text` is only the *head* of a review, and copying that alone once handed somebody a verdict with the defects it counted missing. What reveals the button is the block it belongs to rather than the message around it — a row that lit up six buttons at once the moment the pointer crossed it would answer "which of these?" with all of them.

The questions are **persisted** (`GoalTileState.PendingQuestions`) — **and so is each answer typed against them**, on the store's own debounce, because coming back with the boxes emptied keeps half of that promise and loses typed text, which is the one thing the rest of this tile refuses to do — and the round reaches the transcript **when it
is answered**, as **one** message carrying the questions *and* the answers (`GoalMessage.Questions`, guarded and drawn by `GoalMessageTemplate.Questions` — exactly the decision `Findings` already makes, one panel over). The live block is replaced in place by the record of itself: same questions, same order, same shape, read-only, at the point in the conversation where they were asked. It used to be *two* messages — the questions, then the answers as a turn of the user's own — which was the same text twice the moment the questions became a block you fill in rather than a paragraph you transcribe; `SubmitCore(echoTyped: false)` is what suppresses the second, and it gates the transcript and nothing else, so what goes to the tool and what is remembered in `ClarificationHistory` are identical either way. The message's `Text` is still the whole round flattened (`GoalTranscript.Answered`), so the clipboard gets everything and a goal file written before this — which has no `Questions` of its own — falls through to the plain template and is drawn as the numbered paragraph it always was. Every question is recorded, including the ones left blank: they were asked, and a round of three answered once is a round of three. It is also what lets a tile be closed mid-question and come back still asking. It also moved a rule:
`WasInterrupted` used to read "the tool spoke last" from the transcript, which stopped being the signal
for a waiting tile the moment the questions left it, so every restart offered Resume and Resume asks
the same round again. A state with pending questions is now waiting, not interrupted.

The plan panel has one button and its label follows the box: empty means **Approve plan**, anything
typed means **Send changes**. Two buttons would have made "Approve" the thing that happens to whatever
the user had just written — or the thing that throws it away.

A question's suggested answers take one of two shapes, because one does not fit both: `appsettings.json / launchSettings.json` is a line worth keeping as a line, while three clause-long options joined the same way is a paragraph with slashes in it — and that is what the tool produces whenever the decision is about behaviour rather than a name. Past sixty characters they go one per line, lettered, which also gives the answer a handle: `1a` is a reply, "the first one" is a guess about what the first one was. The letters are parsed by nothing; they go back to the tool as part of the conversation, which reads them perfectly well. `e.g.` survives both shapes and is load-bearing — these are suggestions, not a closed list, and a lettered list with no label reads as a form to be filled in. That layout is what the message's *text* holds — which is what the clipboard gets and what a goal file written before rounds were kept still renders from. It is no longer what is drawn: both halves of a round are controls now, the live one with the number in its own grid column so a wrapped question keeps its indent instead of returning to column zero under its own marker, and the record with the tile's own `?` and `❯` in place of the number, because a number is a position in a round and the round is over. The prompt fences every piece of borrowed text — the tree, the approved plan, the previous review — with a run of backticks one longer than the longest inside it (`GoalPromptBuilder.Block`): a fixed three-backtick fence is closed by the first fence in any diff that touches a markdown file, and everything after it — the rest of the diff included — reads as prose. The heading says *working tree* rather than *git diff*, because the block also carries untracked file names and, when git could not be read, a note saying so. The rules about the tile rather than the loop live in `GoalTilePolicy` — when answering spends a pause, when closing counts as one, and when a transcript is worth a dialog — pulled out beside `GoalLoopPolicy` because each was an inline condition and each was wrong at least once. `Submit` in those phases says *"This run is stopped. Click Resume…"* once rather than once per keystroke, and hands back what the user typed rather than swallowing it — the guard at the top of `Submit` returns while `IsRunning`, so a tile reaching that case is by definition not working, and the *"AI is working, please wait"* it used to answer was both unreachable and false.

Both phases of a lap go through one `RunLoopPhaseAsync` — they differed in a name, a label and which prompt to build, and were otherwise the same twenty lines twice, which is how the NoTool case came to be added to each by hand and the cancelled case to be fixed in one of them first. **Answering is resuming**: typing into a paused tile clears the pause, except in the working phases where the composer has nothing to send — without that, the run started, happened, and was thrown away at the first hand-over that asks about a pause. Resume itself has a `default:` that writes the cleared pause for the phases it cannot resume, or the file kept saying paused and the button came back after every restart to do nothing again. Resuming an interruption **after** the implementation finished starts at the review (`startAtReview`), rather than implementing again — and a pause is honoured only after the phase has been moved to whatever is actually owed next — Review when the implementation has just finished, the next attempt's Implement when the review has just asked for another pass. Stopping where the loop happened to be had Resume redo the run that had already completed, against an unchanged worktree and for a second copy of the same answer: the tool's answer is already in the transcript and its changes are already on disk, so re-running it asks the tool to redo work it can see it has done — usually a no-op, sometimes a duplicate, and always in the user's own worktree. Both `Pause` and `Dispose` record the pause *before* cancelling, and only when something was running — unconditionally is worse than not at all, since every idle tile then came back claiming to be paused and Resume in Clarify asked its questions a second time. In that order because: a bare cancellation is reported as a system message, which then becomes the last thing in the transcript, and `WasInterrupted` reads a Clarify or Plan whose last message is not the user's as one that already has its answer — so the tile came back saying "answer the questions above" with no questions above it. Falling out of the loop is the budget running out, which is **not** the review passing, and is summarised as such; it used to say "goal completed after 5 iterations" for it. Resuming finishes the attempt that was interrupted instead of opening a new one, so the budget of five is five attempts at the goal rather than five per launch; the resume question is asked *before* the budget question, so an attempt interrupted as the last of the budget is still finishable — `spent < max` alone would refuse to reopen the loop and lose it half-done. Both that and the verdict live in `GoalLoopPolicy`, pure and beside `ChainPolicy` for the same reason: neither was reachable by a test while it sat inside a loop that needs an AI process and a git worktree to turn over once.

The resume is from the top of an iteration, never from the middle of a prompt — the tool's process is gone and its output with it. The one piece of state that survives is the working tree, which is why the git diff now goes into **every** implement prompt rather than only those following a review: on a resume it is the only thing telling the tool that half of its own work is already applied. It is `git diff HEAD`, not `git diff`: a tool that stages its work as it goes leaves a plain `git diff` empty, and a resumed run would be told the tree was clean and would do the work again. Untracked files are invisible to every form of `diff`, and a new file is most of what an implementation produces, so `git ls-files --others --exclude-standard` lists their **names** alongside it — at a line each rather than a whole file, and it reads the index without writing to it, because nothing here may touch the user's repository. `GoalDiffContext.Compose` assembles the two, and it is pure and separate from the git calls because the assembly is where the bug was: the list used to be appended and the *whole thing* then truncated, so the moment the diff passed the cap the list vanished — in exactly the case it exists for, a resume after a large implementation. Each part is now capped on its own and joined afterwards, **least replaceable first**: the note that git could not be read, then the untracked names, then the diff. That order is not presentation — it is which part survives when something cuts the block *again*, and something does: `GoalPromptBuilder.Fit` sees one assembled string, so with the diff on top the very first re-cut threw the file list away, reintroducing the same loss one layer up. Both git commands run with `throwOnError: true` and are caught one at a time, and a failure is carried **into the prompt** rather than only into the log: they used to swallow a broken git, a missing repository or a bad `GitPath` into an empty string, indistinguishable from a clean tree, and a tool told nothing has changed when nobody could find out writes straight over work it cannot see. Each part is cut on a line boundary, because a path cut in half is a filename that does not exist. `WorktreeReader` answers with a `WorktreeSnapshot` whose `Text` is null on a clean tree, and the prompt builder omits the section, so this costs nothing when there is nothing to say. **The caps on it are a transport limit, not a token budget**: every tool that does not read its prompt on stdin is handed it as a command-line argument, and Windows stops at 32 767 characters — 8 191 through the `.cmd` shim npm installs and `AiToolDetector` looks for first. Past that `Process.Start` throws, the run is judged `Failed`, and Resume reproduces the failure for ever, in exactly the resume-after-a-large-implementation this feature exists for. The caps are **1 000 characters for the untracked names** and, for the diff, **whatever the transport allows**: **5 200** where the prompt goes on a Windows command line and **40 000** where it does not — a tool reading its prompt on stdin (Claude Code and opencode do), or any tool off Windows. The summary's own cap moves with it, 800 against 3 000, and the two together are why the first number is 5 200 rather than 6 000: the summary was added without taking its room from anywhere, which on a command line is not free. The worktree block would have gone from at most 7 000 characters to at most 10 000 against the **8 191** a `.cmd` shim allows, so `Fit` would have begun cutting the diff harder than before the summary existed — silently, for the tools still on a command line. `GoalDiffContext.CapsFor` now answers with both caps at once, and on that path they still sum to the old 7 000: about ten files named, bought with thirteen per cent of a diff that was already a fragment. That cap was a constant, and it was the command line's number charged on channels without a command line: measured on this repository mid-change, a 140 000-character `git diff HEAD` across twenty-one files reached the tool as its first 6 000 characters — four per cent, which by path order was two markdown files. "Detect goal" then named a goal drawn from that fragment, correctly and about the wrong change, with nothing on screen to say it had been shown a twenty-fifth of the work. `GoalDiffContext.CapsFor` asks `AiProcessRunner.PromptBudget` which case this is; 40 000 rather than no cap at all, because past the crash it becomes a token bill and the diff is the largest compressible thing in any of these prompts. **`git diff HEAD --stat` goes in too**, above the body beside the untracked names, and for the same reason they are there: it is bounded by the *file count* rather than by the size of the change, so it survives a cut that takes most of the diff and is the only part that says the change reaches files the body never got to. A third git process for one line per file, with explicit widths — git formats it for a terminal and falls back to 80 columns when there is none, abbreviating the middle of exactly the long paths that say which area of the project moved. It is `--stat=1000,1000 --stat-graph-width=10`, and the two halves are the opposite way round from the obvious: git clamps the *name* to about three eighths of the total width once name, numbers and graph exceed it, so asking for a narrow total to save room on the bar of plusses truncates the path instead — measured, at `--stat=100,180` an 84-character path with 900 changed lines came back elided with its leading directories, the informative half, gone. Narrowing the *graph* alone is both shorter and complete: 1 473 characters against 1 685 for this repository's own working tree, every path whole. Its failure is not joined into the note, since it fails only where the diff already has. It is out of the fingerprint for the same reason: derived from the diff, it cannot move unless the diff has. The detect prompt is told the diff **may be truncated** and that a file listed but absent from it has still changed, and to say so when the changes plainly cover more than one piece of work rather than picking one. The remaining cap is `GoalPromptBuilder.MaxBorrowedChars` = **2 000** for each other borrowed piece — the goal, the clarifications, the approved plan, the previous review. The plan and the review are the tool's own output and have no natural size, so capping only the tree was capping the smaller half. **They bound the prompt; they do not promise it fits — so the prompt is now fitted to the transport before it is sent.** The arithmetic is not close: a review carrying the goal, the quality rules, seven thousand characters of working tree, the severity rules and an example runs to about twelve thousand characters, against the 8 191 a `.cmd` shim allows — every tool that does not read stdin goes that way, and the case that overflows is the one the feature exists for, a resume after a large implementation in a busy working tree. `AiProcessRunner.PromptBudget` answers how much this particular tool can be handed (null for stdin, null off Windows), and `GoalPromptBuilder.Fit` rebuilds the prompt with smaller borrowed blocks — 3 000, 1 500, 750, 300, then none at all — until it goes. Trimming costs the tool some context; refusing cost the user the run. The goal keeps a floor of 200 characters, because a prompt that has trimmed away what it was asked to do is not a smaller prompt but a different one. `AiProcessRunner` still refuses what will not fit even then, with a message naming the cause — measured **as quoted**, since a prompt of code grows on the way onto a command line, and only on Windows, since a POSIX system allows some two megabytes. Without it the refusal surfaced as a `Win32Exception` about nothing in particular.

**Claude Code is told to stream, and is given no turn limit at all.** It used to be `--max-turns 20`, an arbitrary number of mine that was doing harm two ways: an agent that reads a few files, loads a skill and then edits spends turns quickly, so twenty was reachable in ordinary work — and with `--output-format text` there is no way to tell *I finished* from *I ran out of turns*. A run cut off half way through an implementation looked exactly like a completed one: it went to review, the review found unfinished work, and the next attempt started from a state nobody had named. Turns are the wrong unit for the work; what bounds this tile is its attempt budget and the Pause button, both of which are about the work rather than about the tool's inner loop. It came back once at **200** on the grounds that a stream can say when a ceiling is hit — `error_max_turns` arrives as a result line marked as an error and is reported as one, so hitting it is told apart from finishing — and that was true and not enough: the 200 was reached half way through a real implementation, and a truncation you can *see* is still a truncation. **There is no `--max-turns` now, at any number** — which leaves one run with no ceiling of any kind, and that is an accepted risk rather than something covered elsewhere. Not "the user can set one in their settings": measured against Claude Code 2.1.251, `maxTurns` is a hidden CLI flag, a field in an agent file's front matter and an SDK option, and `settings.json` has no equivalent — the only ceiling available for one of these runs is the flag being refused here. The attempt budget bounds how many runs a goal gets rather than how long one lasts, and `RunPlainAsync` deliberately has no wall-clock timeout, so **Pause is the whole of the stop**. A ceiling read as an error is still kept working (`error_max_turns` → an error chunk, never an answer) for anyone who reintroduces one. And `RunPlainAsync` has never had a wall-clock timeout (deliberately — an agent forty minutes in is doing what it was asked, and it writes as it goes). Streaming is `--output-format stream-json --verbose`, the second flag not optional: print mode refuses the pair without it. **A run the tool says failed is not an answer.** The stream carries the fact beside the words (`AiOutput`), and the loop judges on the fact: a run that ended in a turn limit, a refused key or a credit balance comes back with text in it — an apology, a half-finished note — and read as an answer that text became the plan, or the review, and was acted on. The words are still shown, because a failed implementation has usually already written files and this is the only account of what is now in the worktree. **The same rule now applies to the other three tools**, where it is a change: a non-zero exit with something on stderr used to come back as glued-together text and be judged an answer, and now pauses the run. A CLI that exits non-zero over warnings while printing a perfectly good reply will stop the loop where it used to carry on — deliberate, because the alternative is believing a tool that said it failed, and the pause is recoverable in a click. A link in that markdown is **asked about before it opens**, with its address shown rather than the words it was written on: this text comes from a model, from a prompt carrying a working tree that may contain anything, and `[click here](…)` hides exactly the half that matters. Only `http` and `https` are offered at all. The tile already puts a barrier in front of a *command* that arrives in a goal file; a link opening on one click with the destination visible nowhere was the same trust in the same author with none of the same care. What the stream buys is **`Activity`** — the tool call in progress, as `Read src/Cart.cs` or `Skill code-review`, shown faint beside the phase on the status strip and nowhere else. It is not transcript: a run touches dozens of files and every one of those lines would be in the way tomorrow, while the question it answers — *is this still doing something?* — is only ever asked about now. `IAiAgent.SupportsStreaming` is opted into per tool, like `AcceptsPromptOnStdin` and for the same reason: it is a claim about somebody else's CLI, and the other three are run exactly as they were. Passing an activity callback is what turns streaming on, so the plain path is still one call away and still what the tests use. Skills, `CLAUDE.md` and `.claude/settings.json` are picked up normally — the tool runs with the workspace as its working directory and no flag narrowing what it may do, so **permissions are the user's own**: in `-p` mode there is nobody to ask, so anything their settings would prompt for simply does not happen, silently. The real fix is stdin, and **Claude Code now uses it**: `IAiAgent.AcceptsPromptOnStdin` is opt-in and false by default, true for `ClaudeAgent`, because `claude -p` with nothing after it reads standard input — and for `OpenCodeAgent` too, measured 2026-09-01 against 1.18.18 and 1.18.25: `opencode run` with no message argument answers the prompt it is piped, while on the command line the same prompt went through the npm `.cmd` shim's re-parsing and past ~8 000 characters cmd.exe refused the line outright ("The command line is too long.") with the stdin run answering beside it. It is per tool rather than assumed — a claim about somebody else's CLI, and a tool that does *not* read stdin would sit waiting for input that never comes. Which is also why an unrecognised binary now falls back to `GenericAgent` (prompt as a plain argument, no stdin) rather than to `ClaudeAgent`: that fallback was survivable while Claude ran on the command line and became a hang the moment it did not — a custom tool launched with Claude's flags, no prompt anywhere on its command line, and a pipe it had never agreed to read. The prompt is written and the pipe **closed at once**, or the tool waits for end-of-input that never arrives; the readers start first, or a prompt large enough to fill the pipe deadlocks against a child writing output nobody is draining; the cancellation registration is in place before the write, or a pause during one has nothing to interrupt it; and a broken pipe is logged rather than thrown, because letting it out skipped the awaits on stdout and stderr and threw away the tool's own account of what went wrong. The caps stay where they are for the tools still on the command line. `LoadState` keeps the saved agent **whether or not it is still available**, and says so rather than substituting: a goal planned against one model being carried out by another, announced once in a transcript nobody rereads, is the failure the old "use whatever is installed instead" caused. A goal file written before agents had instances still names one, because `SelectedToolName` is matched against the agents' own names (`GoalAgents.MatchingToolName`) — read for ever rather than migrated once, since a goal file travels with a branch. Approving a plan adopts what the tool **proposed** (`GoalWorkflowEngine.ProposedPlan`), which a new planning run clears before it starts — a rejected plan otherwise outlived the rejection, and "ok" after a second run that produced nothing approved the plan the user had just turned down. Not the last assistant message in the transcript: once an empty or failed run could leave the Plan phase paused with no answer in it, "ok" approved the clarifying questions as the plan, or approved an empty string and started implementing in silence. With nothing proposed it now says so. `GoalResumeTests`, `GoalLoopPolicyTests`, `GoalTilePolicyTests`, `GoalDiffContextTests`, `GoalPromptBuilderTests`, `GoalStateNullGuardTests`, `GoalStatePersistenceTests`, `GoalWorkflowLoopTests`, `GoalReviewTests` (parsing, completion criteria, transcript rendering) and `AiProcessRunnerTests` pin the rules. The loop tests' worktree stub returns a **different** tree on every read, and that is load-bearing: a stub answering the same string every time is a tool that never changes anything, so the no-change stop ended every run after one attempt — the stop working exactly as intended, on a fixture that lied. Goal files under `.mtiles/goals/` are **never pruned** — a tile removed from a layout leaves its session behind, which is deliberate for now: nothing distinguishes a goal file whose tile was closed from one whose workspace is simply not open.

**UI:** a terminal transcript, not a chat window. The tile borrows the terminal's own monospace face and size (`TerminalFontFamily`, `UiFontSize`/`UiFontSizeSm`) and the app's colour tokens, which `ThemeBridge` derives from the active terminal theme — so it follows the colour scheme like every other tile. Each message is a fixed 16px gutter glyph plus its text (`>` you, a dot the tool, a corner a note from the tile), aligned to one column. The bubbles it replaced aligned every message differently and made a two-line answer look like a different kind of thing from a twenty-line one; the gutter also gives the transcript a single left edge to read down. A message is drawn one of two ways, and which one is the message's own answer (`GoalMessage.IsMarkdown`). **The tool's own prose is rendered as markdown** by `GoalMarkdownView` — agents write headings, bold and fenced code whether or not anything reads them, and raw those markers are noise in every answer. **Anything this application composed is not**, and that distinction is load-bearing rather than tidy: a review is a column of severities with each detail indented under its title, and markdown collapses runs of spaces, reads a two-space indent as a continuation, and turns a `*` inside a finding into emphasis — so the one part of the transcript arranged to be read in columns was the part being re-flowed. Neither is what the user typed, where an asterisk is one they meant. Both controls hold their own selection, so a transcript can be copied out — which is why `TerminalClipboardCoordinator.HandlesItsOwnCopy` has to know them: this tunnel handler runs before the control's own Ctrl+C, and without it a selection made here was answered with text from whichever terminal in another tile still held one. It matches by **type**, not by namespace, because `is` catches a subclass and a prefix does not — the markdown view is a subclass living in this assembly, and the prefix version missed the very control it was written for. Each message also carries a copy button, revealed on hover, which is the only thing in the row that is not the message. The other half of that change is deliberate too: with focus in a Goal transcript and **nothing** selected, Ctrl+C no longer copies from a terminal in another tile — the same rule a focused `TextBox` has always had.

Three strips: a status strip on top (tool selector · phase dot · phase label — a `DockPanel`, because a horizontal `StackPanel` measures with infinite width and `TextTrimming` there is inert), the transcript, and a composer at the bottom drawn as one field with the prompt glyph inside it — the `Border` owns the border and focus ring, the `TextBox` gives up its own, and a click anywhere in it puts the caret in the box. Enter sends, Shift+Enter breaks a line. Buttons are the app-wide `tile-btn`. Pause/Resume appear while the tool is running; `+` starts a fresh goal.

**`@` names a file.** In the composer, the plan box and every answer box, typing `@` offers the
workspace's files and folders, and picking one writes the path in —
`@src/mTiles/Services/TileScript.cs` rather than a name the tool has to go and find. Up/Down move,
Escape puts the list away, **Enter takes the row that is lit and Tab types the part every row agrees
on** — the shell's completion, so Tab narrows and only picks when there is nothing left to narrow.
While the list is up Enter belongs to it rather than to Send, taken in the tunnel phase, because the
bubble phase would reach it after the box's own handler had already sent a goal with a half-typed
`@go` in it. **A folder is a step rather than an answer**: taking one types it without the trailing
space and leaves the list up on what is inside, so Enter walks down a tree exactly as Tab does.

**Typed beside a detection, the composer becomes a scope** — and it is two mechanisms, not one.
The words travel as a narrowing block (`"The user narrowed this detection/review"` — soft, and all a
phrase like "tylko zmiany dotyczące agentów" can be, since a phrase names no files to filter by); the
paths it names as `@` mentions are the hard half: `GoalScopeFilter` extracts them (`@path`, and
`@"path with spaces"` for names with whitespace; a token with neither a directory nor an extension —
`@admin` — is prose with an at-sign in it, not a path) and every part of the working-tree block is
filtered to them before the prompt is built. A diff that the filter emptied carries a note saying the
user's scope matched nothing — a block gone silent about every file must not read as a clean tree;
the note rides on the diff alone, because the untracked list and the stat empty as a matter of course
under any scope that names one file. The fingerprint is taken from the raw diff on purpose, so the
no-change check compares what the tree *is*, not what the prompt was allowed to say about it. Where
the words land is deliberately uneven: **Detect goal** and **Detect & run** carry them into the
detection whose goal then carries the narrowing for the rest of the run; **Review** over an empty
composer and **Re-review** carry them into the one review the entry produces — while **Review** with
something typed takes those words as the goal itself rather than as a narrowing on top of one — as parameters, never onto the session scope,
because a narrowing typed for a single look at the tree must not be inherited by a Continue that
comes after. **The composer is consumed only where the words are acted on**: a detection that ends
without a goal leaves the draft in the box.

The whole thing follows what the popular tools of this kind do, measured against a 3443-file
TypeScript repository, and the reason is one number. The ranking here used to say that a hit in the
file's own name beats a hit in a directory above it, which is wrong for any tree organised by folder
— and that repository is the extreme case, with 121 files called `index.ts` and 45 called
`prompt.ts`. Typing `@bash` there put
`tools/BashTool/prompt.ts` at position **32 of 66** and filled every row with files merely spelling
"bash" in their name, so nothing else in `BashTool/` was reachable at all, whatever the user typed
next.

`FileMentionMatcher` is now an fzf-style scorer of the kind those tools use:
a **fuzzy subsequence** over the whole path, greedy and earliest, scoring each matched character and
then paying bonuses for where it landed — after a boundary (`/ \ - _ .` or a space), on a camel-case
capital, immediately after the previous match — against penalties for the gaps it had to jump. There
is deliberately **no term for the file's own name**: a folder is a first-class thing to type, and the
boundary bonus pays for hitting the start of one exactly as it pays for hitting the start of a file
name. The only thing separating two equally good matches is a bonus for a short path, which is what
the old `ThenBy(path.Length)` was for and now says so on the same scale as everything else. Same
tree, same query: `@bash` now answers with `BashTool/` and `utils/bash/`, and `@btp` finds
`tools/BashTool/prompt.ts` at #2 — a class of query a substring ranking cannot answer at all.
**Smart case** comes with it: a query in lower case ignores case, one carrying a capital means it.
Measured at 0.3–0.5 ms per keystroke over those 3443 paths, which is why there is no debounce —
tools of this kind wait around 50 ms because they filter a quarter of a million paths, and a wait
here would only add latency, and its own cost is elsewhere: the corpus — every path folded to lower case and a
26-bit map of which letters `a`–`z` it holds — is built once per reading of the tree
(`FileMentionCorpus`), and the letter map rejects most candidates without
reading them at all. Measured at the declared ceiling of 200 000 paths: **2.4–4.0 ms a keystroke and
under 3 MB allocated**, against 8–17 ms and 15–18 MB when the fold happened inside the loop. A delay
would also not be free to add: `Task.Delay` yields unconditionally, and with no synchronisation
context to post back to the continuation rewrites the bound collection off the UI thread.

`FileMentionToken` holds the rest of the text rules — when an `@` is a request and not an email
address, and the quotes a path with a space in it is given, because a mention ends at the first space
for whatever reads the prompt and `@docs/my notes.md` unquoted would arrive as `@docs/my` and a loose
`notes.md`, naming no file at all. A quote *inside* a name is left alone rather than escaped: it is
illegal in a Windows file name, and the backslash this used to write was worse than nothing, since
the parser on the other side reads `@"([^"]+)"` and an escaped quote ends the mention exactly where a
bare one does — with the backslash then delivered as part of the name. **The query stops at the caret
but the replacement takes the whole word**, which is the usual rule and replaced the opposite one:
completing `@fi|le.cs` now replaces `@file.cs` rather than leaving `le.cs` stranded after the path it
had just inserted.

`WorkspaceFileMentionSource` is what there is to offer, and it is the arrangement these popups use
rather than a simpler one: **git decides what exists, three rules decide what is shown.** The corpus is
`ls-files --cached --recurse-submodules` plus `--others --exclude-standard` — the tracked files and
the untracked ones the repository does not ignore, which is the user's own `.gitignore` rather than a
list this application would have to keep in step with. Two commands rather than one flag more,
because `--recurse-submodules` is only accepted alongside `--cached`. Both carry
`--no-optional-locks` like every command in `WorktreeReader`, and more sharply: these run unprompted
while the user is typing, and `ls-files` refreshes the index on the way past when
`core.untrackedCache` or fsmonitor is on — taking `index.lock` behind the back of a rebase in the
terminal tile next door because a popup was deciding what to offer. `-z` rather than
`-c core.quotepath=false`: same fix for the same problem — git quotes any path that is not plain
ASCII, so a repository with Polish file names would suggest paths that do not exist — and it also
survives a newline in a file name, which splitting on lines cannot. Both exclude `.mtiles` by
pathspec, as `WorktreeReader` does: it holds this tile's own transcript and the images pasted into
it.

**The untracked half is not awaited.** The tracked list answers nearly every mention, and making the
first `@` of a session wait for a second walk of the working tree spends the user's time on the half
they are least likely to want; the untracked files merge into the reading when they arrive, or into
the next one. A **generation counter** is what makes that safe — a refresh that has already replaced
the reading has established what is there *now*, and merging a slower answer to an older question on
top of it would put back files that refresh had just found were gone. Its failure is not the tracked
read's, either: a repository mid-rebase can refuse this while answering that perfectly well, and half
a list is worth having.

The three rules are applied where such a list belongs — to **every** candidate, git's output
included, which is the part that is easy to get wrong by filtering only the fallback:

- **`ExcludedDirectories`**, the excluded-directory list a popup like this carries. This is the one
  place the
  popup overrules the user's own `.gitignore`, and it earns that: a repository that commits its
  `dist/` has said something about distributing it and nothing at all about wanting eight hundred
  bundled files in a popup. The last four entries — `bin`, `obj`, `packages`, `target` — are ours;
  lists like this are written for JavaScript and Python repositories and never had to name the .NET,
  Java and Rust build outputs, and a tile in *this* application offering
  `bin/Debug/net10.0/mTiles.dll` is the failure the list exists to prevent. The cost is stated rather
  than hidden: a repository that deliberately tracks something under `bin/` — and some do — will
  not have it offered here. Matched against every directory *above* the
  file, never the file's own name, so a file called `build` is still a file.
- **`FileSuggestionIgnore`**, the workspace's `.ignore` / `.rgignore`. ripgrep's own files, which
  exist for exactly this case — "git tracks it, but do not show it to me" — so a user who has written
  one has already answered the question the popup asks. A stated subset of the gitignore syntax:
  comments, blanks, `!` negation with last-match-wins, a leading `/` anchoring to the root, a
  trailing `/` for directories only, and `*` `?` `**`. Not character classes or escapes — and the
  failure direction is deliberate, since a pattern it cannot read matches nothing, so the worst case
  is something offered that the user did not want to see, never a file they were reaching for
  silently gone.
- **The ceiling**, `MaxPaths` = 200 000, the figure these listings settle on.

**The agent's own configuration is added outside all of that** — `CLAUDE.md`, `AGENTS.md`,
`CLAUDE.local.md`, and the markdown one level down in `.claude/commands`, `.claude/agents`,
`.claude/skills`, `.claude/output-styles`. Offered the way a popup like this offers it, pointed at
the agent this application actually drives, and the reason is visible in almost any repository's
`.gitignore`, which routinely carries `CLAUDE.md` and `/.claude`: the files telling the agent how to work are routinely the ones
the repository declines to track, and they are exactly what somebody writing a goal wants to point
at. Markdown only, one level deep, and only those directories — narrow enough that it cannot quietly
become a second file listing that ignores `.gitignore`.

Outside a repository there is no git list, so the walk skips what starts with a dot — `.git` and the
editors' own directories — and then goes through the same three rules. Tools of this kind fall back
to **ripgrep** here; this walks, because ripgrep is a program a user may not have and a workspace that
offers nothing is worse than one that walks. It runs on the thread pool, because the popup asks while
the user is typing and a walk on the dispatcher is the keyboard stopping mid-word.

Folders are **derived** from the file list rather than listed — git has no command for them, and a
second walk would answer for a tree the first has already described — each ending in `/`, which is
what tells the two apart on screen and in the text and is also the boundary character the scorer pays
for. One reading is shared by all three boxes and stands for five seconds, or until git touches
`.git/index`: the timestamp is read on every ask, so a commit or a branch switch in the terminal tile
next door shows up at once and a quiet minute of typing still costs one reading. The boxes never hold
the keyboard at once, so there is only ever one list on screen.

What a phase says while it waits comes from `GoalWorkflowEngine.GetPhaseLabel` and nowhere else: the same four sentences used to exist in three places — the engine, the labels passed into `RunPhaseAsync`, and the summary — with nothing keeping them in step. The phase is the one saturated colour in the tile, and it is set as a style class (`phase-clarify`…) rather than a brush resolved in code-behind, so it repaints on a theme change. Its five colours come from the ANSI palette (`ThemeBridge`) and are pulled toward the foreground on a light theme — a six-pixel dot in ANSI yellow is invisible on white.

**Services:** `AiProcessRunner` — process launcher with `ArgumentList`-based arg passing and concurrent stdout/stderr reading (deadlock-safe); `IAiAgent` / `ClaudeAgent` live beside it. `GoalWorkflowEngine` holds the phase machine, `GoalPromptBuilder` the prompts, `GoalStatePersistence` the `.mtiles/goals/*.json` round trip, `WorktreeReader` the git commands, which now answer with a `WorktreeSnapshot` — the text **and whether anybody managed to look**, because inferring the second from the first was wrong in a way nothing noticed: a workspace that is not a repository produces the same answer on every read, so comparing two of them said the implementation had changed nothing and every goal there ended after one attempt with a confident and false explanation. "I could not tell" is not "nothing happened". It carries a **fingerprint** as well, and for the same reason one layer along: the snapshot's text is clipped to fit a command line, so two reads of a large working tree are character-for-character identical whenever the change falls past the cut — which is the ordinary case on exactly the resume-after-a-big-implementation the no-change stop exists for. The fingerprint is a digest of the whole git output before any of it was cut (a digest, because two snapshots are held at once and a diff is measured in megabytes), and `ProvablyUnchangedFrom` compares only that: what goes in the prompt is what fits, and what stops a run has to be what happened. Every command carries `--no-optional-locks`, because they run unprompted in a repository the user is working in and git refreshes its index while reading unless told not to: taking that lock behind somebody's back is how a rebase in the terminal tile next door fails with `index.lock: File exists` because a tile was deciding whether to show a button. `.mterminal` is excluded by pathspec on **both** tree commands: the listing would otherwise hand the agent the path to this tile's own transcript, and a goal file that has been committed — nothing adds `.mterminal/` to `.gitignore` except the Git tile — shows up in `diff HEAD` with its contents — and `GoalDiffContext` the assembly of what they return, `GoalLoopPolicy` and `GoalTilePolicy` the rules — the view model drives them and owns none of the rules.
