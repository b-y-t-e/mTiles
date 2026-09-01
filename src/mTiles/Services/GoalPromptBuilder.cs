using System.Globalization;
using System.Text;
using mTiles.Models;

namespace mTiles.Services;

public sealed class GoalPromptBuilder
{
    private readonly Func<GoalCompletionCriteria> _criteria;
    private readonly Func<IReadOnlyList<GoalImageAttachment>> _attachments;

    /// <param name="criteria">The goal's criteria, read fresh on every prompt. A function rather than a
    /// value because they are edited while a run is paused, and the builder outlives the object they
    /// were read from — <c>GoalWorkflowEngine.Criteria</c> is replaced wholesale on every keystroke in
    /// the panel. Captured by value, a switch flipped in the panel would take effect on the next tile
    /// rather than the next attempt. Defaults to the defaults, which is what a builder made without an
    /// opinion should ask for.</param>
    /// <param name="attachments">The images pasted into the goal, read fresh for the same reason and
    /// handed over the same way — a paste during a paused run belongs to the next prompt, not to the
    /// next tile. Read here rather than added to four signatures: every prompt that carries the goal
    /// carries the same list, so a parameter would have been the same value spelled out at every call
    /// site and one of them would eventually be spelled wrong.</param>
    public GoalPromptBuilder(
        Func<GoalCompletionCriteria>? criteria = null,
        Func<IReadOnlyList<GoalImageAttachment>>? attachments = null)
    {
        _criteria = criteria ?? (static () => new GoalCompletionCriteria());
        _attachments = attachments ?? (static () => []);
    }

    /// <summary>
    /// The rules every change here is held to: Clean Code always, and the SOLID principles the user
    /// left switched on.
    /// </summary>
    /// <remarks>
    /// <para>A method rather than the constant it used to be, and it earns the change twice. It used to
    /// name two of the five and wave at the rest with "especially", which is how a reviewer decides for
    /// itself which principles are in scope; each one that is on is now stated outright.</para>
    /// <para>The out-of-scope sentence is the half that does the work. Saying nothing about a switched
    /// off principle does not switch it off — a model reviewing C# reports a fat interface whether it
    /// was asked to or not, and the finding lands as a warning against a tolerance of zero. So the
    /// prompt has to say what is <em>not</em> being asked for, and only when that is not obvious from
    /// the list above it: with all five on there is nothing to exclude, and with none on there is no
    /// list to read the exclusion off.</para>
    /// </remarks>
    private string QualityRules()
    {
        var solid = _criteria().Solid;
        var text = new StringBuilder(
            "All changes MUST follow Clean Code principles (descriptive naming, small single-purpose " +
            "functions, no duplication, self-documenting code).\n");

        if (!solid.Any)
        {
            // Stated, not omitted. This is a goal whose author has said the abstractions are not the
            // point — a script, a spike, a one-page fix — and a reviewer left to its own devices will
            // spend the run's remaining attempts on exactly what they switched off.
            text.Append("SOLID principles are out of scope for this goal: do not report violations of " +
                        "them.\n\n");
            return text.ToString();
        }

        text.Append("They MUST also follow these SOLID principles:\n");
        foreach (var principle in SolidPrincipleCatalog.All.Where(p => p.IsOn(solid)))
            text.Append("- ").Append(principle.Rule).Append('\n');

        if (solid.Partial)
            text.Append("The SOLID principles not listed are out of scope for this goal: do not report " +
                        "violations of them.\n");

        return text.Append('\n').ToString();
    }

    /// <summary>
    /// The two things the finished work has to leave true — that the project builds, and that its tests
    /// pass — as an instruction to the tool rather than a command this tile runs.
    /// </summary>
    /// <remarks>
    /// <para>What this replaces was a verify command: the clarification round proposed a shell line,
    /// the user approved it, the tile ran it, and a non-zero exit was a hard gate on completion. It
    /// only ever worked in a repository that was already green. A project whose suite has failures
    /// nobody has got to yet is the ordinary case, and there the gate spent every attempt of every goal
    /// on failures the work had not caused, then reported the goal as not reached.</para>
    /// <para>So the checking moves to the tool, which is standing in the repository and knows how this
    /// project is built — no shell line to propose, approve or maintain, and no exit code the user had
    /// to underwrite before anything could run. The tile states what has to be true and leaves the how
    /// alone.</para>
    /// <para>The sentence about pre-existing failures is the load-bearing one, and it is the same
    /// argument as the out-of-scope sentence in <see cref="QualityRules"/>: a tool told the tests must
    /// pass, in front of a suite that was already red, goes and fixes somebody else's tests with the
    /// attempts meant for the goal. It is told to report them instead.</para>
    /// </remarks>
    /// <param name="review">Whether this is the reviewer being asked to establish it, rather than the
    /// implementer being asked to leave it true.</param>
    private string HealthRules(bool review)
    {
        var criteria = _criteria();
        if (!criteria.RequireBuild && !criteria.RequireTestsPass) return "";

        var text = new StringBuilder(review
            ? "Establish these yourself, by running this project's own commands rather than by reading " +
              "the diff:\n"
            : "When you are finished these MUST be true, and checking them is part of the work — use " +
              "this project's own commands, worked out from the repository:\n");

        if (criteria.RequireBuild) text.Append("- the project builds\n");
        if (criteria.RequireTestsPass) text.Append("- the project's tests pass\n");

        text.Append(review
            ? "A failure these changes caused is an error finding. One that was already failing before " +
              "them is not: say so in your reasoning and leave it out of the findings.\n" +
              // The reviewer is the only read-only phase that is nonetheless allowed to run
              // commands, because a build writes and a read-only sandbox would fail the very
              // check this asks for. This sentence is what stands in for the sandbox that was
              // withdrawn: it may compile and test, it may not edit.
              "Running the build and the tests is the only change you may make: do not edit, " +
              "create or delete any file, and do not commit.\n"
            : "A failure that was already there before you started is not yours to fix: say so in your " +
              "closing line rather than working around it.\n");

        return text.Append('\n').ToString();
    }

    /// <summary>How a violation of these rules is described where the review is told what a warning is.
    /// SOLID is left out of that sentence when none of it applies, so the one place the reviewer is
    /// given a reason to reach for the severity does not contradict the scope it was just given.</summary>
    private string WarningSubjects() => _criteria().Solid.Any ? "a Clean Code / SOLID violation" : "a Clean Code violation";

    /// <summary>
    /// Every prompt here ends with one worked example of the answer it wants, and never more than one.
    /// <para>Not decoration: two of these prompts ask for a JSON object, and a model shown a
    /// schema in prose invents a neighbouring one — <c>issues</c> for <c>findings</c>, a severity scale
    /// of five, the object wrapped in an array. One concrete example fixes all of that, and
    /// <see cref="GoalResponseParser"/> exists for the times it does not.</para>
    /// <para>Kept to a single line each because they are fixed overhead in every prompt, and the prompt
    /// has a command-line budget — see <see cref="GoalDiffContext.MaxDiffChars"/>. A second example
    /// would buy very little and is charged for on every run.</para>
    /// </summary>
    private const string ClarifyExample =
        "Answer with one fenced json block and nothing else.\n\n" +
        "Example:\n" +
        "```json\n" +
        "{\"needsClarification\":true,\"questions\":[" +
        "{\"question\":\"Which file holds the port?\",\"why\":\"There are two candidates.\"," +
        "\"options\":[\"appsettings.json\",\"launchSettings.json\"]}]}\n" +
        "```";

    private const string ReviewExample =
        "Answer with your reasoning first, then one fenced json block as the last thing in your reply.\n\n" +
        "Example:\n" +
        "```json\n" +
        "{\"goalMet\":false,\"findings\":[" +
        "{\"severity\":\"error\",\"category\":\"correctness\",\"file\":\"src/Cart.cs\",\"line\":42," +
        "\"title\":\"Total ignores discounts\",\"detail\":\"Sum() runs before ApplyDiscount().\"}]}\n" +
        "```";

    /// <summary>
    /// Answer in the language the user is writing in.
    /// </summary>
    /// <remarks>
    /// <para>The tool decides this for itself otherwise, and it decides inconsistently: the same run
    /// would ask its questions in the user's language and then hand back a plan in English, because
    /// every instruction around it — the worked examples especially — is written in English.
    /// A user reading their own goal answered in a language they did not choose is being asked to do
    /// the translating.</para>
    /// <para>Anchored on the goal rather than on a setting or a detector. The goal is in every one of
    /// these prompts already, it is the user's own words, and it costs nothing to point at — while
    /// guessing a language in C# is a thing to get wrong on short text, and a setting is a question
    /// nobody should have to answer about their own writing.</para>
    /// <para>The carve-out is the load-bearing half, and it is wider than the json. These prompts ask
    /// for English keys and a fixed set of severity values that <see cref="GoalResponseParser"/> matches
    /// on; they also ask for two literal markers that are parsed rather than read — the
    /// <c>Rejected:</c> line <c>GoalWorkflowEngine.Note</c> keeps for the next attempt, and the
    /// <c>VERDICT: PASS</c> line that is the review's fallback when no json arrives. Translate either
    /// and the machinery quietly stops seeing it: the note falls back to the last two lines, and a
    /// review whose verdict cannot be read counts as not met, for the whole budget. And what these
    /// prompts act on is code in a project with its own conventions. A model told simply to answer in
    /// the user's language translates exactly the things nothing can read afterwards.</para>
    /// <para>One line, because it is fixed overhead in every prompt — the same argument that keeps
    /// the examples to one each. Fixed rather than borrowed, so <see cref="Fit"/> never trims it away:
    /// the prompts that survive to the last rung are the large ones, and a large one is exactly where
    /// the user least wants an answer they have to translate.</para>
    /// </remarks>
    private const string AnswerLanguage =
        "Answer in the same language as the goal above. Keep json keys, severity values, marker " +
        "words, code and identifiers in English.\n\n";

    public string BuildClarify(string goal, IReadOnlyList<string> clarificationHistory, int? budget = null) =>
        Fit(cap => ComposeClarify(goal, clarificationHistory, cap), budget);

    private string ComposeClarify(string goal, IReadOnlyList<string> clarificationHistory, int cap)
    {
        var prompt = "You are helping implement a goal in a software project.\n\n"
                     + Block("The goal is", goal, GoalCap(cap))
                     + Images(cap);
        // `cap > 0` as everywhere else: at the last fitting step there is no room for borrowed text,
        // and Recent would otherwise contribute a block containing only "… earlier turns omitted."
        if (clarificationHistory.Count > 0 && cap > 0)
            prompt += Block("Previous conversation", Recent(clarificationHistory, Cap(cap)), int.MaxValue);

        prompt += "Decide whether the goal is specific, measurable and achievable in code.\n" +
                  "- If anything material is missing, ask at most 3 short questions, each about one " +
                  "decision you cannot make yourself. Set needsClarification to true.\n" +
                  "- If you can plan the work as it stands, set needsClarification to false and send " +
                  "no questions. Do not invent questions to be thorough — an unnecessary round costs " +
                  "the user a reply.\n" +
                  "- Offer options when the sensible answers are few and knowable.\n" +
                  "Do not implement anything yet.\n\n" +
                  AnswerLanguage +
                  ClarifyExample;
        return prompt;
    }

    public string BuildPlan(string goal, IReadOnlyList<string> clarificationHistory, int? budget = null) =>
        Fit(cap => ComposePlan(goal, clarificationHistory, cap), budget);

    private string ComposePlan(string goal, IReadOnlyList<string> clarificationHistory, int cap)
    {
        var prompt = "You are planning the implementation of a goal in a software project.\n\n"
                     + Block("Original goal", goal, GoalCap(cap))
                     + Images(cap);
        if (clarificationHistory.Count > 0 && cap > 0)
            prompt += Block("User clarifications", Recent(clarificationHistory, Cap(cap)), int.MaxValue);
        prompt += QualityRules();
        prompt += AnswerLanguage;
        // Said three ways, because a plan that inflates is this phase's characteristic failure and one
        // sentence of "be concise" does not stop it. What comes back otherwise is the user's goal
        // rewritten at four times the length, steps grouped under invented headings, and each one
        // annotated with the principle it serves — the principles being in this prompt at all is what
        // invites the last of those, which is why they are named as rules to follow rather than
        // material to write about.
        //
        // It matters more here than it reads. This plan is what the user approves, and it becomes the
        // ApprovedPlan every implement prompt carries: scope invented at this step is scope the run
        // then spends its attempts building, and a goal restated more grandly than it was written is
        // the tile agreeing to something nobody asked for.
        //
        // HealthRules is deliberately not here. Whether the build and the tests are left standing is
        // something the implementation and the review are held to; in a plan it only ever came back as
        // two more steps saying "run the tests".
        prompt += "Write the plan. Keep it minimal:\n" +
                  "- Goal: one sentence. Restate the goal above as the user wrote it, only tighter. Do " +
                  "not add scope, requirements or detail they did not give you.\n" +
                  "- Steps: one line each — the file, then what changes in it. As few steps as the work " +
                  "needs.\n" +
                  "- Success criteria: one line each, each one checkable.\n" +
                  "Plan only what the goal asks for. Do not invent files, requirements or constraints, " +
                  "do not name the principles above or justify the steps, and do not describe anything " +
                  "you are not changing.\n" +
                  "Do not implement anything yet.\n\n" +
                  "Example shape:\n" +
                  "Goal: one sentence restating what will be true when this is done.\n" +
                  "Steps:\n" +
                  "1. src/Cart.cs — apply discounts before totalling.\n" +
                  "Success criteria:\n" +
                  "- Cart with a 10% discount totals 90 for a 100 basket.";
        return prompt;
    }

    /// <summary>
    /// How much of a prompt one piece of borrowed text may take.
    /// <para><b>This bounds the prompt; it does not guarantee a size.</b> The plan and the previous
    /// review are the tool's own output and have no natural size at all — a plan for a large change
    /// runs to pages — so leaving them uncapped while capping the working tree was capping the smaller
    /// half. But the parts together (six blocks plus the quality rules and the examples) can still
    /// exceed the 8 191 characters a <c>.cmd</c> shim allows on a command line, and choosing numbers
    /// that always fit would mean a diff of two thousand characters, which is not worth sending. What
    /// actually makes the failure survivable is the pair below it:
    /// <c>AiProcessRunner.GuardPromptLength</c>, which refuses with a message naming the cause, and
    /// <c>IAiToolRunner.AcceptsPromptOnStdin</c>, which removes the limit for tools that read standard
    /// input.</para>
    /// </summary>
    public const int MaxBorrowedChars = 2_000;

    /// <summary>The least working tree a review is sent, however tight the prompt. Enough for the
    /// line saying git could not be read, which is the one part of the tree that cannot be looked up by
    /// the tool itself — see <c>ComposeReview</c>.</summary>
    private const int TreeFloor = 200;

    private const int Roomy = 3_000;
    private const int Tight = 1_500;
    private const int Cramped = 750;
    private const int Bare = 300;

    /// <summary>
    /// The rungs <see cref="Fit"/> climbs down, named because parts of the prompt are chosen by
    /// comparing against them.
    /// </summary>
    /// <remarks>
    /// A block that says <c>cap >= 1_500</c> is saying "survive as far as the second rung", and written
    /// as a bare number that is a coupling nobody can see: moving a step here would silently change what
    /// gives way first, in a file that does not mention this array. Halving, and then off a cliff — the
    /// last step drops every borrowed block, which is a poor prompt and still an enormously better one
    /// than an exception.
    /// </remarks>
    private static readonly int[] FittingSteps = [Roomy, Tight, Cramped, Bare, 0];

    /// <summary>
    /// Builds the prompt, and keeps rebuilding it smaller until the command line can carry it.
    /// <para>The caps above bound the prompt and never promised a size, and the arithmetic is not
    /// close: a review carrying the goal, the quality rules, seven thousand
    /// characters of working tree, the severity rules and an example runs to about twelve thousand —
    /// against the <b>8 191</b> a <c>.cmd</c> shim allows, which is what npm installs and what
    /// <c>AiToolDetector</c> looks for first. Every tool that does not read its prompt on stdin goes
    /// that way, and the case that overflows is the one the whole feature exists for: a resume after a
    /// large implementation in a busy working tree.</para>
    /// <para>Refusing was the old answer and a poor one — the run is judged failed, the tile pauses,
    /// and Resume reproduces the identical failure for ever. Trimming costs the tool some context and
    /// costs the user nothing, so the borrowed blocks give way in order of size until the thing fits.
    /// <c>AiProcessRunner.GuardPromptLength</c> stays as the last line of defence, for a goal so long
    /// that even the instructions alone will not go.</para>
    /// <para>The budget arrives as a number and the measuring is <see cref="CommandLineLength"/>'s, so
    /// this class still knows nothing about tools, processes or platforms — which is what its own
    /// documentation claims, and briefly stopped being true when it reached into the process runner for
    /// the measurement.</para>
    /// </summary>
    private static string Fit(Func<int, string> compose, int? budget)
    {
        var prompt = compose(int.MaxValue);
        if (budget is not { } limit || CommandLineLength.Quoted(prompt) <= limit) return prompt;

        // A limit of zero or less — which Budget answers for a pathologically long executable path -
        // is nothing to fit into. Each step below would be composed and rejected in turn, and the
        // caller's own guard refuses the result either way; reach the floor in one step instead.
        if (limit <= 0) return compose(0);

        foreach (var cap in FittingSteps)
        {
            prompt = compose(cap);
            if (CommandLineLength.Quoted(prompt) <= limit) return prompt;
        }

        return prompt;
    }

    /// <summary>
    /// The clarification conversation, cut from the <b>front</b> when it will not fit.
    /// <para>Everything else here keeps its head and drops its tail, which is right for a diff and
    /// exactly wrong for a conversation: the newest turns are the ones the next round has to act on,
    /// and cutting from the end dropped the user's latest answers while faithfully preserving the
    /// questions from three rounds ago.</para>
    /// </summary>
    private static string Recent(IReadOnlyList<string> turns, int maxChars)
    {
        var whole = string.Join("\n", turns);
        if (whole.Length <= maxChars) return whole;

        var tail = whole[^maxChars..];

        // On a line boundary, so a turn is not handed over starting halfway through a sentence.
        var firstBreak = tail.IndexOf('\n');
        if (firstBreak >= 0 && firstBreak < tail.Length - 1) tail = tail[(firstBreak + 1)..];

        return $"… earlier turns omitted.\n{tail}";
    }

    /// <summary>A borrowed block's cap at this fitting step — never more than it would have had.</summary>
    private static int Cap(int step) => Math.Min(step, MaxBorrowedChars);

    /// <summary>The goal's, which has a floor: a prompt that has trimmed away what it was asked to do
    /// is not a smaller prompt, it is a different one.</summary>
    private static int GoalCap(int step) => Math.Max(200, Cap(step));

    /// <summary>
    /// The images the user pasted, as the markers they left in the text and the files those markers
    /// stand for.
    /// </summary>
    /// <remarks>
    /// <para>Markers and paths together, because either alone is useless: the text says
    /// <c>[Image #1]</c> and the tool has no way to turn that into something it can open, while a bare
    /// list of paths does not say which sentence each picture belongs to.</para>
    /// <para>Asking rather than telling — <em>open it if the work depends on it</em>. Every tool this
    /// tile drives can read a file, but reading three screenshots on a prompt that only mentions them
    /// in passing spends the run's time on nothing, and a review that has to look at a picture before
    /// it may report anything is a review that will find something in the picture.</para>
    /// <para>Capped like every other borrowed block and dropped whole at the last rung, and the
    /// instruction goes with it: a line telling the tool to open the files above, printed above
    /// nothing, is a prompt asking for something it did not say.</para>
    /// </remarks>
    private string Images(int cap)
    {
        if (_attachments() is not { Count: > 0 } images) return "";

        var listed = string.Join("\n", images.Select(i => $"{GoalImageMarker.For(i.Index)} {i.Path}"));
        var block = Block("Attached images", listed, Cap(cap));
        if (block.Length == 0) return "";

        return block +
               "Each marker above appears in the text and names a file on this machine. Open the ones " +
               "the work depends on.\n\n";
    }

    /// <summary>
    /// Wraps a block of borrowed text in a fence long enough that the text cannot end it.
    /// <para>A three-backtick fence is broken by any diff that touches a markdown file — the file's own
    /// fences close ours, and everything after them reads as prose, including the rest of the diff. The
    /// fence is therefore one backtick longer than the longest run inside, which is what CommonMark
    /// asks for and what no fixed length can promise.</para>
    /// <para>The heading says "working tree" rather than "git diff" because the block is no longer only
    /// a diff: it also carries the names of untracked files and, when git could not be read at all, a
    /// note saying so.</para>
    /// </summary>
    internal static string Block(string heading, string content) => Block(heading, content, int.MaxValue);

    /// <summary>
    /// The composer's words as a scope, or nothing — the soft half of a narrowed task.
    /// </summary>
    /// <remarks>
    /// <para>What the user typed beside the buttons travels as a block of its own rather than being
    /// folded into the goal: a goal is something the run was asked to achieve, and a narrowing is
    /// something it has been told to respect — different sentences in the transcript, different things
    /// in the prompt. The hard half lives in <see cref="GoalScopeFilter"/> when the words name paths.</para>
    /// <para><b>Capped like every other borrowed block</b>, and not as tidiness: the words are
    /// user-typed and unbounded — a paste, a dictated paragraph — and an uncapped block beside a review
    /// on an agent whose prompt rides a command line is a run that stops at the guard with "the prompt
    /// is too long" where the day before it ran. That is the same lesson the instance's extra arguments
    /// taught the other way round. The narrowing is a sentence in the ordinary case; a draft longer than
    /// the cap degrades the way a diff does, and says so where it was cut.</para>
    /// </remarks>
    private static string Narrowing(string? guideline, string what, int cap) =>
        string.IsNullOrWhiteSpace(guideline)
            ? ""
            : Block($"The user narrowed this {what}", guideline.Trim(), Cap(cap));

    internal static string Block(string heading, string content, int maxChars)
    {
        // No room means no block. Without this the last fitting step emitted a heading, a fence and the
        // words "truncated at 0 characters" — several lines of prompt saying nothing, in the one case
        // where every character was already spoken for.
        if (maxChars <= 0 || content.Length == 0) return "";

        if (content.Length > maxChars)
        {
            var cut = content[..maxChars];
            var lastBreak = cut.LastIndexOf('\n');
            content = (lastBreak > 0 ? cut[..lastBreak] : cut)
                      + $"\n… truncated at {maxChars} characters.";
        }

        var longestRun = 0;
        var run = 0;
        foreach (var c in content)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longestRun) longestRun = run;
        }

        var fence = new string('`', Math.Max(3, longestRun + 1));
        return $"{heading}:\n{fence}\n{content}\n{fence}\n\n";
    }

    /// <summary>
    /// Everything one implementation attempt is told, as one object.
    /// </summary>
    /// <remarks>
    /// A record rather than seven positional parameters, which is what this had grown to. Several of
    /// them are nullable strings, and a call site that swapped two of them would compile.
    /// </remarks>
    public sealed record ImplementContext(
        string Goal,
        string? ApprovedPlan = null,
        string? ReviewFeedback = null,
        string? GitDiff = null,
        IReadOnlyList<string>? AttemptLog = null,
        int Attempt = 0,
        int Attempts = 0);

    public string BuildImplement(ImplementContext context, int? budget = null) =>
        Fit(cap => ComposeImplement(context, cap), budget);

    private string ComposeImplement(ImplementContext c, int cap)
    {
        var prompt = Block("Implement the following goal in this project", c.Goal, GoalCap(cap))
                     + Images(cap);
        if (!string.IsNullOrEmpty(c.ApprovedPlan))
            prompt += Block("Approved implementation plan", c.ApprovedPlan, Cap(cap));
        prompt += QualityRules();
        prompt += HealthRules(review: false);
        // The first thing dropped when the prompt will not fit, and this is a reversal of what was
        // written here before. The old rule kept the diff to the last rung and dropped the attempt notes
        // first, on the grounds that the diff is the state of the work. It is — and the tool can read
        // it: these are agents running in the workspace with their own tools, and `git diff HEAD` is one
        // call away. A note about the path an earlier attempt tried and backed out of is recoverable by
        // nothing at all. When it goes, it is gone.
        //
        // The irony decided it: Fit only descends this ladder when the prompt will not fit, which means
        // a large working tree after several attempts — the exact run where the notes are worth most,
        // and the only one in which they were being thrown away.
        //
        // The cap is the fitting step itself, with no ceiling over it. It used to be
        // Math.Min(step, MaxDiffChars + MaxUntrackedChars), and that arithmetic was wrong twice over:
        // the tree arrives from GoalDiffContext already clipped part by part, and the assembled string
        // is *longer* than the sum of those caps — the headings and the blank lines between the parts
        // are not free — so the ceiling bit on every single build, fitted or not, and cut the tail off a
        // block that was already the right size. Whatever it cuts is the last thing in the block, which
        // is why GoalDiffContext orders the parts with the least replaceable first.
        if (c.GitDiff != null && cap >= Tight)
            prompt += Block("Current state of the working tree", c.GitDiff, cap);

        if (c.ReviewFeedback != null && cap > 0)
            prompt += Block("Fix these findings from the previous review", c.ReviewFeedback, Cap(cap));

        // Kept as long as anything else here is, and capped like the rest rather than halved: it is two
        // short lines per attempt, and it is the one thing in this prompt that cannot be looked up.
        if (c.AttemptLog is { Count: > 0 } log && cap > 0)
            prompt += Block("What earlier attempts did and decided", string.Join("\n", log), Cap(cap));

        // Said plainly, because a model that does not know it is nearly out of attempts keeps
        // experimenting: the last one should be the safe, minimal version rather than a fresh idea.
        if (c.Attempt > 0 && c.Attempts > 0)
            prompt += $"This is attempt {c.Attempt} of {c.Attempts}.\n\n";

        prompt += "Follow the approved plan. Make the necessary code changes. Be precise and minimal.\n" +
                  OtherPeoplesWork +
                  "Finish with one line saying what you changed, then one line starting \"Rejected:\" " +
                  "naming anything you tried or considered and did not do, and why.\n\n" +
                  AnswerLanguage +
                  "Example: Changed src/Cart.cs and added tests/CartTests.cs; discounts now apply before totalling.\n" +
                  "Rejected: caching the totals — the basket is rebuilt per request, so it would never hit.";
        return prompt;
    }

    /// <summary>
    /// The working tree is not the tool's to undo, and a review finding is never an instruction to
    /// delete something.
    /// </summary>
    /// <remarks>
    /// <para>This is a data-loss guard, and it was paid for. The review is handed the whole of
    /// <c>git diff HEAD</c> under the heading "the code changes that were just made", which is a claim
    /// this tile cannot support: the user works in the terminal tiles next door while a goal runs, so
    /// the tree routinely holds their own parallel change as well. The reviewer read that honestly and
    /// reported it — <em>unrelated changes glued onto this one</em> — as a warning, which
    /// <see cref="GoalTranscript.Feedback"/> then passed back verbatim under "Fix these findings". The
    /// next attempt did the only thing that makes such a finding go away: it reverted the user's files
    /// and deleted the ones they had not committed.</para>
    /// <para>Every link in that chain was behaving correctly, which is why the fix is a sentence rather
    /// than a condition. And the last clause is the load-bearing one, for the same reason it is in
    /// <see cref="HealthRules"/>: a warning counts against a tolerance of zero, so the run cannot
    /// finish while the finding stands. Forbidding the repair without offering a way past it leaves the
    /// tool holding something it may neither ignore nor fix. Saying so in its closing line is the way
    /// past — it reaches the user, and it costs no files.</para>
    /// </remarks>
    private const string OtherPeoplesWork =
        "The working tree may also contain the user's own parallel work. Change only what the goal and " +
        "the plan ask for. Never revert, delete or restore a file to make a review finding go away — " +
        "if a finding is about changes that are not yours, say so in your closing line instead.\n";

    /// <param name="scoped">Whether the working tree block is only what changed since the goal
    /// started. See <see cref="OtherPeoplesWorkInReview"/> for what turns on it.</param>
    public string BuildReview(string goal, string? gitDiff, bool scoped = false, int? budget = null,
        string? guideline = null) =>
        Fit(cap => ComposeReview(goal, gitDiff, scoped, cap, guideline), budget);

    /// <summary>
    /// The warning that the working tree is not all one change — said only where it is true.
    /// </summary>
    /// <remarks>
    /// <para>This sentence is a liability, and it is kept only for the case where nothing better is
    /// available. It asks the reviewer for a distinction it has no data to make, and the two ways of
    /// getting that wrong are not symmetrical: a finding it invents is in the transcript where somebody
    /// sees it, while a finding it swallows leaves no trace anywhere. Trading a loud error for a silent
    /// one is a bad trade.</para>
    /// <para>So where the block is read against the goal's baseline it is dropped outright —
    /// everything in it happened during the run, and the reviewer can be left alone to judge all of it.
    /// It survives for the fallback: a repository whose baseline could not be taken, where the block is
    /// <c>git diff HEAD</c> and does hold whatever the user had lying around from last week. Without it
    /// there, a scope warning against a tolerance of zero blocks every remaining attempt over files
    /// nothing in the run will ever touch.</para>
    /// <para>It closes only half of the problem either way, and the implement prompt's
    /// <see cref="OtherPeoplesWork"/> closes the other and is <b>not</b> conditional: that one forbids
    /// an action rather than asking for a judgement, so it has none of this one's downside — and no
    /// baseline narrows the window to nothing.</para>
    /// </remarks>
    private const string OtherPeoplesWorkInReview =
        "The working tree may also contain unrelated work by the user. Judge only what serves the " +
        "goal; do not report their unrelated changes as a finding.\n\n";

    private string ComposeReview(string goal, string? gitDiff, bool scoped, int cap,
        string? guideline = null)
    {
        var prompt = "Review the code changes that were just made in this project.\n\n"
                     + Block("The original goal was", goal, GoalCap(cap))
                     + Narrowing(guideline, "review", cap)
                     + Images(cap);
        prompt += QualityRules();
        prompt += HealthRules(review: true);
        // The fitting step itself, with no ceiling over it — see the note in ComposeImplement —
        // and with a floor under it, for the same reason the goal has one.
        //
        // The floor is what makes "the note that git could not be read is never cut" true by
        // construction. It was true by arithmetic: the last rung dropped this block outright, and the
        // rung above it happened to fit with about a hundred characters to spare, so the guarantee held
        // only while nothing else in the prompt grew. Adding one fixed sentence took it away, silently,
        // and the failing case is the one the note exists for — a tool told nothing has changed writes
        // over work it cannot see.
        //
        // It costs little where it bites: GoalDiffContext puts the note first and Block cuts on line
        // boundaries, so at this size a diff of one enormous line contributes nothing at all and only
        // the note survives. It is charged only at the last rung, which is reached only by a prompt
        // that fits nowhere else.
        if (gitDiff != null)
            prompt += Block("Current state of the working tree", gitDiff, Math.Max(TreeFloor, cap));

        // The user's thresholds are deliberately not in here. A reviewer told that one warning is
        // allowed has been told how to pass, and the severities are the one thing in its answer nothing
        // else can check.
        if (!scoped) prompt += OtherPeoplesWorkInReview;

        prompt += "Judge two things separately:\n" +
                  "- goalMet: whether the changes actually do what the goal asked for. Clean code that " +
                  "does the wrong thing is not the goal met.\n" +
                  "- findings: what is wrong with them. One entry per issue.\n\n" +
                  "Use exactly these severities:\n" +
                  "- blocker: it works, and it still must not stand — it breaks a stated constraint or " +
                  "assumption of the goal, or fails outside the case in front of you: a platform limit, " +
                  "a race, data loss, a security hole.\n" +
                  "- error: it is wrong — broken, incorrect, or missing.\n" +
                  "- warning: it works, but should not stay — a real risk, or " + WarningSubjects() + ".\n" +
                  "- suggestion: worth knowing, not worth blocking on.\n" +
                  "The line between blocker and error is whether the code is unacceptable or simply " +
                  "wrong. Do not reach for blocker to add weight to an error.\n" +
                  "Report every issue you find at its honest severity. Send an empty findings list when " +
                  "there is nothing to report.\n\n" +
                  AnswerLanguage +
                  ReviewExample +
                  // First line of defence against the one malformed shape that no reading of the
                  // brackets repairs: a quote left unescaped inside a string value. Measured live,
                  // 2026-09-01 — a reviewer put a C# interpolation with its own quotes inside a
                  // finding's detail and the block died for every reader this tile has. A prompt is a
                  // request rather than a protocol, so the parser's salvage round stands behind this;
                  // the sentence is what makes the round the exception instead of the rule.
                  "\nInside string values, escape every double quote as \\\" so the block is strictly " +
                  "valid JSON." +
                  // Asked for as well as the block, and not as a belt-and-braces flourish: it is the
                  // fallback's trigger. GoalResponseParser reads an answer with no JSON in it by
                  // looking for these words, and while nothing asked for them a tool that ignores the
                  // schema could never say the goal was met — so it burned the whole budget and ended
                  // every goal unfinished. A fallback whose phrase is never requested is not a
                  // fallback.
                  "\n\nIf you cannot produce the json block, end your reply with the line " +
                  "VERDICT: PASS or VERDICT: FAIL instead.";
        return prompt;
    }

    /// <summary>
    /// Asks the tool to re-send a review block it wrote as invalid JSON, as valid JSON.
    /// </summary>
    /// <remarks>
    /// <para>The salvage round's prompt, and <b>the answer travels alone</b> — no goal, no diff, no
    /// criteria. The tool that wrote the block is the only reader who knows what it meant, so the repair
    /// belongs to it; but everything the block was judged against already did its part, and sending it
    /// again would make one unescaped quote cost a whole review run. A few hundred characters in, a few
    /// hundred out.</para>
    /// <para>No budget, on purpose: the caller has no choice about fitting, because trimming the
    /// <em>broken</em> text changes what is being repaired — a finding cut out of the middle is a
    /// finding the retry cannot return. Reviews are the smallest of the prompts this tile sends, and
    /// the agents whose prompt rides a command line get the guard's refusal, with its reason, on the
    /// rare answer big enough to matter.</para>
    /// </remarks>
    public string BuildJsonSalvage(string brokenAnswer) =>
        "The text below was meant to be a single JSON review block, but it is not valid JSON. The " +
        "usual cause is a double quote or a newline inside a string value.\n\n" +
        "Return exactly the same JSON — every key and every value unchanged, finding for finding — " +
        "with every double quote inside a string value escaped as \\\" and every newline inside a " +
        "string value written as \\n. No prose, no code fence: your whole reply is the JSON.\n\n" +
        Block("The answer to repair", brokenAnswer);

    /// <summary>
    /// Asks how the run's own work should be divided into commits.
    /// </summary>
    /// <remarks>
    /// <para>The one judgement here that nothing in this application can make: which of these changes
    /// is a feature and which is the chore that made it possible. Grouping by directory, or by the
    /// order files were touched, produces a history that is technically a series of commits and tells
    /// nobody anything.</para>
    /// <para><b>The file list is given, not asked for.</b> It is worked out from the goal's own
    /// baseline — the tree as it stood when the goal started — with the files the user had already
    /// changed before that removed. The tool is told to place these paths and no others, and
    /// <c>GoalCommitter</c> holds the answer against the same list rather than trusting it: a path
    /// invented here would put somebody's parallel work into a commit claiming to be about something
    /// else, which is the failure <see cref="OtherPeoplesWork"/> exists to prevent one layer down.</para>
    /// <para><b>The file list and no diff.</b> A <c>gitDiff</c> parameter used to be threaded through
    /// here and was never once given anything but null — a block of prompt-building that could not be
    /// reached, describing context nothing supplied. Grouping by meaning does want the diff, and adding
    /// it back is a real change rather than a restoration: it would be the largest borrowed block in
    /// any of these prompts, it arrives after the run has already spent its budget, and it would have
    /// to be trimmed against a list of paths that is never trimmed.</para>
    /// <para><b>So this prompt barely compresses, and that is its shape rather than an oversight.</b>
    /// What is left to shrink is the goal sentence; the paths are the answer the tool is being asked to
    /// group, and dropping one is a change nobody commits. A list too long for the command line
    /// therefore cannot be fitted — which the caller does not treat as the end of the matter: it offers
    /// the single sweeping commit rather than asking again and failing the same way.</para>
    /// </remarks>
    public string BuildCommitPlan(string goal, IReadOnlyList<string> files, int? budget = null) =>
        Fit(cap => ComposeCommitPlan(goal, files, cap), budget);

    private static string ComposeCommitPlan(string goal, IReadOnlyList<string> files, int cap)
    {
        var prompt = "The work below has just been done in this project and is not committed yet.\n\n"
                     + Block("The goal was", goal, GoalCap(cap));

        // Never trimmed, and this is the one block in any of these prompts with no cap at all. Every
        // other borrowed thing here degrades — a shorter diff is less context, a shorter plan is less
        // detail — while a path cut out of this list is a change that silently never gets committed.
        // It is one line per file and bounded by how much a single run touched, which is the same
        // argument that keeps `--stat` above the diff in GoalDiffContext.
        prompt += Block("Commit exactly these files, and no others", string.Join("\n", files));

        prompt += "Divide them into commits by what they mean, not by where they live. One commit per " +
                  "coherent change: a feature and the refactoring that made room for it are two " +
                  "commits, and the same feature spread over four directories is one.\n" +
                  "Use a conventional-commit type — feat, fix, chore, refactor, test, docs — and a " +
                  "subject in the imperative, under 72 characters.\n" +
                  "Every file above must appear in exactly one commit. Do not name a file that is not " +
                  "listed. One commit is a correct answer when the work is one thing.\n\n" +
                  AnswerLanguage +
                  "Answer with one fenced json block and nothing else.\n\n" +
                  "Example:\n" +
                  "```json\n" +
                  "{\"commits\":[" +
                  "{\"type\":\"feat\",\"subject\":\"apply discounts before totalling\"," +
                  "\"files\":[\"src/Cart.cs\",\"tests/CartTests.cs\"]}," +
                  "{\"type\":\"chore\",\"subject\":\"drop the unused price formatter\"," +
                  "\"files\":[\"src/PriceFormat.cs\"]}]}\n" +
                  "```";
        return prompt;
    }

    /// <summary>
    /// Works out what the user was in the middle of doing, from the changes they have not committed.
    /// <para>The other entry into this tile. Typing a goal describes work that has not started; this
    /// describes work that has, which is the more common way somebody arrives at a tile like this —
    /// half-finished, wanting it finished.</para>
    /// <para>Plain prose out, alone among these prompts, because the answer is one sentence that goes
    /// into the composer for the user to edit before anything acts on it.</para>
    /// </summary>
    public string BuildDetectGoal(string gitDiff, int? budget = null, string? guideline = null) =>
        Fit(cap => ComposeDetectGoal(gitDiff, cap, guideline), budget);

    /// <summary>
    /// The language this machine is set up in, as an instruction — for the one prompt that cannot ask
    /// for "the language of the goal above" because working out the goal is what it is for.
    /// </summary>
    /// <remarks>
    /// <para>Every other prompt anchors on the user's own words (see <see cref="AnswerLanguage"/>),
    /// which is both free and impossible to get wrong. This one is reached from the + button over an
    /// uncommitted working tree: nothing has been typed, so the only thing in the prompt is a diff —
    /// and a diff is written in English whoever wrote it. The answer came back in English and went
    /// straight into the composer as the user's own goal, where every later prompt then anchored on
    /// it: one phase with nothing to read from set the language of the whole run.</para>
    /// <para>The display language rather than the formats: <c>CurrentUICulture</c> is what Windows
    /// shows its own menus in and what <c>LANG</c> says on Linux, while <c>CurrentCulture</c> is dates
    /// and decimal separators — a machine set to English with Polish formats would have been answered
    /// in Polish. Named in English (<c>EnglishName</c> of the neutral culture, so "Polish" rather than
    /// "Polish (Poland)"), because the rest of the prompt is English and a model reads a language's
    /// English name more reliably than a tag. The invariant culture asks for nothing: it means the
    /// machine did not say, and English is what the prompt is already in.</para>
    /// </remarks>
    internal static string AnswerInSystemLanguage(CultureInfo culture)
    {
        var neutral = culture.IsNeutralCulture ? culture : culture.Parent;
        return neutral.Equals(CultureInfo.InvariantCulture) || neutral.TwoLetterISOLanguageName == "en"
            ? ""
            : $"Answer in {neutral.EnglishName}. Keep code and identifiers as they are.\n\n";
    }

    private static string ComposeDetectGoal(string gitDiff, int cap, string? guideline = null)
    {
        // A floor, as the goal has in the other prompts and for the same reason: this one asks what the
        // changes are for, so a version of it with the changes trimmed away is not a smaller prompt but
        // an unanswerable one. If even that will not fit, the guard refuses and says why.
        return "Below are the uncommitted changes in a software project.\n\n"
               + Narrowing(guideline, "detection", cap)
               + Block("Working tree", gitDiff, Math.Max(500, cap))
               + "Work out what the person making these changes is trying to achieve, and state it as a " +
                 "goal that is not yet finished — what should be true when the work is done, not a list " +
                 "of what has been touched.\n" +
                 // The block above is clipped and the file list in it is not, so the tool can see that
                 // the change reaches files whose diff it was never shown. Without this it answered
                 // from the fragment with complete confidence and no hint that there was more — which
                 // is how a working tree of twenty-one files came back as a goal about the two that
                 // sorted first.
                 "Use the whole of what you are shown, including the list of changed files: the diff " +
                 "may be truncated, and a file listed there but absent from it has still changed. If " +
                 "the changes plainly cover more than one piece of work, say so in that sentence and " +
                 "name the largest.\n" +
                 "Answer with that one sentence and nothing else: no preamble, no bullet points, no " +
                 "code block.\n\n" +
                 AnswerInSystemLanguage(CultureInfo.CurrentUICulture) +
                 "Example: Phone pairings survive a restart, so a paired device does not have to scan " +
                 "the QR code again.";
    }
}
