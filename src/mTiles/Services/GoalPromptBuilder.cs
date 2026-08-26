namespace mTiles.Services;

public sealed class GoalPromptBuilder
{
    private const string QualityRules =
        "All changes MUST follow Clean Code principles (descriptive naming, small single-purpose functions, " +
        "no duplication, self-documenting code) and SOLID principles — especially:\n" +
        "- Single Responsibility Principle: each class/method has one reason to change\n" +
        "- Open/Closed Principle: open for extension, closed for modification\n\n";

    /// <summary>
    /// Every prompt here ends with one worked example of the answer it wants, and never more than one.
    /// <para>Not decoration: three of these prompts now ask for a JSON object, and a model shown a
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
        "{\"needsClarification\":true,\"verify\":\"npm test\",\"questions\":[" +
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

    private static string ComposeClarify(string goal, IReadOnlyList<string> clarificationHistory, int cap)
    {
        var prompt = "You are helping implement a goal in a software project.\n\n"
                     + Block("The goal is", goal, GoalCap(cap));
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
                  // The one instruction here that is not about questions, and it replaces a text box.
                  // The criteria panel used to ask the user to turn "the tests must pass" into a shell
                  // command themselves and to know which command this project uses — a fair question
                  // in a C# repository and a worse one in every other, since the panel had no idea
                  // whether it was looking at dotnet, npm or cargo. The tool is standing in the
                  // repository, so it is asked to look; and where it cannot tell, to ask, which is the
                  // one thing this round already exists to do.
                  //
                  // Asked for on *every* goal, not only one that mentions a command. Most goals are
                  // business goals — "add cart discounts" — and they still have to compile; making the
                  // command conditional on the user saying so meant the gate almost never armed, in
                  // front of the failure it exists for. A review written by the same family of model
                  // that wrote the code will call a build broken or working with equal confidence, and
                  // an exit code will not.
                  "- Set verify to the command this project uses to check itself, found by looking at " +
                  "the repository rather than assumed: its test runner where it has one, and otherwise " +
                  "its build. Prefer tests — compiling is a precondition of running them, so tests " +
                  "cover both — but a project with no tests is the ordinary case, not a problem: use " +
                  "the build. One line, no shell operators. If the goal names a check of its own, that " +
                  "one wins. Leave verify out when the repository offers nothing to run at all, and " +
                  "when the goal's work is not code that runs — writing documentation, prose, a " +
                  "README — since no exit code says anything about whether that was done well. When " +
                  "you cannot tell which command the project uses, ask about that instead: never " +
                  "guess, and never propose a command this repository gives no evidence for.\n" +
                  "Do not implement anything yet.\n\n" +
                  AnswerLanguage +
                  ClarifyExample;
        return prompt;
    }

    public string BuildPlan(string goal, IReadOnlyList<string> clarificationHistory, int? budget = null) =>
        Fit(cap => ComposePlan(goal, clarificationHistory, cap), budget);

    private static string ComposePlan(string goal, IReadOnlyList<string> clarificationHistory, int cap)
    {
        var prompt = "You are planning the implementation of a goal in a software project.\n\n"
                     + Block("Original goal", goal, GoalCap(cap));
        if (clarificationHistory.Count > 0 && cap > 0)
            prompt += Block("User clarifications", Recent(clarificationHistory, Cap(cap)), int.MaxValue);
        prompt += QualityRules;
        prompt += AnswerLanguage;
        prompt += "Create a concise implementation plan. Do not implement anything yet.\n\n" +
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
    /// <summary>The least working tree a review is sent, however tight the prompt. Enough for the
    /// line saying git could not be read, which is the one part of the tree that cannot be looked up by
    /// the tool itself — see <c>ComposeReview</c>.</summary>
    private const int TreeFloor = 200;

    private const int Roomy = 3_000;
    private const int Tight = 1_500;
    private const int Cramped = 750;
    private const int Bare = 300;

    private static readonly int[] FittingSteps = [Roomy, Tight, Cramped, Bare, 0];

    /// <summary>
    /// Builds the prompt, and keeps rebuilding it smaller until the command line can carry it.
    /// <para>The caps above bound the prompt and never promised a size, and the arithmetic is not
    /// close: a review carrying the goal, the quality rules, a verify command's output, seven thousand
    /// characters of working tree, the severity rules and an example runs to about twelve thousand —
    /// against the <b>8 191</b> a <c>.cmd</c> shim allows, which is what npm installs and what
    /// <c>AiToolDetector</c> looks for first. Three of the four supported tools go that way, and the
    /// case that overflows is the one the whole feature exists for: a resume after a large
    /// implementation, in a workspace with a verify command configured.</para>
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
    /// A record rather than eight positional parameters, which is what this had grown to. Six of them
    /// were nullable strings, and a call site that swapped two of them would compile.
    /// </remarks>
    public sealed record ImplementContext(
        string Goal,
        string? ApprovedPlan = null,
        string? ReviewFeedback = null,
        string? VerifyOutput = null,
        string? GitDiff = null,
        IReadOnlyList<string>? AttemptLog = null,
        int Attempt = 0,
        int Attempts = 0);

    public string BuildImplement(ImplementContext context, int? budget = null) =>
        Fit(cap => ComposeImplement(context, cap), budget);

    private static string ComposeImplement(ImplementContext c, int cap)
    {
        var prompt = Block("Implement the following goal in this project", c.Goal, GoalCap(cap));
        if (!string.IsNullOrEmpty(c.ApprovedPlan))
            prompt += Block("Approved implementation plan", c.ApprovedPlan, Cap(cap));
        prompt += QualityRules;
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

        // The build's own words, ahead of the reviewer's account of them. Without this the one being
        // asked to fix a broken build was shown a review *about* the compiler error rather than the
        // error: a line number turned into "there is a type mismatch somewhere in the cart code". It is
        // already clipped to 2 000 characters by VerifyCommandRunner before it gets here.
        if (c.VerifyOutput is { Length: > 0 } && cap > 0)
            prompt += Block("The project's verify command failed with this output", c.VerifyOutput, Cap(cap));

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
                  "Finish with one line saying what you changed, then one line starting \"Rejected:\" " +
                  "naming anything you tried or considered and did not do, and why.\n\n" +
                  AnswerLanguage +
                  "Example: Changed src/Cart.cs and added tests/CartTests.cs; discounts now apply before totalling.\n" +
                  "Rejected: caching the totals — the basket is rebuilt per request, so it would never hit.";
        return prompt;
    }

    /// <param name="verifyOutput">What the user's verify command printed, when there is one. It goes in
    /// ahead of the diff on purpose: a compiler's opinion of the change outranks the reviewer's, and a
    /// review written without it argues about style over code that does not build.</param>
    public string BuildReview(string goal, string? gitDiff, string? verifyOutput = null, int? budget = null) =>
        Fit(cap => ComposeReview(goal, gitDiff, verifyOutput, cap), budget);

    private static string ComposeReview(string goal, string? gitDiff, string? verifyOutput, int cap)
    {
        var prompt = "Review the code changes that were just made in this project.\n\n"
                     + Block("The original goal was", goal, GoalCap(cap));
        prompt += QualityRules;
        if (verifyOutput is { Length: > 0 } && cap > 0)
            prompt += Block("Output of the project's verify command", verifyOutput, Cap(cap));
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
        prompt += "Judge two things separately:\n" +
                  "- goalMet: whether the changes actually do what the goal asked for. Clean code that " +
                  "does the wrong thing is not the goal met.\n" +
                  "- findings: what is wrong with them. One entry per issue.\n\n" +
                  "Use exactly these severities:\n" +
                  "- blocker: it works, and it still must not stand — it breaks a stated constraint or " +
                  "assumption of the goal, or fails outside the case in front of you: a platform limit, " +
                  "a race, data loss, a security hole.\n" +
                  "- error: it is wrong — broken, incorrect, or missing.\n" +
                  "- warning: it works, but should not stay — a real risk, or a Clean Code / SOLID violation.\n" +
                  "- suggestion: worth knowing, not worth blocking on.\n" +
                  "The line between blocker and error is whether the code is unacceptable or simply " +
                  "wrong. Do not reach for blocker to add weight to an error.\n" +
                  "Report every issue you find at its honest severity. Send an empty findings list when " +
                  "there is nothing to report.\n\n" +
                  AnswerLanguage +
                  ReviewExample +
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
    /// Works out what the user was in the middle of doing, from the changes they have not committed.
    /// <para>The other entry into this tile. Typing a goal describes work that has not started; this
    /// describes work that has, which is the more common way somebody arrives at a tile like this —
    /// half-finished, wanting it finished.</para>
    /// <para>Plain prose out, alone among these prompts, because the answer is one sentence that goes
    /// into the composer for the user to edit before anything acts on it.</para>
    /// </summary>
    public string BuildDetectGoal(string gitDiff, int? budget = null) =>
        Fit(cap => ComposeDetectGoal(gitDiff, cap), budget);

    private static string ComposeDetectGoal(string gitDiff, int cap)
    {
        // A floor, as the goal has in the other prompts and for the same reason: this one asks what the
        // changes are for, so a version of it with the changes trimmed away is not a smaller prompt but
        // an unanswerable one. If even that will not fit, the guard refuses and says why.
        return "Below are the uncommitted changes in a software project.\n\n"
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
                 "Example: Phone pairings survive a restart, so a paired device does not have to scan " +
                 "the QR code again.";
    }
}
