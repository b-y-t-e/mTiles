namespace mTiles.Services;

public sealed class GoalPromptBuilder
{
    private const string QualityRules =
        "All changes MUST follow Clean Code principles (descriptive naming, small single-purpose functions, " +
        "no duplication, self-documenting code) and SOLID principles — especially:\n" +
        "- Single Responsibility Principle: each class/method has one reason to change\n" +
        "- Open/Closed Principle: open for extension, closed for modification\n\n";

    public string BuildClarify(string goal, IReadOnlyList<string> clarificationHistory)
    {
        var prompt = "You are helping implement a goal in a software project.\n\n"
                     + Block("The goal is", goal, MaxBorrowedChars);
        if (clarificationHistory.Count > 0)
            prompt += Block("Previous conversation", string.Join("\n", clarificationHistory), MaxBorrowedChars);
        prompt += "Your tasks:\n" +
                  "1. Verify the goal is specific, measurable, and achievable in code. If not, point out what is vague.\n" +
                  "2. Ask 2-3 short, specific clarifying questions to fill any gaps.\n" +
                  "3. If the goal is already fully clear and achievable, say so and confirm you have no questions.\n\n" +
                  "Be concise. Only ask questions, do not implement anything yet.";
        return prompt;
    }

    public string BuildPlan(string goal, IReadOnlyList<string> clarificationHistory)
    {
        var prompt = "You are planning the implementation of a goal in a software project.\n\n"
                     + Block("Original goal", goal, MaxBorrowedChars);
        if (clarificationHistory.Count > 0)
            prompt += Block("User clarifications", string.Join("\n", clarificationHistory), MaxBorrowedChars);
        prompt += QualityRules;
        prompt += "Create a concise implementation plan:\n" +
                  "1. Restate the goal in one clear sentence\n" +
                  "2. List the concrete steps (files to create/modify, logic to add)\n" +
                  "3. State clear success criteria — how to verify the goal is met\n\n" +
                  "Be specific and actionable. Do not implement anything yet.";
        return prompt;
    }

    /// <summary>
    /// How much of a prompt one piece of borrowed text may take.
    /// <para><b>This bounds the prompt; it does not guarantee a size.</b> The plan and the previous
    /// review are the tool's own output and have no natural size at all — a plan for a large change
    /// runs to pages — so leaving them uncapped while capping the working tree was capping the smaller
    /// half. But the parts together (six blocks plus the quality rules) can still exceed the 8 191
    /// characters a <c>.cmd</c> shim allows on a command line, and choosing numbers that always fit
    /// would mean a diff of two thousand characters, which is not worth sending. What actually makes
    /// the failure survivable is the pair below it: <c>AiProcessRunner.GuardPromptLength</c>, which
    /// refuses with a message naming the cause, and <c>IAiToolRunner.AcceptsPromptOnStdin</c>, which
    /// removes the limit for tools that read standard input.</para>
    /// </summary>
    public const int MaxBorrowedChars = 2_000;

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

    public string BuildImplement(string goal, string? approvedPlan, string? reviewFeedback, string? gitDiff)
    {
        var prompt = Block("Implement the following goal in this project", goal, MaxBorrowedChars);
        if (!string.IsNullOrEmpty(approvedPlan))
            prompt += Block("Approved implementation plan", approvedPlan, MaxBorrowedChars);
        prompt += QualityRules;
        if (gitDiff != null)
            prompt += Block("Current state of the working tree", gitDiff);
        if (reviewFeedback != null)
            prompt += Block("Previous review feedback", reviewFeedback, MaxBorrowedChars);
        prompt += "Follow the approved plan. Make the necessary code changes. Be precise and minimal.";
        return prompt;
    }

    public string BuildReview(string goal, string? gitDiff)
    {
        var prompt = "Review the code changes that were just made in this project.\n\n"
                     + Block("The original goal was", goal, MaxBorrowedChars);
        prompt += QualityRules;
        if (gitDiff != null)
            prompt += Block("Current state of the working tree", gitDiff);
        prompt += "Check if the changes correctly implement the goal. Report:\n" +
                  "1. Whether the goal is fully met\n" +
                  "2. Any bugs or issues found\n" +
                  "3. Clean Code and SOLID violations found\n" +
                  "4. Your verdict: PASS or FAIL\n\n" +
                  "If PASS, say 'VERDICT: PASS'. If there are issues, say 'VERDICT: FAIL' and describe what needs fixing.";
        return prompt;
    }
}
